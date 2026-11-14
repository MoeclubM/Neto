package moe.telecom.neto.sdk

import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.activity.compose.BackHandler
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
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
    updateRepo: String = "",
    appVersion: String = "",
    extraHeaderSlot: (@Composable () -> Unit)? = null,
    generalExtraSlot: (@Composable () -> Unit)? = null,
    extraFooterSlot: (@Composable () -> Unit)? = null,
) {
    val settings = snapshot.settings
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var subpage by rememberSaveable { mutableStateOf("root") }
    var checkingUpdate by remember { mutableStateOf(false) }
    var pendingUpdate by remember { mutableStateOf<NetoUpdateCheckResult?>(null) }
    var showUpdateDialog by remember { mutableStateOf(false) }

    BackHandler(enabled = subpage != "root") {
        subpage = "root"
    }

    val themeModes = listOf("system" to "跟随系统", "light" to "浅色", "dark" to "深色")
    val dnsModes = listOf("virtual" to "虚拟 DNS", "over_tcp" to "TCP 转发", "direct" to "直连 DNS")
    val dnsIdx = dnsModes.indexOfFirst { it.first == settings.vpnDnsMode }.coerceAtLeast(0)

    when (subpage) {
        "app_rules" -> NetoAppRulesSubpage(
            settings = settings,
            installedApps = installedApps,
            client = client,
            modifier = modifier,
            onBack = { subpage = "root" },
        )

        "dns_tun" -> LazyColumn(
            modifier = modifier.fillMaxSize(),
            contentPadding = PaddingValues(top = 4.dp, bottom = 28.dp),
            overscrollEffect = null,
        ) {
            item(key = "hdr") {
                NetoSubpageHeader(title = "DNS 与网络参数", onBack = { subpage = "root" })
            }
            item(key = "dns-card") {
                NetoPreferenceCard {
                    SwitchPreference(
                        title = "启用 IPv6",
                        checked = settings.ipv6Enabled,
                        onCheckedChange = { client.updateSettings(JSONObject().put("ipv6_enabled", it), restartIfConnected = true) },
                    )
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
                        title = "虚拟 DNS 网段",
                        value = settings.virtualDnsPool,
                        onConfirm = { v ->
                            if (v.isNotBlank()) {
                                client.updateSettings(JSONObject().put("virtual_dns_pool", v), restartIfConnected = true)
                            }
                        },
                    )
                    NetoEditPreference(
                        title = "连通测速地址",
                        value = settings.nodeTestUrl,
                        onConfirm = { client.updateSettings(JSONObject().put("node_test_url", it)) },
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
                }
            }
        }

        "logs" -> NetoLogsSubpage(
            logs = logs,
            client = client,
            modifier = modifier,
            onBack = { subpage = "root" },
        )

        else -> LazyColumn(
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
                SmallTitle("常规")
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
                            subpage = "app_rules"
                        },
                    )
                    if (generalExtraSlot != null) {
                        generalExtraSlot()
                    }
                }
            }

            item(key = "access") {
                SmallTitle("网络接入")
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
                }
            }

            item(key = "advanced") {
                SmallTitle("高级与诊断")
                NetoPreferenceCard {
                    ArrowPreference(
                        title = "DNS 与网络参数",
                        summary = "${dnsModes[dnsIdx].second} · IPv6 ${if (settings.ipv6Enabled) "开" else "关"}",
                        onClick = { subpage = "dns_tun" },
                    )
                    ArrowPreference(
                        title = "运行日志",
                        summary = if (logs.isEmpty()) "查看运行日志" else "最近 ${logs.size} 条记录",
                        onClick = {
                            client.refreshLogs()
                            subpage = "logs"
                        },
                    )
                }
            }

            if (updateRepo.isNotBlank() && appVersion.isNotBlank()) {
                item(key = "about-update") {
                    SmallTitle("关于")
                    NetoPreferenceCard {
                        ArrowPreference(
                            title = "检查更新",
                            summary = if (checkingUpdate) "正在检查…" else "当前版本 v${appVersion.removePrefix("v")}",
                            onClick = {
                                if (checkingUpdate) return@ArrowPreference
                                checkingUpdate = true
                                scope.launch {
                                    try {
                                        val res = client.checkUpdate(updateRepo, appVersion)
                                        if (res.hasUpdate) {
                                            pendingUpdate = res
                                            showUpdateDialog = true
                                        } else {
                                            Toast.makeText(context, "已是最新版本", Toast.LENGTH_SHORT).show()
                                        }
                                    } catch (err: Throwable) {
                                        Toast.makeText(context, "检查更新失败：${err.message}", Toast.LENGTH_SHORT).show()
                                    } finally {
                                        checkingUpdate = false
                                    }
                                }
                            },
                        )
                    }
                }
            }

            if (extraFooterSlot != null) {
                item(key = "extra-footer") {
                    extraFooterSlot()
                }
            }
        }
    }

    val updateInfo = pendingUpdate
    if (updateInfo != null) {
        OverlayDialog(
            show = showUpdateDialog,
            title = "发现新版本 ${updateInfo.latestVersion}",
            summary = buildString {
                append("当前版本 v${appVersion.removePrefix("v")} → 最新版本 ${updateInfo.latestVersion}")
                if (updateInfo.releaseNotes.isNotBlank()) {
                    append("\n\n")
                    append(updateInfo.releaseNotes)
                }
            },
            onDismissRequest = { showUpdateDialog = false },
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxWidth()) {
                TextButton(
                    text = "稍后",
                    onClick = { showUpdateDialog = false },
                    modifier = Modifier.weight(1f),
                )
                TextButton(
                    text = "下载更新",
                    onClick = {
                        showUpdateDialog = false
                        val target = updateInfo.downloadUrl.ifBlank { updateInfo.releaseUrl }
                        if (target.isNotBlank()) {
                            runCatching {
                                context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(target)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                            }
                        }
                    },
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.textButtonColorsPrimary(),
                )
            }
        }
    }
}

@Composable
private fun NetoAppRulesSubpage(
    settings: NetoSettings,
    installedApps: List<InstalledAppEntry>,
    client: NetoControllerClient,
    modifier: Modifier = Modifier,
    onBack: () -> Unit,
) {
    var search by rememberSaveable { mutableStateOf("") }
    var showSystem by rememberSaveable { mutableStateOf(false) }
    val selectedSet = if (settings.appRuleMode == "allow") settings.allowedApps.toSet() else settings.excludedApps.toSet()
    val filtered = installedApps.filter {
        (showSystem || !it.systemApp) &&
            (search.isBlank() || it.label.contains(search, ignoreCase = true) || it.packageName.contains(search, ignoreCase = true))
    }
    Column(modifier = modifier.fillMaxSize()) {
        NetoSubpageHeader(title = "分应用代理", onBack = onBack)
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp)) {
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
                label = "搜索应用名称或包名",
                useLabelAsPlaceholder = true,
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(6.dp))
        }
        NetoPreferenceCard {
            SwitchPreference(
                title = "显示系统应用",
                checked = showSystem,
                onCheckedChange = { showSystem = it },
            )
        }
        LazyColumn(
            modifier = Modifier.fillMaxWidth().weight(1f),
            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp),
            overscrollEffect = null,
        ) {
            items(filtered, key = { it.packageName }) { app ->
                val checked = selectedSet.contains(app.packageName)
                Card(modifier = Modifier.fillMaxWidth().padding(bottom = 6.dp)) {
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
        }
    }
}

@Composable
private fun NetoLogsSubpage(
    logs: List<NetoLogEntry>,
    client: NetoControllerClient,
    modifier: Modifier = Modifier,
    onBack: () -> Unit,
) {
    var level by rememberSaveable { mutableStateOf("all") }
    var keyword by rememberSaveable { mutableStateOf("") }

    LaunchedEffect(level, keyword) {
        client.refreshLogs(level = level, filter = keyword.trim(), limit = 200)
    }

    Column(modifier = modifier.fillMaxSize()) {
        NetoSubpageHeader(
            title = "运行日志",
            onBack = onBack,
            trailing = {
                TextButton(text = "清空", onClick = client::clearLogs)
            },
        )
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxWidth()) {
                listOf("all" to "全部", "info" to "INFO", "warn" to "WARN", "error" to "ERR").forEach { (k, label) ->
                    Button(
                        onClick = { level = k },
                        modifier = Modifier.weight(1f),
                        colors = if (level == k) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                    ) {
                        Text(label)
                    }
                }
            }
            Spacer(Modifier.height(8.dp))
            TextField(
                value = keyword,
                onValueChange = { keyword = it },
                label = "筛选日志内容",
                useLabelAsPlaceholder = true,
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
        }
        Card(
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f)
                .padding(horizontal = 12.dp)
                .padding(bottom = 14.dp),
            insideMargin = PaddingValues(12.dp),
        ) {
            if (logs.isEmpty()) {
                Text("暂无日志", color = MiuixTheme.colorScheme.onSurfaceVariantSummary)
            } else {
                LazyColumn(modifier = Modifier.fillMaxSize(), overscrollEffect = null) {
                    items(logs.takeLast(160)) { entry ->
                        Text(
                            "[${entry.level.uppercase()}] ${entry.message}",
                            style = MiuixTheme.textStyles.body2,
                            fontFamily = FontFamily.Monospace,
                            modifier = Modifier.padding(vertical = 2.dp),
                        )
                    }
                }
            }
        }
    }
}
