package moe.telecom.neto.sdk

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import java.io.File

object NetoUpdateNotifier {
    private const val CHANNEL_ID = "neto_app_updates"
    private const val NOTIFICATION_ID = 2050

    private fun canPostNotifications(context: Context): Boolean {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                return false
            }
        }
        return true
    }

    private fun ensureChannel(nm: NotificationManager) {
        if (nm.getNotificationChannel(CHANNEL_ID) == null) {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "应用更新", NotificationManager.IMPORTANCE_LOW),
            )
        }
    }

    private fun launchPendingIntent(context: Context): PendingIntent? {
        val intent = context.packageManager.getLaunchIntentForPackage(context.packageName) ?: return null
        return PendingIntent.getActivity(
            context,
            NOTIFICATION_ID,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    private fun post(
        context: Context,
        title: String,
        content: String,
        ongoing: Boolean = false,
        progressMax: Int = 0,
        progressValue: Int = 0,
        indeterminate: Boolean = false,
    ) {
        if (!canPostNotifications(context)) return
        val nm = context.getSystemService(NotificationManager::class.java) ?: return
        ensureChannel(nm)
        val builder = Notification.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_neto_notification)
            .setContentTitle(title)
            .setContentText(content)
            .setOngoing(ongoing)
            .setAutoCancel(!ongoing)
            .setOnlyAlertOnce(true)
        if (progressMax > 0 || indeterminate) {
            builder.setProgress(progressMax, progressValue, indeterminate)
        }
        launchPendingIntent(context)?.let(builder::setContentIntent)
        runCatching { nm.notify(NOTIFICATION_ID, builder.build()) }
    }

    fun notifyChecking(context: Context, appTitle: String, currentVersion: String) {
        val ver = currentVersion.removePrefix("v")
        post(
            context = context,
            title = "$appTitle · 检查更新",
            content = "正在检查新版本（当前 v$ver）…",
            ongoing = true,
            indeterminate = true,
        )
    }

    fun notifyCheckResult(context: Context, appTitle: String, hasUpdate: Boolean, latestVersion: String, currentVersion: String) {
        if (hasUpdate) {
            post(
                context = context,
                title = "$appTitle · 发现新版本 $latestVersion",
                content = "当前版本 v${currentVersion.removePrefix("v")}，可立即下载更新。",
                ongoing = false,
            )
        } else {
            post(
                context = context,
                title = "$appTitle · 已是最新版本",
                content = "当前版本 v${currentVersion.removePrefix("v")}",
                ongoing = false,
            )
        }
    }

    fun notifyError(context: Context, appTitle: String, message: String) {
        post(
            context = context,
            title = "$appTitle · 更新失败",
            content = message,
            ongoing = false,
        )
    }

    fun notifyDownloadProgress(context: Context, appTitle: String, version: String, progress: NetoUpdateProgress) {
        val pct = progress.percent
        if (pct != null && progress.totalBytes > 0L) {
            post(
                context = context,
                title = "$appTitle · 正在下载 $version ($pct%)",
                content = "${formatNetoBytes(progress.downloadedBytes)} / ${formatNetoBytes(progress.totalBytes)}",
                ongoing = true,
                progressMax = 100,
                progressValue = pct.coerceIn(0, 100),
                indeterminate = false,
            )
        } else {
            val detail = if (progress.downloadedBytes > 0L) {
                "已下载 ${formatNetoBytes(progress.downloadedBytes)}"
            } else {
                "正在连接下载源…"
            }
            post(
                context = context,
                title = "$appTitle · 正在下载 $version",
                content = detail,
                ongoing = true,
                indeterminate = true,
            )
        }
    }

    fun notifyDownloadReady(context: Context, appTitle: String, version: String, bytes: Long) {
        post(
            context = context,
            title = "$appTitle · 更新下载完成",
            content = "$version (${formatNetoBytes(bytes)}) 已就绪，正在打开安装程序…",
            ongoing = false,
        )
    }

    suspend fun downloadWithNotifications(
        context: Context,
        client: NetoControllerClient,
        appTitle: String,
        version: String,
        url: String,
        targetFile: File,
        onProgress: ((NetoUpdateProgress) -> Unit)? = null,
    ): NetoDownloadUpdateResult = coroutineScope {
        val initial = NetoUpdateProgress(phase = "downloading", version = version, message = "正在连接下载源…")
        notifyDownloadProgress(context, appTitle, version, initial)
        onProgress?.invoke(initial)
        val downloadDeferred = async {
            client.downloadUpdate(url, targetFile.absolutePath)
        }
        try {
            while (downloadDeferred.isActive) {
                delay(160L)
                if (!downloadDeferred.isActive) break
                val snap = client.getUpdateProgress()
                notifyDownloadProgress(context, appTitle, version, snap)
                onProgress?.invoke(snap)
            }
            val done = downloadDeferred.await()
            val readySnap = NetoUpdateProgress(
                phase = "ready",
                version = version,
                downloadedBytes = done.bytes,
                totalBytes = done.bytes,
                percent = 100,
                message = "更新安装包下载完成",
            )
            notifyDownloadReady(context, appTitle, version, done.bytes)
            onProgress?.invoke(readySnap)
            done
        } catch (err: Throwable) {
            notifyError(context, appTitle, err.message ?: "下载更新失败")
            throw err
        }
    }
}
