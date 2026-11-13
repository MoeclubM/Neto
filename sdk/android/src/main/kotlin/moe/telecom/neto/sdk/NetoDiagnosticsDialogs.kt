package moe.telecom.neto.sdk

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
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

@Composable
fun NetoActiveConnectionsDialog(
    client: NetoControllerClient,
    onDismiss: () -> Unit,
) {
    var snap by remember { mutableStateOf(NetoConnectionsSnapshot()) }
    var search by rememberSaveable { mutableStateOf("") }
    var netFilter by rememberSaveable { mutableStateOf("all") }
    var paused by rememberSaveable { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(paused) {
        snap = client.fetchConnections()
        while (isActive && !paused) {
            delay(1000L)
            snap = client.fetchConnections()
        }
    }

    val q = search.trim().lowercase()
    val filtered = snap.active.filter { c ->
        (netFilter == "all" || c.network.equals(netFilter, ignoreCase = true)) &&
            (q.isEmpty() || c.host.lowercase().contains(q) || c.rule.lowercase().contains(q) || c.outbound.lowercase().contains(q))
    }.sortedByDescending { it.startedAtMs }

    OverlayDialog(
        show = true,
        title = "活跃会话",
        onDismissRequest = onDismiss,
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            TextField(
                value = search,
                onValueChange = { search = it },
                label = "筛选主机、规则或出站",
                useLabelAsPlaceholder = true,
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxWidth()) {
                listOf("all" to "全部", "tcp" to "TCP", "udp" to "UDP").forEach { (key, label) ->
                    Button(
                        onClick = { netFilter = key },
                        modifier = Modifier.weight(1f),
                        colors = if (netFilter == key) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                    ) {
                        Text(label)
                    }
                }
                Button(onClick = { paused = !paused }) {
                    Text(if (paused) "继续" else "暂停")
                }
            }
            Spacer(Modifier.height(8.dp))
            Text(
                "活跃 ${snap.active.size} · ↑ ${formatNetoBytes(snap.totalUploadBytes)} ↓ ${formatNetoBytes(snap.totalDownloadBytes)}",
                style = MiuixTheme.textStyles.body2,
                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
            )
            Spacer(Modifier.height(8.dp))
            if (filtered.isEmpty()) {
                Text(
                    if (snap.active.isEmpty()) "暂无活跃会话" else "无匹配会话",
                    color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                    modifier = Modifier.padding(vertical = 12.dp),
                )
            } else {
                LazyColumn(modifier = Modifier.fillMaxWidth().height(280.dp)) {
                    items(filtered.take(120), key = { it.id }) { item ->
                        Card(
                            modifier = Modifier.fillMaxWidth().padding(bottom = 6.dp),
                            insideMargin = PaddingValues(10.dp),
                        ) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.SpaceBetween,
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        item.host,
                                        fontWeight = FontWeight.Medium,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                    Text(
                                        "${item.network.uppercase()} · ${item.outbound.ifBlank { "-" }} · ↑${formatNetoBytes(item.uploadBytes)} ↓${formatNetoBytes(item.downloadBytes)}",
                                        style = MiuixTheme.textStyles.body2,
                                        color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                }
                                TextButton(
                                    text = "断开",
                                    onClick = {
                                        client.closeConnection(item.id)
                                        scope.launch {
                                            delay(120L)
                                            snap = client.fetchConnections()
                                        }
                                    },
                                )
                            }
                        }
                    }
                }
            }
            Spacer(Modifier.height(10.dp))
            Row(horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                TextButton(
                    text = "断开全部",
                    onClick = {
                        client.closeAllConnections()
                        scope.launch {
                            delay(150L)
                            snap = client.fetchConnections()
                        }
                    },
                )
                TextButton(
                    text = "关闭",
                    onClick = onDismiss,
                    colors = ButtonDefaults.textButtonColorsPrimary(),
                )
            }
        }
    }
}

@Composable
fun NetoRulesAndRouteTestDialog(
    client: NetoControllerClient,
    onDismiss: () -> Unit,
) {
    var testTarget by rememberSaveable { mutableStateOf("") }
    var testNet by rememberSaveable { mutableStateOf("tcp") }
    var testOutput by remember { mutableStateOf<String?>(null) }
    var ruleFilter by rememberSaveable { mutableStateOf("") }
    var rulesRes by remember { mutableStateOf(NetoRulesListResult()) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(ruleFilter) {
        rulesRes = client.fetchRules(ruleFilter.trim(), 200)
    }

    OverlayDialog(
        show = true,
        title = "分流规则与匹配",
        onDismissRequest = onDismiss,
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            TextField(
                value = testTarget,
                onValueChange = { testTarget = it },
                label = "测试域名或 IP",
                useLabelAsPlaceholder = true,
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxWidth()) {
                listOf("tcp" to "TCP", "udp" to "UDP").forEach { (net, label) ->
                    Button(
                        onClick = { testNet = net },
                        colors = if (testNet == net) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                    ) {
                        Text(label)
                    }
                }
                Button(
                    onClick = {
                        val t = testTarget.trim()
                        if (t.isNotEmpty()) {
                            scope.launch {
                                testOutput = runCatching {
                                    val r = client.testRoute(t, testNet)
                                    val out = r.outbound?.let { "${r.decision} ($it)" } ?: r.decision
                                    "$out · ${r.matchedRule}"
                                }.getOrElse { it.message ?: "匹配失败" }
                            }
                        }
                    },
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.buttonColorsPrimary(),
                ) {
                    Text("匹配测试")
                }
            }
            if (!testOutput.isNullOrBlank()) {
                Spacer(Modifier.height(6.dp))
                Text(
                    testOutput.orEmpty(),
                    style = MiuixTheme.textStyles.body2,
                    fontFamily = FontFamily.Monospace,
                )
            }
            Spacer(Modifier.height(10.dp))
            TextField(
                value = ruleFilter,
                onValueChange = { ruleFilter = it },
                label = "筛选规则（共 ${rulesRes.total} 条）",
                useLabelAsPlaceholder = true,
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            if (rulesRes.items.isEmpty()) {
                Text(
                    if (rulesRes.total == 0) "暂无分流规则" else "无匹配规则",
                    color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                    modifier = Modifier.padding(vertical = 10.dp),
                )
            } else {
                LazyColumn(modifier = Modifier.fillMaxWidth().height(230.dp)) {
                    items(rulesRes.items, key = { it.index }) { item ->
                        Text(
                            "[${item.ruleType}] ${item.payload.ifBlank { "全匹配" }} -> ${item.target}",
                            style = MiuixTheme.textStyles.body2,
                            fontFamily = FontFamily.Monospace,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.padding(vertical = 3.dp),
                        )
                    }
                }
            }
            Spacer(Modifier.height(10.dp))
            Row(horizontalArrangement = Arrangement.End, modifier = Modifier.fillMaxWidth()) {
                TextButton(
                    text = "关闭",
                    onClick = onDismiss,
                    colors = ButtonDefaults.textButtonColorsPrimary(),
                )
            }
        }
    }
}

@Composable
fun NetoLogsDialog(
    logs: List<NetoLogEntry>,
    client: NetoControllerClient,
    onDismiss: () -> Unit,
) {
    var level by rememberSaveable { mutableStateOf("all") }
    var keyword by rememberSaveable { mutableStateOf("") }

    LaunchedEffect(level, keyword) {
        client.refreshLogs(level = level, filter = keyword.trim(), limit = 200)
    }

    OverlayDialog(
        show = true,
        title = "运行日志",
        onDismissRequest = onDismiss,
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
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
            if (logs.isEmpty()) {
                Text("暂无日志", color = MiuixTheme.colorScheme.onSurfaceVariantSummary)
            } else {
                LazyColumn(modifier = Modifier.fillMaxWidth().height(250.dp)) {
                    items(logs.takeLast(120)) { entry ->
                        Text(
                            "[${entry.level.uppercase()}] ${entry.message}",
                            style = MiuixTheme.textStyles.body2,
                            fontFamily = FontFamily.Monospace,
                            modifier = Modifier.padding(vertical = 2.dp),
                        )
                    }
                }
            }
            Spacer(Modifier.height(12.dp))
            Row(horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                TextButton(text = "清空", onClick = client::clearLogs)
                TextButton(
                    text = "关闭",
                    onClick = onDismiss,
                    colors = ButtonDefaults.textButtonColorsPrimary(),
                )
            }
        }
    }
}
