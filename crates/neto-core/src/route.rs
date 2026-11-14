use crate::config_compat::node_to_proxy_config;
use crate::connections::{RouteMatchFn, spawn_tracked_socks_ingress};
use crate::mihomo_sanitize::sanitize_mihomo_route_yaml;
use crate::protocol::{AerionProxyConfig, spawn_aerion_listener};
use crate::runtime::{core_event_json, on_event, on_log, proxy_tun_bypass_cidrs};
use crate::telemetry;
use aerion::{
    MihomoClientConfig, MihomoConfig, RouteDecision, RouteNetwork, RouteProxyConfig,
    RouteProxyState, RouteTable, protocol::ProxyTarget, run_route_proxy_with_state,
};
use anyhow::{Context, Result, bail, ensure};
use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::env;
use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex as StdMutex};
use tokio::net::TcpListener;
use tokio::task::JoinHandle;

const EMBEDDED_GEOIP_CN: &str = include_str!("../assets/route/geoip/cn.txt");
const ROUTE_ASSETS_DIR_ENV: &str = "NETO_ROUTE_ASSETS_DIR";

static BUNDLED_ASSETS_DIR: Lazy<Option<PathBuf>> = Lazy::new(|| {
    let dir = env::temp_dir().join("neto-route-assets");
    let cn_dir = dir.join("geoip");
    std::fs::create_dir_all(&cn_dir).ok()?;
    let cn_file = cn_dir.join("cn.txt");
    if !cn_file.is_file() {
        std::fs::write(&cn_file, EMBEDDED_GEOIP_CN).ok()?;
    }
    Some(dir)
});

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StartRouteRequest {
    pub config_yaml: String,
    #[serde(default)]
    pub geoip_dir: Option<String>,
    #[serde(default)]
    pub global_proxy: Option<String>,
    #[serde(default)]
    pub selected_proxy: Option<String>,
    #[serde(default)]
    pub selected_node: Option<serde_json::Value>,
    #[serde(default)]
    pub group_targets: BTreeMap<String, String>,
    #[serde(default)]
    pub resolved_nodes: BTreeMap<String, serde_json::Value>,
}

pub(crate) struct RouteRuntime {
    pub socks_addr: SocketAddr,
    pub router_task: JoinHandle<Result<()>>,
    pub outbound_tasks: Vec<JoinHandle<Result<()>>>,
    pub ingress_task: JoinHandle<()>,
    pub core: aerion::ProxyCore,
    pub bypass_cidrs: Vec<String>,
    pub rule_count: usize,
    pub outbound_tags: Vec<String>,
}

#[derive(Deserialize)]
struct TestRouteRequest {
    config_yaml: String,
    #[serde(default)]
    geoip_dir: Option<String>,
    #[serde(default)]
    global_proxy: Option<String>,
    #[serde(default)]
    selected_proxy: Option<String>,
    #[serde(default)]
    group_targets: BTreeMap<String, String>,
    target: String,
    #[serde(default)]
    network: Option<String>,
}

struct RouteSession {
    router_task: JoinHandle<Result<()>>,
    outbound_tasks: Vec<JoinHandle<Result<()>>>,
    ingress_task: JoinHandle<()>,
    _log_task: JoinHandle<()>,
    _event_task: JoinHandle<()>,
    _core: aerion::ProxyCore,
}

static NEXT_ROUTE_SESSION_ID: AtomicU64 = AtomicU64::new(1);
static ROUTE_SESSIONS: Lazy<StdMutex<HashMap<u64, RouteSession>>> =
    Lazy::new(|| StdMutex::new(HashMap::new()));

fn ephemeral_loopback() -> SocketAddr {
    SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 0)
}

fn parse_mihomo_config(text: &str) -> Result<MihomoConfig> {
    let sanitized = sanitize_mihomo_route_yaml(text)?;
    let mut config: MihomoConfig =
        serde_yaml::from_str(&sanitized).context("parse sanitized mihomo route config")?;
    config.source_dir = None;
    Ok(config)
}

fn route_proxy_tags(routes: &RouteTable) -> Vec<String> {
    let mut tags = BTreeSet::new();
    for rule in &routes.rules {
        if let RouteDecision::Proxy(tag) = &rule.action {
            tags.insert(tag.clone());
        }
    }
    if let RouteDecision::Proxy(tag) = &routes.default {
        tags.insert(tag.clone());
    }
    tags.into_iter().collect()
}

fn mihomo_client_config(config: MihomoClientConfig) -> AerionProxyConfig {
    match config {
        MihomoClientConfig::AnyTls(config) => AerionProxyConfig::AnyTls(config),
        MihomoClientConfig::Flux(config) => AerionProxyConfig::Flux(config),
        MihomoClientConfig::HttpProxy(config) => AerionProxyConfig::HttpProxy(config),
        MihomoClientConfig::Hysteria2(config) => AerionProxyConfig::Hysteria2(config),
        MihomoClientConfig::Trojan(config) => AerionProxyConfig::Trojan(config),
        MihomoClientConfig::Vless(config) => AerionProxyConfig::Vless(config),
        MihomoClientConfig::Vmess(config) => AerionProxyConfig::Vmess(config),
        MihomoClientConfig::Mieru(config) => AerionProxyConfig::Mieru(config),
        MihomoClientConfig::Sudoku(config) => AerionProxyConfig::Sudoku(config),
        MihomoClientConfig::Naive(config) => AerionProxyConfig::Naive(config),
        MihomoClientConfig::Route(config) => AerionProxyConfig::Route(config),
        MihomoClientConfig::Shadowsocks(config) => AerionProxyConfig::Shadowsocks(config),
        MihomoClientConfig::SocksProxy(config) => AerionProxyConfig::SocksProxy(config),
        MihomoClientConfig::Tuic(config) => AerionProxyConfig::Tuic(config),
    }
}

async fn bind_listener(label: &str, listen: SocketAddr) -> Result<TcpListener> {
    TcpListener::bind(listen)
        .await
        .with_context(|| format!("bind {label} on {listen}"))
}

async fn spawn_route_outbounds(
    config: &MihomoConfig,
    routes: &RouteTable,
    selected_proxy: Option<&str>,
    selected_node: Option<&serde_json::Value>,
    resolved_nodes: &BTreeMap<String, serde_json::Value>,
) -> Result<(
    BTreeMap<String, SocketAddr>,
    Vec<JoinHandle<Result<()>>>,
    Vec<String>,
)> {
    let mut upstreams = BTreeMap::new();
    let mut tasks = Vec::new();
    let mut bypass_cidrs = Vec::new();
    for tag in route_proxy_tags(routes) {
        let listener =
            bind_listener(&format!("route outbound {tag}"), ephemeral_loopback()).await?;
        let upstream = listener.local_addr()?;
        let proxy = if let Some(node) = resolved_nodes.get(&tag) {
            node_to_proxy_config(node, upstream)
                .with_context(|| format!("resolve prepared route outbound {tag}"))?
        } else if selected_proxy == Some(tag.as_str()) {
            let node = selected_node
                .with_context(|| format!("selected route outbound {tag} missing selected_node"))?;
            node_to_proxy_config(node, upstream)
                .with_context(|| format!("resolve selected route outbound {tag}"))?
        } else {
            let client = config
                .resolved_proxy_config(&tag, upstream)
                .with_context(|| format!("resolve mihomo route outbound {tag}"))?;
            mihomo_client_config(client)
        };
        for cidr in proxy_tun_bypass_cidrs(&proxy, !cfg!(target_os = "windows")) {
            if !bypass_cidrs.contains(&cidr) {
                bypass_cidrs.push(cidr);
            }
        }
        tasks.push(spawn_aerion_listener(listener, proxy, None));
        upstreams.insert(tag, upstream);
    }
    Ok((upstreams, tasks, bypass_cidrs))
}

fn apply_global_proxy(config: &mut MihomoConfig, proxy: &str) {
    config.rules = vec![format!("MATCH,{proxy}")];
    config.proxy_groups.clear();
    config.rule_providers.clear();
}

fn split_rule_action(rule: &str) -> Result<(&str, &str, &'static str)> {
    let trimmed = rule.trim();
    let (head, suffix) = match trimmed.rsplit_once(',') {
        Some((before, last)) if last.trim().eq_ignore_ascii_case("no-resolve") => {
            (before.trim_end(), ",no-resolve")
        }
        _ => (trimmed, ""),
    };
    let Some((prefix, action)) = head.rsplit_once(',') else {
        bail!("mihomo route rule has no action: {rule}");
    };
    Ok((prefix.trim_end(), action.trim(), suffix))
}

fn resolve_rule_target(
    raw_action: &str,
    group_targets: &BTreeMap<String, String>,
    config: &MihomoConfig,
    resolved_nodes: &BTreeMap<String, serde_json::Value>,
    selected_proxy: Option<&str>,
) -> String {
    let mut cur = raw_action.trim().to_string();
    let mut seen = BTreeSet::new();
    for _ in 0..16 {
        if cur.eq_ignore_ascii_case("DIRECT") {
            return "DIRECT".to_string();
        }
        if cur.eq_ignore_ascii_case("REJECT") || cur.eq_ignore_ascii_case("REJECT-DROP") {
            return "REJECT".to_string();
        }
        if cur.eq_ignore_ascii_case("PASS") {
            return "PASS".to_string();
        }
        if !seen.insert(cur.clone()) {
            break;
        }
        if let Some(next) = group_targets.get(&cur) {
            cur = next.trim().to_string();
            continue;
        }
        if selected_proxy.is_none()
            && group_targets.is_empty()
            && let Some(grp) = config.proxy_group(&cur)
            && let Some(first) = grp.proxies.first()
        {
            cur = first.trim().to_string();
            continue;
        }
        break;
    }
    if cur.eq_ignore_ascii_case("DIRECT") {
        return "DIRECT".to_string();
    }
    if cur.eq_ignore_ascii_case("REJECT") || cur.eq_ignore_ascii_case("REJECT-DROP") {
        return "REJECT".to_string();
    }
    if cur.eq_ignore_ascii_case("PASS") {
        return "PASS".to_string();
    }
    if resolved_nodes.contains_key(&cur)
        || (config.proxy_group(&cur).is_none() && config.proxy(&cur).is_some())
    {
        return cur;
    }
    if let Some(sel) = selected_proxy.map(str::trim).filter(|s| !s.is_empty()) {
        return sel.to_string();
    }
    cur
}

fn apply_resolved_routing(
    config: &mut MihomoConfig,
    group_targets: &BTreeMap<String, String>,
    resolved_nodes: &BTreeMap<String, serde_json::Value>,
    selected_proxy: Option<&str>,
) -> Result<()> {
    let snapshot = config.clone();
    for rule in &mut config.rules {
        let (prefix, action, suffix) = split_rule_action(rule)?;
        let target = resolve_rule_target(
            action,
            group_targets,
            &snapshot,
            resolved_nodes,
            selected_proxy,
        );
        if target == "PASS" {
            continue;
        }
        *rule = format!("{prefix},{target}{suffix}");
    }
    config.proxy_groups.clear();
    Ok(())
}

fn compile_route_config(
    config_yaml: &str,
    geoip_dir: Option<&str>,
    global_proxy: Option<&str>,
    selected_proxy: Option<&str>,
    group_targets: &BTreeMap<String, String>,
    resolved_nodes: &BTreeMap<String, serde_json::Value>,
) -> Result<(MihomoConfig, Option<PathBuf>, RouteTable)> {
    ensure!(!config_yaml.trim().is_empty(), "route config_yaml is empty");
    let mut config = parse_mihomo_config(config_yaml)?;
    let is_global = if let Some(proxy) = global_proxy.map(str::trim).filter(|v| !v.is_empty()) {
        apply_global_proxy(&mut config, proxy);
        true
    } else if selected_proxy.is_some()
        || !group_targets.is_empty()
        || !config.proxy_groups.is_empty()
    {
        apply_resolved_routing(&mut config, group_targets, resolved_nodes, selected_proxy)?;
        false
    } else {
        false
    };
    let assets_dir = route_assets_dir(geoip_dir);
    if assets_dir.is_some() && !is_global {
        ensure_geoip_baseline(&mut config);
    }
    ensure!(!config.rules.is_empty(), "mihomo route config has no rules");
    let routes = config
        .route_table_with_assets(assets_dir.as_deref())
        .context("compile mihomo route table")?;
    Ok((config, assets_dir, routes))
}

fn build_route_matcher(
    config: &MihomoConfig,
    assets_dir: Option<&std::path::Path>,
    routes: &RouteTable,
) -> RouteMatchFn {
    let mut indexed_cfg = config.clone();
    let mut rule_labels = Vec::with_capacity(config.rules.len());
    for (idx, rule) in indexed_cfg.rules.iter_mut().enumerate() {
        if let Ok((prefix, _, suffix)) = split_rule_action(rule) {
            rule_labels.push(prefix.to_string());
            *rule = format!("{prefix},__idx_{idx}{suffix}");
        } else {
            rule_labels.push(rule.clone());
        }
    }
    let indexed_table = indexed_cfg
        .route_table_with_assets(assets_dir)
        .ok()
        .map(Arc::new);
    let routes_arc = Arc::new(routes.clone());
    let labels_arc = Arc::new(rule_labels);
    Arc::new(move |target, network| {
        let outbound = match routes_arc.try_decide(target, network) {
            Ok(RouteDecision::Direct) => "DIRECT".to_string(),
            Ok(RouteDecision::Block) => "REJECT".to_string(),
            Ok(RouteDecision::Proxy(tag)) => tag,
            Err(_) => "DIRECT".to_string(),
        };
        let matched = indexed_table
            .as_ref()
            .and_then(|tbl| match tbl.try_decide(target, network).ok()? {
                RouteDecision::Proxy(tag) => tag
                    .strip_prefix("__idx_")
                    .and_then(|s| s.parse::<usize>().ok())
                    .and_then(|i| labels_arc.get(i).cloned()),
                _ => None,
            })
            .unwrap_or_else(|| "MATCH".to_string());
        (matched, outbound)
    })
}

fn ensure_geoip_baseline(config: &mut MihomoConfig) {
    if config.rules.iter().any(|rule| {
        rule.trim_start()
            .to_ascii_uppercase()
            .starts_with("GEOIP,CN,")
    }) {
        return;
    }
    let insert_at = config
        .rules
        .iter()
        .position(|rule| rule.trim_start().to_ascii_uppercase().starts_with("MATCH,"))
        .unwrap_or(config.rules.len());
    config
        .rules
        .insert(insert_at, "GEOIP,CN,DIRECT,no-resolve".to_string());
}

pub fn route_assets_dir(geoip_dir: Option<&str>) -> Option<PathBuf> {
    geoip_dir
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .or_else(|| {
            env::var(ROUTE_ASSETS_DIR_ENV)
                .ok()
                .map(|value| value.trim().to_string())
                .filter(|value| !value.is_empty())
                .map(PathBuf::from)
        })
        .or_else(|| BUNDLED_ASSETS_DIR.clone())
}

pub(crate) async fn start_route_runtime(request: StartRouteRequest) -> Result<RouteRuntime> {
    let (config, assets_dir, routes) = compile_route_config(
        &request.config_yaml,
        request.geoip_dir.as_deref(),
        request.global_proxy.as_deref(),
        request.selected_proxy.as_deref(),
        &request.group_targets,
        &request.resolved_nodes,
    )?;
    let outbound_tags = route_proxy_tags(&routes);
    let listen = ephemeral_loopback();
    let router_listener = bind_listener("route proxy", listen).await?;
    let router_addr = router_listener.local_addr()?;
    let selected_proxy = request
        .selected_proxy
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty());
    let (upstreams, outbound_tasks, bypass_cidrs) = spawn_route_outbounds(
        &config,
        &routes,
        selected_proxy,
        request.selected_node.as_ref(),
        &request.resolved_nodes,
    )
    .await?;
    let matcher = build_route_matcher(&config, assets_dir.as_deref(), &routes);
    let (tracked_addr, ingress_task) = spawn_tracked_socks_ingress(router_addr, matcher).await?;
    let core = aerion::ProxyCore::from_credentials("route", &[]);
    let route_config = RouteProxyConfig { routes, upstreams };
    let state = RouteProxyState::from_config(route_config).with_core(core.clone());
    let router_task = tokio::spawn(async move {
        run_route_proxy_with_state(router_listener, state)
            .await
            .context("run mihomo route proxy")
    });
    Ok(RouteRuntime {
        socks_addr: tracked_addr,
        router_task,
        outbound_tasks,
        ingress_task,
        core,
        bypass_cidrs,
        rule_count: config.rules.len(),
        outbound_tags,
    })
}

pub async fn start_route_from_json(input: &str) -> Result<String> {
    let request: StartRouteRequest =
        serde_json::from_str(input).context("parse start route request")?;
    let log_bridge = aerion::LogBridge::new();
    let runtime = start_route_runtime(request).await?;
    let session_id = NEXT_ROUTE_SESSION_ID.fetch_add(1, Ordering::SeqCst);
    let log_task = {
        let mut rx = log_bridge.subscribe();
        tokio::spawn(async move {
            while let Some(entry) = rx.recv().await {
                on_log(&entry.level.to_string(), &entry.message);
            }
        })
    };
    let event_task = {
        let mut rx = runtime.core.subscribe_events();
        tokio::spawn(async move {
            while let Some(event) = rx.recv().await {
                telemetry::record_core_event(&event);
                on_event(&core_event_json(&event, Some(session_id)));
            }
        })
    };
    ROUTE_SESSIONS
        .lock()
        .expect("route session map lock poisoned")
        .insert(
            session_id,
            RouteSession {
                router_task: runtime.router_task,
                outbound_tasks: runtime.outbound_tasks,
                ingress_task: runtime.ingress_task,
                _log_task: log_task,
                _event_task: event_task,
                _core: runtime.core,
            },
        );
    Ok(json!({
        "ok": true,
        "session_id": session_id,
        "socks_addr": runtime.socks_addr.to_string(),
        "rule_count": runtime.rule_count,
        "outbound_tags": runtime.outbound_tags,
        "bypass_cidrs": runtime.bypass_cidrs,
    })
    .to_string())
}

pub async fn stop_route(session_id: u64) -> Result<String> {
    let session = ROUTE_SESSIONS
        .lock()
        .expect("route session map lock poisoned")
        .remove(&session_id)
        .with_context(|| format!("route session not found: {session_id}"))?;
    session._core.cancel_all_sessions();
    session.ingress_task.abort();
    session.router_task.abort();
    for task in session.outbound_tasks {
        task.abort();
    }
    session._log_task.abort();
    session._event_task.abort();
    Ok(json!({"ok": true, "session_id": session_id}).to_string())
}

pub fn inspect_route_config_yaml(text: &str) -> Result<String> {
    let config = parse_mihomo_config(text)?;
    let preview: Vec<&str> = config.rules.iter().take(20).map(String::as_str).collect();
    Ok(json!({
        "ok": true,
        "rule_count": config.rules.len(),
        "proxy_group_count": config.proxy_groups.len(),
        "rule_provider_count": config.rule_providers.len(),
        "rules_preview": preview,
        "rules": config.rules,
        "has_rules": !config.rules.is_empty(),
    })
    .to_string())
}

fn parse_test_target(raw: &str) -> Result<ProxyTarget> {
    let trimmed = raw.trim();
    ensure!(!trimmed.is_empty(), "route test target is required");
    let (authority, default_port) = match trimmed.split_once("://") {
        Some((scheme, rest)) => {
            let port = if scheme.eq_ignore_ascii_case("http") || scheme.eq_ignore_ascii_case("ws") {
                80
            } else {
                443
            };
            let host_part = rest.split(['/', '?', '#']).next().unwrap_or(rest);
            let host_part = host_part
                .rsplit_once('@')
                .map(|(_, h)| h)
                .unwrap_or(host_part);
            (host_part.trim(), port)
        }
        None => {
            let host_part = trimmed.split(['/', '?', '#']).next().unwrap_or(trimmed);
            (host_part.trim(), 443)
        }
    };
    ensure!(!authority.is_empty(), "route test target host is empty");
    if let Ok(addr) = authority.parse::<SocketAddr>() {
        return Ok(ProxyTarget::Ip(addr));
    }
    if let Some(inner) = authority
        .strip_prefix('[')
        .and_then(|s| s.strip_suffix(']'))
    {
        let ip: IpAddr = inner
            .parse()
            .with_context(|| format!("invalid IPv6 address: {authority}"))?;
        return Ok(ProxyTarget::Ip(SocketAddr::new(ip, default_port)));
    }
    if let Ok(ip) = authority.parse::<IpAddr>() {
        return Ok(ProxyTarget::Ip(SocketAddr::new(ip, default_port)));
    }
    if let Some((host, port_str)) = authority.rsplit_once(':')
        && !host.contains(':')
        && !host.is_empty()
    {
        let port: u16 = port_str
            .parse()
            .with_context(|| format!("invalid target port: {port_str}"))?;
        if let Ok(ip) = host.parse::<IpAddr>() {
            return Ok(ProxyTarget::Ip(SocketAddr::new(ip, port)));
        }
        return Ok(ProxyTarget::Domain(host.to_string(), port));
    }
    Ok(ProxyTarget::Domain(authority.to_string(), default_port))
}

pub fn test_route_from_json(input: &str) -> Result<String> {
    let request: TestRouteRequest =
        serde_json::from_str(input).context("parse test route request")?;
    let target = parse_test_target(&request.target)?;
    let network_name = request
        .network
        .as_deref()
        .map(str::trim)
        .filter(|v| !v.is_empty())
        .unwrap_or("tcp")
        .to_ascii_lowercase();
    let network = match network_name.as_str() {
        "tcp" => RouteNetwork::Tcp,
        "udp" => RouteNetwork::Udp,
        other => bail!("unsupported route test network: {other}"),
    };
    let (config, assets_dir, routes) = compile_route_config(
        &request.config_yaml,
        request.geoip_dir.as_deref(),
        request.global_proxy.as_deref(),
        request.selected_proxy.as_deref(),
        &request.group_targets,
        &BTreeMap::new(),
    )?;
    let decision = routes.try_decide(&target, network)?;

    let mut indexed_config = config.clone();
    for (idx, rule) in indexed_config.rules.iter_mut().enumerate() {
        let (prefix, _, suffix) = split_rule_action(rule)?;
        *rule = format!("{prefix},__idx_{idx}{suffix}");
    }
    let matched_rule = indexed_config
        .route_table_with_assets(assets_dir.as_deref())
        .ok()
        .and_then(|table| match table.try_decide(&target, network).ok()? {
            RouteDecision::Proxy(tag) => tag
                .strip_prefix("__idx_")
                .and_then(|s| s.parse::<usize>().ok())
                .and_then(|idx| config.rules.get(idx).cloned()),
            _ => None,
        })
        .unwrap_or_else(|| "MATCH".to_string());

    let (decision_kind, outbound) = match decision {
        RouteDecision::Direct => ("DIRECT", None),
        RouteDecision::Block => ("REJECT", None),
        RouteDecision::Proxy(tag) => ("PROXY", Some(tag)),
    };
    let normalized_target = match &target {
        ProxyTarget::Ip(addr) => addr.to_string(),
        ProxyTarget::Domain(host, port) => format!("{host}:{port}"),
    };
    Ok(json!({
        "ok": true,
        "target": normalized_target,
        "network": network_name,
        "decision": decision_kind,
        "outbound": outbound,
        "matched_rule": matched_rule,
    })
    .to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compiles_geoip_cn_with_embedded_route_assets() -> Result<()> {
        let config = parse_mihomo_config(
            r#"
proxies: []
rules:
  - GEOIP,CN,DIRECT,no-resolve
  - MATCH,DIRECT
"#,
        )?;
        let assets = route_assets_dir(None);
        assert!(assets.is_some());
        config.route_table_with_assets(assets.as_deref())?;
        Ok(())
    }

    #[test]
    fn resolves_rule_targets_via_group_targets_and_selected_proxy() -> Result<()> {
        let yaml = r#"
proxies:
  - name: NodeA
    type: socks5
    server: 127.0.0.1
    port: 1080
  - name: NodeB
    type: socks5
    server: 127.0.0.1
    port: 1081
proxy-groups:
  - name: 代理选择
    type: select
    proxies:
      - 自动选择
      - NodeA
      - NodeB
  - name: 自动选择
    type: url-test
    proxies:
      - NodeA
      - NodeB
rules:
  - DOMAIN-SUFFIX,google.com,代理选择
  - MATCH,代理选择
"#;
        let (_, _, routes_fallback) = compile_route_config(
            yaml,
            None,
            None,
            Some("NodeB"),
            &BTreeMap::new(),
            &BTreeMap::new(),
        )?;
        let decision = routes_fallback.try_decide(
            &ProxyTarget::Domain("www.google.com".to_string(), 443),
            RouteNetwork::Tcp,
        )?;
        assert_eq!(decision, RouteDecision::Proxy("NodeB".to_string()));

        let mut gt = BTreeMap::new();
        gt.insert("代理选择".to_string(), "自动选择".to_string());
        gt.insert("自动选择".to_string(), "NodeB".to_string());
        let (_, _, routes_grouped) =
            compile_route_config(yaml, None, None, Some("NodeA"), &gt, &BTreeMap::new())?;
        let decision2 = routes_grouped.try_decide(
            &ProxyTarget::Domain("www.google.com".to_string(), 443),
            RouteNetwork::Tcp,
        )?;
        assert_eq!(decision2, RouteDecision::Proxy("NodeB".to_string()));
        Ok(())
    }
}
