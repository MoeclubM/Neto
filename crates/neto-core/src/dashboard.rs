use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DashboardWidgetId {
    NetworkSpeed,
    TrafficUsage,
    ActiveConnections,
    ExitCheck,
    LatencyProbe,
    ProfilesSummary,
    RuntimeInfo,
}

impl DashboardWidgetId {
    pub const ALL: &'static [DashboardWidgetId] = &[
        Self::NetworkSpeed,
        Self::TrafficUsage,
        Self::ActiveConnections,
        Self::ExitCheck,
        Self::LatencyProbe,
        Self::ProfilesSummary,
        Self::RuntimeInfo,
    ];

    pub fn key(self) -> &'static str {
        match self {
            Self::NetworkSpeed => "network_speed",
            Self::ActiveConnections => "active_connections",
            Self::ExitCheck => "exit_check",
            Self::TrafficUsage => "traffic_usage",
            Self::ProfilesSummary => "profiles_summary",
            Self::LatencyProbe => "latency_probe",
            Self::RuntimeInfo => "runtime_info",
        }
    }

    pub fn title_zh(self) -> &'static str {
        match self {
            Self::NetworkSpeed => "实时速率",
            Self::ActiveConnections => "活跃会话",
            Self::ExitCheck => "出口检测",
            Self::TrafficUsage => "流量统计",
            Self::ProfilesSummary => "订阅概览",
            Self::LatencyProbe => "连通延迟",
            Self::RuntimeInfo => "运行信息",
        }
    }

    pub fn default_enabled(self) -> bool {
        matches!(
            self,
            Self::NetworkSpeed | Self::TrafficUsage | Self::ExitCheck | Self::ProfilesSummary
        )
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DashboardWidgetItem {
    pub id: DashboardWidgetId,
    pub enabled: bool,
}

pub fn deserialize_dashboard_widgets<'de, D>(
    deserializer: D,
) -> Result<Vec<DashboardWidgetItem>, D::Error>
where
    D: serde::Deserializer<'de>,
{
    let raw: Vec<serde_json::Value> = Vec::deserialize(deserializer)?;
    let parsed: Vec<DashboardWidgetItem> = raw
        .into_iter()
        .filter_map(|v| serde_json::from_value(v).ok())
        .collect();
    Ok(normalize_dashboard_widgets(&parsed))
}

pub fn default_dashboard_widgets() -> Vec<DashboardWidgetItem> {
    DashboardWidgetId::ALL
        .iter()
        .copied()
        .map(|id| DashboardWidgetItem {
            id,
            enabled: id.default_enabled(),
        })
        .collect()
}

pub fn normalize_dashboard_widgets(items: &[DashboardWidgetItem]) -> Vec<DashboardWidgetItem> {
    let mut out = Vec::with_capacity(DashboardWidgetId::ALL.len());
    for item in items {
        if !out
            .iter()
            .any(|existing: &DashboardWidgetItem| existing.id == item.id)
        {
            out.push(item.clone());
        }
    }
    for &id in DashboardWidgetId::ALL {
        if !out.iter().any(|existing| existing.id == id) {
            out.push(DashboardWidgetItem {
                id,
                enabled: id.default_enabled(),
            });
        }
    }
    out
}

pub fn move_dashboard_widget(
    items: &mut Vec<DashboardWidgetItem>,
    id: DashboardWidgetId,
    delta: isize,
) {
    *items = normalize_dashboard_widgets(items);
    let Some(index) = items.iter().position(|item| item.id == id) else {
        return;
    };
    let target = (index as isize + delta).clamp(0, items.len().saturating_sub(1) as isize) as usize;
    if index != target {
        let item = items.remove(index);
        items.insert(target, item);
    }
}

pub fn set_dashboard_widget_enabled(
    items: &mut Vec<DashboardWidgetItem>,
    id: DashboardWidgetId,
    enabled: bool,
) {
    *items = normalize_dashboard_widgets(items);
    if let Some(item) = items.iter_mut().find(|item| item.id == id) {
        item.enabled = enabled;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_and_reorders_dashboard_widgets() {
        let mut widgets = vec![DashboardWidgetItem {
            id: DashboardWidgetId::TrafficUsage,
            enabled: false,
        }];
        widgets = normalize_dashboard_widgets(&widgets);
        assert_eq!(widgets.len(), DashboardWidgetId::ALL.len());
        assert_eq!(widgets[0].id, DashboardWidgetId::TrafficUsage);
        assert!(!widgets[0].enabled);

        move_dashboard_widget(&mut widgets, DashboardWidgetId::TrafficUsage, 2);
        assert_eq!(widgets[2].id, DashboardWidgetId::TrafficUsage);

        set_dashboard_widget_enabled(&mut widgets, DashboardWidgetId::TrafficUsage, true);
        assert!(widgets[2].enabled);
    }
}
