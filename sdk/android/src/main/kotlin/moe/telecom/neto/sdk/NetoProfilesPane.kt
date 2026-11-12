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
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.basic.TextField
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
    var name by rememberSaveable { mutableStateOf("") }
    var url by rememberSaveable { mutableStateOf("") }
    var showYamlImport by rememberSaveable { mutableStateOf(false) }
    var yamlText by rememberSaveable { mutableStateOf("") }

    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(top = 8.dp, bottom = 28.dp),
        overscrollEffect = null,
    ) {
        item(key = "import-remote") {
            SmallTitle("添加订阅")
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp)
                    .padding(bottom = 10.dp),
                insideMargin = PaddingValues(14.dp),
            ) {
                TextField(
                    value = name,
                    onValueChange = { name = it },
                    label = "名称（可选）",
                    useLabelAsPlaceholder = true,
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(8.dp))
                TextField(
                    value = url,
                    onValueChange = { url = it },
                    label = "订阅链接 URL",
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
                        text = if (showYamlImport) "收起本地 YAML" else "导入本地 YAML",
                        onClick = { showYamlImport = !showYamlImport },
                    )
                    Button(
                        onClick = {
                            if (url.isNotBlank()) {
                                client.addRemoteProfile(name.trim(), url.trim()) {
                                    name = ""
                                    url = ""
                                }
                            }
                        },
                        enabled = !busy && url.isNotBlank(),
                        colors = ButtonDefaults.buttonColorsPrimary(),
                    ) {
                        Text(if (busy) "导入中" else "拉取并添加")
                    }
                }

                if (showYamlImport) {
                    Spacer(Modifier.height(10.dp))
                    TextField(
                        value = yamlText,
                        onValueChange = { yamlText = it },
                        label = "粘贴 Clash / Aerion YAML",
                        useLabelAsPlaceholder = true,
                        minLines = 5,
                        maxLines = 10,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(8.dp))
                    Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                        Button(
                            onClick = {
                                client.importLocalProfile(name.trim(), yamlText) {
                                    name = ""
                                    yamlText = ""
                                    showYamlImport = false
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
                SmallTitle("已保存订阅")
            }
            items(snapshot.profiles, key = { it.id }) { prof ->
                val quota = if (prof.totalBytes > 0L) {
                    " · ${formatNetoBytes(prof.usedBytes)} / ${formatNetoBytes(prof.totalBytes)}"
                } else ""
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp)
                        .padding(bottom = 8.dp),
                ) {
                    RadioButtonPreference(
                        title = prof.name,
                        summary = "${prof.nodeCount} 节点 · ${prof.ruleCount} 规则$quota",
                        selected = prof.active,
                        onClick = { client.selectProfile(prof.id) },
                        endActions = {
                            Row(verticalAlignment = Alignment.CenterVertically) {
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
}
