package moe.telecom.neto.sdk

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.overlay.OverlayDialog
import top.yukonga.miuix.kmp.preference.ArrowPreference
import top.yukonga.miuix.kmp.theme.ColorSchemeMode
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.theme.ThemeController

@Composable
fun NetoTheme(themeMode: String, content: @Composable () -> Unit) {
    val mode = when (themeMode) {
        "dark" -> ColorSchemeMode.MonetDark
        "light" -> ColorSchemeMode.MonetLight
        else -> ColorSchemeMode.MonetSystem
    }
    val controller = remember(mode) { ThemeController(mode) }
    MiuixTheme(controller = controller, content = content)
}

@Composable
fun NetoPreferenceCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    Card(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp)
            .padding(bottom = 10.dp),
        content = content,
    )
}

@Composable
fun NetoSubpageHeader(
    title: String,
    onBack: () -> Unit,
    trailing: (@Composable () -> Unit)? = null,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        TextButton(
            text = "← 返回",
            onClick = onBack,
        )
        Text(
            text = title,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        if (trailing != null) {
            trailing()
        }
    }
}

@Composable
fun NetoEditPreference(
    title: String,
    value: String,
    onConfirm: (String) -> Unit,
    summary: String? = null,
    singleLine: Boolean = true,
    minLines: Int = 1,
) {
    var show by remember { mutableStateOf(false) }
    var draft by remember { mutableStateOf(value) }
    ArrowPreference(
        title = title,
        summary = summary ?: value.ifBlank { "未设置" },
        onClick = {
            draft = value
            show = true
        },
        holdDownState = show,
    )
    OverlayDialog(
        show = show,
        title = title,
        onDismissRequest = { show = false },
    ) {
        TextField(
            value = draft,
            onValueChange = { draft = it },
            singleLine = singleLine,
            minLines = minLines,
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 16.dp),
        )
        Row(horizontalArrangement = Arrangement.SpaceBetween, modifier = Modifier.fillMaxWidth()) {
            TextButton(
                text = "取消",
                onClick = { show = false },
                modifier = Modifier.weight(1f),
            )
            Spacer(Modifier.width(16.dp))
            TextButton(
                text = "确定",
                onClick = {
                    onConfirm(draft.trim())
                    show = false
                },
                modifier = Modifier.weight(1f),
                colors = ButtonDefaults.textButtonColorsPrimary(),
            )
        }
    }
}

@Composable
fun NetoSpeedChartCard(
    upload: List<Long>,
    download: List<Long>,
    upBps: Long,
    downBps: Long,
    modifier: Modifier = Modifier,
    title: String? = null,
) {
    val gridColor = MiuixTheme.colorScheme.outline
    val upColor = MiuixTheme.colorScheme.primary
    val downColor = MiuixTheme.colorScheme.secondary
    Card(modifier = modifier, insideMargin = PaddingValues(12.dp)) {
        if (!title.isNullOrBlank()) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = title,
                    style = MiuixTheme.textStyles.body2,
                    color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                )
                Text(
                    text = "↑ ${formatNetoSpeed(upBps)}  ↓ ${formatNetoSpeed(downBps)}",
                    style = MiuixTheme.textStyles.body2,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        } else {
            Text(
                "↑ ${formatNetoSpeed(upBps)}  ↓ ${formatNetoSpeed(downBps)}",
                style = MiuixTheme.textStyles.body2,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Spacer(Modifier.height(8.dp))
        Canvas(modifier = Modifier.fillMaxWidth().height(68.dp)) {
            drawChartGrid(gridColor)
            val peak = ((upload + download).maxOrNull() ?: 1L).coerceAtLeast(1L)
            drawSeriesLine(download, peak, downColor)
            drawSeriesLine(upload, peak, upColor)
        }
    }
}

@Composable
fun NetoLatencyChartCard(
    samples: List<Long>,
    currentMs: Long?,
    modifier: Modifier = Modifier,
    title: String? = null,
    trailing: (@Composable () -> Unit)? = null,
) {
    val gridColor = MiuixTheme.colorScheme.outline
    val lineColor = MiuixTheme.colorScheme.primary
    val label = if (currentMs != null && currentMs >= 0L) "$currentMs ms" else "- ms"
    Card(modifier = modifier, insideMargin = PaddingValues(12.dp)) {
        if (!title.isNullOrBlank() || trailing != null) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Row(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    if (!title.isNullOrBlank()) {
                        Text(
                            text = title,
                            style = MiuixTheme.textStyles.body2,
                            color = MiuixTheme.colorScheme.onSurfaceVariantSummary,
                        )
                    }
                    Text(
                        text = label,
                        style = MiuixTheme.textStyles.body2,
                        fontWeight = FontWeight.SemiBold,
                        color = lineColor,
                    )
                }
                if (trailing != null) {
                    trailing()
                }
            }
        } else {
            Text(
                label,
                style = MiuixTheme.textStyles.body2,
                color = lineColor,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Spacer(Modifier.height(8.dp))
        Canvas(modifier = Modifier.fillMaxWidth().height(68.dp)) {
            drawChartGrid(gridColor)
            val valid = samples.filter { it >= 0L }
            if (valid.size >= 2) {
                val peak = (valid.maxOrNull() ?: 100L).coerceAtLeast(1L)
                drawSeriesLine(valid, peak, lineColor)
            }
        }
    }
}

private fun DrawScope.drawChartGrid(color: Color) {
    for (i in 0..3) {
        val y = size.height * (i / 3f)
        drawLine(
            color = color.copy(alpha = 0.25f),
            start = Offset(0f, y),
            end = Offset(size.width, y),
            strokeWidth = 0.5.dp.toPx(),
        )
    }
}

private fun DrawScope.drawSeriesLine(values: List<Long>, peak: Long, color: Color) {
    if (values.size < 2) return
    val path = Path()
    values.forEachIndexed { idx, v ->
        val x = (idx.toFloat() / (values.size - 1)) * size.width
        val y = size.height * (1f - (v.toFloat() / peak.toFloat()).coerceIn(0f, 1f))
        if (idx == 0) path.moveTo(x, y) else path.lineTo(x, y)
    }
    drawPath(path = path, color = color, style = Stroke(width = 2.dp.toPx()))
}
