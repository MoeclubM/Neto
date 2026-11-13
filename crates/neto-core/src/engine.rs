use crate::dashboard::{
    DashboardWidgetId, DashboardWidgetItem, default_dashboard_widgets, move_dashboard_widget,
    normalize_dashboard_widgets, set_dashboard_widget_enabled,
};
use crate::doh::prepare_node_for_connect;
use crate::http_proxy::HttpProxyBridge;
use crate::profile::{
    NodeSummary, ParsedProfilePayload, ProfileRecord, SubscriptionUserInfo,
    build_profile_from_raw_nodes, fetch_and_parse_subscription, merge_preserved_latencies,
    parse_yaml_profile,
};
use crate::route::{StartRouteRequest, start_route_from_json, stop_route, test_route_from_json};
use crate::runtime::{
    StartVpnRequest, TestNodeRequest, probe_via_socks, start_socks_from_json, start_vpn_from_json,
    stop_socks, stop_vpn, test_node,
};
use crate::state::{
    ConnectionSnapshot, EngineSnapshot, NetoSettings, NodeSortMode, PersistedStore, ProfileBrief,
    RoutingMode,
};
use crate::telemetry;
use anyhow::{Context, Result, anyhow, bail, ensure};
use serde::Deserialize;
use serde_json::{Value, json};
use std::net::{IpAddr, SocketAddr};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use tokio::sync::{Mutex as AsyncMutex, Semaphore};
use tokio::task::JoinSet;

pub trait PlatformHooks: Send + Sync {
    fn set_system_proxy(
        &self,
        _host: &str,
        _http_port: u16,
        _socks_port: u16,
        _bypass: &str,
    ) -> Result<()> {
        Ok(())
    }

    fn clear_system_proxy(&self) -> Result<()> {
        Ok(())
    }

    fn set_autostart(&self, _enabled: bool) -> Result<()> {
        Ok(())
    }

    fn can_use_tun(&self) -> bool {
        true
    }
}

struct NoopPlatformHooks;
impl PlatformHooks for NoopPlatformHooks {}

#[derive(Clone, Copy, PartialEq, Eq)]
enum SessionKind {
    Tun,
    Route,
    Socks,
}

impl SessionKind {
    fn as_str(self) -> &'static str {
        match self {
            Self::Tun => "tun",
            Self::Route => "route",
            Self::Socks => "socks",
        }
    }
}

struct ActiveRuntime {
    kind: SessionKind,
    session_id: u64,
    socks_addr: SocketAddr,
    connected_at_ms: u64,
    node_name: String,
    node_protocol: String,
}

struct MutableState {
    store: PersistedStore,
    connecting: bool,
    nodes_testing: bool,
    last_error: Option<String>,
    active: Option<ActiveRuntime>,
    http_bridge: Option<HttpProxyBridge>,
    system_proxy_active: bool,
}

pub struct NetoController {
    data_dir: PathBuf,
    user_agent: String,
    hooks: Arc<dyn PlatformHooks>,
    state: Mutex<MutableState>,
    lifecycle_lock: AsyncMutex<()>,
}

impl NetoController {
    pub fn new(
        data_dir: impl Into<PathBuf>,
        user_agent: impl Into<String>,
        hooks: Option<Arc<dyn PlatformHooks>>,
    ) -> Result<Arc<Self>> {
        let data_dir = data_dir.into();
        std::fs::create_dir_all(&data_dir)
            .with_context(|| format!("create Neto data_dir {}", data_dir.display()))?;
        let store = load_store(&data_dir).unwrap_or_default();
        Ok(Arc::new(Self {
            data_dir,
            user_agent: user_agent.into(),
            hooks: hooks.unwrap_or_else(|| Arc::new(NoopPlatformHooks)),
            state: Mutex::new(MutableState {
                store,
                connecting: false,
                nodes_testing: false,
                last_error: None,
                active: None,
                http_bridge: None,
                system_proxy_active: false,
            }),
            lifecycle_lock: AsyncMutex::new(()),
        }))
    }

    pub fn data_dir(&self) -> &Path {
        &self.data_dir
    }

    pub fn snapshot(&self) -> EngineSnapshot {
        let guard = self.state.lock().expect("controller state lock poisoned");
        let connected = guard.active.is_some();
        let telemetry = telemetry::sample_telemetry(connected);
        let http_proxy_addr = guard
            .http_bridge
            .as_ref()
            .map(|b| format!("127.0.0.1:{}", b.port()))
            .unwrap_or_default();

        let connection = match &guard.active {
            Some(rt) => ConnectionSnapshot {
                status: if guard.connecting {
                    "connecting".to_string()
                } else {
                    "connected".to_string()
                },
                connected: true,
                connecting: guard.connecting,
                session_kind: rt.kind.as_str().to_string(),
                connected_at_ms: rt.connected_at_ms,
                socks_addr: rt.socks_addr.to_string(),
                http_proxy_addr,
                system_proxy_active: guard.system_proxy_active,
                tun_active: rt.kind == SessionKind::Tun,
                active_node_name: rt.node_name.clone(),
                active_node_protocol: rt.node_protocol.clone(),
                last_error: guard.last_error.clone(),
            },
            None => ConnectionSnapshot {
                status: if guard.connecting {
                    "connecting".to_string()
                } else {
                    "disconnected".to_string()
                },
                connected: false,
                connecting: guard.connecting,
                session_kind: String::new(),
                connected_at_ms: 0,
                socks_addr: String::new(),
                http_proxy_addr,
                system_proxy_active: guard.system_proxy_active,
                tun_active: false,
                active_node_name: String::new(),
                active_node_protocol: String::new(),
                last_error: guard.last_error.clone(),
            },
        };

        let active_id = guard.store.active_profile_id.clone();
        let profiles = guard
            .store
            .profiles
            .iter()
            .map(|p| ProfileBrief {
                id: p.id.clone(),
                name: p.name.clone(),
                kind: p.kind.clone(),
                url: p.url.clone(),
                flag: p.flag.clone(),
                updated_at_unix: p.updated_at_unix,
                auto_update_mins: p.auto_update_mins,
                node_count: p.nodes.len(),
                rule_count: p.rule_count,
                used_bytes: p.user_info.as_ref().map(|u| u.used_bytes()).unwrap_or(0),
                total_bytes: p.user_info.as_ref().map(|u| u.total_bytes).unwrap_or(0),
                expire_unix: p.user_info.as_ref().and_then(|u| u.expire_unix),
                active: active_id.as_deref() == Some(p.id.as_str()),
            })
            .collect::<Vec<_>>();

        let active_profile = active_id
            .as_deref()
            .and_then(|id| guard.store.profiles.iter().find(|p| p.id == id))
            .or_else(|| guard.store.profiles.first());

        let (mut nodes, proxy_groups, rule_count, rules_preview) = match active_profile {
            Some(p) => (
                p.nodes.clone(),
                p.proxy_groups.clone(),
                p.rule_count,
                p.rules_preview.clone(),
            ),
            None => (Vec::new(), Vec::new(), 0, Vec::new()),
        };

        match guard.store.node_sort_mode {
            NodeSortMode::Default => {}
            NodeSortMode::Latency => {
                nodes.sort_by_key(|n| (n.latency_ms.unwrap_or(u64::MAX), n.index));
            }
            NodeSortMode::Name => {
                nodes.sort_by(|a, b| a.name.cmp(&b.name));
            }
        }

        let selected_node_index = if nodes.is_empty() {
            0
        } else {
            guard.store.selected_node_index.min(nodes.iter().map(|n| n.index).max().unwrap_or(0))
        };

        EngineSnapshot {
            connection,
            settings: guard.store.settings.clone(),
            active_profile_id: active_profile.map(|p| p.id.clone()),
            profiles,
            selected_node_index,
            node_sort_mode: guard.store.node_sort_mode,
            nodes_testing: guard.nodes_testing,
            nodes,
            proxy_groups,
            rule_count,
            rules_preview,
            telemetry,
        }
    }

    fn persist(&self) -> Result<()> {
        let store = self
            .state
            .lock()
            .expect("controller state lock poisoned")
            .store
            .clone();
        save_store(&self.data_dir, &store)
    }

    pub async fn add_remote_profile(
        &self,
        name: &str,
        url: &str,
        flag: &str,
        auto_update_mins: u32,
    ) -> Result<String> {
        let clean_url = url.trim();
        ensure!(!clean_url.is_empty(), "subscription URL is empty");
        let effective_flag = if flag.trim().is_empty() {
            "aerion"
        } else {
            flag.trim()
        };
        let payload = fetch_and_parse_subscription(
            clean_url,
            effective_flag,
            Some(&self.user_agent),
            None,
        )
        .await?;
        let id = format!("prof-{}", telemetry::now_ms());
        let display_name = if name.trim().is_empty() {
            reqwest::Url::parse(clean_url)
                .ok()
                .and_then(|u| u.host_str().map(ToString::to_string))
                .unwrap_or_else(|| "Subscription".to_string())
        } else {
            name.trim().to_string()
        };
        let record = ProfileRecord {
            id: id.clone(),
            name: display_name,
            kind: "remote".to_string(),
            url: clean_url.to_string(),
            flag: effective_flag.to_string(),
            updated_at_unix: telemetry::now_ms() / 1000,
            auto_update_mins,
            user_info: payload.user_info,
            nodes: payload.nodes,
            proxy_groups: payload.proxy_groups,
            route_config_yaml: payload.route_config_yaml,
            rule_count: payload.rule_count,
            rules_preview: payload.rules_preview,
        };
        {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            guard.store.profiles.push(record);
            if guard.store.active_profile_id.is_none() {
                guard.store.active_profile_id = Some(id.clone());
                guard.store.selected_node_index = 0;
            }
        }
        self.persist()?;
        Ok(id)
    }

    pub async fn import_local_profile(&self, name: &str, yaml_text: &str) -> Result<String> {
        let payload = parse_yaml_profile(yaml_text).await?;
        let id = format!("local-{}", telemetry::now_ms());
        let display_name = if name.trim().is_empty() {
            "Local YAML".to_string()
        } else {
            name.trim().to_string()
        };
        let record = ProfileRecord {
            id: id.clone(),
            name: display_name,
            kind: "local".to_string(),
            url: String::new(),
            flag: "meta".to_string(),
            updated_at_unix: telemetry::now_ms() / 1000,
            auto_update_mins: 0,
            user_info: None,
            nodes: payload.nodes,
            proxy_groups: payload.proxy_groups,
            route_config_yaml: payload.route_config_yaml,
            rule_count: payload.rule_count,
            rules_preview: payload.rules_preview,
        };
        {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            guard.store.profiles.push(record);
            if guard.store.active_profile_id.is_none() {
                guard.store.active_profile_id = Some(id.clone());
                guard.store.selected_node_index = 0;
            }
        }
        self.persist()?;
        Ok(id)
    }

    pub fn upsert_managed_profile(
        &self,
        id: &str,
        name: &str,
        url: &str,
        payload: ParsedProfilePayload,
    ) -> Result<()> {
        self.upsert_managed_profile_with_options(id, name, url, payload, false)
    }

    pub fn upsert_managed_profile_with_options(
        &self,
        id: &str,
        name: &str,
        url: &str,
        mut payload: ParsedProfilePayload,
        preserve_routes: bool,
    ) -> Result<()> {
        {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            let existing_nodes_len = guard
                .store
                .profiles
                .iter_mut()
                .find(|p| p.id == id)
                .map(|existing| {
                    merge_preserved_latencies(&existing.nodes, &mut payload.nodes);
                    existing.name = name.to_string();
                    existing.url = url.to_string();
                    existing.updated_at_unix = telemetry::now_ms() / 1000;
                    existing.user_info = payload.user_info.clone();
                    existing.nodes = payload.nodes.clone();
                    existing.proxy_groups = payload.proxy_groups.clone();
                    if !preserve_routes || payload.route_config_yaml.is_some() {
                        existing.route_config_yaml = payload.route_config_yaml.clone();
                        existing.rule_count = payload.rule_count;
                        existing.rules_preview = payload.rules_preview.clone();
                    }
                    existing.nodes.len()
                });
            if let Some(nodes_len) = existing_nodes_len {
                if guard.store.selected_node_index >= nodes_len {
                    guard.store.selected_node_index = 0;
                }
            } else {
                guard.store.profiles.insert(
                    0,
                    ProfileRecord {
                        id: id.to_string(),
                        name: name.to_string(),
                        kind: "managed".to_string(),
                        url: url.to_string(),
                        flag: "aerion".to_string(),
                        updated_at_unix: telemetry::now_ms() / 1000,
                        auto_update_mins: 30,
                        user_info: payload.user_info,
                        nodes: payload.nodes,
                        proxy_groups: payload.proxy_groups,
                        route_config_yaml: payload.route_config_yaml,
                        rule_count: payload.rule_count,
                        rules_preview: payload.rules_preview,
                    },
                );
            }
            guard.store.active_profile_id = Some(id.to_string());
        }
        self.persist()
    }

    pub async fn sync_managed_subscription(
        &self,
        id: &str,
        name: &str,
        url: &str,
        flag: &str,
        user_agent_override: Option<&str>,
    ) -> Result<Option<SubscriptionUserInfo>> {
        let clean_url = url.trim();
        ensure!(!clean_url.is_empty(), "subscription URL is empty");
        let effective_flag = if flag.trim().is_empty() {
            "aerion"
        } else {
            flag.trim()
        };
        let ua = user_agent_override
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .unwrap_or(&self.user_agent);
        let payload = fetch_and_parse_subscription(clean_url, effective_flag, Some(ua), None).await?;
        let user_info = payload.user_info.clone();
        self.upsert_managed_profile(id, name, clean_url, payload)?;
        Ok(user_info)
    }

    pub async fn refresh_profile(&self, profile_id: Option<&str>) -> Result<()> {
        let (id, url, flag) = {
            let guard = self.state.lock().expect("controller state lock poisoned");
            let target_id = profile_id
                .map(ToString::to_string)
                .or_else(|| guard.store.active_profile_id.clone())
                .or_else(|| guard.store.profiles.first().map(|p| p.id.clone()))
                .context("no profile available to refresh")?;
            let prof = guard
                .store
                .profiles
                .iter()
                .find(|p| p.id == target_id)
                .with_context(|| format!("profile not found: {target_id}"))?;
            ensure!(
                !prof.url.trim().is_empty(),
                "local profile has no remote subscription URL"
            );
            (prof.id.clone(), prof.url.clone(), prof.flag.clone())
        };
        let mut payload =
            fetch_and_parse_subscription(&url, &flag, Some(&self.user_agent), None).await?;
        {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            let updated_len = guard
                .store
                .profiles
                .iter_mut()
                .find(|p| p.id == id)
                .map(|prof| {
                    merge_preserved_latencies(&prof.nodes, &mut payload.nodes);
                    prof.updated_at_unix = telemetry::now_ms() / 1000;
                    prof.user_info = payload.user_info;
                    prof.nodes = payload.nodes;
                    prof.proxy_groups = payload.proxy_groups;
                    prof.route_config_yaml = payload.route_config_yaml;
                    prof.rule_count = payload.rule_count;
                    prof.rules_preview = payload.rules_preview;
                    prof.nodes.len()
                });
            if let Some(nodes_len) = updated_len
                && guard.store.selected_node_index >= nodes_len
            {
                guard.store.selected_node_index = 0;
            }
        }
        self.persist()
    }

    pub async fn select_profile(self: &Arc<Self>, id: &str) -> Result<()> {
        let was_connected = {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            ensure!(
                guard.store.profiles.iter().any(|p| p.id == id),
                "profile not found: {id}"
            );
            guard.store.active_profile_id = Some(id.to_string());
            guard.store.selected_node_index = 0;
            guard.active.is_some()
        };
        self.persist()?;
        if was_connected {
            self.connect().await?;
        }
        Ok(())
    }

    pub async fn delete_profile(self: &Arc<Self>, id: &str) -> Result<()> {
        let switched_active = {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            let was_active = guard.store.active_profile_id.as_deref() == Some(id);
            guard.store.profiles.retain(|p| p.id != id);
            if was_active {
                guard.store.active_profile_id = guard.store.profiles.first().map(|p| p.id.clone());
                guard.store.selected_node_index = 0;
            }
            was_active && guard.active.is_some()
        };
        self.persist()?;
        if switched_active {
            let has_any = !self
                .state
                .lock()
                .expect("controller state lock poisoned")
                .store
                .profiles
                .is_empty();
            if has_any {
                self.connect().await?;
            } else {
                self.disconnect().await?;
            }
        }
        Ok(())
    }

    pub async fn select_node(self: &Arc<Self>, index: usize) -> Result<()> {
        let was_connected = {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            guard.store.selected_node_index = index;
            guard.active.is_some()
        };
        self.persist()?;
        if was_connected {
            self.connect().await?;
        }
        Ok(())
    }

    pub fn select_group_proxy(&self, group_name: &str, proxy_name: &str) -> Result<()> {
        {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            let prof_idx = guard
                .store
                .active_profile_id
                .as_deref()
                .and_then(|id| guard.store.profiles.iter().position(|p| p.id == id))
                .unwrap_or(0);
            let matched_node_idx = {
                let prof = guard
                    .store
                    .profiles
                    .get_mut(prof_idx)
                    .context("no active profile")?;
                let group = prof
                    .proxy_groups
                    .iter_mut()
                    .find(|g| g.name == group_name)
                    .with_context(|| format!("proxy group not found: {group_name}"))?;
                group.selected = Some(proxy_name.to_string());
                prof.nodes.iter().position(|n| n.name == proxy_name)
            };
            if let Some(node_idx) = matched_node_idx {
                guard.store.selected_node_index = node_idx;
            }
        }
        self.persist()
    }

    pub fn set_node_sort(&self, mode: NodeSortMode) -> Result<()> {
        self.state
            .lock()
            .expect("controller state lock poisoned")
            .store
            .node_sort_mode = mode;
        self.persist()
    }

    pub async fn test_single_node(&self, index: usize) -> Result<u64> {
        let (node_raw, dns_url, test_url, profile_id) = {
            let guard = self.state.lock().expect("controller state lock poisoned");
            let prof = active_profile_ref(&guard.store).context("no active profile")?;
            let node = prof
                .nodes
                .get(index)
                .with_context(|| format!("node index out of range: {index}"))?;
            ensure!(node.supported, "node protocol is not supported");
            (
                node.raw.clone(),
                guard.store.settings.node_dns.clone(),
                guard.store.settings.node_test_url.clone(),
                prof.id.clone(),
            )
        };
        let (target_host, target_port, target_tls) = parse_probe_url(&test_url)?;
        let prepared =
            prepare_node_for_connect(node_raw, &dns_url, Some(&self.user_agent)).await?;
        let result = test_node(TestNodeRequest {
            node: prepared,
            target_host,
            target_port,
            target_tls,
            timeout_ms: 8000,
        })
        .await;
        {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            if let Some(prof) = guard.store.profiles.iter_mut().find(|p| p.id == profile_id)
                && let Some(node) = prof.nodes.get_mut(index)
            {
                match &result {
                    Ok(ms) => {
                        node.latency_ms = Some(*ms);
                        node.last_error = None;
                    }
                    Err(err) => {
                        node.latency_ms = None;
                        node.last_error = Some(err.to_string());
                    }
                }
            }
        }
        let _ = self.persist();
        result
    }

    pub async fn test_all_nodes(self: &Arc<Self>) -> Result<()> {
        let (nodes, dns_url, test_url, profile_id) = {
            let mut guard = self.state.lock().expect("controller state lock poisoned");
            if guard.nodes_testing {
                return Ok(());
            }
            let (nodes, profile_id) = {
                let prof = active_profile_ref(&guard.store).context("no active profile")?;
                (prof.nodes.clone(), prof.id.clone())
            };
            let dns_url = guard.store.settings.node_dns.clone();
            let test_url = guard.store.settings.node_test_url.clone();
            guard.nodes_testing = true;
            (nodes, dns_url, test_url, profile_id)
        };

        let (target_host, target_port, target_tls) = match parse_probe_url(&test_url) {
            Ok(v) => v,
            Err(err) => {
                self.state
                    .lock()
                    .expect("controller state lock poisoned")
                    .nodes_testing = false;
                return Err(err);
            }
        };

        let semaphore = Arc::new(Semaphore::new(8));
        let mut join_set = JoinSet::new();
        for node in nodes {
            if !node.supported {
                continue;
            }
            let sem = semaphore.clone();
            let dns = dns_url.clone();
            let ua = self.user_agent.clone();
            let thost = target_host.clone();
            let idx = node.index;
            let raw = node.raw;
            join_set.spawn(async move {
                let _permit = sem.acquire_owned().await.expect("semaphore open");
                let outcome = match prepare_node_for_connect(raw, &dns, Some(&ua)).await {
                    Ok(prepared) => {
                        test_node(TestNodeRequest {
                            node: prepared,
                            target_host: thost,
                            target_port,
                            target_tls,
                            timeout_ms: 8000,
                        })
                        .await
                    }
                    Err(err) => Err(err),
                };
                (idx, outcome)
            });
        }

        while let Some(joined) = join_set.join_next().await {
            if let Ok((idx, outcome)) = joined {
                let mut guard = self.state.lock().expect("controller state lock poisoned");
                if let Some(prof) = guard.store.profiles.iter_mut().find(|p| p.id == profile_id)
                    && let Some(node) = prof.nodes.get_mut(idx)
                {
                    match outcome {
                        Ok(ms) => {
                            node.latency_ms = Some(ms);
                            node.last_error = None;
                        }
                        Err(err) => {
                            node.latency_ms = None;
                            node.last_error = Some(err.to_string());
                        }
                    }
                }
            }
        }

        self.state
            .lock()
            .expect("controller state lock poisoned")
            .nodes_testing = false;
        self.persist()
    }

    pub async fn probe_active_tunnel(&self) -> Result<Option<u64>> {
        let (socks_addr, test_url) = {
            let guard = self.state.lock().expect("controller state lock poisoned");
            let Some(rt) = &guard.active else {
                return Ok(None);
            };
            (rt.socks_addr, guard.store.settings.node_test_url.clone())
        };
        let (host, port, tls) = parse_probe_url(&test_url)?;
        match tokio::time::timeout(
            std::time::Duration::from_secs(5),
            probe_via_socks(socks_addr, &host, port, tls),
        )
        .await
        {
            Ok(Ok(ms)) => {
                telemetry::record_latency_sample(Some(ms));
                Ok(Some(ms))
            }
            _ => {
                telemetry::record_latency_sample(None);
                Ok(None)
            }
        }
    }

    pub async fn connect(self: &Arc<Self>) -> Result<()> {
        let _guard = self.lifecycle_lock.lock().await;
        self.disconnect_unlocked().await?;
        {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            st.connecting = true;
            st.last_error = None;
        }
        let result = self.start_desktop_or_local_unlocked().await;
        if let Err(err) = &result {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            st.connecting = false;
            st.last_error = Some(format!("{err:#}"));
        }
        result
    }

    pub async fn connect_android_tun(
        self: &Arc<Self>,
        tun_fd: i32,
        bypass: Option<Vec<String>>,
    ) -> Result<SocketAddr> {
        let _guard = self.lifecycle_lock.lock().await;
        self.disconnect_unlocked().await?;
        {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            st.connecting = true;
            st.last_error = None;
        }
        let result = self.start_android_tun_unlocked(tun_fd, bypass).await;
        if let Err(err) = &result {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            st.connecting = false;
            st.last_error = Some(format!("{err:#}"));
        }
        result
    }

    pub async fn disconnect(self: &Arc<Self>) -> Result<()> {
        let _guard = self.lifecycle_lock.lock().await;
        self.disconnect_unlocked().await
    }

    pub async fn toggle_connect(self: &Arc<Self>) -> Result<bool> {
        let is_connected = self
            .state
            .lock()
            .expect("controller state lock poisoned")
            .active
            .is_some();
        if is_connected {
            self.disconnect().await?;
            Ok(false)
        } else {
            self.connect().await?;
            Ok(true)
        }
    }

    async fn disconnect_unlocked(&self) -> Result<()> {
        let (active, bridge, had_sys_proxy) = {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            let active = st.active.take();
            let bridge = st.http_bridge.take();
            let sys = st.system_proxy_active;
            st.system_proxy_active = false;
            st.connecting = false;
            (active, bridge, sys)
        };
        if had_sys_proxy {
            let _ = self.hooks.clear_system_proxy();
        }
        if let Some(b) = bridge {
            b.stop();
        }
        if let Some(rt) = active {
            match rt.kind {
                SessionKind::Tun => {
                    let _ = stop_vpn(rt.session_id).await;
                }
                SessionKind::Route => {
                    let _ = stop_route(rt.session_id).await;
                }
                SessionKind::Socks => {
                    let _ = stop_socks(rt.session_id).await;
                }
            }
        }
        telemetry::reset_session_telemetry();
        Ok(())
    }

    fn prepare_session_inputs(
        &self,
    ) -> Result<(
        NetoSettings,
        NodeSummary,
        Option<String>,
    )> {
        let guard = self.state.lock().expect("controller state lock poisoned");
        let settings = guard.store.settings.clone();
        if settings.routing_mode == RoutingMode::Direct {
            let direct_node = NodeSummary {
                index: 0,
                name: "DIRECT".to_string(),
                protocol: "direct".to_string(),
                protocol_label: "Direct".to_string(),
                server: "127.0.0.1".to_string(),
                port: 0,
                udp: true,
                supported: true,
                tags: Vec::new(),
                latency_ms: None,
                last_error: None,
                raw: json!({ "type": "direct", "name": "DIRECT" }),
            };
            return Ok((settings, direct_node, None));
        }
        let prof = active_profile_ref(&guard.store).context("请先添加或选择订阅配置")?;
        ensure!(!prof.nodes.is_empty(), "当前订阅没有可用节点");
        let idx = guard
            .store
            .selected_node_index
            .min(prof.nodes.len().saturating_sub(1));
        let node = prof.nodes[idx].clone();
        ensure!(
            node.supported,
            "当前节点协议 {} 暂不支持",
            node.protocol_label
        );
        let route_yaml = if !settings.custom_route_yaml.trim().is_empty() {
            Some(settings.custom_route_yaml.trim().to_string())
        } else {
            prof.route_config_yaml.clone()
        };
        Ok((settings, node, route_yaml))
    }

    async fn start_android_tun_unlocked(
        &self,
        tun_fd: i32,
        bypass: Option<Vec<String>>,
    ) -> Result<SocketAddr> {
        let (settings, node, route_yaml) = self.prepare_session_inputs()?;
        let resolved_node = prepare_node_for_connect(
            node.raw.clone(),
            &settings.node_dns,
            Some(&self.user_agent),
        )
        .await?;
        let route_req = if settings.routing_mode == RoutingMode::Rule
            && let Some(yaml) = route_yaml.filter(|s| !s.trim().is_empty())
        {
            Some(StartRouteRequest {
                config_yaml: yaml,
                geoip_dir: (!settings.geoip_dir.trim().is_empty())
                    .then(|| settings.geoip_dir.trim().to_string()),
                global_proxy: None,
                selected_proxy: Some(node.name.clone()),
                selected_node: Some(resolved_node.clone()),
            })
        } else {
            None
        };
        let dns_addr = extract_dns_ip(if settings.vpn_dns_mode == "direct" {
            &settings.direct_dns
        } else {
            &settings.overseas_dns
        });
        let vpn_req = StartVpnRequest {
            node: resolved_node,
            route: route_req,
            tun_fd: Some(tun_fd),
            mtu: settings.mtu,
            dns: settings.vpn_dns_mode.clone(),
            dns_addr,
            virtual_dns_pool: settings.virtual_dns_pool.clone(),
            bypass,
            ipv6: settings.ipv6_enabled,
            tcp_timeout_secs: None,
            udp_timeout_secs: Some(60),
            max_sessions: Some(2048),
            exit_on_fatal_error: Some(false),
        };
        telemetry::reset_session_telemetry();
        let out_str = start_vpn_from_json(&serde_json::to_string(&vpn_req)?).await?;
        let out: Value = serde_json::from_str(&out_str)?;
        let session_id = out
            .get("session_id")
            .and_then(Value::as_u64)
            .context("start_vpn missing session_id")?;
        let socks_addr: SocketAddr = out
            .get("socks_addr")
            .and_then(Value::as_str)
            .context("start_vpn missing socks_addr")?
            .parse()?;
        let bridge =
            HttpProxyBridge::start(socks_addr, settings.mixed_port, settings.allow_lan).await.ok();
        {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            st.connecting = false;
            st.http_bridge = bridge;
            st.active = Some(ActiveRuntime {
                kind: SessionKind::Tun,
                session_id,
                socks_addr,
                connected_at_ms: telemetry::now_ms(),
                node_name: node.name,
                node_protocol: node.protocol_label,
            });
        }
        Ok(socks_addr)
    }

    async fn start_desktop_or_local_unlocked(&self) -> Result<()> {
        let (settings, node, route_yaml) = self.prepare_session_inputs()?;
        let resolved_node = prepare_node_for_connect(
            node.raw.clone(),
            &settings.node_dns,
            Some(&self.user_agent),
        )
        .await?;

        telemetry::reset_session_telemetry();
        let want_tun = settings.tun_enabled;
        if want_tun && !self.hooks.can_use_tun() {
            bail!("TUN 模式需要管理员权限或 /dev/net/tun 访问权限");
        }

        let (kind, session_id, socks_addr) = if want_tun {
            let route_req = if settings.routing_mode == RoutingMode::Rule
                && let Some(yaml) = route_yaml.filter(|s| !s.trim().is_empty())
            {
                Some(StartRouteRequest {
                    config_yaml: yaml,
                    geoip_dir: (!settings.geoip_dir.trim().is_empty())
                        .then(|| settings.geoip_dir.trim().to_string()),
                    global_proxy: None,
                    selected_proxy: Some(node.name.clone()),
                    selected_node: Some(resolved_node.clone()),
                })
            } else {
                None
            };
            let dns_addr = extract_dns_ip(if settings.vpn_dns_mode == "direct" {
                &settings.direct_dns
            } else {
                &settings.overseas_dns
            });
            let req = StartVpnRequest {
                node: resolved_node,
                route: route_req,
                tun_fd: None,
                mtu: settings.mtu,
                dns: settings.vpn_dns_mode.clone(),
                dns_addr,
                virtual_dns_pool: settings.virtual_dns_pool.clone(),
                bypass: None,
                ipv6: settings.ipv6_enabled,
                tcp_timeout_secs: None,
                udp_timeout_secs: Some(60),
                max_sessions: Some(2048),
                exit_on_fatal_error: Some(false),
            };
            let out: Value =
                serde_json::from_str(&start_vpn_from_json(&serde_json::to_string(&req)?).await?)?;
            let sid = out
                .get("session_id")
                .and_then(Value::as_u64)
                .context("missing session_id")?;
            let saddr: SocketAddr = out
                .get("socks_addr")
                .and_then(Value::as_str)
                .context("missing socks_addr")?
                .parse()?;
            (SessionKind::Tun, sid, saddr)
        } else if settings.routing_mode == RoutingMode::Rule
            && let Some(yaml) = route_yaml.filter(|s| !s.trim().is_empty())
        {
            let req = StartRouteRequest {
                config_yaml: yaml,
                geoip_dir: (!settings.geoip_dir.trim().is_empty())
                    .then(|| settings.geoip_dir.trim().to_string()),
                global_proxy: None,
                selected_proxy: Some(node.name.clone()),
                selected_node: Some(resolved_node),
            };
            let out: Value =
                serde_json::from_str(&start_route_from_json(&serde_json::to_string(&req)?).await?)?;
            let sid = out
                .get("session_id")
                .and_then(Value::as_u64)
                .context("missing session_id")?;
            let saddr: SocketAddr = out
                .get("socks_addr")
                .and_then(Value::as_str)
                .context("missing socks_addr")?
                .parse()?;
            (SessionKind::Route, sid, saddr)
        } else {
            let out: Value = serde_json::from_str(
                &start_socks_from_json(&json!({ "node": resolved_node }).to_string()).await?,
            )?;
            let sid = out
                .get("session_id")
                .and_then(Value::as_u64)
                .context("missing session_id")?;
            let saddr: SocketAddr = out
                .get("socks_addr")
                .and_then(Value::as_str)
                .context("missing socks_addr")?
                .parse()?;
            (SessionKind::Socks, sid, saddr)
        };

        let bridge = match HttpProxyBridge::start(
            socks_addr,
            settings.mixed_port,
            settings.allow_lan,
        )
        .await
        {
            Ok(b) => b,
            Err(_) if settings.mixed_port > 0 => {
                HttpProxyBridge::start(socks_addr, 0, settings.allow_lan).await?
            }
            Err(err) => return Err(err),
        };
        let http_port = bridge.port();
        let mut sys_proxy_applied = false;
        if settings.system_proxy_enabled {
            self.hooks.set_system_proxy(
                "127.0.0.1",
                http_port,
                socks_addr.port(),
                &settings.system_proxy_bypass,
            )?;
            sys_proxy_applied = true;
        }

        {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            st.connecting = false;
            st.http_bridge = Some(bridge);
            st.system_proxy_active = sys_proxy_applied;
            st.active = Some(ActiveRuntime {
                kind,
                session_id,
                socks_addr,
                connected_at_ms: telemetry::now_ms(),
                node_name: node.name,
                node_protocol: node.protocol_label,
            });
        }
        Ok(())
    }

    pub async fn set_routing_mode(self: &Arc<Self>, mode: RoutingMode) -> Result<()> {
        let reconnect = {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            let changed = st.store.settings.routing_mode != mode;
            st.store.settings.routing_mode = mode;
            changed && st.active.is_some()
        };
        self.persist()?;
        if reconnect {
            self.connect().await?;
        }
        Ok(())
    }

    pub async fn set_tun_enabled(self: &Arc<Self>, enabled: bool) -> Result<()> {
        let reconnect = {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            let changed = st.store.settings.tun_enabled != enabled;
            st.store.settings.tun_enabled = enabled;
            changed && st.active.is_some()
        };
        self.persist()?;
        if reconnect {
            self.connect().await?;
        }
        Ok(())
    }

    pub fn set_system_proxy_enabled(&self, enabled: bool) -> Result<()> {
        let action = {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            st.store.settings.system_proxy_enabled = enabled;
            match (&st.active, &st.http_bridge) {
                (Some(rt), Some(bridge)) => Some((
                    enabled,
                    bridge.port(),
                    rt.socks_addr.port(),
                    st.store.settings.system_proxy_bypass.clone(),
                )),
                _ => None,
            }
        };
        self.persist()?;
        if let Some((on, http_port, socks_port, bypass)) = action {
            if on {
                self.hooks
                    .set_system_proxy("127.0.0.1", http_port, socks_port, &bypass)?;
                self.state
                    .lock()
                    .expect("controller state lock poisoned")
                    .system_proxy_active = true;
            } else {
                self.hooks.clear_system_proxy()?;
                self.state
                    .lock()
                    .expect("controller state lock poisoned")
                    .system_proxy_active = false;
            }
        }
        Ok(())
    }

    pub fn update_settings(&self, patch: &Value) -> Result<NetoSettings> {
        let (updated, autostart_changed) = {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            let prev_autostart = st.store.settings.autostart;
            let mut current_json = serde_json::to_value(&st.store.settings)?;
            if let (Some(dst), Some(src)) = (current_json.as_object_mut(), patch.as_object()) {
                for (k, v) in src {
                    dst.insert(k.clone(), v.clone());
                }
            }
            let next: NetoSettings = serde_json::from_value(current_json)?;
            let next = next.normalized();
            let auto_diff =
                (next.autostart != prev_autostart).then_some(next.autostart);
            st.store.settings = next.clone();
            (next, auto_diff)
        };
        if let Some(enable_auto) = autostart_changed {
            let _ = self.hooks.set_autostart(enable_auto);
        }
        self.persist()?;
        Ok(updated)
    }

    pub fn update_dashboard_widget(
        &self,
        id: DashboardWidgetId,
        enabled: Option<bool>,
        move_delta: Option<isize>,
    ) -> Result<Vec<DashboardWidgetItem>> {
        let out = {
            let mut st = self.state.lock().expect("controller state lock poisoned");
            if let Some(en) = enabled {
                set_dashboard_widget_enabled(&mut st.store.settings.dashboard_widgets, id, en);
            }
            if let Some(delta) = move_delta {
                move_dashboard_widget(&mut st.store.settings.dashboard_widgets, id, delta);
            }
            st.store.settings.dashboard_widgets.clone()
        };
        self.persist()?;
        Ok(out)
    }

    pub fn reset_dashboard_widgets(&self) -> Result<Vec<DashboardWidgetItem>> {
        let widgets = default_dashboard_widgets();
        self.state
            .lock()
            .expect("controller state lock poisoned")
            .store
            .settings
            .dashboard_widgets = widgets.clone();
        self.persist()?;
        Ok(widgets)
    }

    pub async fn dispatch_json(self: &Arc<Self>, method: &str, params: Value) -> Result<Value> {
        match method {
            "snapshot" => Ok(serde_json::to_value(self.snapshot())?),
            "logs" => {
                let limit = params.get("limit").and_then(Value::as_u64).unwrap_or(200) as usize;
                Ok(serde_json::to_value(telemetry::list_logs(limit))?)
            }
            "clear_logs" => {
                telemetry::clear_logs();
                Ok(json!({ "ok": true }))
            }
            "connect" => {
                self.connect().await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "connect_android_tun" => {
                let tun_fd = params
                    .get("tun_fd")
                    .and_then(Value::as_i64)
                    .context("missing tun_fd")? as i32;
                let bypass = params
                    .get("bypass")
                    .and_then(Value::as_array)
                    .map(|arr| {
                        arr.iter()
                            .filter_map(Value::as_str)
                            .map(ToString::to_string)
                            .collect::<Vec<_>>()
                    });
                let socks = self.connect_android_tun(tun_fd, bypass).await?;
                Ok(json!({ "ok": true, "socks_addr": socks.to_string() }))
            }
            "disconnect" => {
                self.disconnect().await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "toggle_connect" => {
                self.toggle_connect().await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "select_node" => {
                let idx = params
                    .get("index")
                    .and_then(Value::as_u64)
                    .context("missing index")? as usize;
                self.select_node(idx).await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "select_group_proxy" => {
                let group = params
                    .get("group_name")
                    .and_then(Value::as_str)
                    .context("missing group_name")?;
                let proxy = params
                    .get("proxy_name")
                    .and_then(Value::as_str)
                    .context("missing proxy_name")?;
                self.select_group_proxy(group, proxy)?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "set_node_sort" => {
                let mode: NodeSortMode = serde_json::from_value(
                    params.get("mode").cloned().unwrap_or(json!("default")),
                )?;
                self.set_node_sort(mode)?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "test_node" => {
                let idx = params
                    .get("index")
                    .and_then(Value::as_u64)
                    .context("missing index")? as usize;
                let latency = self.test_single_node(idx).await?;
                Ok(json!({ "ok": true, "latency_ms": latency }))
            }
            "test_all_nodes" => {
                self.test_all_nodes().await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "probe_tunnel" => {
                let ms = self.probe_active_tunnel().await?;
                Ok(json!({ "latency_ms": ms }))
            }
            "record_external_bytes" => {
                let rx = params.get("rx_bytes").and_then(Value::as_u64).unwrap_or(0);
                let tx = params.get("tx_bytes").and_then(Value::as_u64).unwrap_or(0);
                telemetry::record_external_bytes(rx, tx);
                Ok(json!({ "ok": true }))
            }
            "set_routing_mode" => {
                let mode: RoutingMode =
                    serde_json::from_value(params.get("mode").cloned().context("missing mode")?)?;
                self.set_routing_mode(mode).await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "set_tun_enabled" => {
                let enabled = params
                    .get("enabled")
                    .and_then(Value::as_bool)
                    .context("missing enabled")?;
                self.set_tun_enabled(enabled).await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "set_system_proxy_enabled" => {
                let enabled = params
                    .get("enabled")
                    .and_then(Value::as_bool)
                    .context("missing enabled")?;
                self.set_system_proxy_enabled(enabled)?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "update_settings" => {
                self.update_settings(&params)?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "add_remote_profile" => {
                let name = params.get("name").and_then(Value::as_str).unwrap_or("");
                let url = params
                    .get("url")
                    .and_then(Value::as_str)
                    .context("missing url")?;
                let flag = params
                    .get("flag")
                    .and_then(Value::as_str)
                    .unwrap_or("aerion");
                let mins = params
                    .get("auto_update_mins")
                    .and_then(Value::as_u64)
                    .unwrap_or(60) as u32;
                let id = self.add_remote_profile(name, url, flag, mins).await?;
                Ok(json!({ "id": id, "snapshot": self.snapshot() }))
            }
            "import_local_profile" => {
                let name = params.get("name").and_then(Value::as_str).unwrap_or("");
                let yaml = params
                    .get("yaml")
                    .and_then(Value::as_str)
                    .context("missing yaml")?;
                let id = self.import_local_profile(name, yaml).await?;
                Ok(json!({ "id": id, "snapshot": self.snapshot() }))
            }
            "refresh_profile" => {
                let id = params.get("id").and_then(Value::as_str);
                self.refresh_profile(id).await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "select_profile" => {
                let id = params
                    .get("id")
                    .and_then(Value::as_str)
                    .context("missing id")?;
                self.select_profile(id).await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "delete_profile" => {
                let id = params
                    .get("id")
                    .and_then(Value::as_str)
                    .context("missing id")?;
                self.delete_profile(id).await?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "upsert_managed_profile" => {
                #[derive(Deserialize)]
                struct UpsertReq {
                    id: String,
                    name: String,
                    #[serde(default)]
                    url: String,
                    #[serde(default)]
                    nodes: Vec<Value>,
                    #[serde(default)]
                    route_config_yaml: Option<String>,
                    #[serde(default)]
                    rules_preview: Vec<String>,
                    #[serde(default)]
                    rule_count: usize,
                    #[serde(default)]
                    preserve_routes: bool,
                    #[serde(default)]
                    user_info: Option<SubscriptionUserInfo>,
                }
                let req: UpsertReq = serde_json::from_value(params)?;
                let payload = build_profile_from_raw_nodes(
                    req.nodes,
                    req.route_config_yaml,
                    req.rules_preview,
                    req.rule_count,
                    req.user_info,
                );
                self.upsert_managed_profile_with_options(
                    &req.id,
                    &req.name,
                    &req.url,
                    payload,
                    req.preserve_routes,
                )?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "sync_managed_subscription" => {
                let id = params
                    .get("id")
                    .and_then(Value::as_str)
                    .unwrap_or("managed-default");
                let name = params
                    .get("name")
                    .and_then(Value::as_str)
                    .unwrap_or("Managed");
                let url = params
                    .get("url")
                    .and_then(Value::as_str)
                    .context("missing url")?;
                let flag = params
                    .get("flag")
                    .and_then(Value::as_str)
                    .unwrap_or("aerion");
                let ua = params.get("user_agent").and_then(Value::as_str);
                let user_info = self
                    .sync_managed_subscription(id, name, url, flag, ua)
                    .await?;
                Ok(json!({
                    "user_info": user_info,
                    "snapshot": self.snapshot(),
                }))
            }
            "update_dashboard_widget" => {
                let id: DashboardWidgetId =
                    serde_json::from_value(params.get("id").cloned().context("missing id")?)?;
                let enabled = params.get("enabled").and_then(Value::as_bool);
                let move_delta = params
                    .get("move_delta")
                    .and_then(Value::as_i64)
                    .map(|v| v as isize);
                self.update_dashboard_widget(id, enabled, move_delta)?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "set_dashboard_widgets" => {
                let widgets: Vec<DashboardWidgetItem> = serde_json::from_value(
                    params.get("widgets").cloned().context("missing widgets")?,
                )?;
                {
                    let mut st = self.state.lock().expect("controller state lock poisoned");
                    st.store.settings.dashboard_widgets = normalize_dashboard_widgets(&widgets);
                }
                self.persist()?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "reset_dashboard_widgets" => {
                self.reset_dashboard_widgets()?;
                Ok(serde_json::to_value(self.snapshot())?)
            }
            "test_route" => {
                let target = params
                    .get("target")
                    .and_then(Value::as_str)
                    .context("missing target")?;
                let network = params
                    .get("network")
                    .and_then(Value::as_str)
                    .unwrap_or("tcp");
                let yaml = {
                    let st = self.state.lock().expect("controller state lock poisoned");
                    if !st.store.settings.custom_route_yaml.trim().is_empty() {
                        st.store.settings.custom_route_yaml.clone()
                    } else {
                        active_profile_ref(&st.store)
                            .and_then(|p| p.route_config_yaml.clone())
                            .unwrap_or_default()
                    }
                };
                ensure!(!yaml.trim().is_empty(), "当前没有可用的分流规则");
                let out_str = test_route_from_json(
                    &json!({
                        "config_yaml": yaml,
                        "target": target,
                        "network": network,
                    })
                    .to_string(),
                )?;
                Ok(serde_json::from_str(&out_str)?)
            }
            other => Err(anyhow!("unknown Neto method: {other}")),
        }
    }
}

fn active_profile_ref(store: &PersistedStore) -> Option<&ProfileRecord> {
    store
        .active_profile_id
        .as_deref()
        .and_then(|id| store.profiles.iter().find(|p| p.id == id))
        .or_else(|| store.profiles.first())
}

fn store_file_path(data_dir: &Path) -> PathBuf {
    data_dir.join("neto_store.json")
}

fn load_store(data_dir: &Path) -> Result<PersistedStore> {
    let path = store_file_path(data_dir);
    let bytes = std::fs::read(&path)?;
    let mut store: PersistedStore = serde_json::from_slice(&bytes)?;
    store.settings = store.settings.normalized();
    Ok(store)
}

fn save_store(data_dir: &Path, store: &PersistedStore) -> Result<()> {
    let path = store_file_path(data_dir);
    let tmp = data_dir.join("neto_store.json.tmp");
    let bytes = serde_json::to_vec_pretty(store)?;
    std::fs::write(&tmp, bytes)?;
    std::fs::rename(&tmp, &path)?;
    Ok(())
}

pub fn extract_dns_ip(raw: &str) -> String {
    let trimmed = raw.trim();
    if trimmed.parse::<IpAddr>().is_ok() {
        return trimmed.to_string();
    }
    if let Ok(url) = reqwest::Url::parse(trimmed)
        && let Some(host) = url.host_str()
        && host.parse::<IpAddr>().is_ok()
    {
        return host.to_string();
    }
    "1.1.1.1".to_string()
}

pub fn parse_probe_url(raw: &str) -> Result<(String, u16, bool)> {
    let trimmed = raw.trim();
    if let Ok(url) = reqwest::Url::parse(trimmed) {
        let tls = url.scheme().eq_ignore_ascii_case("https");
        let host = url
            .host_str()
            .context("node test URL missing host")?
            .to_string();
        let port = url.port().unwrap_or(if tls { 443 } else { 80 });
        return Ok((host, port, tls));
    }
    Ok((trimmed.to_string(), 443, true))
}

pub fn init_rustls_provider() {
    let _ = rustls::crypto::ring::default_provider().install_default();
}
