package moe.telecom.neto.sdk

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.delay
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.preference.ArrowPreference
import top.yukonga.miuix.kmp.preference.SwitchPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun NetoHomePane(
    snapshot: NetoSnapshot,
    client: NetoControllerClient,
    onToggleConnect: () -> Unit,
    onOpenNodes: () -> Unit,
    onOpenProfiles: (() -> Unit)? = null,
    onOpenConnections: (() -> Unit)? = null,
    actionError: String? = null,
    applyStatusBarPadding: Boolean = true,
    showProfilesSummaryWidget: Boolean = true,
    modifier: Modifier = Modifier,
    extraHeaderSlot: (@Composable () -> Unit)? = null,
    blockedContent: (@Composable () -> Unit)? = null,
) {
    var customizing by rememberSaveable { mutableStateOf(false) }
    val checkingConnectivity by client.checkingConnectivity.collectAsStateWithLifecycle()
    val nowMs by produceState(initialValue = System.currentTimeMillis(), key1 = snapshot.connection.connected) {
        while (snapshot.connection.connected) {
            value = System.currentTimeMillis()
            delay(1000L)
        }
    }

    val rootModifier = if (applyStatusBarPadding) {
        modifier.fillMaxSize().statusBarsPadding()
    } else {
        modifier.fillMaxSize()
    }

    LazyColumn(
        modifier = rootModifier,
        contentPadding = PaddingValues(top = 8.dp, bottom = 24.dp),
        overscrollEffect = null,
    ) {
        if (extraHeaderSlot != null) {
            item(key = "extra-header") {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(bottom = 8.dp),
                ) {
                    extraHeaderSlot()
                }
            }
        }

        if (blockedContent != null) {
            item(key = "blocked") {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp),
                ) {
                    blockedContent()
                }
            }
            return@LazyColumn
        }

        item(key = "conn-card") {
            val conn = snapshot.connection
            val dotColor = when {
                conn.connected -> Color(0xFF2AA876)
                conn.connecting -> Color(0xFFE09F3E)
                else -> MiuixTheme.colorScheme.outline
            }
            val statusText = when {
                conn.connected -> "已连接"
                conn.connecting -> "连接中"
                else -> "未连接"
            }
            val elapsedMs = if (conn.connected && conn.connectedAtMs > 0L) {
                (nowMs - conn.connectedAtMs).coerceAtLeast(0L)
            } else {
                0L
            }
            val modeLabel = when (snapshot.settings.routingMode.lowercase()) {
                "global" -> "全局代理"
                "direct" -> "直连模式"
                else -> "规则分流"
            }
            val detailText = if (conn.connected) {
                val activeLabel = conn.activeNodeName.ifBlank { snapshot.effectiveRuleLeafName.orEmpty() }
                if (activeLabel.isNotBlank() && !snapshot.settings.routingMode.equals("direct", ignoreCase = true)) {
                    "$modeLabel · $activeLabel"
                } else {
                    modeLabel
                }
            } else {
                modeLabel
            }

            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp)
                    .padding(bottom = 8.dp),
                insideMargin = PaddingValues(horizontal = 14.dp, vertical = 12.dp),
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(
                        modifier = Modifier.weight(1f).padding(end = 12.dp),
                        verticalArrangement = Arrangement.spacedBy(2.dp),
                    ) {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                        ) {
                            Box(
                                modifier = Modifier
                                    .size(9.dp)
                                    .clip(CircleShape)
                                    .background(dotColor),
                            )
                            Text(
                                text = statusText,
                                fontWeight = FontWeight.SemiBold,
                            )
                        }
                        Text(
                            text = formatNetoDuration(elapsedMs),
                            style = MiuixTheme.textStyles.title3,
                            fontFamily = FontFamily.Monospace,
                        )
                        Text(
                            text = detailText,
                            style = MiuixTheme.textStyles.body2,
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    Button(
                        onClick = onToggleConnect,
                        enabled = !conn.connecting && (conn.connected || snapshot.nodes.isNotEmpty() || snapshot.settings.routingMode.equals("direct", ignoreCase = true)),
                        colors = if (conn.connected) ButtonDefaults.buttonColors() else ButtonDefaults.buttonColorsPrimary(),
                    ) {
                        Text(
                            when {
                                conn.connecting -> "连接中"
                                conn.connected -> "断开"
                                else -> "连接"
                            }
                        )
                    }
                }
            }
        }

        item(key = "node-selector") {
            val isDirect = snapshot.settings.routingMode.equals("direct", ignoreCase = true)
            val isRule = snapshot.settings.routingMode.equals("rule", ignoreCase = true)
            val primaryGroup = if (isRule) snapshot.primarySelectGroup else null
            val leafName = if (isRule) snapshot.effectiveRuleLeafName else snapshot.selectedNode?.name
            val leafNode = leafName?.let { name -> snapshot.nodes.firstOrNull { it.name == name } } ?: snapshot.selectedNode
            val selectorTitle = when {
                isDirect -> "直连模式"
                primaryGroup != null -> {
                    val directPick = primaryGroup.now ?: primaryGroup.selected ?: ""
                    if (directPick.isNotBlank() && !leafName.isNullOrBlank() && directPick != leafName) {
                        "$directPick · $leafName"
                    } else {
                        directPick.ifBlank { leafNode?.name ?: "选择节点" }
                    }
                }
                else -> leafNode?.name ?: "选择节点"
            }
            val summary = when {
                isDirect -> "流量不经过代理节点，点击可管理节点"
                snapshot.nodes.isEmpty() -> "暂无可用节点"
                leafName.equals("DIRECT", ignoreCase = true) -> "${primaryGroup?.name ?: "策略组"} · 直接连接"
                leafName.equals("REJECT", ignoreCase = true) -> "${primaryGroup?.name ?: "策略组"} · 拦截连接"
                leafNode != null -> buildString {
                    if (primaryGroup != null) append("${primaryGroup.name} · ")
                    append(leafNode.protocolLabel.ifBlank { leafNode.protocol.uppercase() })
                    leafNode.latencyMs?.let { append(" · $it ms") }
                }
                else -> "点击选择节点或策略组"
            }
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp)
                    .padding(bottom = 8.dp),
            ) {
                ArrowPreference(
                    title = selectorTitle,
                    summary = summary,
                    onClick = onOpenNodes,
                )
            }
        }

        item(key = "routing-mode") {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp)
                    .padding(bottom = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                listOf("rule" to "规则", "global" to "全局", "direct" to "直连").forEach { (mode, label) ->
                    val active = snapshot.settings.routingMode.equals(mode, ignoreCase = true)
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

        val errorText = actionError?.takeIf { it.isNotBlank() }
            ?: snapshot.connection.lastError?.takeIf { it.isNotBlank() }
        if (errorText != null) {
            item(key = "error-banner") {
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp)
                        .padding(bottom = 8.dp),
                    insideMargin = PaddingValues(12.dp),
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            text = errorText,
                            style = MiuixTheme.textStyles.body2,
                            color = Color(0xFFD8524C),
                            modifier = Modifier.weight(1f),
                        )
                        if (actionError != null) {
                            TextButton(text = "关闭", onClick = client::clearError)
                        }
                    }
                }
            }
        }

        val allWidgets = snapshot.settings.dashboardWidgets.filter {
            showProfilesSummaryWidget || it.id != "profiles_summary"
        }

        item(key = "widgets-toolbar") {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp, vertical = 2.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = "状态概览",
                    style = MiuixTheme.textStyles.body2,
                    color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (customizing) {
                        TextButton(
                            text = "恢复默认",
                            onClick = client::resetDashboardWidgets,
                        )
                    }
                    TextButton(
                        text = if (customizing) "完成" else "自定义",
                        onClick = { customizing = !customizing },
                    )
                }
            }
        }

        if (customizing) {
            item(key = "customize-panel") {
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp)
                        .padding(bottom = 8.dp),
                ) {
                    allWidgets.forEachIndexed { idx, w ->
                        Row(
                            modifier = Modifier.fillMaxWidth().padding(end = 8.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Box(modifier = Modifier.weight(1f)) {
                                SwitchPreference(
                                    title = w.titleZh,
                                    checked = w.enabled,
                                    onCheckedChange = { on ->
                                        client.updateDashboardWidget(w.id, enabled = on)
                                    },
                                )
                            }
                            if (idx > 0) {
                                TextButton(
                                    text = "↑",
                                    onClick = { client.updateDashboardWidget(w.id, moveDelta = -1) },
                                )
                            }
                            if (idx < allWidgets.lastIndex) {
                                TextButton(
                                    text = "↓",
                                    onClick = { client.updateDashboardWidget(w.id, moveDelta = 1) },
                                )
                            }
                        }
                    }
                }
            }
        }

        val enabledWidgets = allWidgets.filter { it.enabled }
        enabledWidgets.forEach { w ->
            item(key = "widget-${w.id}") {
                val cardMod = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp)
                    .padding(bottom = 8.dp)

                when (w.id) {
                    "network_speed" -> NetoSpeedChartCard(
                        upload = snapshot.telemetry.uploadHistory,
                        download = snapshot.telemetry.downloadHistory,
                        upBps = snapshot.telemetry.uploadSpeedBps,
                        downBps = snapshot.telemetry.downloadSpeedBps,
                        title = "实时速率",
                        modifier = cardMod,
                    )

                    "latency_probe" -> NetoLatencyChartCard(
                        samples = snapshot.telemetry.latencyHistory,
                        currentMs = snapshot.telemetry.latencyMs,
                        title = "连通延迟",
                        trailing = {
                            TextButton(text = "探测", onClick = client::probeTunnelNow)
                        },
                        modifier = cardMod,
                    )

                    "active_connections" -> Card(
                        modifier = cardMod,
                        insideMargin = PaddingValues(12.dp),
                    ) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(
                                text = "活跃会话",
                                style = MiuixTheme.textStyles.body2,
                                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                            )
                            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                                if (snapshot.connection.connected) {
                                    TextButton(text = "断开全部", onClick = client::closeAllConnections)
                                }
                                if (onOpenConnections != null) {
                                    TextButton(text = "查看", onClick = onOpenConnections)
                                }
                            }
                        }
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = "${snapshot.telemetry.activeSessions} 个并发会话",
                            fontWeight = FontWeight.SemiBold,
                        )
                        Text(
                            text = "累计 ↑ ${formatNetoBytes(snapshot.telemetry.uploadBytes)} · ↓ ${formatNetoBytes(snapshot.telemetry.downloadBytes)}",
                            style = MiuixTheme.textStyles.body2,
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        )
                    }

                    "exit_check" -> Card(
                        modifier = cardMod,
                        insideMargin = PaddingValues(12.dp),
                    ) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(
                                text = "出口检测",
                                style = MiuixTheme.textStyles.body2,
                                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                            )
                            TextButton(
                                text = if (checkingConnectivity) "检测中..." else "检测",
                                onClick = client::checkConnectivity,
                            )
                        }
                        Spacer(Modifier.height(4.dp))
                        val checkRes = snapshot.lastNetworkCheck
                        if (checkRes == null) {
                            Text(text = "未检测", fontWeight = FontWeight.SemiBold)
                            Text(
                                text = "检测出口 IP 与站点连通延迟",
                                style = MiuixTheme.textStyles.body2,
                                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                            )
                        } else {
                            val ipLine = listOfNotNull(checkRes.exitIp, checkRes.exitCountry, checkRes.exitIsp)
                                .filter { it.isNotBlank() }
                                .joinToString(" · ")
                            Text(
                                text = ipLine.ifBlank { "未识别出口 IP" },
                                fontWeight = FontWeight.SemiBold,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            if (checkRes.targets.isNotEmpty()) {
                                Spacer(Modifier.height(6.dp))
                                FlowRow(
                                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                                    verticalArrangement = Arrangement.spacedBy(6.dp),
                                ) {
                                    checkRes.targets.forEach { t ->
                                        val tint = when {
                                            !t.ok || t.latencyMs == null -> Color(0xFFD8524C)
                                            t.latencyMs < 200L -> Color(0xFF2AA876)
                                            t.latencyMs < 500L -> Color(0xFFE09F3E)
                                            else -> Color(0xFFD8524C)
                                        }
                                        NetoInlineBadge(
                                            text = t.latencyMs?.let { "${t.name} ${it}ms" } ?: "${t.name} 超时",
                                            color = tint,
                                        )
                                    }
                                }
                            }
                        }
                    }

                    "traffic_usage" -> Card(
                        modifier = cardMod,
                        insideMargin = PaddingValues(12.dp),
                    ) {
                        val totalSession = snapshot.telemetry.uploadBytes + snapshot.telemetry.downloadBytes
                        Text(
                            text = "流量统计",
                            style = MiuixTheme.textStyles.body2,
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = "会话流量 ${formatNetoBytes(totalSession)}",
                            fontWeight = FontWeight.SemiBold,
                        )
                        Text(
                            text = "上传 ${formatNetoBytes(snapshot.telemetry.uploadBytes)} · 下载 ${formatNetoBytes(snapshot.telemetry.downloadBytes)}",
                            style = MiuixTheme.textStyles.body2,
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        )
                        val prof = snapshot.activeProfile
                        if (showProfilesSummaryWidget && prof != null && prof.totalBytes > 0L) {
                            val frac = (prof.usedBytes.toFloat() / prof.totalBytes.toFloat()).coerceIn(0f, 1f)
                            Spacer(Modifier.height(6.dp))
                            Text(
                                text = "配额 ${formatNetoBytes(prof.usedBytes)} / ${formatNetoBytes(prof.totalBytes)}",
                                style = MiuixTheme.textStyles.body2,
                            )
                            Spacer(Modifier.height(4.dp))
                            Box(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .height(6.dp)
                                    .clip(RoundedCornerShape(3.dp))
                                    .background(MiuixTheme.colorScheme.outline.copy(alpha = 0.2f)),
                            ) {
                                Box(
                                    modifier = Modifier
                                        .fillMaxWidth(frac)
                                        .height(6.dp)
                                        .clip(RoundedCornerShape(3.dp))
                                        .background(MiuixTheme.colorScheme.primary),
                                )
                            }
                        }
                    }

                    "profiles_summary" -> {
                        val prof = snapshot.activeProfile
                        Card(
                            modifier = cardMod,
                            insideMargin = PaddingValues(12.dp),
                        ) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text(
                                    text = "订阅概览",
                                    style = MiuixTheme.textStyles.body2,
                                    color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                                )
                                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                                    if (prof != null && prof.url.isNotBlank()) {
                                        TextButton(text = "刷新", onClick = { client.refreshProfile(prof.id) })
                                    }
                                    if (onOpenProfiles != null) {
                                        TextButton(text = "管理", onClick = onOpenProfiles)
                                    }
                                }
                            }
                            Spacer(Modifier.height(4.dp))
                            Text(
                                text = prof?.name ?: "未配置订阅",
                                fontWeight = FontWeight.SemiBold,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                text = if (prof != null) "${prof.nodeCount} 节点 · ${prof.ruleCount} 规则" else "暂无订阅",
                                style = MiuixTheme.textStyles.body2,
                                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                            )
                        }
                    }

                    "runtime_info" -> Card(
                        modifier = cardMod,
                        insideMargin = PaddingValues(12.dp),
                    ) {
                        val ips = listOfNotNull(snapshot.telemetry.intranetIpv4, snapshot.telemetry.intranetIpv6)
                            .filter { it.isNotBlank() }
                            .joinToString(" · ")
                        Text(
                            text = "运行信息",
                            style = MiuixTheme.textStyles.body2,
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = "内网地址 ${ips.ifBlank { "-" }}",
                            fontWeight = FontWeight.SemiBold,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            text = "混合端口 ${snapshot.settings.mixedPort} · 内存 ${formatNetoBytes(snapshot.telemetry.memoryBytes)}",
                            style = MiuixTheme.textStyles.body2,
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        )
                    }
                }
            }
        }
    }
}

@Composable
internal fun NetoInlineBadge(
    text: String,
    color: Color? = null,
) {
    val bg = (color ?: MiuixTheme.colorScheme.outline).copy(alpha = 0.16f)
    val fg = color ?: MiuixTheme.colorScheme.onSurfaceVariantSummary
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(4.dp))
            .background(bg)
            .padding(horizontal = 6.dp, vertical = 2.dp),
    ) {
        Text(
            text = text,
            style = MiuixTheme.textStyles.body2,
            color = fg,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}
