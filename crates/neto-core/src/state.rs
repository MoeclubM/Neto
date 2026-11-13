use crate::dashboard::{
    DashboardWidgetItem, default_dashboard_widgets, deserialize_dashboard_widgets,
    normalize_dashboard_widgets,
};
use crate::profile::ProfileRecord;
use crate::telemetry::TelemetrySnapshot;
use serde::{Deserialize, Serialize};

pub const DEFAULT_SYSTEM_PROXY_BYPASS: &str =
    "<local>;localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.2*;172.30.*;172.31.*;192.168.*;[::1]";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum RoutingMode {
    #[default]
    Rule,
    Global,
    Direct,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum NodeSortMode {
    #[default]
    Default,
    Latency,
    Name,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct NetoSettings {
    #[serde(default)]
    pub routing_mode: RoutingMode,
    #[serde(default)]
    pub tun_enabled: bool,
    #[serde(default = "default_true")]
    pub system_proxy_enabled: bool,
    #[serde(default = "default_mixed_port")]
    pub mixed_port: u16,
    #[serde(default)]
    pub allow_lan: bool,
    #[serde(default = "default_bypass")]
    pub system_proxy_bypass: String,
    #[serde(default = "default_node_dns")]
    pub node_dns: String,
    #[serde(default = "default_overseas_dns")]
    pub overseas_dns: String,
    #[serde(default = "default_direct_dns")]
    pub direct_dns: String,
    #[serde(default = "default_vpn_dns_mode")]
    pub vpn_dns_mode: String,
    #[serde(default = "default_virtual_dns_pool")]
    pub virtual_dns_pool: String,
    #[serde(default = "default_true")]
    pub ipv6_enabled: bool,
    #[serde(default = "default_mtu")]
    pub mtu: u16,
    #[serde(default = "default_test_url")]
    pub node_test_url: String,
    #[serde(default)]
    pub custom_route_yaml: String,
    #[serde(default)]
    pub geoip_dir: String,
    #[serde(default = "default_app_rule_mode")]
    pub app_rule_mode: String,
    #[serde(default)]
    pub excluded_apps: Vec<String>,
    #[serde(default)]
    pub allowed_apps: Vec<String>,
    #[serde(default)]
    pub autostart: bool,
    #[serde(default)]
    pub silent_start: bool,
    #[serde(default = "default_theme_mode")]
    pub theme_mode: String,
    #[serde(default = "default_language")]
    pub language: String,
    #[serde(
        default = "default_dashboard_widgets",
        deserialize_with = "deserialize_dashboard_widgets"
    )]
    pub dashboard_widgets: Vec<DashboardWidgetItem>,
}

fn default_true() -> bool {
    true
}
fn default_mixed_port() -> u16 {
    7890
}
fn default_bypass() -> String {
    DEFAULT_SYSTEM_PROXY_BYPASS.to_string()
}
fn default_node_dns() -> String {
    "https://dns.alidns.com/resolve".to_string()
}
fn default_overseas_dns() -> String {
    "https://cloudflare-dns.com/dns-query".to_string()
}
fn default_direct_dns() -> String {
    "223.5.5.5".to_string()
}
fn default_vpn_dns_mode() -> String {
    "virtual".to_string()
}
fn default_virtual_dns_pool() -> String {
    "198.18.0.0/15".to_string()
}
fn default_mtu() -> u16 {
    1500
}
fn default_test_url() -> String {
    "https://cp.cloudflare.com/generate_204".to_string()
}
fn default_app_rule_mode() -> String {
    "exclude".to_string()
}
fn default_theme_mode() -> String {
    "system".to_string()
}
fn default_language() -> String {
    "system".to_string()
}

impl Default for NetoSettings {
    fn default() -> Self {
        Self {
            routing_mode: RoutingMode::Rule,
            tun_enabled: false,
            system_proxy_enabled: true,
            mixed_port: default_mixed_port(),
            allow_lan: false,
            system_proxy_bypass: default_bypass(),
            node_dns: default_node_dns(),
            overseas_dns: default_overseas_dns(),
            direct_dns: default_direct_dns(),
            vpn_dns_mode: default_vpn_dns_mode(),
            virtual_dns_pool: default_virtual_dns_pool(),
            ipv6_enabled: true,
            mtu: default_mtu(),
            node_test_url: default_test_url(),
            custom_route_yaml: String::new(),
            geoip_dir: String::new(),
            app_rule_mode: default_app_rule_mode(),
            excluded_apps: Vec::new(),
            allowed_apps: Vec::new(),
            autostart: false,
            silent_start: false,
            theme_mode: default_theme_mode(),
            language: default_language(),
            dashboard_widgets: default_dashboard_widgets(),
        }
    }
}

impl NetoSettings {
    pub fn normalized(mut self) -> Self {
        self.dashboard_widgets = normalize_dashboard_widgets(&self.dashboard_widgets);
        if self.system_proxy_bypass.trim().is_empty() {
            self.system_proxy_bypass = default_bypass();
        }
        if self.node_test_url.trim().is_empty() {
            self.node_test_url = default_test_url();
        }
        self
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct PersistedStore {
    #[serde(default)]
    pub settings: NetoSettings,
    #[serde(default)]
    pub profiles: Vec<ProfileRecord>,
    #[serde(default)]
    pub active_profile_id: Option<String>,
    #[serde(default)]
    pub selected_node_index: usize,
    #[serde(default)]
    pub node_sort_mode: NodeSortMode,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct ConnectionSnapshot {
    pub status: String,
    pub connected: bool,
    pub connecting: bool,
    pub session_kind: String,
    pub connected_at_ms: u64,
    pub socks_addr: String,
    pub http_proxy_addr: String,
    pub system_proxy_active: bool,
    pub tun_active: bool,
    #[serde(default)]
    pub can_use_tun: bool,
    #[serde(default)]
    pub tun_service_status: String,
    pub active_node_name: String,
    pub active_node_protocol: String,
    pub last_error: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProfileBrief {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub url: String,
    pub flag: String,
    pub updated_at_unix: u64,
    pub auto_update_mins: u32,
    pub node_count: usize,
    pub rule_count: usize,
    pub used_bytes: u64,
    pub total_bytes: u64,
    pub expire_unix: Option<u64>,
    pub active: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EngineSnapshot {
    pub connection: ConnectionSnapshot,
    pub settings: NetoSettings,
    pub active_profile_id: Option<String>,
    pub profiles: Vec<ProfileBrief>,
    pub selected_node_index: usize,
    pub node_sort_mode: NodeSortMode,
    pub nodes_testing: bool,
    pub nodes: Vec<crate::profile::NodeSummary>,
    pub proxy_groups: Vec<crate::profile::ProxyGroupSummary>,
    pub rule_count: usize,
    pub rules_preview: Vec<String>,
    pub telemetry: TelemetrySnapshot,
    #[serde(default)]
    pub last_network_check: Option<crate::diagnostics::NetworkCheckResult>,
}
