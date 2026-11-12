using System.Globalization;
using System.Text.Json.Serialization;

namespace Neto.Sdk.Windows;

public sealed class NetoConnection
{
    [JsonPropertyName("status")] public string Status { get; set; } = "disconnected";
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("connecting")] public bool Connecting { get; set; }
    [JsonPropertyName("session_kind")] public string SessionKind { get; set; } = "";
    [JsonPropertyName("connected_at_ms")] public ulong ConnectedAtMs { get; set; }
    [JsonPropertyName("socks_addr")] public string SocksAddr { get; set; } = "";
    [JsonPropertyName("http_proxy_addr")] public string HttpProxyAddr { get; set; } = "";
    [JsonPropertyName("system_proxy_active")] public bool SystemProxyActive { get; set; }
    [JsonPropertyName("tun_active")] public bool TunActive { get; set; }
    [JsonPropertyName("active_node_name")] public string ActiveNodeName { get; set; } = "";
    [JsonPropertyName("active_node_protocol")] public string ActiveNodeProtocol { get; set; } = "";
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
}

public sealed class NetoWidgetItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string TitleZh => Id switch
    {
        "network_speed" => "实时速率",
        "outbound_mode" => "出站模式",
        "current_outbound" => "当前节点",
        "network_takeover" => "网络接管",
        "traffic_usage" => "流量统计",
        "profiles_summary" => "订阅概览",
        "latency_probe" => "连通延迟",
        "runtime_info" => "运行信息",
        _ => Id,
    };
}

public sealed class NetoSettings
{
    [JsonPropertyName("routing_mode")] public string RoutingMode { get; set; } = "rule";
    [JsonPropertyName("tun_enabled")] public bool TunEnabled { get; set; }
    [JsonPropertyName("system_proxy_enabled")] public bool SystemProxyEnabled { get; set; } = true;
    [JsonPropertyName("mixed_port")] public ushort MixedPort { get; set; } = 7890;
    [JsonPropertyName("allow_lan")] public bool AllowLan { get; set; }
    [JsonPropertyName("system_proxy_bypass")] public string SystemProxyBypass { get; set; } = "";
    [JsonPropertyName("node_dns")] public string NodeDns { get; set; } = "https://dns.alidns.com/resolve";
    [JsonPropertyName("overseas_dns")] public string OverseasDns { get; set; } = "https://cloudflare-dns.com/dns-query";
    [JsonPropertyName("direct_dns")] public string DirectDns { get; set; } = "223.5.5.5";
    [JsonPropertyName("vpn_dns_mode")] public string VpnDnsMode { get; set; } = "virtual";
    [JsonPropertyName("virtual_dns_pool")] public string VirtualDnsPool { get; set; } = "198.18.0.0/15";
    [JsonPropertyName("ipv6_enabled")] public bool Ipv6Enabled { get; set; } = true;
    [JsonPropertyName("mtu")] public ushort Mtu { get; set; } = 1500;
    [JsonPropertyName("node_test_url")] public string NodeTestUrl { get; set; } = "https://cp.cloudflare.com/generate_204";
    [JsonPropertyName("custom_route_yaml")] public string CustomRouteYaml { get; set; } = "";
    [JsonPropertyName("autostart")] public bool Autostart { get; set; }
    [JsonPropertyName("silent_start")] public bool SilentStart { get; set; }
    [JsonPropertyName("theme_mode")] public string ThemeMode { get; set; } = "system";
    [JsonPropertyName("dashboard_widgets")] public List<NetoWidgetItem> DashboardWidgets { get; set; } = [];
}

public sealed class NetoProfileBrief
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "remote";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("flag")] public string Flag { get; set; } = "aerion";
    [JsonPropertyName("updated_at_unix")] public ulong UpdatedAtUnix { get; set; }
    [JsonPropertyName("auto_update_mins")] public uint AutoUpdateMins { get; set; }
    [JsonPropertyName("node_count")] public int NodeCount { get; set; }
    [JsonPropertyName("rule_count")] public int RuleCount { get; set; }
    [JsonPropertyName("used_bytes")] public ulong UsedBytes { get; set; }
    [JsonPropertyName("total_bytes")] public ulong TotalBytes { get; set; }
    [JsonPropertyName("expire_unix")] public ulong? ExpireUnix { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
}

public sealed class NetoNodeItem
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("protocol_label")] public string ProtocolLabel { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("port")] public ushort Port { get; set; }
    [JsonPropertyName("udp")] public bool Udp { get; set; }
    [JsonPropertyName("supported")] public bool Supported { get; set; } = true;
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonPropertyName("latency_ms")] public ulong? LatencyMs { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
}

public sealed class NetoProxyGroup
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "select";
    [JsonPropertyName("proxies")] public List<string> Proxies { get; set; } = [];
    [JsonPropertyName("selected")] public string? Selected { get; set; }
}

public sealed class NetoTelemetry
{
    [JsonPropertyName("upload_bytes")] public ulong UploadBytes { get; set; }
    [JsonPropertyName("download_bytes")] public ulong DownloadBytes { get; set; }
    [JsonPropertyName("upload_speed_bps")] public ulong UploadSpeedBps { get; set; }
    [JsonPropertyName("download_speed_bps")] public ulong DownloadSpeedBps { get; set; }
    [JsonPropertyName("upload_history")] public List<ulong> UploadHistory { get; set; } = [];
    [JsonPropertyName("download_history")] public List<ulong> DownloadHistory { get; set; } = [];
    [JsonPropertyName("latency_ms")] public ulong? LatencyMs { get; set; }
    [JsonPropertyName("latency_history")] public List<long> LatencyHistory { get; set; } = [];
    [JsonPropertyName("active_sessions")] public int ActiveSessions { get; set; }
    [JsonPropertyName("memory_bytes")] public ulong MemoryBytes { get; set; }
    [JsonPropertyName("intranet_ipv4")] public string? IntranetIpv4 { get; set; }
    [JsonPropertyName("intranet_ipv6")] public string? IntranetIpv6 { get; set; }
}

public sealed class NetoSnapshot
{
    [JsonPropertyName("connection")] public NetoConnection Connection { get; set; } = new();
    [JsonPropertyName("settings")] public NetoSettings Settings { get; set; } = new();
    [JsonPropertyName("active_profile_id")] public string? ActiveProfileId { get; set; }
    [JsonPropertyName("profiles")] public List<NetoProfileBrief> Profiles { get; set; } = [];
    [JsonPropertyName("selected_node_index")] public int SelectedNodeIndex { get; set; }
    [JsonPropertyName("node_sort_mode")] public string NodeSortMode { get; set; } = "default";
    [JsonPropertyName("nodes_testing")] public bool NodesTesting { get; set; }
    [JsonPropertyName("nodes")] public List<NetoNodeItem> Nodes { get; set; } = [];
    [JsonPropertyName("proxy_groups")] public List<NetoProxyGroup> ProxyGroups { get; set; } = [];
    [JsonPropertyName("rule_count")] public int RuleCount { get; set; }
    [JsonPropertyName("rules_preview")] public List<string> RulesPreview { get; set; } = [];
    [JsonPropertyName("telemetry")] public NetoTelemetry Telemetry { get; set; } = new();

    [JsonIgnore]
    public NetoNodeItem? SelectedNode =>
        Nodes.FirstOrDefault(n => n.Index == SelectedNodeIndex) ?? Nodes.FirstOrDefault();

    [JsonIgnore]
    public NetoProfileBrief? ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Active) ?? Profiles.FirstOrDefault();
}

public sealed class NetoLogEntry
{
    [JsonPropertyName("timestamp_ms")] public ulong TimestampMs { get; set; }
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public static class NetoFormat
{
    public static string FormatBytes(ulong bytes)
    {
        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;
        const double tb = gb * 1024.0;
        if (bytes >= (ulong)tb) return string.Format(CultureInfo.InvariantCulture, "{0:F2} TB", bytes / tb);
        if (bytes >= (ulong)gb) return string.Format(CultureInfo.InvariantCulture, "{0:F2} GB", bytes / gb);
        if (bytes >= (ulong)mb) return string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / mb);
        if (bytes >= (ulong)kb) return string.Format(CultureInfo.InvariantCulture, "{0:F1} KB", bytes / kb);
        return $"{bytes} B";
    }

    public static string FormatSpeed(ulong bps) => $"{FormatBytes(bps)}/s";

    public static string FormatDurationMs(ulong durationMs)
    {
        var totalSec = durationMs / 1000UL;
        var h = totalSec / 3600UL;
        var m = (totalSec % 3600UL) / 60UL;
        var s = totalSec % 60UL;
        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", h, m, s);
    }

    public static ulong NowUnixMs() =>
        (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
