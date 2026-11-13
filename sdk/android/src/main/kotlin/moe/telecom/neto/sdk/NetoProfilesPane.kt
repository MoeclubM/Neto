package moe.telecom.neto.sdk

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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
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
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.overlay.OverlayDialog
import top.yukonga.miuix.kmp.preference.OverlayDropdownPreference
import top.yukonga.miuix.kmp.preference.RadioButtonPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme

@Composable
fun NetoProfilesPane(
    snapshot: NetoSnapshot,
    client: NetoControllerClient,
    busy: Boolean,
    actionError: String?,
    modifier: Modifier = Modifier,
) {
    var subpage by rememberSaveable { mutableStateOf("root") }
    var quickUrl by rememberSaveable { mutableStateOf("") }
    var advName by rememberSaveable { mutableStateOf("") }
    var advUrl by rememberSaveable { mutableStateOf("") }
    var flag by rememberSaveable { mutableStateOf("aerion") }
    var localName by rememberSaveable { mutableStateOf("") }
    var yamlText by rememberSaveable { mutableStateOf("") }
    var editingProfile by remember { mutableStateOf<NetoProfileBrief?>(null) }
    val flagOptions = listOf("aerion" to "Aerion", "meta" to "Clash Meta")

    BackHandler(enabled = subpage != "root") {
        subpage = "root"
    }

    if (subpage == "advanced") {
        LazyColumn(
            modifier = modifier.fillMaxSize(),
            contentPadding = PaddingValues(top = 4.dp, bottom = 28.dp),
            overscrollEffect = null,
        ) {
            item(key = "hdr") {
                NetoSubpageHeader(title = "高级与本地导入", onBack = { subpage = "root" })
            }
            item(key = "adv-remote") {
                SmallTitle("远程订阅参数")
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp)
                        .padding(bottom = 10.dp),
                    insideMargin = PaddingValues(14.dp),
                ) {
                    TextField(
                        value = advName,
                        onValueChange = { advName = it },
                        label = "备注名称（可选）",
                        useLabelAsPlaceholder = true,
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(8.dp))
                    TextField(
                        value = advUrl,
                        onValueChange = { advUrl = it },
                        label = "订阅链接 URL",
                        useLabelAsPlaceholder = true,
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(8.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                        flagOptions.forEach { (key, label) ->
                            Button(
                                onClick = { flag = key },
                                modifier = Modifier.weight(1f),
                                colors = if (flag == key) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                            ) {
                                Text(label)
                            }
                        }
                    }
                    Spacer(Modifier.height(10.dp))
                    Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                        Button(
                            onClick = {
                                if (advUrl.isNotBlank()) {
                                    client.addRemoteProfile(advName.trim(), advUrl.trim(), flag = flag) {
                                        advName = ""
                                        advUrl = ""
                                        quickUrl = ""
                                        subpage = "root"
                                    }
                                }
                            },
                            enabled = !busy && advUrl.isNotBlank(),
                            colors = ButtonDefaults.buttonColorsPrimary(),
                        ) {
                            Text(if (busy) "导入中" else "拉取添加")
                        }
                    }
                }
            }
            item(key = "adv-local") {
                SmallTitle("本地 YAML 配置")
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp)
                        .padding(bottom = 10.dp),
                    insideMargin = PaddingValues(14.dp),
                ) {
                    TextField(
                        value = localName,
                        onValueChange = { localName = it },
                        label = "配置名称（可选）",
                        useLabelAsPlaceholder = true,
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(8.dp))
                    TextField(
                        value = yamlText,
                        onValueChange = { yamlText = it },
                        label = "粘贴 Clash / Aerion YAML",
                        useLabelAsPlaceholder = true,
                        minLines = 7,
                        maxLines = 14,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(10.dp))
                    Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                        Button(
                            onClick = {
                                client.importLocalProfile(localName.trim(), yamlText) {
                                    localName = ""
                                    yamlText = ""
                                    subpage = "root"
                                }
                            },
                            enabled = !busy && yamlText.isNotBlank(),
                            colors = ButtonDefaults.buttonColorsPrimary(),
                        ) {
                            Text("保存本地配置")
                        }
                    }
                }
            }
        }
        return
    }

    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(top = 8.dp, bottom = 28.dp),
        overscrollEffect = null,
    ) {
        item(key = "import-quick") {
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp)
                    .padding(bottom = 10.dp),
                insideMargin = PaddingValues(14.dp),
            ) {
                TextField(
                    value = quickUrl,
                    onValueChange = { quickUrl = it },
                    label = "https://... 订阅链接 URL",
                    useLabelAsPlaceholder = true,
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(10.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    TextButton(
                        text = "高级 / 本地导入",
                        onClick = {
                            if (quickUrl.isNotBlank() && advUrl.isBlank()) {
                                advUrl = quickUrl.trim()
                            }
                            subpage = "advanced"
                        },
                    )
                    Button(
                        onClick = {
                            if (quickUrl.isNotBlank()) {
                                client.addRemoteProfile("", quickUrl.trim(), flag = "aerion") {
                                    quickUrl = ""
                                }
                            }
                        },
                        enabled = !busy && quickUrl.isNotBlank(),
                        colors = ButtonDefaults.buttonColorsPrimary(),
                    ) {
                        Text(if (busy) "导入中" else "导入")
                    }
                }
            }
        }

        if (!actionError.isNullOrBlank()) {
            item(key = "err") {
                NetoPreferenceCard {
                    Text(
                        actionError,
                        color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        modifier = Modifier.padding(14.dp),
                    )
                }
            }
        }

        if (snapshot.profiles.isNotEmpty()) {
            item(key = "saved-header") {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(end = 12.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    SmallTitle("已保存订阅")
                    if (snapshot.profiles.count { it.url.isNotBlank() } > 1) {
                        TextButton(
                            text = "全部更新",
                            onClick = client::refreshAllProfiles,
                        )
                    }
                }
            }
            items(snapshot.profiles, key = { it.id }) { prof ->
                val quota = if (prof.totalBytes > 0L) {
                    " · ${formatNetoBytes(prof.usedBytes)} / ${formatNetoBytes(prof.totalBytes)}"
                } else ""
                val fmtTag = if (prof.url.isNotBlank()) {
                    if (prof.flag.equals("meta", ignoreCase = true)) "Meta · " else "Aerion · "
                } else ""
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp)
                        .padding(bottom = 8.dp),
                ) {
                    RadioButtonPreference(
                        title = prof.name,
                        summary = "$fmtTag${prof.nodeCount} 节点 · ${prof.ruleCount} 规则$quota",
                        selected = prof.active,
                        onClick = { client.selectProfile(prof.id) },
                        endActions = {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                IconButton(
                                    onClick = { editingProfile = prof },
                                    modifier = Modifier.size(34.dp),
                                ) {
                                    Text("✎")
                                }
                                if (prof.url.isNotBlank()) {
                                    IconButton(
                                        onClick = { client.refreshProfile(prof.id) },
                                        modifier = Modifier.size(34.dp),
                                    ) {
                                        Text("↻")
                                    }
                                }
                                IconButton(
                                    onClick = { client.deleteProfile(prof.id) },
                                    modifier = Modifier.size(34.dp),
                                ) {
                                    Text("✕")
                                }
                            }
                        },
                    )
                }
            }
        }
    }

    val editing = editingProfile
    if (editing != null) {
        var editName by remember(editing.id) { mutableStateOf(editing.name) }
        var editUrl by remember(editing.id) { mutableStateOf(editing.url) }
        var editFlagIdx by remember(editing.id) {
            mutableIntStateOf(flagOptions.indexOfFirst { it.first.equals(editing.flag, ignoreCase = true) }.coerceAtLeast(0))
        }
        val intervals = listOf(15 to "每 15 分钟", 30 to "每 30 分钟", 60 to "每 1 小时", 360 to "每 6 小时", 1440 to "每 24 小时", 0 to "手动更新")
        var editAutoIdx by remember(editing.id) {
            mutableIntStateOf(intervals.indexOfFirst { it.first == editing.autoUpdateMins }.let { if (it >= 0) it else 2 })
        }
        OverlayDialog(
            show = true,
            title = "编辑订阅",
            onDismissRequest = { editingProfile = null },
        ) {
            Column(modifier = Modifier.fillMaxWidth()) {
                TextField(
                    value = editName,
                    onValueChange = { editName = it },
                    label = "订阅名称",
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                if (editing.url.isNotBlank()) {
                    Spacer(Modifier.height(8.dp))
                    TextField(
                        value = editUrl,
                        onValueChange = { editUrl = it },
                        label = "订阅地址",
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(8.dp))
                    OverlayDropdownPreference(
                        title = "订阅格式",
                        summary = flagOptions[editFlagIdx].second,
                        items = flagOptions.map { it.second },
                        selectedIndex = editFlagIdx,
                        onSelectedIndexChange = { editFlagIdx = it },
                    )
                    OverlayDropdownPreference(
                        title = "更新周期",
                        summary = intervals[editAutoIdx].second,
                        items = intervals.map { it.second },
                        selectedIndex = editAutoIdx,
                        onSelectedIndexChange = { editAutoIdx = it },
                    )
                }
                Spacer(Modifier.height(14.dp))
                Row(horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
                    TextButton(
                        text = "取消",
                        onClick = { editingProfile = null },
                        modifier = Modifier.weight(1f),
                    )
                    Spacer(Modifier.width(12.dp))
                    TextButton(
                        text = "保存",
                        onClick = {
                            client.updateProfile(
                                id = editing.id,
                                name = editName.trim(),
                                url = if (editing.url.isNotBlank()) editUrl.trim() else null,
                                flag = if (editing.url.isNotBlank()) flagOptions[editFlagIdx].first else null,
                                autoUpdateMins = if (editing.url.isNotBlank()) intervals[editAutoIdx].first else null,
                            ) {
                                editingProfile = null
                            }
                        },
                        modifier = Modifier.weight(1f),
                        colors = ButtonDefaults.textButtonColorsPrimary(),
                    )
                }
            }
        }
    }
}
