package moe.telecom.neto.sdk

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.overlay.OverlayDialog
import top.yukonga.miuix.kmp.theme.MiuixTheme

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun NetoConnectionsPane(
    snapshot: NetoSnapshot,
    client: NetoControllerClient,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var subTab by rememberSaveable { mutableStateOf("active") }
    var query by rememberSaveable { mutableStateOf("") }
    var netFilter by rememberSaveable { mutableStateOf("all") }
    var sortIndex by rememberSaveable { mutableIntStateOf(0) }
    var paused by rememberSaveable { mutableStateOf(false) }
    var connSnap by remember { mutableStateOf(NetoConnectionsSnapshot()) }
    var detailItem by remember { mutableStateOf<Pair<NetoActiveConnection, Boolean>?>(null) }

    val sortLabels = listOf("最新", "下载速率", "上传速率", "总流量", "域名")
    val netOptions = listOf("all" to "全部", "tcp" to "TCP", "udp" to "UDP")

    LaunchedEffect(paused) {
        connSnap = client.fetchConnections()
        while (isActive && !paused) {
            delay(1000L)
            connSnap = client.fetchConnections()
        }
    }

    val isClosed = subTab == "closed"
    val source = if (isClosed) connSnap.closed else connSnap.active
    val q = query.trim().lowercase()
    val filtered = remember(source, netFilter, q, sortIndex, isClosed) {
        val matching = source.filter { c ->
            (netFilter == "all" || c.network.equals(netFilter, ignoreCase = true)) &&
                (q.isEmpty() ||
                    c.host.lowercase().contains(q) ||
                    c.rule.lowercase().contains(q) ||
                    c.outbound.lowercase().contains(q))
        }
        when (sortIndex) {
            1 -> matching.sortedWith(compareByDescending<NetoActiveConnection> { it.downloadSpeedBps }.thenByDescending { it.downloadBytes })
            2 -> matching.sortedWith(compareByDescending<NetoActiveConnection> { it.uploadSpeedBps }.thenByDescending { it.uploadBytes })
            3 -> matching.sortedByDescending { it.uploadBytes + it.downloadBytes }
            4 -> matching.sortedBy { it.host.lowercase() }
            else -> if (isClosed) {
                matching.sortedWith(compareByDescending<NetoActiveConnection> { it.closedAtMs }.thenByDescending { it.id })
            } else {
                matching.sortedWith(compareByDescending<NetoActiveConnection> { it.startedAtMs }.thenByDescending { it.id })
            }
        }.take(200)
    }

    val nowMs = System.currentTimeMillis()
    val summaryText = if (isClosed) {
        "已关闭 ${connSnap.closed.size} 条 · 累计 ↑ ${formatNetoBytes(connSnap.totalUploadBytes)} ↓ ${formatNetoBytes(connSnap.totalDownloadBytes)}"
    } else {
        "活跃 ${connSnap.active.size} · ↑ ${formatNetoSpeed(connSnap.totalUploadSpeedBps)} ↓ ${formatNetoSpeed(connSnap.totalDownloadSpeedBps)} · 累计 ↑ ${formatNetoBytes(connSnap.totalUploadBytes)} ↓ ${formatNetoBytes(connSnap.totalDownloadBytes)}"
    }

    Column(modifier = modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 12.dp, vertical = 6.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Button(
                    onClick = { subTab = "active" },
                    modifier = Modifier.weight(1f),
                    colors = if (!isClosed) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                ) {
                    Text(if (connSnap.active.isNotEmpty()) "活跃 (${connSnap.active.size})" else "活跃", maxLines = 1)
                }
                Button(
                    onClick = { subTab = "closed" },
                    modifier = Modifier.weight(1f),
                    colors = if (isClosed) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                ) {
                    Text(if (connSnap.closed.isNotEmpty()) "已关 (${connSnap.closed.size})" else "已关闭", maxLines = 1)
                }
                Button(
                    onClick = { paused = !paused },
                    colors = if (paused) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                ) {
                    Text(if (paused) "继续" else "暂停", maxLines = 1)
                }
                Button(
                    onClick = {
                        if (isClosed) {
                            client.clearClosedConnections()
                        } else {
                            client.closeAllConnections()
                        }
                        scope.launch {
                            delay(150L)
                            connSnap = client.fetchConnections()
                        }
                    },
                    enabled = if (isClosed) connSnap.closed.isNotEmpty() else connSnap.active.isNotEmpty(),
                ) {
                    Text(if (isClosed) "清空" else "全断", maxLines = 1)
                }
            }

            TextField(
                value = query,
                onValueChange = { query = it },
                label = "筛选主机、规则或出站",
                useLabelAsPlaceholder = true,
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    netOptions.forEach { (key, label) ->
                        NetoFilterChip(
                            label = label,
                            selected = netFilter == key,
                            onClick = { netFilter = key },
                        )
                    }
                }
                NetoFilterChip(
                    label = "排序 · ${sortLabels[sortIndex]}",
                    selected = sortIndex != 0,
                    onClick = { sortIndex = (sortIndex + 1) % sortLabels.size },
                )
            }

            Text(
                text = summaryText,
                style = MiuixTheme.textStyles.body2,
                fontFamily = FontFamily.Monospace,
                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        LazyColumn(
            modifier = Modifier.fillMaxWidth().weight(1f),
            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp),
            overscrollEffect = null,
        ) {
            if (filtered.isEmpty()) {
                item(key = "empty") {
                    Card(
                        modifier = Modifier.fillMaxWidth(),
                        insideMargin = PaddingValues(14.dp),
                    ) {
                        Text(
                            text = if (source.isEmpty()) {
                                if (isClosed) "暂无已关闭会话" else "暂无活跃会话"
                            } else {
                                "无匹配会话"
                            },
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        )
                    }
                }
            } else {
                items(filtered, key = { "$subTab-${it.id}-${it.closedAtMs}" }) { item ->
                    val durationMs = if (isClosed) {
                        (item.closedAtMs - item.startedAtMs).coerceAtLeast(0L)
                    } else {
                        (nowMs - item.startedAtMs).coerceAtLeast(0L)
                    }
                    Card(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(bottom = 6.dp),
                        insideMargin = PaddingValues(12.dp),
                    ) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column(
                                modifier = Modifier
                                    .weight(1f)
                                    .clickable { detailItem = item to isClosed },
                                verticalArrangement = Arrangement.spacedBy(4.dp),
                            ) {
                                Text(
                                    text = item.host,
                                    fontWeight = FontWeight.SemiBold,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                                FlowRow(
                                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                                    verticalArrangement = Arrangement.spacedBy(4.dp),
                                ) {
                                    NetoInlineBadge(item.network.uppercase())
                                    if (item.outbound.isNotBlank()) {
                                        NetoInlineBadge(item.outbound, Color(0xFF3884EB))
                                    }
                                    if (item.rule.isNotBlank()) {
                                        NetoInlineBadge(item.rule)
                                    }
                                }
                                Text(
                                    text = if (isClosed) {
                                        "↑ ${formatNetoBytes(item.uploadBytes)} ↓ ${formatNetoBytes(item.downloadBytes)} · ${formatNetoDuration(durationMs)} · ${formatNetoTimeMs(item.closedAtMs)}"
                                    } else {
                                        "↑ ${formatNetoSpeed(item.uploadSpeedBps)} ↓ ${formatNetoSpeed(item.downloadSpeedBps)} · 累计 ↑ ${formatNetoBytes(item.uploadBytes)} ↓ ${formatNetoBytes(item.downloadBytes)} · ${formatNetoDuration(durationMs)}"
                                    },
                                    style = MiuixTheme.textStyles.body2,
                                    fontFamily = FontFamily.Monospace,
                                    color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                            if (!isClosed) {
                                TextButton(
                                    text = "断开",
                                    onClick = {
                                        client.closeConnection(item.id)
                                        scope.launch {
                                            delay(120L)
                                            connSnap = client.fetchConnections()
                                        }
                                    },
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    val currentDetail = detailItem
    OverlayDialog(
        show = currentDetail != null,
        title = "会话详情",
        onDismissRequest = { detailItem = null },
    ) {
        if (currentDetail != null) {
            val (item, closedFlag) = currentDetail
            val durationMs = if (closedFlag) {
                (item.closedAtMs - item.startedAtMs).coerceAtLeast(0L)
            } else {
                (System.currentTimeMillis() - item.startedAtMs).coerceAtLeast(0L)
            }
            Column(
                modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                NetoConnectionDetailRow("目标主机", item.host)
                NetoConnectionDetailRow("传输协议", item.network.uppercase())
                NetoConnectionDetailRow("出站策略", item.outbound.ifBlank { "-" })
                NetoConnectionDetailRow("命中规则", item.rule.ifBlank { "-" })
                NetoConnectionDetailRow("会话状态", if (closedFlag) "已关闭" else "活跃")
                if (!closedFlag) {
                    NetoConnectionDetailRow(
                        "实时速率",
                        "↑ ${formatNetoSpeed(item.uploadSpeedBps)} · ↓ ${formatNetoSpeed(item.downloadSpeedBps)}",
                    )
                }
                NetoConnectionDetailRow(
                    "累计流量",
                    "↑ ${formatNetoBytes(item.uploadBytes)} · ↓ ${formatNetoBytes(item.downloadBytes)}",
                )
                NetoConnectionDetailRow("建立时间", formatNetoTimeMs(item.startedAtMs))
                NetoConnectionDetailRow("持续时长", formatNetoDuration(durationMs))
                if (closedFlag) {
                    NetoConnectionDetailRow("关闭时间", formatNetoTimeMs(item.closedAtMs))
                }
            }
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                TextButton(
                    text = "关闭",
                    onClick = { detailItem = null },
                    modifier = Modifier.weight(1f),
                )
                if (!closedFlag) {
                    TextButton(
                        text = "断开连接",
                        onClick = {
                            client.closeConnection(item.id)
                            detailItem = null
                            scope.launch {
                                delay(120L)
                                connSnap = client.fetchConnections()
                            }
                        },
                        modifier = Modifier.weight(1f),
                    )
                }
                TextButton(
                    text = "复制主机",
                    onClick = {
                        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
                        cm?.setPrimaryClip(ClipData.newPlainText("host", item.host))
                        detailItem = null
                    },
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.textButtonColorsPrimary(),
                )
            }
        }
    }
}

@Composable
private fun NetoConnectionDetailRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text(
            text = label,
            style = MiuixTheme.textStyles.body2,
            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
            modifier = Modifier.width(72.dp),
        )
        Text(
            text = value,
            style = MiuixTheme.textStyles.body2,
            fontFamily = FontFamily.Monospace,
            modifier = Modifier.weight(1f),
        )
    }
}
