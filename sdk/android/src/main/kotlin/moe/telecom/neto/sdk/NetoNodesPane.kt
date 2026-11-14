package moe.telecom.neto.sdk

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.theme.MiuixTheme

private data class GroupMemberEntry(
    val name: String,
    val subGroup: NetoProxyGroup? = null,
    val node: NetoNodeItem? = null,
    val builtinLabel: String? = null,
)

@Composable
fun NetoNodesPane(
    snapshot: NetoSnapshot,
    client: NetoControllerClient,
    modifier: Modifier = Modifier,
    blockedContent: (@Composable () -> Unit)? = null,
) {
    var query by rememberSaveable { mutableStateOf("") }
    var protoFilter by rememberSaveable { mutableStateOf("") }
    var selectedGroupName by rememberSaveable { mutableStateOf("") }

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

    val isRuleWithGroups = snapshot.settings.routingMode.equals("rule", ignoreCase = true) &&
        snapshot.proxyGroups.isNotEmpty()
    val isDirectMode = snapshot.settings.routingMode.equals("direct", ignoreCase = true)
    val q = query.trim().lowercase()

    val nodesByName = remember(snapshot.nodes) {
        snapshot.nodes.associateBy { it.name }
    }
    val groupsByName = remember(snapshot.proxyGroups) {
        snapshot.proxyGroups.associateBy { it.name }
    }

    val activeGroup = if (isRuleWithGroups) {
        snapshot.proxyGroups.firstOrNull { it.name == selectedGroupName }
            ?: snapshot.primarySelectGroup
            ?: snapshot.proxyGroups.first()
    } else {
        null
    }

    val protocols = remember(snapshot.nodes) {
        snapshot.nodes.map { it.protocolLabel }.filter { it.isNotBlank() }.distinct().sorted()
    }

    val groupMembers = remember(activeGroup, nodesByName, groupsByName, q, snapshot.nodeSortMode) {
        if (activeGroup == null) {
            emptyList()
        } else {
            val rawEntries = activeGroup.proxies.map { memberName ->
                val subGrp = groupsByName[memberName]
                val node = if (subGrp == null) nodesByName[memberName] else null
                val builtin = when {
                    memberName.equals("DIRECT", ignoreCase = true) -> "直接连接"
                    memberName.equals("REJECT", ignoreCase = true) ||
                        memberName.equals("REJECT-DROP", ignoreCase = true) -> "拦截连接"
                    else -> null
                }
                GroupMemberEntry(
                    name = memberName,
                    subGroup = subGrp,
                    node = node,
                    builtinLabel = builtin,
                )
            }.filter { entry ->
                q.isEmpty() ||
                    entry.name.lowercase().contains(q) ||
                    entry.node?.protocolLabel?.lowercase()?.contains(q) == true ||
                    entry.node?.tags?.any { it.lowercase().contains(q) } == true ||
                    entry.subGroup?.groupTypeLabel?.lowercase()?.contains(q) == true
            }
            val pinned = rawEntries.filter { it.node == null }
            val nodeItems = rawEntries.filter { it.node != null }
            val sortedNodes = when (snapshot.nodeSortMode) {
                "latency" -> nodeItems.sortedWith(
                    compareBy<GroupMemberEntry> { it.node?.latencyMs ?: Long.MAX_VALUE }
                        .thenBy { it.node?.index ?: Int.MAX_VALUE }
                )
                "name" -> nodeItems.sortedBy { it.name.lowercase() }
                else -> nodeItems
            }
            pinned + sortedNodes
        }
    }

    val flatVisibleNodes = remember(snapshot.nodes, protoFilter, q) {
        snapshot.nodes.filter { node ->
            (protoFilter.isEmpty() || node.protocolLabel.equals(protoFilter, ignoreCase = true)) &&
                (q.isEmpty() ||
                    node.name.lowercase().contains(q) ||
                    node.protocolLabel.lowercase().contains(q) ||
                    node.tags.any { it.lowercase().contains(q) })
        }
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
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                TextField(
                    value = query,
                    onValueChange = { query = it },
                    label = "搜索节点或协议",
                    useLabelAsPlaceholder = true,
                    singleLine = true,
                    modifier = Modifier.weight(1f),
                )
                Button(
                    onClick = {
                        if (activeGroup != null && activeGroup.proxies.any { nodesByName.containsKey(it) }) {
                            client.testGroupNodes(activeGroup.name)
                        } else {
                            client.testAllNodes()
                        }
                    },
                    enabled = !snapshot.nodesTesting && snapshot.nodes.isNotEmpty(),
                ) {
                    Text(if (snapshot.nodesTesting) "测速中" else "测速")
                }
            }

            if (isDirectMode) {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    insideMargin = PaddingValues(horizontal = 12.dp, vertical = 8.dp),
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            text = "当前为直连模式，节点暂不接管流量",
                            style = MiuixTheme.textStyles.body2,
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                            modifier = Modifier.weight(1f),
                        )
                        TextButton(
                            text = "切到规则",
                            onClick = { client.setRoutingMode("rule") },
                            colors = ButtonDefaults.textButtonColorsPrimary(),
                        )
                    }
                }
            }

            if (isRuleWithGroups && activeGroup != null) {
                LazyRow(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    items(snapshot.proxyGroups, key = { "grp-tab-" + it.name }) { grp ->
                        val selected = grp.name == activeGroup.name
                        val targetLabel = grp.now ?: grp.selected ?: "-"
                        val borderColor = if (selected) {
                            MiuixTheme.colorScheme.primary
                        } else {
                            MiuixTheme.colorScheme.outline.copy(alpha = 0.28f)
                        }
                        val bgColor = if (selected) {
                            MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)
                        } else {
                            MiuixTheme.colorScheme.surface
                        }
                        Column(
                            modifier = Modifier
                                .clip(RoundedCornerShape(10.dp))
                                .background(bgColor)
                                .border(1.dp, borderColor, RoundedCornerShape(10.dp))
                                .clickable { selectedGroupName = grp.name }
                                .padding(horizontal = 12.dp, vertical = 7.dp),
                        ) {
                            Text(
                                text = grp.name,
                                style = MiuixTheme.textStyles.body2,
                                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium,
                                color = if (selected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface,
                                maxLines = 1,
                            )
                            Text(
                                text = targetLabel,
                                style = MiuixTheme.textStyles.body2,
                                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                    }
                }

                val currentTarget = activeGroup.now ?: activeGroup.selected ?: activeGroup.proxies.firstOrNull().orEmpty()
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = activeGroup.groupTypeLabel + " · " + currentTarget,
                        style = MiuixTheme.textStyles.body2,
                        color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f).padding(end = 8.dp),
                    )
                    NetoSortPills(
                        currentMode = snapshot.nodeSortMode,
                        onSelect = client::setNodeSort,
                    )
                }
            } else {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = "共 " + flatVisibleNodes.size + " 个节点",
                        style = MiuixTheme.textStyles.body2,
                        color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                    )
                    NetoSortPills(
                        currentMode = snapshot.nodeSortMode,
                        onSelect = client::setNodeSort,
                    )
                }
                if (protocols.size > 1) {
                    LazyRow(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                    ) {
                        item(key = "proto-all") {
                            NetoFilterChip(
                                label = "全部",
                                selected = protoFilter.isEmpty(),
                                onClick = { protoFilter = "" },
                            )
                        }
                        items(protocols, key = { "proto-" + it }) { proto ->
                            NetoFilterChip(
                                label = proto,
                                selected = protoFilter.equals(proto, ignoreCase = true),
                                onClick = { protoFilter = if (protoFilter.equals(proto, ignoreCase = true)) "" else proto },
                            )
                        }
                    }
                }
            }
        }

        LazyColumn(
            modifier = Modifier.fillMaxWidth().weight(1f),
            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
            overscrollEffect = null,
        ) {
            if (isRuleWithGroups && activeGroup != null) {
                val activeTarget = activeGroup.now ?: activeGroup.selected
                if (groupMembers.isEmpty()) {
                    item(key = "grp-empty") {
                        Card(
                            modifier = Modifier.fillMaxWidth(),
                            insideMargin = PaddingValues(14.dp),
                        ) {
                            Text(
                                text = "当前分组没有匹配的条目",
                                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                            )
                        }
                    }
                } else {
                    items(groupMembers, key = { "gm-" + activeGroup.name + "-" + it.name }) { entry ->
                        val isSelected = entry.name == activeTarget
                        val resolvedLeafNode = if (entry.subGroup != null) {
                            snapshot.resolveGroupLeafTarget(entry.subGroup.name)?.let { nodesByName[it] }
                        } else {
                            entry.node
                        }
                        val subtitle = when {
                            entry.subGroup != null -> {
                                val subNow = entry.subGroup.now ?: entry.subGroup.selected ?: "-"
                                entry.subGroup.groupTypeLabel + " · " + subNow
                            }
                            entry.builtinLabel != null -> entry.builtinLabel
                            entry.node != null -> {
                                val tags = entry.node.tags.takeIf { it.isNotEmpty() }?.joinToString(" · ")
                                listOfNotNull(entry.node.protocolLabel, tags).joinToString(" · ")
                            }
                            else -> ""
                        }
                        NetoCompactNodeItem(
                            title = entry.name,
                            subtitle = subtitle,
                            selected = isSelected,
                            latencyMs = resolvedLeafNode?.latencyMs,
                            hasTimeout = entry.node?.lastError != null && entry.node.latencyMs == null,
                            onClick = {
                                if (activeGroup.isManualSelect) {
                                    if (entry.node == null || entry.node.supported) {
                                        client.selectGroupProxy(activeGroup.name, entry.name)
                                    }
                                } else if (entry.subGroup != null) {
                                    selectedGroupName = entry.subGroup.name
                                } else if (entry.node != null && entry.node.supported) {
                                    client.testNode(entry.node.index)
                                }
                            },
                            onTrailingAction = when {
                                entry.subGroup != null -> ({ selectedGroupName = entry.subGroup.name })
                                entry.node != null -> ({ client.testNode(entry.node.index) })
                                else -> null
                            },
                            trailingSymbol = if (entry.subGroup != null) "›" else "↻",
                        )
                    }
                }
            } else {
                if (flatVisibleNodes.isEmpty()) {
                    item(key = "flat-empty") {
                        Card(
                            modifier = Modifier.fillMaxWidth(),
                            insideMargin = PaddingValues(14.dp),
                        ) {
                            Text(
                                text = if (snapshot.nodes.isEmpty()) "当前暂无可用节点" else "没有匹配的节点",
                                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                            )
                        }
                    }
                } else {
                    items(flatVisibleNodes, key = { "node-" + it.index }) { node ->
                        val tags = node.tags.takeIf { it.isNotEmpty() }?.joinToString(" · ")
                        val subtitle = listOfNotNull(node.protocolLabel, tags).joinToString(" · ")
                        NetoCompactNodeItem(
                            title = node.name,
                            subtitle = subtitle,
                            selected = node.index == snapshot.selectedNodeIndex,
                            latencyMs = node.latencyMs,
                            hasTimeout = node.lastError != null && node.latencyMs == null,
                            onClick = { if (node.supported) client.selectNode(node.index) },
                            onTrailingAction = { client.testNode(node.index) },
                            trailingSymbol = "↻",
                        )
                    }
                }
            }
            item(key = "bottom-pad") {
                Spacer(Modifier.height(16.dp))
            }
        }
    }
}

@Composable
internal fun NetoSortPills(
    currentMode: String,
    onSelect: (String) -> Unit,
) {
    Row(
        horizontalArrangement = Arrangement.spacedBy(4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        listOf("default" to "默认", "latency" to "延迟", "name" to "名称").forEach { (mode, label) ->
            NetoFilterChip(
                label = label,
                selected = currentMode == mode,
                onClick = { onSelect(mode) },
            )
        }
    }
}

@Composable
internal fun NetoFilterChip(
    label: String,
    selected: Boolean,
    onClick: () -> Unit,
) {
    val bg = if (selected) {
        MiuixTheme.colorScheme.primary.copy(alpha = 0.14f)
    } else {
        MiuixTheme.colorScheme.outline.copy(alpha = 0.10f)
    }
    val fg = if (selected) {
        MiuixTheme.colorScheme.primary
    } else {
        MiuixTheme.colorScheme.onSurfaceVariantSummary
    }
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(6.dp))
            .background(bg)
            .clickable(onClick = onClick)
            .padding(horizontal = 9.dp, vertical = 4.dp),
    ) {
        Text(
            text = label,
            style = MiuixTheme.textStyles.body2,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
            color = fg,
            maxLines = 1,
        )
    }
}

@Composable
private fun NetoCompactNodeItem(
    title: String,
    subtitle: String,
    selected: Boolean,
    latencyMs: Long?,
    hasTimeout: Boolean,
    onClick: () -> Unit,
    onTrailingAction: (() -> Unit)?,
    trailingSymbol: String,
) {
    val primary = MiuixTheme.colorScheme.primary
    val borderMod = if (selected) {
        Modifier.border(1.dp, primary.copy(alpha = 0.65f), RoundedCornerShape(12.dp))
    } else {
        Modifier
    }
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .then(borderMod)
            .clickable(onClick = onClick),
        cornerRadius = 12.dp,
        insideMargin = PaddingValues(horizontal = 12.dp, vertical = 10.dp),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Box(
                modifier = Modifier
                    .size(16.dp)
                    .clip(CircleShape)
                    .then(
                        if (selected) {
                            Modifier.background(primary)
                        } else {
                            Modifier.border(1.5.dp, MiuixTheme.colorScheme.outline.copy(alpha = 0.45f), CircleShape)
                        }
                    ),
                contentAlignment = Alignment.Center,
            ) {
                if (selected) {
                    Box(
                        modifier = Modifier
                            .size(6.dp)
                            .clip(CircleShape)
                            .background(Color.White),
                    )
                }
            }
            Column(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                Text(
                    text = title,
                    fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium,
                    color = if (selected) primary else MiuixTheme.colorScheme.onSurface,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                if (subtitle.isNotBlank()) {
                    Text(
                        text = subtitle,
                        style = MiuixTheme.textStyles.body2,
                        color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            val latencyText = when {
                latencyMs != null -> latencyMs.toString() + " ms"
                hasTimeout -> "超时"
                else -> null
            }
            if (latencyText != null) {
                val latColor = when {
                    latencyMs == null -> Color(0xFFD8524C)
                    latencyMs < 200L -> Color(0xFF2AA876)
                    latencyMs < 500L -> Color(0xFFE09F3E)
                    else -> Color(0xFFD8524C)
                }
                Text(
                    text = latencyText,
                    style = MiuixTheme.textStyles.body2,
                    fontWeight = FontWeight.Medium,
                    color = latColor,
                    maxLines = 1,
                )
            }
            if (onTrailingAction != null) {
                IconButton(
                    onClick = onTrailingAction,
                    modifier = Modifier.size(30.dp),
                ) {
                    Text(
                        text = trailingSymbol,
                        color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                    )
                }
            } else {
                Spacer(Modifier.width(4.dp))
            }
        }
    }
}