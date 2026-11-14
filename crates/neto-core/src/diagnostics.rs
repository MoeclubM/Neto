use crate::http_proxy::connect_via_socks5;
use crate::mihomo_sanitize::sanitize_mihomo_route_yaml;
use crate::telemetry::now_ms;
use anyhow::{Context, Result, ensure};
use serde::{Deserialize, Serialize};
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Instant;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::time::{Duration, timeout};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ConnectivityTargetResult {
    pub name: String,
    pub latency_ms: Option<u64>,
    pub ok: bool,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct NetworkCheckResult {
    pub exit_ip: Option<String>,
    pub exit_country: Option<String>,
    pub exit_isp: Option<String>,
    pub targets: Vec<ConnectivityTargetResult>,
    pub checked_at_ms: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RuleItem {
    pub index: usize,
    pub rule_type: String,
    pub payload: String,
    pub target: String,
    pub raw: String,
}

fn build_tls_connector() -> Result<tokio_rustls::TlsConnector> {
    let mut roots = rustls::RootCertStore::empty();
    roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
    let cfg = rustls::ClientConfig::builder_with_provider(Arc::new(
        rustls::crypto::ring::default_provider(),
    ))
    .with_safe_default_protocol_versions()?
    .with_root_certificates(roots)
    .with_no_client_auth();
    Ok(tokio_rustls::TlsConnector::from(Arc::new(cfg)))
}

async fn open_tls_stream(
    socks_addr: Option<SocketAddr>,
    host: &str,
    port: u16,
    connector: &tokio_rustls::TlsConnector,
) -> Result<tokio_rustls::client::TlsStream<TcpStream>> {
    let tcp = match socks_addr {
        Some(socks) => connect_via_socks5(socks, host, port).await?,
        None => TcpStream::connect((host, port)).await?,
    };
    let _ = tcp.set_nodelay(true);
    let server_name = rustls::pki_types::ServerName::try_from(host.to_string())
        .with_context(|| format!("invalid TLS hostname {host}"))?;
    Ok(connector.connect(server_name, tcp).await?)
}

async fn probe_https_head(
    socks_addr: Option<SocketAddr>,
    host: &str,
    path: &str,
    connector: &tokio_rustls::TlsConnector,
) -> Option<u64> {
    timeout(Duration::from_millis(4500), async {
        let mut tls = open_tls_stream(socks_addr, host, 443, connector).await.ok()?;
        let req = format!(
            "HEAD {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: Neto/1.0\r\nConnection: close\r\n\r\n"
        );
        let started = Instant::now();
        tls.write_all(req.as_bytes()).await.ok()?;
        let mut buf = [0u8; 512];
        let mut total = Vec::with_capacity(512);
        loop {
            let n = tls.read(&mut buf).await.ok()?;
            if n == 0 {
                return None;
            }
            total.extend_from_slice(&buf[..n]);
            if total.starts_with(b"HTTP/") && total.windows(4).any(|w| w == b"\r\n\r\n") {
                return Some(started.elapsed().as_millis() as u64);
            }
            if total.len() > 4096 {
                return None;
            }
        }
    })
    .await
    .ok()
    .flatten()
}

async fn https_get_small_body(
    socks_addr: Option<SocketAddr>,
    host: &str,
    path: &str,
    connector: &tokio_rustls::TlsConnector,
) -> Result<String> {
    timeout(Duration::from_millis(4500), async {
        let mut tls = open_tls_stream(socks_addr, host, 443, connector).await?;
        let req = format!(
            "GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: curl/8.7.1\r\nAccept: application/json, text/plain, */*\r\nConnection: close\r\n\r\n"
        );
        tls.write_all(req.as_bytes()).await?;
        let mut raw = Vec::with_capacity(2048);
        let mut buf = [0u8; 1024];
        while raw.len() < 16 * 1024 {
            let n = tls.read(&mut buf).await?;
            if n == 0 {
                break;
            }
            raw.extend_from_slice(&buf[..n]);
        }
        let text = String::from_utf8_lossy(&raw);
        let Some((hdr, body)) = text.split_once("\r\n\r\n") else {
            anyhow::bail!("incomplete HTTP response");
        };
        ensure!(
            hdr.starts_with("HTTP/1.1 200") || hdr.starts_with("HTTP/1.0 200"),
            "non-200 HTTP status"
        );
        Ok(body.trim().to_string())
    })
    .await
    .context("HTTPS GET timed out")?
}

pub async fn run_network_check(socks_addr: Option<SocketAddr>) -> Result<NetworkCheckResult> {
    let connector = build_tls_connector()?;
    let (cf_trace, ip_sb, baidu_ms, cf_ms, google_ms, github_ms) = tokio::join!(
        https_get_small_body(
            socks_addr,
            "www.cloudflare.com",
            "/cdn-cgi/trace",
            &connector
        ),
        https_get_small_body(socks_addr, "api.ip.sb", "/geoip", &connector),
        probe_https_head(socks_addr, "www.baidu.com", "/", &connector),
        probe_https_head(socks_addr, "cp.cloudflare.com", "/generate_204", &connector),
        probe_https_head(socks_addr, "www.google.com", "/generate_204", &connector),
        probe_https_head(socks_addr, "github.com", "/", &connector),
    );

    let mut exit_ip: Option<String> = None;
    let mut exit_country: Option<String> = None;
    let mut exit_isp: Option<String> = None;

    if let Ok(body) = ip_sb {
        let json_start = body.find('{');
        let json_end = body.rfind('}');
        if let (Some(s), Some(e)) = (json_start, json_end)
            && s < e
            && let Ok(val) = serde_json::from_str::<serde_json::Value>(&body[s..=e])
        {
            exit_ip = val
                .get("ip")
                .and_then(|v| v.as_str())
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .map(ToString::to_string);
            exit_country = val
                .get("country_code")
                .or_else(|| val.get("country"))
                .and_then(|v| v.as_str())
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .map(ToString::to_string);
            exit_isp = val
                .get("isp")
                .or_else(|| val.get("organization"))
                .and_then(|v| v.as_str())
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .map(ToString::to_string);
        }
    }

    if let Ok(trace) = cf_trace {
        let mut colo = None;
        for line in trace.lines() {
            if let Some((k, v)) = line.split_once('=') {
                let v = v.trim();
                if v.is_empty() {
                    continue;
                }
                match k.trim() {
                    "ip" if exit_ip.is_none() => exit_ip = Some(v.to_string()),
                    "loc" if exit_country.is_none() => exit_country = Some(v.to_string()),
                    "colo" => colo = Some(v.to_string()),
                    _ => {}
                }
            }
        }
        if exit_isp.is_none()
            && let Some(c) = colo
        {
            exit_isp = Some(format!("POP {c}"));
        }
    }

    let targets = vec![
        ConnectivityTargetResult {
            name: "国内连通".to_string(),
            latency_ms: baidu_ms,
            ok: baidu_ms.is_some(),
        },
        ConnectivityTargetResult {
            name: "Cloudflare".to_string(),
            latency_ms: cf_ms,
            ok: cf_ms.is_some(),
        },
        ConnectivityTargetResult {
            name: "Google".to_string(),
            latency_ms: google_ms,
            ok: google_ms.is_some(),
        },
        ConnectivityTargetResult {
            name: "GitHub".to_string(),
            latency_ms: github_ms,
            ok: github_ms.is_some(),
        },
    ];

    Ok(NetworkCheckResult {
        exit_ip,
        exit_country,
        exit_isp,
        targets,
        checked_at_ms: now_ms(),
    })
}

pub fn parse_rule_items(yaml: &str, filter: &str, limit: usize) -> Result<(usize, Vec<RuleItem>)> {
    if yaml.trim().is_empty() {
        return Ok((0, Vec::new()));
    }
    let sanitized = sanitize_mihomo_route_yaml(yaml)?;
    let root: serde_yaml::Value = serde_yaml::from_str(&sanitized)?;
    let raw_rules: Vec<String> = root
        .as_mapping()
        .and_then(|m| m.get(serde_yaml::Value::String("rules".into())))
        .and_then(serde_yaml::Value::as_sequence)
        .map(|seq| {
            seq.iter()
                .filter_map(|v| v.as_str().map(|s| s.trim().to_string()))
                .filter(|s| !s.is_empty())
                .collect()
        })
        .unwrap_or_default();

    let total = raw_rules.len();
    let q = filter.trim().to_ascii_lowercase();
    let cap = if limit == 0 { 500 } else { limit };
    let mut items = Vec::new();
    for (index, raw) in raw_rules.into_iter().enumerate() {
        if !q.is_empty() && !raw.to_ascii_lowercase().contains(&q) {
            continue;
        }
        let parts: Vec<&str> = raw.split(',').map(str::trim).collect();
        let (rule_type, payload, target) = match parts.as_slice() {
            [kind, act] => ((*kind).to_string(), "*".to_string(), (*act).to_string()),
            [kind, pay, act, ..] => ((*kind).to_string(), (*pay).to_string(), (*act).to_string()),
            _ => ("RULE".to_string(), raw.clone(), String::new()),
        };
        items.push(RuleItem {
            index,
            rule_type,
            payload,
            target,
            raw,
        });
        if items.len() >= cap {
            break;
        }
    }
    Ok((total, items))
}

pub fn validate_custom_route_yaml(yaml: &str) -> Result<String> {
    let sanitized = sanitize_mihomo_route_yaml(yaml)?;
    let cfg: aerion::MihomoConfig =
        serde_yaml::from_str(&sanitized).context("解析分流规则配置失败")?;
    ensure!(!cfg.rules.is_empty(), "分流配置未包含任何规则");
    Ok(sanitized)
}

pub fn prepend_custom_route_rule(
    base_yaml: &str,
    custom_yaml: &str,
    rule_line: &str,
) -> Result<String> {
    let rule = rule_line.trim();
    ensure!(!rule.is_empty(), "规则内容不能为空");
    let seed = if !custom_yaml.trim().is_empty() {
        custom_yaml.trim()
    } else if !base_yaml.trim().is_empty() {
        base_yaml.trim()
    } else {
        "rules:\n  - MATCH,PROXY\n"
    };
    let sanitized = sanitize_mihomo_route_yaml(seed)?;
    let mut root: serde_yaml::Value = serde_yaml::from_str(&sanitized)?;
    let map = root
        .as_mapping_mut()
        .context("规则配置根节点必须是 YAML 映射")?;
    let rules_key = serde_yaml::Value::String("rules".into());
    let entry = map
        .entry(rules_key)
        .or_insert_with(|| serde_yaml::Value::Sequence(Vec::new()));
    let seq = entry.as_sequence_mut().context("rules 字段必须是列表")?;
    seq.insert(0, serde_yaml::Value::String(rule.to_string()));
    let encoded = serde_yaml::to_string(&root)?;
    validate_custom_route_yaml(&encoded)?;
    Ok(encoded)
}
