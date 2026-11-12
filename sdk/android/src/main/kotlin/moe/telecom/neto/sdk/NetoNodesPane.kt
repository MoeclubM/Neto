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
import androidx.compose.ui.unit.dp
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.SmallTitle
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.preference.OverlayDropdownPreference
import top.yukonga.miuix.kmp.preference.RadioButtonPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme

@Composable
fun NetoNodesPane(
    snapshot: NetoSnapshot,
    client: NetoControllerClient,
    modifier: Modifier = Modifier,
    blockedContent: (@Composable () -> Unit)? = null,
) {
    var query by rememberSaveable { mutableStateOf("") }
    val q = query.trim().lowercase()
    val visibleNodes = if (q.isEmpty()) {
        snapshot.nodes
    } else {
        snapshot.nodes.filter {
            it.name.lowercase().contains(q) || it.protocolLabel.lowercase().contains(q)
        }
    }

    if (blockedContent != null) {
        Column(
            modifier = modifier
                .fillMaxSize()
                .padding(16.dp),
        ) {
            blockedContent()
        }
        return
    }

    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(top = 8.dp, bottom = 28.dp),
        overscrollEffect = null,
    ) {
        item(key = "controls") {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp)
                    .padding(bottom = 10.dp),
            ) {
                TextField(
                    value = query,
                    onValueChange = { query = it },
                    label = "筛选节点",
                    useLabelAsPlaceholder = true,
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(8.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    listOf("default" to "默认", "latency" to "延迟", "name" to "名称").forEach { (mode, label) ->
                        val active = snapshot.nodeSortMode == mode
                        Button(
                            onClick = { client.setNodeSort(mode) },
                            modifier = Modifier.weight(1f),
                            colors = if (active) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                        ) {
                            Text(label)
                        }
                    }
                    Button(
                        onClick = client::testAllNodes,
                        enabled = !snapshot.nodesTesting && snapshot.nodes.isNotEmpty(),
                    ) {
                        Text(if (snapshot.nodesTesting) "测速中" else "测速")
                    }
                }
            }
        }

        if (snapshot.proxyGroups.isNotEmpty()) {
            item(key = "groups") {
                SmallTitle("策略组")
                NetoPreferenceCard {
                    snapshot.proxyGroups.forEach { group ->
                        if (group.proxies.isNotEmpty()) {
                            val selIdx = group.proxies.indexOf(group.selected ?: "").coerceAtLeast(0)
                            OverlayDropdownPreference(
                                title = group.name,
                                summary = group.selected ?: group.proxies.first(),
                                items = group.proxies,
                                selectedIndex = selIdx,
                                onSelectedIndexChange = { idx ->
                                    client.selectGroupProxy(group.name, group.proxies[idx])
                                },
                            )
                        }
                    }
                }
            }
        }

        if (visibleNodes.isEmpty()) {
            item(key = "empty") {
                NetoPreferenceCard {
                    Text(
                        if (snapshot.nodes.isEmpty()) "当前暂无可用节点" else "没有匹配的节点",
                        color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        modifier = Modifier.padding(16.dp),
                    )
                }
            }
        } else {
            items(visibleNodes, key = { "node-${it.index}" }) { node ->
                val latencyPart = when {
                    node.latencyMs != null -> "${node.latencyMs} ms"
                    node.lastError != null -> "超时"
                    else -> null
                }
                val tagsPart = node.tags.joinToString(" · ").takeIf { it.isNotBlank() }
                val summary = listOfNotNull(node.protocolLabel, tagsPart, latencyPart).joinToString(" · ")
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp)
                        .padding(bottom = 8.dp),
                ) {
                    RadioButtonPreference(
                        title = node.name,
                        summary = summary,
                        selected = node.index == snapshot.selectedNodeIndex,
                        onClick = { if (node.supported) client.selectNode(node.index) },
                        endActions = {
                            IconButton(
                                onClick = { client.testNode(node.index) },
                                modifier = Modifier.size(34.dp),
                            ) {
                                Text("↻")
                            }
                        },
                    )
                }
            }
        }
    }
}
