pub mod config_compat;
pub mod connections;
pub mod dashboard;
pub mod diagnostics;
pub mod doh;
pub mod engine;
pub mod http_proxy;
pub mod mihomo_sanitize;
pub mod profile;
pub mod protocol;
pub mod route;
pub mod runtime;
pub mod state;
pub mod telemetry;
pub mod tun_service;
pub mod updater;

pub use connections::{
    ActiveConnectionEntry, ConnectionsSnapshot, clear_closed_connections, close_all_connections,
    close_connection, snapshot_connections,
};
pub use dashboard::{DashboardWidgetId, DashboardWidgetItem};
pub use diagnostics::{
    ConnectivityTargetResult, NetworkCheckResult, RuleItem, parse_rule_items,
    prepend_custom_route_rule, run_network_check, validate_custom_route_yaml,
};
pub use engine::{NetoController, PlatformHooks, init_rustls_provider};
pub use mihomo_sanitize::sanitize_mihomo_route_yaml;
pub use profile::{
    NodeSummary, ParsedProfilePayload, ProfileRecord, ProxyGroupSummary, SubscriptionUserInfo,
    build_profile_from_raw_nodes, fetch_and_parse_subscription, parse_yaml_profile,
};
pub use route::{
    inspect_route_config_yaml, start_route_from_json, stop_route, test_route_from_json,
};
#[cfg(any(target_os = "windows", target_os = "linux"))]
pub use runtime::shutdown_vpns;
pub use runtime::{
    set_event_callback, set_log_callback, start_socks_from_json, start_vpn_from_json, stop_socks,
    stop_vpn, test_node_from_json,
};
pub use state::{
    ConnectionSnapshot, EngineSnapshot, NetoSettings, NodeSortMode, ProfileBrief, RoutingMode,
};
pub use telemetry::{LogEntry, TelemetrySnapshot};
pub use updater::{
    DownloadUpdateResult, UpdateCheckResult, check_github_release, download_release_asset,
};
