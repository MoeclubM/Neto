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
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
import org.json.JSONObject
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.SmallTitle
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.preference.OverlayDropdownPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme

private val QUICK_RULE_TYPES = listOf(
    "DOMAIN-SUFFIX",
    "DOMAIN",
    "DOMAIN-KEYWORD",
    "IP-CIDR",
    "DST-PORT",
)

@Composable
fun NetoRulesPane(
    snapshot: NetoSnapshot,
    client: NetoControllerClient,
    modifier: Modifier = Modifier,
) {
    val scope = rememberCoroutineScope()
    var subTab by rememberSaveable { mutableStateOf("rules") }
    var ruleFilter by rememberSaveable { mutableStateOf("") }
    var rulesResult by remember { mutableStateOf(NetoRulesListResult()) }
    var showTester by rememberSaveable { mutableStateOf(false) }
    var routeTarget by rememberSaveable { mutableStateOf("") }
    var routeNetwork by rememberSaveable { mutableStateOf("tcp") }
    var routeTestOutput by remember { mutableStateOf<Pair<String, Boolean>?>(null) }
    var testingRoute by remember { mutableStateOf(false) }

    var quickTypeIndex by rememberSaveable { mutableIntStateOf(0) }
    var quickMatch by rememberSaveable { mutableStateOf("") }
    var quickTarget by rememberSaveable { mutableStateOf("PROXY") }
    var quickFeedback by remember { mutableStateOf<Pair<String, Boolean>?>(null) }

    var overrideDraft by rememberSaveable(snapshot.settings.customRouteYaml) {
        mutableStateOf(snapshot.settings.customRouteYaml)
    }
    var overrideFeedback by remember { mutableStateOf<Pair<String, Boolean>?>(null) }

    val targetOptions = remember(snapshot.proxyGroups) {
        buildList {
            add("PROXY")
            add("DIRECT")
            add("REJECT")
            snapshot.proxyGroups.forEach { g ->
                val trimmed = g.name.trim()
                if (trimmed.isNotEmpty() && none { it.equals(trimmed, ignoreCase = true) }) {
                    add(trimmed)
                }
            }
        }
    }
    val selectedTargetIdx = targetOptions.indexOfFirst { it.equals(quickTarget, ignoreCase = true) }
        .coerceAtLeast(0)

    LaunchedEffect(ruleFilter, snapshot.activeProfileId, snapshot.ruleCount, snapshot.settings.customRouteYaml) {
        rulesResult = client.fetchRules(filter = ruleFilter.trim(), limit = 300)
    }

    Column(modifier = modifier.fillMaxSize()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 12.dp, vertical = 6.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Button(
                onClick = { subTab = "rules" },
                modifier = Modifier.weight(1f),
                colors = if (subTab == "rules") ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
            ) {
                Text("分流规则")
            }
            Button(
                onClick = { subTab = "override" },
                modifier = Modifier.weight(1f),
                colors = if (subTab == "override") ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
            ) {
                Text("规则覆写")
            }
        }

        if (subTab == "rules") {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    TextField(
                        value = ruleFilter,
                        onValueChange = { ruleFilter = it },
                        label = "筛选规则类型、内容或策略",
                        useLabelAsPlaceholder = true,
                        singleLine = true,
                        modifier = Modifier.weight(1f),
                    )
                    Button(
                        onClick = { showTester = !showTester },
                        colors = if (showTester) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                    ) {
                        Text("匹配测试")
                    }
                }

                if (showTester) {
                    Card(
                        modifier = Modifier.fillMaxWidth(),
                        insideMargin = PaddingValues(12.dp),
                    ) {
                        TextField(
                            value = routeTarget,
                            onValueChange = { routeTarget = it },
                            label = "输入目标域名或 IP（如 www.google.com 或 223.5.5.5:53）",
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
                            listOf("tcp" to "TCP", "udp" to "UDP").forEach { (net, label) ->
                                Button(
                                    onClick = { routeNetwork = net },
                                    colors = if (routeNetwork == net) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                                ) {
                                    Text(label)
                                }
                            }
                            Spacer(Modifier.weight(1f))
                            Button(
                                onClick = {
                                    val target = routeTarget.trim()
                                    if (target.isEmpty() || testingRoute) return@Button
                                    testingRoute = true
                                    scope.launch {
                                        try {
                                            val res = client.testRoute(target, routeNetwork)
                                            val out = if (res.outbound.isNullOrBlank()) res.decision else "${res.decision} (${res.outbound})"
                                            routeTestOutput = "${res.target} [${res.network.uppercase()}] -> $out | ${res.matchedRule}" to false
                                        } catch (err: Throwable) {
                                            routeTestOutput = (err.message ?: "匹配测试失败") to true
                                        } finally {
                                            testingRoute = false
                                        }
                                    }
                                },
                                enabled = !testingRoute && routeTarget.isNotBlank(),
                                colors = ButtonDefaults.buttonColorsPrimary(),
                            ) {
                                Text(if (testingRoute) "测试中" else "测试")
                            }
                        }
                        routeTestOutput?.let { (msg, isErr) ->
                            Spacer(Modifier.height(6.dp))
                            Text(
                                text = msg,
                                style = MiuixTheme.textStyles.body2,
                                fontFamily = FontFamily.Monospace,
                                color = if (isErr) Color(0xFFD8524C) else MiuixTheme.colorScheme.onSurface,
                            )
                        }
                    }
                }

                val countLabel = if (ruleFilter.isBlank()) {
                    "共 ${rulesResult.total} 条"
                } else {
                    "${rulesResult.items.size} / ${rulesResult.total}"
                }
                val sourceHint = if (snapshot.settings.customRouteYaml.isNotBlank()) " · 自定义规则生效中" else ""
                Text(
                    text = "$countLabel$sourceHint",
                    style = MiuixTheme.textStyles.body2,
                    color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                )
            }

            Spacer(Modifier.height(6.dp))

            LazyColumn(
                modifier = Modifier.fillMaxWidth().weight(1f),
                contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp),
                overscrollEffect = null,
            ) {
                if (rulesResult.items.isEmpty()) {
                    item(key = "empty") {
                        Card(
                            modifier = Modifier.fillMaxWidth(),
                            insideMargin = PaddingValues(14.dp),
                        ) {
                            Text(
                                text = if (rulesResult.total == 0) "暂无分流规则" else "无匹配规则",
                                color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                            )
                        }
                    }
                } else {
                    items(rulesResult.items, key = { it.index }) { r ->
                        val targetTint = when (r.target.uppercase()) {
                            "DIRECT" -> Color(0xFF2AA876)
                            "REJECT" -> Color(0xFFD8524C)
                            else -> Color(0xFF3884EB)
                        }
                        Card(
                            modifier = Modifier.fillMaxWidth().padding(bottom = 4.dp),
                            insideMargin = PaddingValues(horizontal = 10.dp, vertical = 8.dp),
                        ) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                NetoInlineBadge(r.ruleType)
                                Text(
                                    text = r.payload.ifBlank { "全匹配" },
                                    style = MiuixTheme.textStyles.body2,
                                    fontFamily = FontFamily.Monospace,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                    modifier = Modifier.weight(1f),
                                )
                                NetoInlineBadge(r.target, targetTint)
                            }
                        }
                    }
                }
            }
        } else {
            LazyColumn(
                modifier = Modifier.fillMaxWidth().weight(1f),
                contentPadding = PaddingValues(top = 4.dp, bottom = 28.dp),
                overscrollEffect = null,
            ) {
                item(key = "quick-add") {
                    SmallTitle("快速添加规则")
                    Card(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 12.dp)
                            .padding(bottom = 10.dp),
                    ) {
                        OverlayDropdownPreference(
                            title = "规则类型",
                            summary = QUICK_RULE_TYPES[quickTypeIndex],
                            items = QUICK_RULE_TYPES,
                            selectedIndex = quickTypeIndex,
                            onSelectedIndexChange = { quickTypeIndex = it },
                        )
                        OverlayDropdownPreference(
                            title = "目标策略",
                            summary = targetOptions[selectedTargetIdx],
                            items = targetOptions,
                            selectedIndex = selectedTargetIdx,
                            onSelectedIndexChange = { idx ->
                                quickTarget = targetOptions[idx]
                            },
                        )
                        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp)) {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                TextField(
                                    value = quickMatch,
                                    onValueChange = { quickMatch = it },
                                    label = "example.com 或 1.2.3.0/24",
                                    useLabelAsPlaceholder = true,
                                    singleLine = true,
                                    modifier = Modifier.weight(1f),
                                )
                                Button(
                                    onClick = {
                                        var pattern = quickMatch.trim()
                                        if (pattern.isEmpty()) return@Button
                                        val rType = QUICK_RULE_TYPES[quickTypeIndex]
                                        if (rType == "IP-CIDR" && !pattern.contains('/')) {
                                            pattern += if (pattern.contains(':')) "/128" else "/32"
                                        }
                                        val target = targetOptions[selectedTargetIdx]
                                        client.addCustomRule("$rType,$pattern,$target") { err ->
                                            if (err == null) {
                                                quickMatch = ""
                                                quickFeedback = "已添加到顶部" to false
                                            } else {
                                                quickFeedback = err to true
                                            }
                                        }
                                    },
                                    enabled = quickMatch.isNotBlank(),
                                    colors = ButtonDefaults.buttonColorsPrimary(),
                                ) {
                                    Text("添加")
                                }
                            }
                            quickFeedback?.let { (msg, isErr) ->
                                Spacer(Modifier.height(6.dp))
                                Text(
                                    text = msg,
                                    style = MiuixTheme.textStyles.body2,
                                    color = if (isErr) Color(0xFFD8524C) else Color(0xFF2AA876),
                                )
                            }
                        }
                    }
                }

                item(key = "yaml-editor") {
                    SmallTitle("规则覆写 YAML（留空则使用当前订阅规则）")
                    Card(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 12.dp)
                            .padding(bottom = 10.dp),
                        insideMargin = PaddingValues(14.dp),
                    ) {
                        TextField(
                            value = overrideDraft,
                            onValueChange = { overrideDraft = it },
                            label = "rules:\n  - DOMAIN-SUFFIX,example.com,PROXY\n  - MATCH,DIRECT",
                            useLabelAsPlaceholder = true,
                            minLines = 10,
                            maxLines = 20,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        overrideFeedback?.let { (msg, isErr) ->
                            Spacer(Modifier.height(6.dp))
                            Text(
                                text = msg,
                                style = MiuixTheme.textStyles.body2,
                                color = if (isErr) Color(0xFFD8524C) else Color(0xFF2AA876),
                            )
                        }
                        Spacer(Modifier.height(10.dp))
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            Button(
                                onClick = {
                                    overrideDraft = ""
                                    client.updateSettings(
                                        JSONObject().put("custom_route_yaml", ""),
                                        restartIfConnected = true,
                                    ) { err ->
                                        overrideFeedback = if (err == null) "已恢复默认" to false else err to true
                                    }
                                },
                                modifier = Modifier.weight(1f),
                            ) {
                                Text("恢复默认")
                            }
                            Button(
                                onClick = {
                                    client.updateSettings(
                                        JSONObject().put("custom_route_yaml", overrideDraft),
                                        restartIfConnected = true,
                                    ) { err ->
                                        overrideFeedback = if (err == null) "已保存" to false else err to true
                                    }
                                },
                                modifier = Modifier.weight(1f),
                                colors = ButtonDefaults.buttonColorsPrimary(),
                            ) {
                                Text("保存规则")
                            }
                        }
                    }
                }
            }
        }
    }
}
