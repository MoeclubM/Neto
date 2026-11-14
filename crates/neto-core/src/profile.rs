use crate::config_compat::node_to_proxy_config;
use crate::doh::SHARED_HTTP_CLIENT;
use anyhow::{Context, Result, bail, ensure};
use reqwest::Url;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::{BTreeMap, BTreeSet};

const SUBSCRIPTION_NODE_TYPES: &str =
    "anytls,hysteria,trojan,vless,vmess,mieru,sudoku,flux,naive,shadowsocks,tuic,http,socks5,direct,block";

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct SubscriptionUserInfo {
    pub upload_bytes: u64,
    pub download_bytes: u64,
    pub total_bytes: u64,
    pub expire_unix: Option<u64>,
}

impl SubscriptionUserInfo {
    pub fn used_bytes(&self) -> u64 {
        self.upload_bytes.saturating_add(self.download_bytes)
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct NodeSummary {
    pub index: usize,
    pub name: String,
    pub protocol: String,
    pub protocol_label: String,
    pub server: String,
    pub port: u16,
    pub udp: bool,
    pub supported: bool,
    #[serde(default)]
    pub tags: Vec<String>,
    #[serde(default)]
    pub latency_ms: Option<u64>,
    #[serde(default)]
    pub last_error: Option<String>,
    pub raw: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProxyGroupSummary {
    pub name: String,
    pub group_type: String,
    pub proxies: Vec<String>,
    #[serde(default)]
    pub selected: Option<String>,
    #[serde(default)]
    pub now: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProfileRecord {
    pub id: String,
    pub name: String,
    pub kind: String,
    #[serde(default)]
    pub url: String,
    #[serde(default = "default_profile_flag")]
    pub flag: String,
    pub updated_at_unix: u64,
    #[serde(default)]
    pub auto_update_mins: u32,
    #[serde(default)]
    pub user_info: Option<SubscriptionUserInfo>,
    #[serde(default)]
    pub nodes: Vec<NodeSummary>,
    #[serde(default)]
    pub proxy_groups: Vec<ProxyGroupSummary>,
    #[serde(default)]
    pub route_config_yaml: Option<String>,
    #[serde(default)]
    pub raw_yaml: Option<String>,
    #[serde(default)]
    pub rule_count: usize,
    #[serde(default)]
    pub rules_preview: Vec<String>,
}

fn default_profile_flag() -> String {
    "aerion".to_string()
}

pub struct ParsedProfilePayload {
    pub user_info: Option<SubscriptionUserInfo>,
    pub nodes: Vec<NodeSummary>,
    pub proxy_groups: Vec<ProxyGroupSummary>,
    pub route_config_yaml: Option<String>,
    pub raw_yaml: Option<String>,
    pub rule_count: usize,
    pub rules_preview: Vec<String>,
}

pub fn parse_subscription_userinfo(header: &str) -> Option<SubscriptionUserInfo> {
    let mut info = SubscriptionUserInfo::default();
    let mut matched = false;
    for part in header.split(';') {
        let Some((k, v)) = part.trim().split_once('=') else {
            continue;
        };
        let key = k.trim().to_ascii_lowercase();
        let val = v.trim();
        match key.as_str() {
            "upload" => {
                if let Ok(n) = val.parse::<u64>() {
                    info.upload_bytes = n;
                    matched = true;
                }
            }
            "download" => {
                if let Ok(n) = val.parse::<u64>() {
                    info.download_bytes = n;
                    matched = true;
                }
            }
            "total" => {
                if let Ok(n) = val.parse::<u64>() {
                    info.total_bytes = n;
                    matched = true;
                }
            }
            "expire" => {
                if let Ok(n) = val.parse::<u64>()
                    && n > 0
                {
                    info.expire_unix = Some(n);
                    matched = true;
                }
            }
            _ => {}
        }
    }
    matched.then_some(info)
}

pub fn protocol_display_label(proto: &str) -> String {
    match proto.to_ascii_lowercase().as_str() {
        "anytls" => "AnyTLS".to_string(),
        "flux" => "Flux".to_string(),
        "sudoku" => "Sudoku".to_string(),
        "mieru" => "Mieru".to_string(),
        "vless" => "VLESS".to_string(),
        "vmess" => "VMess".to_string(),
        "trojan" => "Trojan".to_string(),
        "hysteria" | "hysteria2" => "Hysteria2".to_string(),
        "tuic" => "TUIC".to_string(),
        "ss" | "shadowsocks" => "Shadowsocks".to_string(),
        "naive" => "Naive".to_string(),
        "http" | "https" | "http-proxy" | "https-proxy" => "HTTP".to_string(),
        "socks" | "socks5" | "socks5h" => "SOCKS5".to_string(),
        "direct" => "Direct".to_string(),
        "block" => "Block".to_string(),
        other => other.to_ascii_uppercase(),
    }
}

fn strip_ipv6_brackets(raw: &str) -> String {
    let trimmed = raw.trim();
    if trimmed.len() > 2 && trimmed.starts_with('[') && trimmed.ends_with(']') {
        let inner = &trimmed[1..trimmed.len() - 1];
        if inner.contains(':') {
            return inner.to_string();
        }
    }
    trimmed.to_string()
}

fn is_ip_literal(raw: &str) -> bool {
    strip_ipv6_brackets(raw).parse::<std::net::IpAddr>().is_ok()
}

pub fn normalize_raw_node(index: usize, mut raw: Value) -> Result<NodeSummary> {
    let obj = raw
        .as_object_mut()
        .context("proxy node entry must be a mapping/object")?;
    let raw_server = obj
        .get("host")
        .and_then(Value::as_str)
        .filter(|s| !s.trim().is_empty())
        .or_else(|| obj.get("server").and_then(Value::as_str))
        .map(strip_ipv6_brackets)
        .unwrap_or_default();
    if !raw_server.is_empty() {
        obj.insert("host".to_string(), Value::String(raw_server.clone()));
        obj.insert("server".to_string(), Value::String(raw_server.clone()));
    }
    if !obj.get("insecure").and_then(Value::as_bool).unwrap_or(false) {
        for key in ["skip-cert-verify", "skip_cert_verify", "allow_insecure"] {
            if let Some(Value::Bool(skip)) = obj.get(key) {
                obj.insert("insecure".to_string(), Value::Bool(*skip));
                break;
            }
        }
    }
    let current_sni = obj
        .get("sni")
        .and_then(Value::as_str)
        .map(strip_ipv6_brackets)
        .unwrap_or_default();
    if current_sni.is_empty() || is_ip_literal(&current_sni) {
        let fallback_sni = ["server_name", "servername", "server-name"]
            .into_iter()
            .find_map(|k| {
                obj.get(k)
                    .and_then(Value::as_str)
                    .map(str::trim)
                    .filter(|s| !s.is_empty() && !is_ip_literal(s))
                    .map(ToString::to_string)
            })
            .or_else(|| {
                let tls = obj.get("tls")?.as_object()?;
                ["server_name", "servername", "server-name"]
                    .into_iter()
                    .find_map(|k| {
                        tls.get(k)
                            .and_then(Value::as_str)
                            .map(str::trim)
                            .filter(|s| !s.is_empty() && !is_ip_literal(s))
                            .map(ToString::to_string)
                    })
            });
        if let Some(sni) = fallback_sni {
            obj.insert("sni".to_string(), Value::String(sni));
        } else if !current_sni.is_empty() {
            obj.remove("sni");
        }
    }
    let server = raw_server;
    let port = obj
        .get("port")
        .and_then(|v| {
            v.as_u64()
                .or_else(|| v.as_str().and_then(|s| s.parse::<u64>().ok()))
        })
        .unwrap_or(0) as u16;
    let protocol = obj
        .get("type")
        .or_else(|| obj.get("protocol"))
        .and_then(Value::as_str)
        .unwrap_or("unknown")
        .trim()
        .to_ascii_lowercase();
    let name = obj
        .get("name")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(ToString::to_string)
        .unwrap_or_else(|| format!("Node {}", index + 1));
    let udp = obj.get("udp").and_then(Value::as_bool).unwrap_or(true);
    let mut tags = Vec::new();
    match obj.get("tags") {
        Some(Value::Array(arr)) => {
            for item in arr {
                if let Some(s) = item.as_str().map(str::trim).filter(|s| !s.is_empty())
                    && !tags.iter().any(|t| t == s)
                {
                    tags.push(s.to_string());
                }
            }
        }
        Some(Value::String(raw_tags)) => {
            for part in raw_tags.split([',', '|']) {
                let s = part.trim();
                if !s.is_empty() && !tags.iter().any(|t| t == s) {
                    tags.push(s.to_string());
                }
            }
        }
        _ => {}
    }
    for key in ["tag", "label", "group"] {
        if let Some(s) = obj.get(key).and_then(Value::as_str).map(str::trim).filter(|s| !s.is_empty())
            && !tags.iter().any(|t| t == s)
        {
            tags.push(s.to_string());
        }
    }
    let dummy_listen = "127.0.0.1:1080".parse().expect("valid loopback addr");
    let supported = node_to_proxy_config(&raw, dummy_listen).is_ok();
    Ok(NodeSummary {
        index,
        name,
        protocol_label: protocol_display_label(&protocol),
        protocol,
        server,
        port,
        udp,
        supported,
        tags,
        latency_ms: None,
        last_error: None,
        raw,
    })
}

pub async fn fetch_and_parse_subscription(
    subscribe_url: &str,
    flag: &str,
    user_agent: Option<&str>,
    authorization: Option<&str>,
) -> Result<ParsedProfilePayload> {
    let mut parsed_url = Url::parse(subscribe_url.trim()).context("invalid subscription URL")?;
    if flag.eq_ignore_ascii_case("aerion") {
        let kept: Vec<(String, String)> = parsed_url
            .query_pairs()
            .filter(|(k, _)| k != "flag" && k != "types")
            .map(|(k, v)| (k.into_owned(), v.into_owned()))
            .collect();
        {
            let mut qp = parsed_url.query_pairs_mut();
            qp.clear();
            for (k, v) in kept {
                qp.append_pair(&k, &v);
            }
            qp.append_pair("types", SUBSCRIPTION_NODE_TYPES);
            qp.append_pair("flag", "aerion");
        }
    }
    let ua = user_agent
        .map(str::trim)
        .filter(|v| !v.is_empty())
        .unwrap_or("Aerion");
    let mut req = SHARED_HTTP_CLIENT
        .get(parsed_url.clone())
        .header("User-Agent", ua)
        .header(
            "Accept",
            "text/yaml, application/yaml, text/plain;q=0.9, */*;q=0.8",
        );
    if let Some(auth) = authorization.map(str::trim).filter(|v| !v.is_empty()) {
        req = req.header("Authorization", auth);
    }
    let resp = req.send().await.context("request subscription URL")?;
    let status = resp.status();
    let user_info = resp
        .headers()
        .get("subscription-userinfo")
        .and_then(|v| v.to_str().ok())
        .and_then(parse_subscription_userinfo);
    let final_url = resp.url().clone();
    let text = resp.text().await.context("read subscription response")?;
    if !status.is_success() {
        bail!("subscription HTTP {}: {}", status.as_u16(), text.trim());
    }
    let mut payload = parse_yaml_profile_with_base(&text, Some(&final_url)).await?;
    payload.user_info = user_info;
    Ok(payload)
}

pub async fn parse_yaml_profile(text: &str) -> Result<ParsedProfilePayload> {
    parse_yaml_profile_with_base(text, None).await
}

async fn parse_yaml_profile_with_base(
    text: &str,
    base_url: Option<&Url>,
) -> Result<ParsedProfilePayload> {
    let yaml_val: serde_yaml::Value =
        serde_yaml::from_str(text.trim()).context("parse profile YAML")?;
    let serde_yaml::Value::Mapping(mut root) = yaml_val else {
        bail!("profile YAML root must be a mapping");
    };

    let mut stripped_providers = BTreeSet::new();
    let mut rewritten = false;
    let providers_key = if root.contains_key(serde_yaml::Value::String("rule-providers".into())) {
        Some("rule-providers")
    } else if root.contains_key(serde_yaml::Value::String("rule_providers".into())) {
        Some("rule_providers")
    } else {
        None
    };

    if let Some(pk) = providers_key
        && let Some(serde_yaml::Value::Mapping(providers)) =
            root.get(serde_yaml::Value::String(pk.into())).cloned()
    {
        let mut expanded = serde_yaml::Mapping::new();
        for (raw_name, raw_provider) in providers {
            let Some(name) = yaml_str(&raw_name) else {
                continue;
            };
            let serde_yaml::Value::Mapping(provider) = raw_provider else {
                continue;
            };
            let ptype = yaml_field_str(&provider, "type")
                .unwrap_or_default()
                .to_ascii_lowercase();
            let behavior = yaml_field_str(&provider, "behavior").unwrap_or_else(|| "domain".into());
            let format = yaml_field_str(&provider, "format")
                .unwrap_or_default()
                .to_ascii_lowercase();
            let raw_url = yaml_field_str(&provider, "url").unwrap_or_default();
            let is_mrs = format == "mrs"
                || raw_url
                    .split('?')
                    .next()
                    .unwrap_or_default()
                    .to_ascii_lowercase()
                    .ends_with(".mrs");
            match ptype.as_str() {
                "inline" => {
                    expanded.insert(
                        serde_yaml::Value::String(name),
                        serde_yaml::Value::Mapping(provider),
                    );
                }
                "http" => {
                    if is_mrs || base_url.is_none() {
                        stripped_providers.insert(name);
                        rewritten = true;
                    } else if let Some(base) = base_url
                        && let Ok(lines) =
                            fetch_http_rule_provider(&name, &format, &raw_url, base).await
                    {
                        let mut inline_map = serde_yaml::Mapping::new();
                        inline_map.insert("type".into(), "inline".into());
                        inline_map.insert("behavior".into(), behavior.into());
                        inline_map.insert(
                            "payload".into(),
                            serde_yaml::Value::Sequence(
                                lines.into_iter().map(serde_yaml::Value::String).collect(),
                            ),
                        );
                        expanded.insert(
                            serde_yaml::Value::String(name),
                            serde_yaml::Value::Mapping(inline_map),
                        );
                        rewritten = true;
                    } else {
                        stripped_providers.insert(name);
                        rewritten = true;
                    }
                }
                _ => {
                    stripped_providers.insert(name);
                    rewritten = true;
                }
            }
        }
        if expanded.is_empty() {
            root.remove(serde_yaml::Value::String(pk.into()));
        } else {
            root.insert(
                serde_yaml::Value::String(pk.into()),
                serde_yaml::Value::Mapping(expanded),
            );
        }
    }

    let mut rules: Vec<String> = root
        .get(serde_yaml::Value::String("rules".into()))
        .and_then(serde_yaml::Value::as_sequence)
        .map(|seq| seq.iter().filter_map(yaml_str).collect())
        .unwrap_or_default();

    if !stripped_providers.is_empty() && !rules.is_empty() {
        rules = rewrite_stripped_rule_sets(&rules, &stripped_providers);
        root.insert(
            serde_yaml::Value::String("rules".into()),
            serde_yaml::Value::Sequence(
                rules
                    .iter()
                    .cloned()
                    .map(serde_yaml::Value::String)
                    .collect(),
            ),
        );
    }

    let mut nodes = Vec::new();
    if let Some(serde_yaml::Value::Sequence(proxies)) =
        root.get(serde_yaml::Value::String("proxies".into()))
    {
        for item in proxies {
            let json_val = yaml_to_json(item.clone())?;
            if let Ok(summary) = normalize_raw_node(nodes.len(), json_val) {
                nodes.push(summary);
            }
        }
    }

    let proxy_groups = extract_proxy_groups_from_mapping(&root, &rules, &nodes);

    ensure!(
        !nodes.is_empty() || !rules.is_empty(),
        "profile YAML contains neither proxies nor rules"
    );

    let route_config_yaml = if rules.is_empty() {
        None
    } else if rewritten {
        Some(serde_yaml::to_string(&serde_yaml::Value::Mapping(root))?)
    } else {
        Some(text.trim().to_string())
    };
    let rules_preview = rules.iter().take(20).cloned().collect();
    Ok(ParsedProfilePayload {
        user_info: None,
        nodes,
        proxy_groups,
        route_config_yaml,
        raw_yaml: Some(text.trim().to_string()),
        rule_count: rules.len(),
        rules_preview,
    })
}

pub fn build_profile_from_raw_nodes(
    raw_nodes: Vec<Value>,
    route_config_yaml: Option<String>,
    rules_preview: Vec<String>,
    rule_count: usize,
    user_info: Option<SubscriptionUserInfo>,
) -> ParsedProfilePayload {
    let mut nodes = Vec::with_capacity(raw_nodes.len());
    for item in raw_nodes {
        if let Ok(summary) = normalize_raw_node(nodes.len(), item) {
            nodes.push(summary);
        }
    }
    let proxy_groups = route_config_yaml
        .as_deref()
        .filter(|s| !s.trim().is_empty())
        .map(|yaml| extract_proxy_groups_from_yaml(yaml, &nodes))
        .unwrap_or_default();
    ParsedProfilePayload {
        user_info,
        nodes,
        proxy_groups,
        raw_yaml: route_config_yaml.clone(),
        route_config_yaml,
        rule_count,
        rules_preview,
    }
}

fn normalize_builtin_action(raw: &str) -> Option<&'static str> {
    let trimmed = raw.trim();
    if trimmed.eq_ignore_ascii_case("DIRECT") {
        Some("DIRECT")
    } else if trimmed.eq_ignore_ascii_case("REJECT") {
        Some("REJECT")
    } else if trimmed.eq_ignore_ascii_case("REJECT-DROP") {
        Some("REJECT-DROP")
    } else if trimmed.eq_ignore_ascii_case("PASS") {
        Some("PASS")
    } else {
        None
    }
}

fn extract_rule_action_name(rule: &str) -> Option<String> {
    let trimmed = rule.trim();
    if trimmed.is_empty() {
        return None;
    }
    let head = match trimmed.rsplit_once(',') {
        Some((before, last)) if last.trim().eq_ignore_ascii_case("no-resolve") => before.trim_end(),
        _ => trimmed,
    };
    let (_, action) = head.rsplit_once(',')?;
    let action = action.trim();
    (!action.is_empty()).then(|| action.to_string())
}

fn node_matches_group_filters(
    node_name: &str,
    filter: Option<&str>,
    exclude_filter: Option<&str>,
) -> bool {
    if let Some(pat) = filter.map(str::trim).filter(|s| !s.is_empty()) {
        let matched = pat
            .split('|')
            .map(|p| p.trim().trim_start_matches("(?i)"))
            .filter(|p| !p.is_empty())
            .any(|p| node_name.to_lowercase().contains(&p.to_lowercase()));
        if !matched {
            return false;
        }
    }
    if let Some(pat) = exclude_filter.map(str::trim).filter(|s| !s.is_empty()) {
        let excluded = pat
            .split('|')
            .map(|p| p.trim().trim_start_matches("(?i)"))
            .filter(|p| !p.is_empty())
            .any(|p| node_name.to_lowercase().contains(&p.to_lowercase()));
        if excluded {
            return false;
        }
    }
    true
}

fn insert_before_trailing_builtins(proxies: &mut Vec<String>, extra: Vec<String>) {
    if extra.is_empty() {
        return;
    }
    let mut insert_pos = proxies.len();
    while insert_pos > 0 && normalize_builtin_action(&proxies[insert_pos - 1]).is_some() {
        insert_pos -= 1;
    }
    for name in extra {
        if !proxies.iter().any(|p| p == &name) {
            proxies.insert(insert_pos, name);
            insert_pos += 1;
        }
    }
}

pub fn extract_proxy_groups_from_yaml(
    yaml_text: &str,
    nodes: &[NodeSummary],
) -> Vec<ProxyGroupSummary> {
    let Ok(serde_yaml::Value::Mapping(root)) = serde_yaml::from_str::<serde_yaml::Value>(yaml_text.trim()) else {
        return Vec::new();
    };
    let rules: Vec<String> = root
        .get(serde_yaml::Value::String("rules".into()))
        .and_then(serde_yaml::Value::as_sequence)
        .map(|seq| seq.iter().filter_map(yaml_str).collect())
        .unwrap_or_default();
    extract_proxy_groups_from_mapping(&root, &rules, nodes)
}

fn extract_proxy_groups_from_mapping(
    root: &serde_yaml::Mapping,
    rules: &[String],
    nodes: &[NodeSummary],
) -> Vec<ProxyGroupSummary> {
    let yaml_proxy_names: BTreeSet<String> = root
        .get(serde_yaml::Value::String("proxies".into()))
        .and_then(serde_yaml::Value::as_sequence)
        .map(|seq| {
            seq.iter()
                .filter_map(|item| item.as_mapping().and_then(|m| yaml_field_str(m, "name")))
                .collect()
        })
        .unwrap_or_default();
    let node_names: BTreeSet<&str> = nodes.iter().map(|n| n.name.as_str()).collect();

    struct RawGroupSpec {
        name: String,
        group_type: String,
        proxies: Vec<String>,
        include_all: bool,
        filter: Option<String>,
        exclude_filter: Option<String>,
    }

    let mut raw_specs = Vec::new();
    if let Some(serde_yaml::Value::Sequence(groups)) = root
        .get(serde_yaml::Value::String("proxy-groups".into()))
        .or_else(|| root.get(serde_yaml::Value::String("proxy_groups".into())))
    {
        for g in groups {
            if let serde_yaml::Value::Mapping(gm) = g {
                let Some(name) = yaml_field_str(gm, "name") else {
                    continue;
                };
                let group_type =
                    yaml_field_str(gm, "type").unwrap_or_else(|| "select".to_string());
                let proxies = gm
                    .get(serde_yaml::Value::String("proxies".into()))
                    .and_then(serde_yaml::Value::as_sequence)
                    .map(|seq| {
                        seq.iter()
                            .filter_map(yaml_str)
                            .map(|s| s.trim().to_string())
                            .filter(|s| !s.is_empty())
                            .collect::<Vec<_>>()
                    })
                    .unwrap_or_default();
                let include_all = gm
                    .get(serde_yaml::Value::String("include-all".into()))
                    .or_else(|| gm.get(serde_yaml::Value::String("include-all-proxies".into())))
                    .and_then(serde_yaml::Value::as_bool)
                    .unwrap_or(false);
                let filter = yaml_field_str(gm, "filter");
                let exclude_filter = yaml_field_str(gm, "exclude-filter");
                raw_specs.push(RawGroupSpec {
                    name,
                    group_type,
                    proxies,
                    include_all,
                    filter,
                    exclude_filter,
                });
            }
        }
    }

    let mut proxy_groups = Vec::new();
    if !raw_specs.is_empty() {
        let group_names: BTreeSet<String> = raw_specs.iter().map(|g| g.name.clone()).collect();
        for spec in raw_specs {
            let had_all_yaml_proxies = !yaml_proxy_names.is_empty()
                && yaml_proxy_names
                    .iter()
                    .all(|yp| spec.proxies.iter().any(|p| p == yp));
            let mut cleaned = Vec::new();
            for entry in &spec.proxies {
                if let Some(builtin) = normalize_builtin_action(entry) {
                    let b = builtin.to_string();
                    if !cleaned.contains(&b) {
                        cleaned.push(b);
                    }
                } else if (entry != &spec.name && group_names.contains(entry))
                    || node_names.contains(entry.as_str())
                {
                    if !cleaned.contains(entry) {
                        cleaned.push(entry.clone());
                    }
                }
            }
            let had_no_valid_nodes = !cleaned.iter().any(|p| node_names.contains(p.as_str()));
            let wants_all_nodes = spec.include_all
                || had_all_yaml_proxies
                || (yaml_proxy_names.is_empty() && had_no_valid_nodes && cleaned.iter().all(|p| normalize_builtin_action(p).is_some()));
            if wants_all_nodes {
                let extra: Vec<String> = nodes
                    .iter()
                    .filter(|n| {
                        node_matches_group_filters(
                            &n.name,
                            spec.filter.as_deref(),
                            spec.exclude_filter.as_deref(),
                        )
                    })
                    .map(|n| n.name.clone())
                    .collect();
                insert_before_trailing_builtins(&mut cleaned, extra);
            }
            if !cleaned.is_empty() {
                let selected = cleaned.first().cloned();
                proxy_groups.push(ProxyGroupSummary {
                    name: spec.name,
                    group_type: spec.group_type,
                    proxies: cleaned,
                    now: selected.clone(),
                    selected,
                });
            }
        }
        if !nodes.is_empty() && !proxy_groups.is_empty() {
            let covered_in_select: BTreeSet<String> = proxy_groups
                .iter()
                .filter(|g| g.group_type.eq_ignore_ascii_case("select"))
                .flat_map(|g| g.proxies.iter().cloned())
                .collect();
            let uncovered: Vec<String> = nodes
                .iter()
                .filter(|n| !covered_in_select.contains(&n.name))
                .map(|n| n.name.clone())
                .collect();
            if !uncovered.is_empty() {
                let primary_idx = find_primary_select_group_index(&proxy_groups, Some(rules));
                if let Some(idx) = primary_idx {
                    insert_before_trailing_builtins(&mut proxy_groups[idx].proxies, uncovered);
                    if proxy_groups[idx].selected.is_none() {
                        proxy_groups[idx].selected = proxy_groups[idx].proxies.first().cloned();
                        proxy_groups[idx].now = proxy_groups[idx].selected.clone();
                    }
                }
            }
        }
    } else if !rules.is_empty() && !nodes.is_empty() {
        let mut rule_group_names = Vec::new();
        let mut match_group_name = None;
        for rule in rules {
            if let Some(action) = extract_rule_action_name(rule) {
                if normalize_builtin_action(&action).is_some()
                    || node_names.contains(action.as_str())
                {
                    continue;
                }
                if rule.trim_start().to_ascii_uppercase().starts_with("MATCH,") {
                    match_group_name = Some(action.clone());
                }
                if !rule_group_names.contains(&action) {
                    rule_group_names.push(action);
                }
            }
        }
        let primary_name = match_group_name
            .or_else(|| rule_group_names.first().cloned())
            .unwrap_or_else(|| "代理选择".to_string());
        let all_node_names: Vec<String> = nodes.iter().map(|n| n.name.clone()).collect();
        let auto_name = if primary_name == "自动选择" {
            "自动测速".to_string()
        } else {
            "自动选择".to_string()
        };
        let mut primary_proxies = Vec::with_capacity(all_node_names.len() + 2);
        primary_proxies.push(auto_name.clone());
        primary_proxies.extend(all_node_names.iter().cloned());
        primary_proxies.push("DIRECT".to_string());
        proxy_groups.push(ProxyGroupSummary {
            name: primary_name.clone(),
            group_type: "select".to_string(),
            selected: primary_proxies.first().cloned(),
            now: primary_proxies.first().cloned(),
            proxies: primary_proxies,
        });
        proxy_groups.push(ProxyGroupSummary {
            name: auto_name.clone(),
            group_type: "url-test".to_string(),
            selected: all_node_names.first().cloned(),
            now: all_node_names.first().cloned(),
            proxies: all_node_names.clone(),
        });
        for extra_name in rule_group_names {
            if extra_name == primary_name || extra_name == auto_name {
                continue;
            }
            let mut proxies = Vec::with_capacity(all_node_names.len() + 3);
            proxies.push(primary_name.clone());
            proxies.push(auto_name.clone());
            proxies.extend(all_node_names.iter().cloned());
            proxies.push("DIRECT".to_string());
            proxy_groups.push(ProxyGroupSummary {
                name: extra_name,
                group_type: "select".to_string(),
                selected: Some(primary_name.clone()),
                now: Some(primary_name.clone()),
                proxies,
            });
        }
    }

    recompute_proxy_groups(nodes, &mut proxy_groups);
    proxy_groups
}

pub fn reconcile_proxy_groups_with_nodes(
    existing_groups: &[ProxyGroupSummary],
    route_config_yaml: Option<&str>,
    nodes: &[NodeSummary],
) -> Vec<ProxyGroupSummary> {
    if let Some(yaml) = route_config_yaml.filter(|s| !s.trim().is_empty()) {
        let mut extracted = extract_proxy_groups_from_yaml(yaml, nodes);
        if !extracted.is_empty() {
            merge_preserved_groups(existing_groups, &mut extracted, nodes);
            return extracted;
        }
    }
    if existing_groups.is_empty() {
        return Vec::new();
    }
    let node_names: BTreeSet<&str> = nodes.iter().map(|n| n.name.as_str()).collect();
    let group_names: BTreeSet<&str> = existing_groups.iter().map(|g| g.name.as_str()).collect();
    let prev_node_set: BTreeSet<&str> = existing_groups
        .iter()
        .flat_map(|g| g.proxies.iter().map(String::as_str))
        .filter(|p| normalize_builtin_action(p).is_none() && !group_names.contains(p))
        .collect();
    let mut updated = Vec::with_capacity(existing_groups.len());
    for g in existing_groups {
        let had_all_prev_nodes =
            !prev_node_set.is_empty() && prev_node_set.iter().all(|pn| g.proxies.iter().any(|p| p == pn));
        let mut cleaned: Vec<String> = g
            .proxies
            .iter()
            .filter(|p| {
                normalize_builtin_action(p).is_some()
                    || (p.as_str() != g.name && group_names.contains(p.as_str()))
                    || node_names.contains(p.as_str())
            })
            .cloned()
            .collect();
        if had_all_prev_nodes {
            let extra: Vec<String> = nodes.iter().map(|n| n.name.clone()).collect();
            insert_before_trailing_builtins(&mut cleaned, extra);
        }
        if !cleaned.is_empty() {
            updated.push(ProxyGroupSummary {
                name: g.name.clone(),
                group_type: g.group_type.clone(),
                proxies: cleaned,
                selected: g.selected.clone(),
                now: g.now.clone(),
            });
        }
    }
    merge_preserved_groups(existing_groups, &mut updated, nodes);
    updated
}

pub fn find_primary_select_group_index(
    groups: &[ProxyGroupSummary],
    rules: Option<&[String]>,
) -> Option<usize> {
    if let Some(rules) = rules {
        for rule in rules.iter().rev() {
            if rule.trim_start().to_ascii_uppercase().starts_with("MATCH,")
                && let Some(action) = extract_rule_action_name(rule)
                && let Some(idx) = groups.iter().position(|g| {
                    g.group_type.eq_ignore_ascii_case("select") && g.name == action
                })
            {
                return Some(idx);
            }
        }
    }
    groups
        .iter()
        .position(|g| {
            g.group_type.eq_ignore_ascii_case("select")
                && matches!(
                    g.name.as_str(),
                    "代理选择" | "节点选择" | "Proxy" | "PROXY" | "🚀 节点选择"
                )
        })
        .or_else(|| {
            groups
                .iter()
                .position(|g| g.group_type.eq_ignore_ascii_case("select"))
        })
}

pub fn primary_select_group_index_from_yaml(
    groups: &[ProxyGroupSummary],
    route_yaml: Option<&str>,
) -> Option<usize> {
    let rules: Option<Vec<String>> = route_yaml.and_then(|yaml| {
        let root = serde_yaml::from_str::<serde_yaml::Value>(yaml.trim()).ok()?;
        root.as_mapping()?
            .get(serde_yaml::Value::String("rules".into()))?
            .as_sequence()
            .map(|seq| seq.iter().filter_map(yaml_str).collect())
    });
    find_primary_select_group_index(groups, rules.as_deref())
}

pub fn resolve_group_leaf_target(
    group_name: &str,
    groups: &[ProxyGroupSummary],
) -> Option<String> {
    let mut cur = group_name.trim().to_string();
    let mut seen = BTreeSet::new();
    for _ in 0..16 {
        if let Some(builtin) = normalize_builtin_action(&cur) {
            return Some(builtin.to_string());
        }
        if !seen.insert(cur.clone()) {
            return None;
        }
        if let Some(grp) = groups.iter().find(|g| g.name == cur) {
            let next = grp
                .now
                .as_deref()
                .or(grp.selected.as_deref())
                .or_else(|| grp.proxies.first().map(String::as_str))?;
            cur = next.trim().to_string();
        } else {
            return Some(cur);
        }
    }
    Some(cur)
}

async fn fetch_http_rule_provider(
    _name: &str,
    format: &str,
    raw_url: &str,
    base_url: &Url,
) -> Result<Vec<String>> {
    let url = base_url.join(raw_url.trim())?;
    let resp = SHARED_HTTP_CLIENT
        .get(url)
        .header("User-Agent", "Aerion")
        .send()
        .await?;
    ensure!(resp.status().is_success(), "HTTP {}", resp.status().as_u16());
    let text = resp.text().await?;
    let fmt = if format.is_empty() { "yaml" } else { format };
    match fmt {
        "yaml" | "yml" => {
            let val: serde_yaml::Value = serde_yaml::from_str(&text)?;
            let seq = val
                .as_mapping()
                .and_then(|m| m.get(serde_yaml::Value::String("payload".into())))
                .and_then(serde_yaml::Value::as_sequence)
                .context("rule-provider YAML missing payload sequence")?;
            Ok(seq
                .iter()
                .filter_map(yaml_str)
                .map(|s| s.trim().to_string())
                .filter(|s| !s.is_empty())
                .collect())
        }
        "text" => Ok(text
            .lines()
            .map(|l| l.split('#').next().unwrap_or_default().trim().to_string())
            .filter(|l| !l.is_empty())
            .collect()),
        other => bail!("unsupported rule-provider format: {other}"),
    }
}

fn rewrite_stripped_rule_sets(rules: &[String], stripped: &BTreeSet<String>) -> Vec<String> {
    let mut has_cn_domain = rules.iter().any(|r| {
        let u = r.trim().to_ascii_uppercase();
        u.starts_with("DOMAIN-SUFFIX,CN,") || u.starts_with("GEOSITE,CN,")
    });
    let mut has_geoip_cn = rules
        .iter()
        .any(|r| r.trim().to_ascii_uppercase().starts_with("GEOIP,CN,"));
    let mut out = Vec::with_capacity(rules.len());
    for raw in rules {
        let trimmed = raw.trim();
        if trimmed.is_empty() {
            continue;
        }
        let parts: Vec<&str> = trimmed.split(',').map(str::trim).collect();
        if parts.len() >= 3 && parts[0].eq_ignore_ascii_case("RULE-SET") {
            let set_name = parts[1];
            let target = parts[2];
            if stripped.contains(set_name) {
                let lower = set_name.to_ascii_lowercase();
                if !has_cn_domain
                    && (lower == "cn-domain" || (lower.contains("cn") && lower.contains("domain")))
                {
                    out.push(format!("DOMAIN-SUFFIX,cn,{target}"));
                    has_cn_domain = true;
                } else if !has_geoip_cn
                    && (lower == "cn-ip" || (lower.contains("cn") && lower.contains("ip")))
                {
                    out.push(format!("GEOIP,CN,{target},no-resolve"));
                    has_geoip_cn = true;
                }
                continue;
            }
        }
        out.push(trimmed.to_string());
    }
    out
}

fn yaml_str(val: &serde_yaml::Value) -> Option<String> {
    match val {
        serde_yaml::Value::String(s) => Some(s.clone()),
        serde_yaml::Value::Number(n) => Some(n.to_string()),
        serde_yaml::Value::Bool(b) => Some(b.to_string()),
        _ => None,
    }
}

fn yaml_field_str(map: &serde_yaml::Mapping, key: &str) -> Option<String> {
    map.get(serde_yaml::Value::String(key.into()))
        .and_then(yaml_str)
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
}

fn yaml_to_json(val: serde_yaml::Value) -> Result<Value> {
    match val {
        serde_yaml::Value::Null => Ok(Value::Null),
        serde_yaml::Value::Bool(v) => Ok(Value::Bool(v)),
        serde_yaml::Value::Number(n) => {
            if let Some(i) = n.as_i64() {
                Ok(Value::Number(i.into()))
            } else if let Some(u) = n.as_u64() {
                Ok(Value::Number(u.into()))
            } else if let Some(f) = n.as_f64() {
                Ok(serde_json::Number::from_f64(f)
                    .map(Value::Number)
                    .unwrap_or(Value::Null))
            } else {
                Ok(Value::Null)
            }
        }
        serde_yaml::Value::String(s) => Ok(Value::String(s)),
        serde_yaml::Value::Sequence(seq) => seq
            .into_iter()
            .map(yaml_to_json)
            .collect::<Result<Vec<_>>>()
            .map(Value::Array),
        serde_yaml::Value::Mapping(map) => {
            let mut out = Map::new();
            for (k, v) in map {
                let Some(key) = yaml_str(&k) else {
                    continue;
                };
                out.insert(key, yaml_to_json(v)?);
            }
            Ok(Value::Object(out))
        }
        serde_yaml::Value::Tagged(tagged) => yaml_to_json(tagged.value),
    }
}

pub fn merge_preserved_latencies(
    previous: &[NodeSummary],
    next: &mut [NodeSummary],
) {
    let mut by_key: BTreeMap<(String, String, u16), Option<u64>> = BTreeMap::new();
    for node in previous {
        if node.latency_ms.is_some() {
            by_key.insert(
                (node.name.clone(), node.server.clone(), node.port),
                node.latency_ms,
            );
        }
    }
    for node in next {
        if let Some(lat) = by_key
            .get(&(node.name.clone(), node.server.clone(), node.port))
            .copied()
            .flatten()
        {
            node.latency_ms = Some(lat);
        }
    }
}

pub fn recompute_proxy_groups(nodes: &[NodeSummary], groups: &mut [ProxyGroupSummary]) {
    let node_map: BTreeMap<&str, &NodeSummary> =
        nodes.iter().map(|n| (n.name.as_str(), n)).collect();
    for group in groups.iter_mut() {
        let kind = group.group_type.trim().to_ascii_lowercase();
        match kind.as_str() {
            "url-test" | "load-balance" => {
                let fastest = group
                    .proxies
                    .iter()
                    .filter_map(|name| {
                        let n = node_map.get(name.as_str())?;
                        n.latency_ms.map(|ms| (ms, name.clone()))
                    })
                    .min_by_key(|(ms, _)| *ms)
                    .map(|(_, name)| name);
                let chosen = fastest
                    .or_else(|| {
                        group
                            .selected
                            .clone()
                            .filter(|s| group.proxies.contains(s))
                    })
                    .or_else(|| group.proxies.first().cloned());
                group.now = chosen.clone();
                group.selected = chosen;
            }
            "fallback" => {
                let any_tested = group.proxies.iter().any(|name| {
                    node_map
                        .get(name.as_str())
                        .is_some_and(|n| n.latency_ms.is_some() || n.last_error.is_some())
                });
                let healthy = if any_tested {
                    group
                        .proxies
                        .iter()
                        .find(|name| {
                            node_map
                                .get(name.as_str())
                                .is_some_and(|n| n.latency_ms.is_some() && n.last_error.is_none())
                        })
                        .cloned()
                } else {
                    None
                };
                let chosen = healthy.or_else(|| group.proxies.first().cloned());
                group.now = chosen.clone();
                group.selected = chosen;
            }
            _ => {
                let valid = group
                    .selected
                    .clone()
                    .filter(|s| group.proxies.contains(s))
                    .or_else(|| group.proxies.first().cloned());
                group.selected = valid.clone();
                group.now = valid;
            }
        }
    }
}

pub fn merge_preserved_groups(
    previous: &[ProxyGroupSummary],
    next: &mut [ProxyGroupSummary],
    nodes: &[NodeSummary],
) {
    let prev_selected: BTreeMap<&str, &str> = previous
        .iter()
        .filter_map(|g| g.selected.as_deref().map(|s| (g.name.as_str(), s)))
        .collect();
    for group in next.iter_mut() {
        if let Some(&saved) = prev_selected.get(group.name.as_str())
            && group.proxies.iter().any(|p| p == saved)
        {
            group.selected = Some(saved.to_string());
        }
    }
    recompute_proxy_groups(nodes, next);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_userinfo_header() {
        let info = parse_subscription_userinfo(
            "upload=1048576; download=2097152; total=10737418240; expire=1893456000",
        )
        .expect("parsed userinfo");
        assert_eq!(info.upload_bytes, 1_048_576);
        assert_eq!(info.download_bytes, 2_097_152);
        assert_eq!(info.used_bytes(), 3_145_728);
        assert_eq!(info.total_bytes, 10_737_418_240);
        assert_eq!(info.expire_unix, Some(1_893_456_000));
    }

    #[tokio::test]
    async fn parses_local_clash_yaml_with_groups_and_nodes() -> Result<()> {
        let yaml = r#"
proxies:
  - name: Tokyo-AnyTLS
    type: anytls
    server: jp.example.com
    port: 443
    password: uuid-secret
    tls: true
proxy-groups:
  - name: Proxy
    type: select
    proxies:
      - Tokyo-AnyTLS
      - DIRECT
rules:
  - DOMAIN-SUFFIX,google.com,Proxy
  - GEOIP,CN,DIRECT,no-resolve
  - MATCH,Proxy
"#;
        let parsed = parse_yaml_profile(yaml).await?;
        assert_eq!(parsed.nodes.len(), 1);
        assert_eq!(parsed.nodes[0].name, "Tokyo-AnyTLS");
        assert_eq!(parsed.nodes[0].protocol_label, "AnyTLS");
        assert!(parsed.nodes[0].supported);
        assert_eq!(parsed.proxy_groups.len(), 1);
        assert_eq!(parsed.rule_count, 3);
        Ok(())
    }

    #[test]
    fn builds_groups_from_raw_nodes_and_injects_extra_plugin_nodes() {
        let raw_nodes = vec![
            serde_json::json!({
                "name": "HK-AnyTLS",
                "type": "anytls",
                "server": "hk.example.com",
                "port": 443,
                "password": "p1",
                "tls": true
            }),
            serde_json::json!({
                "name": "JP-Flux",
                "type": "flux",
                "server": "jp.example.com",
                "port": 443,
                "password": "p2",
                "tls": true
            }),
        ];
        let yaml = r#"
proxies:
  - name: HK-AnyTLS
    type: anytls
    server: hk.example.com
    port: 443
    password: p1
proxy-groups:
  - name: 代理选择
    type: select
    proxies:
      - 自动选择
      - HK-AnyTLS
      - DIRECT
  - name: 自动选择
    type: url-test
    proxies:
      - HK-AnyTLS
rules:
  - GEOIP,CN,DIRECT,no-resolve
  - MATCH,代理选择
"#;
        let payload = build_profile_from_raw_nodes(
            raw_nodes,
            Some(yaml.to_string()),
            vec![],
            2,
            None,
        );
        assert_eq!(payload.nodes.len(), 2);
        assert_eq!(payload.proxy_groups.len(), 2);
        assert_eq!(
            payload.proxy_groups[0].proxies,
            vec!["自动选择", "HK-AnyTLS", "JP-Flux", "DIRECT"]
        );
        assert_eq!(
            payload.proxy_groups[1].proxies,
            vec!["HK-AnyTLS", "JP-Flux"]
        );
        assert_eq!(
            resolve_group_leaf_target("代理选择", &payload.proxy_groups).as_deref(),
            Some("HK-AnyTLS")
        );
    }
}
