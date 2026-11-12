package moe.telecom.neto

import android.Manifest
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.calculateEndPadding
import androidx.compose.foundation.layout.calculateStartPadding
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Bolt
import androidx.compose.material.icons.rounded.CloudDownload
import androidx.compose.material.icons.rounded.Dns
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.Speed
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import moe.telecom.neto.sdk.NetoConnectPane
import moe.telecom.neto.sdk.NetoControllerClient
import moe.telecom.neto.sdk.NetoDashboardPane
import moe.telecom.neto.sdk.NetoNodesPane
import moe.telecom.neto.sdk.NetoProfilesPane
import moe.telecom.neto.sdk.NetoSettingsPane
import moe.telecom.neto.sdk.NetoTheme
import top.yukonga.miuix.kmp.basic.NavigationBar
import top.yukonga.miuix.kmp.basic.NavigationBarItem
import top.yukonga.miuix.kmp.basic.Scaffold
import top.yukonga.miuix.kmp.basic.TopAppBar
import top.yukonga.miuix.kmp.theme.MiuixTheme

enum class NetoTab(val title: String, val icon: ImageVector) {
    CONNECT("连接", Icons.Rounded.Bolt),
    DASHBOARD("主页", Icons.Rounded.Speed),
    NODES("节点", Icons.Rounded.Dns),
    PROFILES("订阅", Icons.Rounded.CloudDownload),
    SETTINGS("设置", Icons.Rounded.Settings),
}

class MainActivity : ComponentActivity() {
    private lateinit var client: NetoControllerClient

    private val vpnPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            if (result.resultCode == RESULT_OK) {
                client.startVpn()
            } else {
                Toast.makeText(this, "未授予 VPN 权限", Toast.LENGTH_SHORT).show()
            }
        }

    private val notificationPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) {}

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        client = NetoControllerClient(applicationContext, appTitle = "Neto", userAgent = "Aerion")
        enableEdgeToEdge()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        setContent {
            val snapshot by client.snapshot.collectAsStateWithLifecycle()
            val logs by client.logs.collectAsStateWithLifecycle()
            val apps by client.installedApps.collectAsStateWithLifecycle()
            val busy by client.busy.collectAsStateWithLifecycle()
            val actionError by client.actionError.collectAsStateWithLifecycle()

            LaunchedEffect(snapshot.settings.themeMode) {
                applyEdgeToEdge(snapshot.settings.themeMode)
            }

            NetoTheme(snapshot.settings.themeMode) {
                NetoStandaloneApp(
                    client = client,
                    snapshot = snapshot,
                    logs = logs,
                    installedApps = apps,
                    busy = busy,
                    actionError = actionError,
                    onRequestConnect = { requestVpnStart() },
                )
            }
        }
    }

    private fun requestVpnStart() {
        val intent = client.prepareVpnPermission()
        if (intent != null) {
            vpnPermissionLauncher.launch(intent)
        } else {
            client.startVpn()
        }
    }

    private fun applyEdgeToEdge(themeMode: String) {
        val dark = when (themeMode) {
            "dark" -> true
            "light" -> false
            else -> (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
        }
        if (dark) {
            enableEdgeToEdge(
                statusBarStyle = SystemBarStyle.dark(Color.TRANSPARENT),
                navigationBarStyle = SystemBarStyle.dark(Color.TRANSPARENT),
            )
        } else {
            enableEdgeToEdge(
                statusBarStyle = SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
                navigationBarStyle = SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT),
            )
        }
    }
}

@Composable
private fun NetoStandaloneApp(
    client: NetoControllerClient,
    snapshot: moe.telecom.neto.sdk.NetoSnapshot,
    logs: List<moe.telecom.neto.sdk.NetoLogEntry>,
    installedApps: List<moe.telecom.neto.sdk.InstalledAppEntry>,
    busy: Boolean,
    actionError: String?,
    onRequestConnect: () -> Unit,
) {
    var currentTab by rememberSaveable { mutableStateOf(NetoTab.CONNECT) }
    val layoutDir = LocalLayoutDirection.current

    Scaffold(
        topBar = {
            if (currentTab != NetoTab.CONNECT) {
                TopAppBar(
                    color = MiuixTheme.colorScheme.surface,
                    title = currentTab.title,
                )
            }
        },
        bottomBar = {
            NavigationBar(color = MiuixTheme.colorScheme.surface) {
                NetoTab.entries.forEach { tab ->
                    NavigationBarItem(
                        selected = currentTab == tab,
                        onClick = { currentTab = tab },
                        icon = tab.icon,
                        label = tab.title,
                    )
                }
            }
        },
    ) { padding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(
                    start = padding.calculateStartPadding(layoutDir),
                    top = padding.calculateTopPadding(),
                    end = padding.calculateEndPadding(layoutDir),
                    bottom = padding.calculateBottomPadding(),
                ),
        ) {
            when (currentTab) {
                NetoTab.CONNECT -> NetoConnectPane(
                    snapshot = snapshot,
                    onToggleConnect = {
                        if (snapshot.connection.connected) client.stopVpn() else onRequestConnect()
                    },
                    onSelectRoutingMode = client::setRoutingMode,
                    onOpenNodes = { currentTab = NetoTab.NODES },
                    actionError = actionError,
                )
                NetoTab.DASHBOARD -> NetoDashboardPane(
                    snapshot = snapshot,
                    client = client,
                    onOpenNodes = { currentTab = NetoTab.NODES },
                    onOpenProfiles = { currentTab = NetoTab.PROFILES },
                )
                NetoTab.NODES -> NetoNodesPane(
                    snapshot = snapshot,
                    client = client,
                )
                NetoTab.PROFILES -> NetoProfilesPane(
                    snapshot = snapshot,
                    client = client,
                    busy = busy,
                    actionError = actionError,
                )
                NetoTab.SETTINGS -> NetoSettingsPane(
                    snapshot = snapshot,
                    logs = logs,
                    installedApps = installedApps,
                    client = client,
                )
            }
        }
    }
}
