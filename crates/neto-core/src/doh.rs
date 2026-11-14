use anyhow::{Context, Result, anyhow, bail, ensure};
use once_cell::sync::Lazy;
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::{BTreeSet, HashMap};
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::sync::Arc;
use std::time::Duration;
use tokio::task::JoinSet;

pub static SHARED_HTTP_CLIENT: Lazy<reqwest::Client> = Lazy::new(|| {
    let mut roots = rustls::RootCertStore::empty();
    roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
    let tls = rustls::ClientConfig::builder_with_provider(Arc::new(
        rustls::crypto::ring::default_provider(),
    ))
    .with_safe_default_protocol_versions()
    .expect("configure rustls protocol versions")
    .with_root_certificates(roots)
    .with_no_client_auth();
    reqwest::Client::builder()
        .use_preconfigured_tls(tls)
        .connect_timeout(Duration::from_secs(20))
        .timeout(Duration::from_secs(30))
        .build()
        .expect("build shared reqwest client")
});

#[derive(Deserialize)]
struct DohResponse {
    #[serde(rename = "Answer")]
    answer: Option<Vec<DohAnswer>>,
}

#[derive(Deserialize)]
struct DohAnswer {
    data: String,
}

pub async fn resolve_node_host(
    dns_url: &str,
    host: &str,
    user_agent: Option<&str>,
) -> Result<String> {
    let trimmed = host.trim().trim_matches(['[', ']']);
    if trimmed.parse::<IpAddr>().is_ok() {
        return Ok(trimmed.to_string());
    }
    let resolver = dns_url.trim();
    ensure!(
        resolver.starts_with("https://") || resolver.starts_with("http://"),
        "node DNS must be an HTTP/HTTPS DoH URL"
    );
    for record_type in ["A", "AAAA"] {
        let list = query_doh_family(resolver, trimmed, record_type, user_agent).await?;
        if let Some(first) = list.into_iter().next() {
            return Ok(first);
        }
    }
    Err(anyhow!(
        "DoH returned no usable A/AAAA record for {trimmed}"
    ))
}

fn normalize_flux_host(raw: &str) -> String {
    let trimmed = raw.trim();
    if let Some(inner) = trimmed.strip_prefix('[').and_then(|s| s.strip_suffix(']'))
        && inner.contains(':')
    {
        return inner.to_string();
    }
    trimmed.to_string()
}

fn channel_effective_host(ch: &serde_json::Map<String, Value>, main_host: &str) -> String {
    for key in [
        "server",
        "connect_host",
        "connect-host",
        "connectHost",
        "host",
    ] {
        if let Some(val) = ch.get(key).and_then(Value::as_str) {
            let norm = normalize_flux_host(val);
            if !norm.is_empty() {
                return norm;
            }
        }
    }
    main_host.to_string()
}

async fn query_doh_family(
    resolver: &str,
    host: &str,
    record_type: &str,
    user_agent: Option<&str>,
) -> Result<Vec<String>> {
    let mut url = reqwest::Url::parse(resolver).context("parse DoH resolver URL")?;
    url.query_pairs_mut()
        .append_pair("name", host)
        .append_pair("type", record_type);
    let mut request = SHARED_HTTP_CLIENT
        .get(url)
        .header("Accept", "application/dns-json");
    if let Some(ua) = user_agent.map(str::trim).filter(|v| !v.is_empty()) {
        request = request.header("User-Agent", ua);
    }
    let response = request
        .send()
        .await
        .map_err(|error| anyhow!("DoH request failed: {error}"))?;
    let status = response.status();
    if !status.is_success() {
        bail!("DoH request failed: HTTP {}", status.as_u16());
    }
    let body = response
        .json::<DohResponse>()
        .await
        .map_err(|error| anyhow!("DoH response is not JSON: {error}"))?;
    let mut out = Vec::new();
    if let Some(answers) = body.answer {
        for item in answers {
            let text = item.data.trim();
            let valid = if record_type == "A" {
                text.parse::<Ipv4Addr>().is_ok()
            } else {
                text.parse::<Ipv6Addr>().is_ok()
            };
            if valid && !out.iter().any(|existing| existing == text) {
                out.push(text.to_string());
            }
        }
    }
    Ok(out)
}

pub async fn resolve_flux_node(
    dns_url: &str,
    mut node: Value,
    user_agent: Option<&str>,
) -> Result<Value> {
    let obj = node
        .as_object_mut()
        .context("Flux node must be a JSON object")?;
    let main_host = obj
        .get("host")
        .or_else(|| obj.get("server"))
        .and_then(Value::as_str)
        .map(normalize_flux_host)
        .unwrap_or_default();
    let mut channels = obj
        .remove("channels")
        .and_then(|v| v.as_array().cloned())
        .or_else(|| {
            obj.get("flux")
                .and_then(|v| v.get("channels"))
                .and_then(Value::as_array)
                .cloned()
        })
        .unwrap_or_default();
    let mut domains = BTreeSet::new();
    if channels.is_empty() {
        if !main_host.is_empty() && main_host.parse::<IpAddr>().is_err() {
            domains.insert(main_host.clone());
        }
    } else {
        for ch in &channels {
            if let Some(ch) = ch
                .as_object()
                .filter(|ch| ch.get("enabled").and_then(Value::as_bool) != Some(false))
            {
                let host = channel_effective_host(ch, &main_host);
                if !host.is_empty() && host.parse::<IpAddr>().is_err() {
                    domains.insert(host);
                }
            }
        }
    }
    let resolver = dns_url.trim().to_string();
    let use_doh = !resolver.is_empty();
    ensure!(
        !use_doh
            || domains.is_empty()
            || resolver.starts_with("https://")
            || resolver.starts_with("http://"),
        "Flux DNS must be an HTTP/HTTPS DoH URL"
    );
    let mut tasks = JoinSet::new();
    if use_doh {
        for domain in domains {
            let resolver = resolver.clone();
            let ua = user_agent.map(str::to_string);
            tasks.spawn(async move {
                let (a, aaaa) = tokio::join!(
                    query_doh_family(&resolver, &domain, "A", ua.as_deref()),
                    query_doh_family(&resolver, &domain, "AAAA", ua.as_deref()),
                );
                (domain, a.unwrap_or_default(), aaaa.unwrap_or_default())
            });
        }
    }
    let mut resolved = HashMap::new();
    while let Some(result) = tasks.join_next().await {
        let (host, v4, v6) = result.context("Flux DNS task failed")?;
        resolved.insert(host, (v4, v6));
    }
    if channels.is_empty() {
        if let Some((v4, v6)) = resolved.get(&main_host) {
            ensure!(
                !v4.is_empty() || !v6.is_empty(),
                "Flux DNS resolved no usable address for {main_host}"
            );
            obj.insert("resolved_v4".into(), json!(v4));
            obj.insert("resolved_v6".into(), json!(v6));
        }
    } else {
        let mut usable = 0;
        for item in &mut channels {
            let ch = item
                .as_object_mut()
                .context("Flux channel must be an object")?;
            if ch.get("enabled").and_then(Value::as_bool) == Some(false) {
                continue;
            }
            let host = channel_effective_host(ch, &main_host);
            let raw_family = ch
                .get("ip-version")
                .or_else(|| ch.get("ip_version"))
                .or_else(|| ch.get("ipVersion"))
                .and_then(Value::as_str)
                .unwrap_or("auto")
                .trim()
                .to_ascii_lowercase()
                .replace(['-', '_'], "");
            let family = match raw_family.as_str() {
                "ipv4" | "v4" | "4" => "ipv4",
                "ipv6" | "v6" | "6" => "ipv6",
                _ => "auto",
            };
            if let Ok(ip) = host.parse::<IpAddr>()
                && ((ip.is_ipv4() && family == "ipv6") || (ip.is_ipv6() && family == "ipv4"))
            {
                ch.insert("enabled".into(), json!(false));
                continue;
            }
            if let Some((v4, v6)) = resolved.get(&host) {
                if (v4.is_empty() || family == "ipv6") && (v6.is_empty() || family == "ipv4") {
                    ch.insert("enabled".into(), json!(false));
                    continue;
                }
                ch.insert("resolved_v4".into(), json!(v4));
                ch.insert("resolved_v6".into(), json!(v6));
            }
            usable += 1;
        }
        ensure!(usable > 0, "Flux has no channel with a usable address");
        if let Some(flux) = obj.get_mut("flux").and_then(Value::as_object_mut) {
            flux.remove("channels");
        }
        obj.insert("channels".into(), json!(channels));
    }
    Ok(node)
}

pub async fn prepare_node_for_connect(
    mut node: Value,
    dns_url: &str,
    user_agent: Option<&str>,
) -> Result<Value> {
    let protocol = node
        .get("type")
        .or_else(|| node.get("protocol"))
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_ascii_lowercase();
    if protocol == "direct" || protocol == "block" {
        return Ok(node);
    }
    if protocol == "flux" {
        return resolve_flux_node(dns_url, node, user_agent).await;
    }
    if !dns_url.trim().is_empty() {
        let host = node
            .get("host")
            .or_else(|| node.get("server"))
            .and_then(Value::as_str)
            .map(str::trim)
            .unwrap_or_default()
            .to_string();
        if !host.is_empty() && host.trim_matches(['[', ']']).parse::<IpAddr>().is_err() {
            if let Some(obj) = node.as_object_mut() {
                let has_explicit_sni = ["sni", "servername", "server_name", "server-name"]
                    .iter()
                    .any(|k| {
                        obj.get(*k)
                            .and_then(Value::as_str)
                            .is_some_and(|v| !v.trim().is_empty())
                    });
                if !has_explicit_sni {
                    obj.insert("servername".to_string(), Value::String(host.clone()));
                }
                let ip = resolve_node_host(dns_url, &host, user_agent).await?;
                obj.insert("host".to_string(), Value::String(ip.clone()));
                obj.insert("server".to_string(), Value::String(ip));
            }
        }
    }
    Ok(node)
}
