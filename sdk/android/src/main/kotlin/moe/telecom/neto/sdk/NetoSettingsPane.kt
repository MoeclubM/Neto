package moe.telecom.neto.sdk

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import org.json.JSONArray
import org.json.JSONObject
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.SmallTitle
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.overlay.OverlayDialog
import top.yukonga.miuix.kmp.preference.ArrowPreference
import top.yukonga.miuix.kmp.preference.CheckboxPreference
import top.yukonga.miuix.kmp.preference.OverlayDropdownPreference
import top.yukonga.miuix.kmp.preference.SwitchPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme

@Composable
fun NetoSettingsPane(
    snapshot: NetoSnapshot,
    logs: List<NetoLogEntry>,
    installedApps: List<InstalledAppEntry>,
    client: NetoControllerClient,
    modifier: Modifier = Modifier,
    extraHeaderSlot: (@Composable () -> Unit)? = null,
    extraFooterSlot: (@Composable () -> Unit)? = null,
) {
    val settings = snapshot.settings
    var showAppRules by rememberSaveable { mutableStateOf(false) }
    var showConnections by rememberSaveable { mutableStateOf(false) }
    var showRulesDialog by rememberSaveable { mutableStateOf(false) }
    var showLogs by rememberSaveable { mutableStateOf(false) }
    val checkingConnectivity by client.checkingConnectivity.collectAsStateWithLifecycle()
    var customRouteDraft by rememberSaveable(settings.customRouteYaml) { mutableStateOf(settings.customRouteYaml) }

    val themeModes = listOf("system" to "跟随系统", "light" to "浅色", "dark" to "深色")
    val dnsModes = listOf("virtual" to "虚拟 DNS", "over_tcp" to "TCP 转发", "direct" to "直连 DNS")

    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(top = 8.dp, bottom = 28.dp),
        overscrollEffect = null,
    ) {
        if (extraHeaderSlot != null) {
            item(key = "extra-header") {
                extraHeaderSlot()
            }
        }

        item(key = "general") {
            SmallTitle("界面与分流")
            NetoPreferenceCard {
                val themeIdx = themeModes.indexOfFirst { it.first == settings.themeMode }.coerceAtLeast(0)
                OverlayDropdownPreference(
                    title = "外观主题",
                    summary = themeModes[themeIdx].second,
                    items = themeModes.map { it.second },
                    selectedIndex = themeIdx,
                    onSelectedIndexChange = { idx ->
                        client.updateSettings(JSONObject().put("theme_mode", themeModes[idx].first))
                    },
                )
                val appCount = if (settings.appRuleMode == "allow") settings.allowedApps.size else settings.excludedApps.size
                ArrowPreference(
                    title = "分应用代理",
                    summary = if (appCount == 0) "默认接管全部应用" else "已选 $appCount 个应用 (${if (settings.appRuleMode == "allow") "仅代理" else "绕过"})",
                    onClick = {
                        client.loadInstalledAppsIfNeeded()
                        showAppRules = true
                    },
                )
            }
        }

        item(key = "network") {
            SmallTitle("网络与 DNS")
            NetoPreferenceCard {
                NetoEditPreference(
                    title = "本地混合端口",
                    value = settings.mixedPort.toString(),
                    onConfirm = { v ->
                        v.toIntOrNull()?.takeIf { it in 1024..65535 }?.let {
                            client.updateSettings(JSONObject().put("mixed_port", it), restartIfConnected = true)
                        }
                    },
                )
                SwitchPreference(
                    title = "允许局域网连接",
                    checked = settings.allowLan,
                    onCheckedChange = { client.updateSettings(JSONObject().put("allow_lan", it), restartIfConnected = true) },
                )
                SwitchPreference(
                    title = "启用 IPv6",
                    checked = settings.ipv6Enabled,
                    onCheckedChange = { client.updateSettings(JSONObject().put("ipv6_enabled", it), restartIfConnected = true) },
                )
                val dnsIdx = dnsModes.indexOfFirst { it.first == settings.vpnDnsMode }.coerceAtLeast(0)
                OverlayDropdownPreference(
                    title = "DNS 模式",
                    summary = dnsModes[dnsIdx].second,
                    items = dnsModes.map { it.second },
                    selectedIndex = dnsIdx,
                    onSelectedIndexChange = { idx ->
                        client.updateSettings(JSONObject().put("vpn_dns_mode", dnsModes[idx].first), restartIfConnected = true)
                    },
                )
                NetoEditPreference(
                    title = "虚拟 DNS 地址池",
                    value = settings.virtualDnsPool,
                    onConfirm = { v ->
                        if (v.isNotBlank()) {
                            client.updateSettings(JSONObject().put("virtual_dns_pool", v), restartIfConnected = true)
                        }
                    },
                )
                NetoEditPreference(
                    title = "节点解析 DoH",
                    value = settings.nodeDns,
                    onConfirm = { client.updateSettings(JSONObject().put("node_dns", it)) },
                )
                NetoEditPreference(
                    title = "代理 DNS",
                    value = settings.overseasDns,
                    onConfirm = { client.updateSettings(JSONObject().put("overseas_dns", it), restartIfConnected = true) },
                )
                NetoEditPreference(
                    title = "直连 DNS",
                    value = settings.directDns,
                    onConfirm = { client.updateSettings(JSONObject().put("direct_dns", it), restartIfConnected = true) },
                )
                NetoEditPreference(
                    title = "连通测速地址",
                    value = settings.nodeTestUrl,
                    onConfirm = { client.updateSettings(JSONObject().put("node_test_url", it)) },
                )
            }
        }

        item(key = "rules") {
            SmallTitle("自定义路由规则")
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp)
                    .padding(bottom = 10.dp),
                insideMargin = PaddingValues(14.dp),
            ) {
                TextField(
                    value = customRouteDraft,
                    onValueChange = { customRouteDraft = it },
                    label = "留空则使用当前订阅规则（已加载 ${snapshot.ruleCount} 条）",
                    useLabelAsPlaceholder = true,
                    minLines = 4,
                    maxLines = 8,
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                    Button(
                        onClick = {
                            client.updateSettings(JSONObject().put("custom_route_yaml", customRouteDraft), restartIfConnected = true)
                        },
                        modifier = Modifier.weight(1f),
                        colors = ButtonDefaults.buttonColorsPrimary(),
                    ) {
                        Text("保存规则")
                    }
                    Button(
                        onClick = {
                            customRouteDraft = ""
                            client.updateSettings(JSONObject().put("custom_route_yaml", ""), restartIfConnected = true)
                        },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text("恢复订阅默认")
                    }
                }
            }
        }

        item(key = "logs") {
            SmallTitle("诊断")
            val checkRes = snapshot.lastNetworkCheck
            val checkSummary = when {
                checkingConnectivity -> "检测中..."
                checkRes == null -> "未检测"
                else -> {
                    val ip = listOfNotNull(checkRes.exitIp, checkRes.exitCountry, checkRes.exitIsp).joinToString(" · ")
                    val delays = checkRes.targets.joinToString(" · ") { t -> t.latencyMs?.let { "${t.name} ${it}ms" } ?: "${t.name} 超时" }
                    listOf(ip, delays).filter { it.isNotBlank() }.joinToString(" | ")
                }
            }
            NetoPreferenceCard {
                ArrowPreference(
                    title = "活跃会话",
                    summary = "并发 ${snapshot.telemetry.activeSessions} · ↑ ${formatNetoBytes(snapshot.telemetry.uploadBytes)} ↓ ${formatNetoBytes(snapshot.telemetry.downloadBytes)}",
                    onClick = { showConnections = true },
                )
                ArrowPreference(
                    title = "分流规则与匹配",
                    summary = "共 ${snapshot.ruleCount} 条规则",
                    onClick = { showRulesDialog = true },
                )
                ArrowPreference(
                    title = "出口检测",
                    summary = checkSummary,
                    onClick = client::checkConnectivity,
                )
                ArrowPreference(
                    title = "运行日志",
                    summary = if (logs.isEmpty()) "无记录" else "${logs.size} 条记录",
                    onClick = {
                        client.refreshLogs()
                        showLogs = true
                    },
                )
            }
        }

        if (extraFooterSlot != null) {
            item(key = "extra-footer") {
                extraFooterSlot()
            }
        }
    }

    if (showAppRules) {
        NetoAppRulesDialog(
            settings = settings,
            installedApps = installedApps,
            client = client,
            onDismiss = { showAppRules = false },
        )
    }

    if (showConnections) {
        NetoActiveConnectionsDialog(
            client = client,
            onDismiss = { showConnections = false },
        )
    }

    if (showRulesDialog) {
        NetoRulesAndRouteTestDialog(
            client = client,
            onDismiss = { showRulesDialog = false },
        )
    }

    if (showLogs) {
        NetoLogsDialog(
            logs = logs,
            client = client,
            onDismiss = { showLogs = false },
        )
    }
}

@Composable
private fun NetoAppRulesDialog(
    settings: NetoSettings,
    installedApps: List<InstalledAppEntry>,
    client: NetoControllerClient,
    onDismiss: () -> Unit,
) {
    var search by rememberSaveable { mutableStateOf("") }
    var showSystem by rememberSaveable { mutableStateOf(false) }
    val selectedSet = if (settings.appRuleMode == "allow") settings.allowedApps.toSet() else settings.excludedApps.toSet()
    val filtered = installedApps.filter {
        (showSystem || !it.systemApp) &&
            (search.isBlank() || it.label.contains(search, ignoreCase = true) || it.packageName.contains(search, ignoreCase = true))
    }
    OverlayDialog(
        show = true,
        title = "分应用代理",
        onDismissRequest = onDismiss,
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                listOf("exclude" to "绕过选中", "allow" to "仅代理选中").forEach { (m, label) ->
                    Button(
                        onClick = { client.updateSettings(JSONObject().put("app_rule_mode", m), restartIfConnected = true) },
                        modifier = Modifier.weight(1f),
                        colors = if (settings.appRuleMode == m) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                    ) {
                        Text(label)
                    }
                }
            }
            Spacer(Modifier.height(8.dp))
            TextField(
                value = search,
                onValueChange = { search = it },
                label = "搜索应用",
                useLabelAsPlaceholder = true,
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            SwitchPreference(
                title = "显示系统应用",
                checked = showSystem,
                onCheckedChange = { showSystem = it },
            )
            LazyColumn(modifier = Modifier.fillMaxWidth().height(260.dp)) {
                items(filtered, key = { it.packageName }) { app ->
                    val checked = selectedSet.contains(app.packageName)
                    CheckboxPreference(
                        title = app.label,
                        summary = app.packageName,
                        checked = checked,
                        onCheckedChange = { on ->
                            val next = selectedSet.toMutableSet().apply {
                                if (on) add(app.packageName) else remove(app.packageName)
                            }.toList()
                            val field = if (settings.appRuleMode == "allow") "allowed_apps" else "excluded_apps"
                            client.updateSettings(JSONObject().put(field, JSONArray(next)), restartIfConnected = true)
                        },
                    )
                }
            }
            Spacer(Modifier.height(10.dp))
            Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
                TextButton(text = "完成", onClick = onDismiss, colors = ButtonDefaults.textButtonColorsPrimary())
            }
        }
    }
}
