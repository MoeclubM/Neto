package moe.telecom.neto.sdk

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.VpnService
import android.os.ParcelFileDescriptor
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.Executors

class NetoVpnService : VpnService() {
    private val worker = Executors.newSingleThreadExecutor()
    @Volatile
    private var tunInterface: ParcelFileDescriptor? = null
    private var networkCallbackRegistered = false

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            underlyingNetwork = network
            setUnderlyingNetworks(arrayOf(network))
        }

        override fun onLost(network: Network) {
            if (underlyingNetwork == network) {
                underlyingNetwork = null
                setUnderlyingNetworks(null)
            }
        }
    }

    override fun onCreate() {
        super.onCreate()
        activeInstance = this
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopTunnelAndSelf()
                return START_NOT_STICKY
            }
            else -> {
                val title = intent?.getStringExtra(EXTRA_TITLE)?.takeIf { it.isNotBlank() } ?: "Neto"
                startForeground(NOTIFICATION_ID, buildNotification(title, "连接中"))
                worker.execute { establishAndConnect(title) }
                return START_STICKY
            }
        }
    }

    override fun onRevoke() {
        stopTunnelAndSelf()
        super.onRevoke()
    }

    override fun onDestroy() {
        unregisterUnderlyingNetworkCallback()
        closeTun()
        if (activeInstance === this) {
            activeInstance = null
        }
        worker.shutdown()
        super.onDestroy()
    }

    private fun establishAndConnect(appTitle: String) {
        try {
            registerUnderlyingNetworkCallback()
            val snapEnv = NetoBridge.call("snapshot")
            val snap = NetoSnapshot.fromJson(snapEnv.getJSONObject("result"))
            val settings = snap.settings

            runCatching { NetoBridge.call("disconnect") }
            closeTun()
            val builder = Builder()
                .setSession(appTitle)
                .setMtu(settings.mtu.coerceIn(1280, 9000))
                .setBlocking(false)
                .setMetered(false)
                .addAddress("172.19.0.1", 30)
                .addRoute("0.0.0.0", 0)

            if (settings.ipv6Enabled) {
                builder.addAddress("fdfe:dcba:9876::1", 126)
                builder.addRoute("::", 0)
            }

            val dnsServer = if (settings.vpnDnsMode == "direct") {
                extractIpOrDefault(settings.directDns, "223.5.5.5")
            } else {
                "172.19.0.2"
            }
            builder.addDnsServer(dnsServer)

            if (settings.appRuleMode == "allow" && settings.allowedApps.isNotEmpty()) {
                for (pkg in settings.allowedApps) {
                    if (pkg.isNotBlank() && pkg != packageName) {
                        builder.addAllowedApplication(pkg)
                    }
                }
            } else {
                builder.addDisallowedApplication(packageName)
                for (pkg in settings.excludedApps) {
                    if (pkg.isNotBlank()) {
                        builder.addDisallowedApplication(pkg)
                    }
                }
            }

            val pfd = builder.establish() ?: error("Failed to establish Android VpnService TUN interface")
            tunInterface = pfd
            underlyingNetwork?.let { setUnderlyingNetworks(arrayOf(it)) }

            val params = JSONObject().apply {
                put("tun_fd", pfd.fd)
                put("bypass", JSONArray())
            }
            NetoBridge.call("connect_android_tun", params)

            val nodeLabel = if (settings.routingMode == "direct") "直连" else (snap.selectedNode?.name ?: "已连接")
            val nm = getSystemService(NotificationManager::class.java)
            nm?.notify(NOTIFICATION_ID, buildNotification(appTitle, nodeLabel))
        } catch (err: Throwable) {
            Log.e(TAG, "Failed to start Neto VPN", err)
            runCatching { NetoBridge.call("disconnect") }
            closeTun()
            unregisterUnderlyingNetworkCallback()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private fun stopTunnelAndSelf() {
        worker.execute {
            runCatching { NetoBridge.call("disconnect") }
            closeTun()
            unregisterUnderlyingNetworkCallback()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private fun closeTun() {
        runCatching { tunInterface?.close() }
        tunInterface = null
    }

    private fun buildNotification(title: String, content: String): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (nm?.getNotificationChannel(CHANNEL_ID) == null) {
            nm?.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "VPN 连接状态", NotificationManager.IMPORTANCE_LOW)
            )
        }
        val launchIntent = packageManager.getLaunchIntentForPackage(packageName)
        val pending = launchIntent?.let {
            PendingIntent.getActivity(
                this,
                0,
                it,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
        }
        val stopIntent = PendingIntent.getService(
            this,
            1,
            Intent(this, NetoVpnService::class.java).apply { action = ACTION_STOP },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_neto_notification)
            .setContentTitle(title)
            .setContentText(content)
            .setOngoing(true)
            .addAction(
                Notification.Action.Builder(
                    Icon.createWithResource(this, android.R.drawable.ic_media_pause),
                    "断开",
                    stopIntent,
                ).build()
            )
            .apply { if (pending != null) setContentIntent(pending) }
            .build()
    }

    private fun registerUnderlyingNetworkCallback() {
        val cm = getSystemService(ConnectivityManager::class.java) ?: return
        val active = cm.activeNetwork?.takeIf { net ->
            val caps = cm.getNetworkCapabilities(net) ?: return@takeIf false
            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) &&
                caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
        }
        if (active != null) {
            underlyingNetwork = active
            setUnderlyingNetworks(arrayOf(active))
        }
        if (networkCallbackRegistered) return
        val req = NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
            .build()
        cm.registerNetworkCallback(req, networkCallback)
        networkCallbackRegistered = true
    }

    private fun unregisterUnderlyingNetworkCallback() {
        if (!networkCallbackRegistered) return
        val cm = getSystemService(ConnectivityManager::class.java) ?: return
        runCatching { cm.unregisterNetworkCallback(networkCallback) }
        networkCallbackRegistered = false
        underlyingNetwork = null
    }

    private fun extractIpOrDefault(raw: String, fallback: String): String {
        val trimmed = raw.trim()
        if (trimmed.matches(Regex("^\\d+\\.\\d+\\.\\d+\\.\\d+$"))) return trimmed
        return fallback
    }

    companion object {
        private const val TAG = "NetoVpnService"
        private const val CHANNEL_ID = "neto_vpn_channel"
        private const val NOTIFICATION_ID = 2048
        const val ACTION_START = "moe.telecom.neto.sdk.START_VPN"
        const val ACTION_STOP = "moe.telecom.neto.sdk.STOP_VPN"
        const val EXTRA_TITLE = "app_title"

        @Volatile
        private var activeInstance: NetoVpnService? = null

        @Volatile
        private var underlyingNetwork: Network? = null

        fun start(context: Context, appTitle: String = "Neto") {
            val intent = Intent(context, NetoVpnService::class.java).apply {
                action = ACTION_START
                putExtra(EXTRA_TITLE, appTitle)
            }
            context.startForegroundService(intent)
        }

        fun stop(context: Context) {
            val intent = Intent(context, NetoVpnService::class.java).apply {
                action = ACTION_STOP
            }
            context.startService(intent)
        }

        @JvmStatic
        fun protectSocketFd(fd: Int): Boolean {
            val service = activeInstance ?: return true
            if (service.tunInterface == null) return true
            if (!service.protect(fd)) {
                return false
            }
            val network = underlyingNetwork
            if (network != null) {
                ParcelFileDescriptor.fromFd(fd).use { dup ->
                    network.bindSocket(dup.fileDescriptor)
                }
            }
            return true
        }

        @JvmStatic
        fun onLog(level: String, message: String) {
            Log.d("NetoCore", "[$level] $message")
        }

        @JvmStatic
        fun onEvent(eventJson: String) {
            Log.v("NetoEvent", eventJson)
        }
    }
}
