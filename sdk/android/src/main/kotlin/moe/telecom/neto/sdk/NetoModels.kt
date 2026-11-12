package moe.telecom.neto.sdk

import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

data class NetoConnection(
    val status: String = "disconnected",
    val connected: Boolean = false,
    val connecting: Boolean = false,
    val sessionKind: String = "",
    val connectedAtMs: Long = 0L,
    val socksAddr: String = "",
    val httpProxyAddr: String = "",
    val activeNodeName: String = "",
    val activeNodeProtocol: String = "",
    val lastError: String? = null,
)

data class NetoWidgetItem(
    val id: String,
    val enabled: Boolean,
) {
    val titleZh: String
        get() = when (id) {
            "network_speed" -> "实时速率"
            "outbound_mode" -> "出站模式"
            "current_outbound" -> "当前节点"
            "network_takeover" -> "网络接管"
            "traffic_usage" -> "流量统计"
            "profiles_summary" -> "订阅概览"
            "latency_probe" -> "连通延迟"
            "runtime_info" -> "运行信息"
            else -> id
        }
}

data class NetoSettings(
    val routingMode: String = "rule",
    val mixedPort: Int = 7890,
    val allowLan: Boolean = false,
    val nodeDns: String = "https://dns.alidns.com/resolve",
    val overseasDns: String = "https://cloudflare-dns.com/dns-query",
    val directDns: String = "223.5.5.5",
    val vpnDnsMode: String = "virtual",
    val virtualDnsPool: String = "198.18.0.0/15",
    val ipv6Enabled: Boolean = true,
    val mtu: Int = 1500,
    val nodeTestUrl: String = "https://cp.cloudflare.com/generate_204",
    val customRouteYaml: String = "",
    val geoipDir: String = "",
    val appRuleMode: String = "exclude",
    val excludedApps: List<String> = emptyList(),
    val allowedApps: List<String> = emptyList(),
    val themeMode: String = "system",
    val language: String = "system",
    val dashboardWidgets: List<NetoWidgetItem> = emptyList(),
)

data class NetoProfileBrief(
    val id: String,
    val name: String,
    val kind: String,
    val url: String,
    val flag: String,
    val updatedAtUnix: Long,
    val autoUpdateMins: Int,
    val nodeCount: Int,
    val ruleCount: Int,
    val usedBytes: Long,
    val totalBytes: Long,
    val expireUnix: Long?,
    val active: Boolean,
)

data class NetoNodeItem(
    val index: Int,
    val name: String,
    val protocol: String,
    val protocolLabel: String,
    val server: String,
    val port: Int,
    val udp: Boolean,
    val supported: Boolean,
    val tags: List<String>,
    val latencyMs: Long?,
    val lastError: String?,
)

data class NetoProxyGroup(
    val name: String,
    val kind: String,
    val proxies: List<String>,
    val selected: String?,
)

data class NetoTelemetry(
    val uploadBytes: Long = 0L,
    val downloadBytes: Long = 0L,
    val uploadSpeedBps: Long = 0L,
    val downloadSpeedBps: Long = 0L,
    val uploadHistory: List<Long> = emptyList(),
    val downloadHistory: List<Long> = emptyList(),
    val latencyMs: Long? = null,
    val latencyHistory: List<Long> = emptyList(),
    val activeSessions: Int = 0,
    val memoryBytes: Long = 0L,
    val intranetIpv4: String? = null,
    val intranetIpv6: String? = null,
)

data class NetoSnapshot(
    val connection: NetoConnection = NetoConnection(),
    val settings: NetoSettings = NetoSettings(),
    val activeProfileId: String? = null,
    val profiles: List<NetoProfileBrief> = emptyList(),
    val selectedNodeIndex: Int = 0,
    val nodeSortMode: String = "default",
    val nodesTesting: Boolean = false,
    val nodes: List<NetoNodeItem> = emptyList(),
    val proxyGroups: List<NetoProxyGroup> = emptyList(),
    val ruleCount: Int = 0,
    val rulesPreview: List<String> = emptyList(),
    val telemetry: NetoTelemetry = NetoTelemetry(),
) {
    val selectedNode: NetoNodeItem?
        get() = nodes.firstOrNull { it.index == selectedNodeIndex } ?: nodes.firstOrNull()

    val activeProfile: NetoProfileBrief?
        get() = profiles.firstOrNull { it.active } ?: profiles.firstOrNull()

    companion object {
        fun fromJson(obj: JSONObject): NetoSnapshot {
            val connObj = obj.optJSONObject("connection") ?: JSONObject()
            val setObj = obj.optJSONObject("settings") ?: JSONObject()
            val telObj = obj.optJSONObject("telemetry") ?: JSONObject()
            return NetoSnapshot(
                connection = NetoConnection(
                    status = connObj.optString("status", "disconnected"),
                    connected = connObj.optBoolean("connected", false),
                    connecting = connObj.optBoolean("connecting", false),
                    sessionKind = connObj.optString("session_kind", ""),
                    connectedAtMs = connObj.optLong("connected_at_ms", 0L),
                    socksAddr = connObj.optString("socks_addr", ""),
                    httpProxyAddr = connObj.optString("http_proxy_addr", ""),
                    activeNodeName = connObj.optString("active_node_name", ""),
                    activeNodeProtocol = connObj.optString("active_node_protocol", ""),
                    lastError = connObj.optString("last_error", "").takeIf { it.isNotBlank() && it != "null" },
                ),
                settings = NetoSettings(
                    routingMode = setObj.optString("routing_mode", "rule"),
                    mixedPort = setObj.optInt("mixed_port", 7890),
                    allowLan = setObj.optBoolean("allow_lan", false),
                    nodeDns = setObj.optString("node_dns", "https://dns.alidns.com/resolve"),
                    overseasDns = setObj.optString("overseas_dns", "https://cloudflare-dns.com/dns-query"),
                    directDns = setObj.optString("direct_dns", "223.5.5.5"),
                    vpnDnsMode = setObj.optString("vpn_dns_mode", "virtual"),
                    virtualDnsPool = setObj.optString("virtual_dns_pool", "198.18.0.0/15"),
                    ipv6Enabled = setObj.optBoolean("ipv6_enabled", true),
                    mtu = setObj.optInt("mtu", 1500),
                    nodeTestUrl = setObj.optString("node_test_url", "https://cp.cloudflare.com/generate_204"),
                    customRouteYaml = setObj.optString("custom_route_yaml", ""),
                    geoipDir = setObj.optString("geoip_dir", ""),
                    appRuleMode = setObj.optString("app_rule_mode", "exclude"),
                    excludedApps = setObj.optJSONArray("excluded_apps").toStringList(),
                    allowedApps = setObj.optJSONArray("allowed_apps").toStringList(),
                    themeMode = setObj.optString("theme_mode", "system"),
                    language = setObj.optString("language", "system"),
                    dashboardWidgets = setObj.optJSONArray("dashboard_widgets").let { arr ->
                        if (arr == null) emptyList()
                        else (0 until arr.length()).mapNotNull { i ->
                            val w = arr.optJSONObject(i) ?: return@mapNotNull null
                            NetoWidgetItem(
                                id = w.optString("id", ""),
                                enabled = w.optBoolean("enabled", true),
                            )
                        }
                    },
                ),
                activeProfileId = obj.optString("active_profile_id", "").takeIf { it.isNotBlank() && it != "null" },
                profiles = obj.optJSONArray("profiles").let { arr ->
                    if (arr == null) emptyList()
                    else (0 until arr.length()).mapNotNull { i ->
                        val p = arr.optJSONObject(i) ?: return@mapNotNull null
                        NetoProfileBrief(
                            id = p.optString("id", ""),
                            name = p.optString("name", ""),
                            kind = p.optString("kind", "remote"),
                            url = p.optString("url", ""),
                            flag = p.optString("flag", "aerion"),
                            updatedAtUnix = p.optLong("updated_at_unix", 0L),
                            autoUpdateMins = p.optInt("auto_update_mins", 0),
                            nodeCount = p.optInt("node_count", 0),
                            ruleCount = p.optInt("rule_count", 0),
                            usedBytes = p.optLong("used_bytes", 0L),
                            totalBytes = p.optLong("total_bytes", 0L),
                            expireUnix = if (p.isNull("expire_unix")) null else p.optLong("expire_unix", 0L).takeIf { it > 0L },
                            active = p.optBoolean("active", false),
                        )
                    }
                },
                selectedNodeIndex = obj.optInt("selected_node_index", 0),
                nodeSortMode = obj.optString("node_sort_mode", "default"),
                nodesTesting = obj.optBoolean("nodes_testing", false),
                nodes = obj.optJSONArray("nodes").let { arr ->
                    if (arr == null) emptyList()
                    else (0 until arr.length()).mapNotNull { i ->
                        val n = arr.optJSONObject(i) ?: return@mapNotNull null
                        NetoNodeItem(
                            index = n.optInt("index", i),
                            name = n.optString("name", "Node ${i + 1}"),
                            protocol = n.optString("protocol", ""),
                            protocolLabel = n.optString("protocol_label", ""),
                            server = n.optString("server", ""),
                            port = n.optInt("port", 0),
                            udp = n.optBoolean("udp", true),
                            supported = n.optBoolean("supported", true),
                            tags = n.optJSONArray("tags").toStringList(),
                            latencyMs = if (n.isNull("latency_ms")) null else n.optLong("latency_ms", -1L).takeIf { it >= 0L },
                            lastError = if (n.isNull("last_error")) null else n.optString("last_error", "").takeIf { it.isNotBlank() },
                        )
                    }
                },
                proxyGroups = obj.optJSONArray("proxy_groups").let { arr ->
                    if (arr == null) emptyList()
                    else (0 until arr.length()).mapNotNull { i ->
                        val g = arr.optJSONObject(i) ?: return@mapNotNull null
                        NetoProxyGroup(
                            name = g.optString("name", ""),
                            kind = g.optString("kind", "select"),
                            proxies = g.optJSONArray("proxies").toStringList(),
                            selected = if (g.isNull("selected")) null else g.optString("selected", "").takeIf { it.isNotBlank() },
                        )
                    }
                },
                ruleCount = obj.optInt("rule_count", 0),
                rulesPreview = obj.optJSONArray("rules_preview").toStringList(),
                telemetry = NetoTelemetry(
                    uploadBytes = telObj.optLong("upload_bytes", 0L),
                    downloadBytes = telObj.optLong("download_bytes", 0L),
                    uploadSpeedBps = telObj.optLong("upload_speed_bps", 0L),
                    downloadSpeedBps = telObj.optLong("download_speed_bps", 0L),
                    uploadHistory = telObj.optJSONArray("upload_history").toLongList(),
                    downloadHistory = telObj.optJSONArray("download_history").toLongList(),
                    latencyMs = if (telObj.isNull("latency_ms")) null else telObj.optLong("latency_ms", -1L).takeIf { it >= 0L },
                    latencyHistory = telObj.optJSONArray("latency_history").toLongList(),
                    activeSessions = telObj.optInt("active_sessions", 0),
                    memoryBytes = telObj.optLong("memory_bytes", 0L),
                    intranetIpv4 = if (telObj.isNull("intranet_ipv4")) null else telObj.optString("intranet_ipv4", "").takeIf { it.isNotBlank() },
                    intranetIpv6 = if (telObj.isNull("intranet_ipv6")) null else telObj.optString("intranet_ipv6", "").takeIf { it.isNotBlank() },
                ),
            )
        }
    }
}

data class NetoLogEntry(
    val timestampMs: Long,
    val level: String,
    val message: String,
)

data class InstalledAppEntry(
    val packageName: String,
    val label: String,
    val systemApp: Boolean,
)

internal fun JSONArray?.toStringList(): List<String> {
    if (this == null) return emptyList()
    return (0 until length()).mapNotNull { optString(it, "").takeIf { s -> s.isNotBlank() } }
}

internal fun JSONArray?.toLongList(): List<Long> {
    if (this == null) return emptyList()
    return (0 until length()).map { optLong(it, 0L) }
}

fun formatNetoBytes(bytes: Long): String {
    val v = bytes.coerceAtLeast(0L)
    val kb = 1024.0
    val mb = kb * 1024.0
    val gb = mb * 1024.0
    return when {
        v >= gb.toLong() -> String.format(Locale.US, "%.2f GB", v / gb)
        v >= mb.toLong() -> String.format(Locale.US, "%.1f MB", v / mb)
        v >= kb.toLong() -> String.format(Locale.US, "%.1f KB", v / kb)
        else -> "$v B"
    }
}

fun formatNetoSpeed(bps: Long): String = "${formatNetoBytes(bps)}/s"

fun formatNetoDuration(durationMs: Long): String {
    val totalSec = (durationMs.coerceAtLeast(0L)) / 1000L
    val h = totalSec / 3600L
    val m = (totalSec % 3600L) / 60L
    val s = totalSec % 60L
    return String.format(Locale.US, "%02d:%02d:%02d", h, m, s)
}

fun formatNetoUnixDate(unixSec: Long): String {
    if (unixSec <= 0L) return "-"
    return SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault()).format(Date(unixSec * 1000L))
}
