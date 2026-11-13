package moe.telecom.neto.sdk

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.CardDefaults
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.preference.ArrowPreference
import top.yukonga.miuix.kmp.theme.MiuixTheme

@Composable
fun NetoConnectPane(
    snapshot: NetoSnapshot,
    onToggleConnect: () -> Unit,
    onSelectRoutingMode: (String) -> Unit,
    onOpenNodes: () -> Unit,
    modifier: Modifier = Modifier,
    actionError: String? = null,
    blockedContent: (@Composable () -> Unit)? = null,
) {
    val conn = snapshot.connection
    var nowMs by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(conn.connected, conn.connectedAtMs) {
        while (conn.connected) {
            nowMs = System.currentTimeMillis()
            delay(1000L)
        }
    }

    Column(
        modifier = modifier
            .fillMaxSize()
            .statusBarsPadding()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 16.dp, vertical = 18.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        if (blockedContent != null) {
            blockedContent()
            return@Column
        }

        val dotColor by animateColorAsState(
            targetValue = when {
                conn.connecting -> MiuixTheme.colorScheme.secondary
                conn.connected -> MiuixTheme.colorScheme.primary
                else -> MiuixTheme.colorScheme.outline
            },
            animationSpec = tween(220),
            label = "neto-status-dot",
        )
        val statusLabel = when {
            conn.connecting -> "连接中"
            conn.connected -> "已连接"
            else -> "未连接"
        }
        val elapsed = if (conn.connected && conn.connectedAtMs > 0L) {
            formatNetoDuration((nowMs - conn.connectedAtMs).coerceAtLeast(0L))
        } else {
            "00:00:00"
        }

        Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.defaultColors(
                color = if (conn.connected) MiuixTheme.colorScheme.secondaryContainer else MiuixTheme.colorScheme.surfaceContainer,
            ),
            insideMargin = PaddingValues(horizontal = 20.dp, vertical = 24.dp),
        ) {
            Column(
                modifier = Modifier.fillMaxWidth(),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Box(
                        modifier = Modifier
                            .size(10.dp)
                            .clip(CircleShape)
                            .background(dotColor)
                    )
                    Text(
                        statusLabel,
                        fontSize = 18.sp,
                        fontWeight = FontWeight.SemiBold,
                    )
                }
                Spacer(Modifier.height(10.dp))
                Text(
                    elapsed,
                    fontSize = 30.sp,
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.Medium,
                )
                Spacer(Modifier.height(20.dp))
                Button(
                    onClick = onToggleConnect,
                    enabled = !conn.connecting,
                    modifier = Modifier.fillMaxWidth(0.76f),
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

        Spacer(Modifier.height(12.dp))

        val selected = snapshot.selectedNode
        val nodeTitle = when {
            snapshot.settings.routingMode == "direct" -> "DIRECT"
            selected != null -> selected.name
            else -> "暂无可用节点"
        }
        val nodeSub = when {
            snapshot.settings.routingMode == "direct" -> "直连出站"
            selected != null -> listOfNotNull(
                selected.protocolLabel.takeIf { it.isNotBlank() },
                selected.latencyMs?.let { "$it ms" },
            ).joinToString(" · ")
            else -> "未选择"
        }
        Card(modifier = Modifier.fillMaxWidth()) {
            ArrowPreference(
                title = nodeTitle,
                summary = nodeSub,
                onClick = onOpenNodes,
            )
        }

        Spacer(Modifier.height(10.dp))

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            listOf("rule" to "规则", "global" to "全局", "direct" to "直连").forEach { (key, label) ->
                val active = snapshot.settings.routingMode == key
                Button(
                    onClick = { onSelectRoutingMode(key) },
                    modifier = Modifier.weight(1f),
                    colors = if (active) ButtonDefaults.buttonColorsPrimary() else ButtonDefaults.buttonColors(),
                ) {
                    Text(label)
                }
            }
        }

        if (conn.connected) {
            Spacer(Modifier.height(12.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                NetoSpeedChartCard(
                    upload = snapshot.telemetry.uploadHistory,
                    download = snapshot.telemetry.downloadHistory,
                    upBps = snapshot.telemetry.uploadSpeedBps,
                    downBps = snapshot.telemetry.downloadSpeedBps,
                    modifier = Modifier.weight(1f),
                )
                NetoLatencyChartCard(
                    samples = snapshot.telemetry.latencyHistory,
                    currentMs = snapshot.telemetry.latencyMs,
                    modifier = Modifier.weight(1f),
                )
            }
        }

        val errText = actionError ?: conn.lastError
        if (!errText.isNullOrBlank()) {
            Spacer(Modifier.height(12.dp))
            Card(
                modifier = Modifier.fillMaxWidth(),
                insideMargin = PaddingValues(12.dp),
            ) {
                Text(
                    errText,
                    style = MiuixTheme.textStyles.body2,
                    color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                )
            }
        }
    }
}
