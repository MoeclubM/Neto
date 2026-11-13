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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.LinearProgressIndicator
import top.yukonga.miuix.kmp.basic.SmallTitle
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.preference.ArrowPreference
import top.yukonga.miuix.kmp.preference.SwitchPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme

@Composable
fun NetoDashboardPane(
    snapshot: NetoSnapshot,
    client: NetoControllerClient,
    onOpenNodes: () -> Unit,
    onOpenProfiles: (() -> Unit)? = null,
    modifier: Modifier = Modifier,
    extraHeaderSlot: (@Composable () -> Unit)? = null,
) {
    var customizing by rememberSaveable { mutableStateOf(false) }
    var showConnections by rememberSaveable { mutableStateOf(false) }
    val checkingConnectivity by client.checkingConnectivity.collectAsStateWithLifecycle()
    val widgets = snapshot.settings.dashboardWidgets

    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(top = 8.dp, bottom = 24.dp),
        overscrollEffect = null,
    ) {
        if (extraHeaderSlot != null) {
            item(key = "extra-header") {
                extraHeaderSlot()
            }
        }

        item(key = "toolbar") {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp, vertical = 4.dp),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (customizing) {
                    TextButton(
                        text = "恢复默认",
                        onClick = client::resetDashboardWidgets,
                    )
                }
                TextButton(
                    text = if (customizing) "完成" else "自定义",
                    onClick = { customizing = !customizing },
                    colors = if (customizing) ButtonDefaults.textButtonColorsPrimary() else ButtonDefaults.textButtonColors(),
                )
            }
        }

        if (customizing) {
            item(key = "customize-panel") {
                NetoPreferenceCard {
                    widgets.forEachIndexed { index, item ->
                        SwitchPreference(
                            title = item.titleZh,
                            checked = item.enabled,
                            onCheckedChange = { client.updateDashboardWidget(item.id, enabled = it) },
                            endActions = {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    IconButton(
                                        onClick = { client.updateDashboardWidget(item.id, moveDelta = -1) },
                                        enabled = index > 0,
                                        modifier = Modifier.size(32.dp),
                                    ) {
                                        Text("↑")
                                    }
                                    IconButton(
                                        onClick = { client.updateDashboardWidget(item.id, moveDelta = 1) },
                                        enabled = index + 1 < widgets.size,
                                        modifier = Modifier.size(32.dp),
                                    ) {
                                        Text("↓")
                                    }
                                }
                            },
                        )
                    }
                }
            }
        }

        items(widgets.filter { it.enabled }, key = { it.id }) { widget ->
            when (widget.id) {
                "network_speed" -> {
                    SmallTitle("实时速率")
                    NetoSpeedChartCard(
                        upload = snapshot.telemetry.uploadHistory,
                        download = snapshot.telemetry.downloadHistory,
                        upBps = snapshot.telemetry.uploadSpeedBps,
                        downBps = snapshot.telemetry.downloadSpeedBps,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 12.dp)
                            .padding(bottom = 10.dp),
                    )
                }
                "outbound_mode" -> {
                    SmallTitle("出站模式")
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 12.dp)
                            .padding(bottom = 10.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        listOf("rule" to "规则", "global" to "全局", "direct" to "直连").forEach { (mode, label) ->
                            val active = snapshot.settings.routingMode == mode
                            Button(
                                onClick = { client.setRoutingMode(mode) },
                                modifier = Modifier.weight(1f),
                                colors = if (active) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                            ) {
                                Text(label)
                            }
                        }
                    }
                }
                "current_outbound" -> {
                    SmallTitle("当前节点")
                    val node = snapshot.selectedNode
                    NetoPreferenceCard {
                        ArrowPreference(
                            title = node?.name ?: "暂无可用节点",
                            summary = node?.let {
                                listOfNotNull(it.protocolLabel, it.latencyMs?.let { ms -> "$ms ms" }).joinToString(" · ")
                            } ?: "未选择",
                            onClick = onOpenNodes,
                        )
                    }
                }
                "active_connections" -> {
                    SmallTitle("活跃会话")
                    NetoPreferenceCard {
                        ArrowPreference(
                            title = "${snapshot.telemetry.activeSessions} 个并发会话",
                            summary = "累计 ↑ ${formatNetoBytes(snapshot.telemetry.uploadBytes)} · ↓ ${formatNetoBytes(snapshot.telemetry.downloadBytes)}",
                            onClick = { showConnections = true },
                            endActions = {
                                if (snapshot.connection.connected && snapshot.telemetry.activeSessions > 0) {
                                    TextButton(text = "断开全部", onClick = client::closeAllConnections)
                                }
                            },
                        )
                    }
                }
                "exit_check" -> {
                    SmallTitle("出口检测")
                    val checkRes = snapshot.lastNetworkCheck
                    val checkTitle = if (checkRes == null) {
                        "未检测"
                    } else {
                        listOfNotNull(checkRes.exitIp, checkRes.exitCountry, checkRes.exitIsp)
                            .joinToString(" · ")
                            .ifBlank { "未识别出口 IP" }
                    }
                    val checkSummary = if (checkRes == null) {
                        "检测出口 IP 与站点延迟"
                    } else {
                        checkRes.targets.joinToString(" · ") { t ->
                            t.latencyMs?.let { "${t.name} ${it}ms" } ?: "${t.name} 超时"
                        }
                    }
                    NetoPreferenceCard {
                        ArrowPreference(
                            title = checkTitle,
                            summary = checkSummary,
                            onClick = client::checkConnectivity,
                            endActions = {
                                TextButton(
                                    text = if (checkingConnectivity) "检测中" else "检测",
                                    onClick = client::checkConnectivity,
                                )
                            },
                        )
                    }
                }
                "network_takeover" -> {
                    SmallTitle("网络接管")
                    NetoPreferenceCard {
                        val status = if (snapshot.connection.connected) "TUN 运行中" else "TUN 待机"
                        val portText = snapshot.connection.httpProxyAddr.ifBlank { "127.0.0.1:${snapshot.settings.mixedPort}" }
                        ArrowPreference(
                            title = status,
                            summary = "混合代理 $portText · DNS ${snapshot.settings.vpnDnsMode} · MTU ${snapshot.settings.mtu}",
                        )
                    }
                }
                "traffic_usage" -> {
                    SmallTitle("流量统计")
                    Card(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 12.dp)
                            .padding(bottom = 10.dp),
                        insideMargin = PaddingValues(14.dp),
                    ) {
                        val sessionTotal = snapshot.telemetry.uploadBytes + snapshot.telemetry.downloadBytes
                        Text(
                            "会话流量 ${formatNetoBytes(sessionTotal)}",
                            fontWeight = FontWeight.Medium,
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            "上传 ${formatNetoBytes(snapshot.telemetry.uploadBytes)} · 下载 ${formatNetoBytes(snapshot.telemetry.downloadBytes)}",
                            style = MiuixTheme.textStyles.body2,
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        )
                        val prof = snapshot.activeProfile
                        if (prof != null && prof.totalBytes > 0L) {
                            Spacer(Modifier.height(10.dp))
                            Text(
                                "配额 ${formatNetoBytes(prof.usedBytes)} / ${formatNetoBytes(prof.totalBytes)}",
                                style = MiuixTheme.textStyles.body2,
                            )
                            Spacer(Modifier.height(6.dp))
                            LinearProgressIndicator(
                                progress = (prof.usedBytes.toFloat() / prof.totalBytes.toFloat()).coerceIn(0f, 1f),
                                modifier = Modifier.fillMaxWidth(),
                            )
                        }
                    }
                }
                "profiles_summary" -> {
                    SmallTitle("订阅概览")
                    val prof = snapshot.activeProfile
                    NetoPreferenceCard {
                        ArrowPreference(
                            title = prof?.name ?: "未配置订阅",
                            summary = prof?.let {
                                "${it.nodeCount} 节点 · ${it.ruleCount} 规则 · 更新于 ${formatNetoUnixDate(it.updatedAtUnix)}"
                            } ?: "暂无订阅",
                            onClick = { onOpenProfiles?.invoke() },
                            endActions = {
                                if (prof != null && prof.url.isNotBlank()) {
                                    IconButton(
                                        onClick = { client.refreshProfile(prof.id) },
                                        modifier = Modifier.size(34.dp),
                                    ) {
                                        Text("↻")
                                    }
                                }
                            },
                        )
                    }
                }
                "latency_probe" -> {
                    SmallTitle("连通延迟")
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 12.dp)
                            .padding(bottom = 10.dp),
                    ) {
                        NetoLatencyChartCard(
                            samples = snapshot.telemetry.latencyHistory,
                            currentMs = snapshot.telemetry.latencyMs,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        if (snapshot.connection.connected) {
                            Spacer(Modifier.height(6.dp))
                            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                                TextButton(text = "立即探测", onClick = client::probeTunnelNow)
                            }
                        }
                    }
                }
                "runtime_info" -> {
                    SmallTitle("运行信息")
                    val ips = listOfNotNull(snapshot.telemetry.intranetIpv4, snapshot.telemetry.intranetIpv6)
                        .joinToString(" · ")
                        .ifBlank { "-" }
                    NetoPreferenceCard {
                        ArrowPreference(
                            title = "内网地址 $ips",
                            summary = "活跃会话 ${snapshot.telemetry.activeSessions} · 内存 ${formatNetoBytes(snapshot.telemetry.memoryBytes)}",
                        )
                    }
                }
            }
        }
    }

    if (showConnections) {
        NetoActiveConnectionsDialog(
            client = client,
            onDismiss = { showConnections = false },
        )
    }
}
