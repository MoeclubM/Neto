package moe.telecom.neto.sdk

import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.net.VpnService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

class NetoControllerClient(
    context: Context,
    private val appTitle: String = "Neto",
    userAgent: String = "Aerion",
) {
    private val appContext = context.applicationContext
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private val _snapshot = MutableStateFlow(NetoSnapshot())
    val snapshot: StateFlow<NetoSnapshot> = _snapshot.asStateFlow()

    private val _logs = MutableStateFlow<List<NetoLogEntry>>(emptyList())
    val logs: StateFlow<List<NetoLogEntry>> = _logs.asStateFlow()

    private val _installedApps = MutableStateFlow<List<InstalledAppEntry>>(emptyList())
    val installedApps: StateFlow<List<InstalledAppEntry>> = _installedApps.asStateFlow()

    private val _actionError = MutableStateFlow<String?>(null)
    val actionError: StateFlow<String?> = _actionError.asStateFlow()

    private val _busy = MutableStateFlow(false)
    val busy: StateFlow<Boolean> = _busy.asStateFlow()

    private var pollJob: Job? = null

    init {
        val initObj = NetoBridge.init(appContext, appTitle, userAgent)
        _snapshot.value = NetoSnapshot.fromJson(initObj.getJSONObject("result"))
        startPolling()
    }

    private fun startPolling() {
        pollJob?.cancel()
        pollJob = scope.launch {
            var tick = 0
            while (isActive) {
                delay(1000L)
                tick++
                withContext(Dispatchers.IO) {
                    runCatching {
                        val snapObj = NetoBridge.call("snapshot").getJSONObject("result")
                        val snap = NetoSnapshot.fromJson(snapObj)
                        _snapshot.value = snap
                        if (snap.connection.connected && tick % 3 == 0) {
                            runCatching { NetoBridge.call("probe_tunnel") }
                        }
                    }
                }
            }
        }
    }

    fun clearError() {
        _actionError.value = null
    }

    fun prepareVpnPermission(): Intent? = VpnService.prepare(appContext)

    fun startVpn() {
        _actionError.value = null
        NetoVpnService.start(appContext, appTitle)
    }

    fun stopVpn() {
        _actionError.value = null
        NetoVpnService.stop(appContext)
        scope.launch(Dispatchers.IO) {
            delay(250L)
            refreshSnapshotNow()
        }
    }

    suspend fun refreshSnapshotNow(): NetoSnapshot = withContext(Dispatchers.IO) {
        val snapObj = NetoBridge.call("snapshot").getJSONObject("result")
        val snap = NetoSnapshot.fromJson(snapObj)
        _snapshot.value = snap
        snap
    }

    fun selectNode(index: Int) {
        val wasConnected = _snapshot.value.connection.connected
        runCall("select_node", JSONObject().put("index", index)) {
            if (wasConnected) {
                NetoVpnService.start(appContext, appTitle)
            }
        }
    }

    fun selectGroupProxy(groupName: String, proxyName: String) {
        val wasConnected = _snapshot.value.connection.connected
        runCall(
            "select_group_proxy",
            JSONObject().put("group_name", groupName).put("proxy_name", proxyName),
        ) {
            if (wasConnected) {
                NetoVpnService.start(appContext, appTitle)
            }
        }
    }

    fun setNodeSort(mode: String) {
        runCall("set_node_sort", JSONObject().put("mode", mode))
    }

    fun testNode(index: Int) {
        scope.launch(Dispatchers.IO) {
            runCatching {
                NetoBridge.call("test_node", JSONObject().put("index", index))
            }
            runCatching { refreshSnapshotNow() }
        }
    }

    fun testAllNodes() {
        scope.launch(Dispatchers.IO) {
            runCatching { NetoBridge.call("test_all_nodes") }
                .onFailure { _actionError.value = it.message }
            runCatching { refreshSnapshotNow() }
        }
    }

    fun probeTunnelNow() {
        scope.launch(Dispatchers.IO) {
            runCatching { NetoBridge.call("probe_tunnel") }
            runCatching { refreshSnapshotNow() }
        }
    }

    fun setRoutingMode(mode: String) {
        val wasConnected = _snapshot.value.connection.connected
        scope.launch(Dispatchers.IO) {
            runCatching {
                NetoBridge.call("update_settings", JSONObject().put("routing_mode", mode))
                refreshSnapshotNow()
                if (wasConnected) {
                    NetoVpnService.start(appContext, appTitle)
                }
            }.onFailure { _actionError.value = it.message }
        }
    }

    fun updateSettings(patch: JSONObject, restartIfConnected: Boolean = false) {
        val wasConnected = _snapshot.value.connection.connected
        runCall("update_settings", patch) {
            if (restartIfConnected && wasConnected) {
                NetoVpnService.start(appContext, appTitle)
            }
        }
    }

    fun addRemoteProfile(name: String, url: String, autoUpdateMins: Int = 60, onDone: (() -> Unit)? = null) {
        scope.launch {
            _busy.value = true
            _actionError.value = null
            try {
                withContext(Dispatchers.IO) {
                    val res = NetoBridge.call(
                        "add_remote_profile",
                        JSONObject()
                            .put("name", name)
                            .put("url", url)
                            .put("flag", "aerion")
                            .put("auto_update_mins", autoUpdateMins),
                    ).getJSONObject("result")
                    _snapshot.value = NetoSnapshot.fromJson(res.getJSONObject("snapshot"))
                }
                onDone?.invoke()
            } catch (err: Throwable) {
                _actionError.value = err.message
            } finally {
                _busy.value = false
            }
        }
    }

    fun importLocalProfile(name: String, yaml: String, onDone: (() -> Unit)? = null) {
        scope.launch {
            _busy.value = true
            _actionError.value = null
            try {
                withContext(Dispatchers.IO) {
                    val res = NetoBridge.call(
                        "import_local_profile",
                        JSONObject().put("name", name).put("yaml", yaml),
                    ).getJSONObject("result")
                    _snapshot.value = NetoSnapshot.fromJson(res.getJSONObject("snapshot"))
                }
                onDone?.invoke()
            } catch (err: Throwable) {
                _actionError.value = err.message
            } finally {
                _busy.value = false
            }
        }
    }

    fun refreshProfile(id: String? = null) {
        val params = JSONObject().apply { if (!id.isNullOrBlank()) put("id", id) }
        runCall("refresh_profile", params)
    }

    fun selectProfile(id: String) {
        val wasConnected = _snapshot.value.connection.connected
        runCall("select_profile", JSONObject().put("id", id)) {
            if (wasConnected) {
                NetoVpnService.start(appContext, appTitle)
            }
        }
    }

    fun deleteProfile(id: String) {
        runCall("delete_profile", JSONObject().put("id", id))
    }

    suspend fun upsertManagedProfile(
        id: String,
        name: String,
        url: String,
        nodes: JSONArray,
        routeConfigYaml: String?,
        rulesPreview: List<String>,
        ruleCount: Int,
        usedUploadBytes: Long = 0L,
        usedDownloadBytes: Long = 0L,
        totalBytes: Long = 0L,
        expireUnix: Long? = null,
    ): NetoSnapshot = withContext(Dispatchers.IO) {
        val params = JSONObject()
            .put("id", id)
            .put("name", name)
            .put("url", url)
            .put("nodes", nodes)
            .put("rules_preview", JSONArray(rulesPreview))
            .put("rule_count", ruleCount)
        if (!routeConfigYaml.isNullOrBlank()) {
            params.put("route_config_yaml", routeConfigYaml)
        }
        if (totalBytes > 0L || expireUnix != null) {
            val ui = JSONObject()
                .put("upload_bytes", usedUploadBytes)
                .put("download_bytes", usedDownloadBytes)
                .put("total_bytes", totalBytes)
            if (expireUnix != null && expireUnix > 0L) {
                ui.put("expire_unix", expireUnix)
            }
            params.put("user_info", ui)
        }
        val env = NetoBridge.call("upsert_managed_profile", params)
        val snap = NetoSnapshot.fromJson(env.getJSONObject("result"))
        _snapshot.value = snap
        snap
    }

    suspend fun syncManagedSubscription(
        id: String = "managed-default",
        name: String = "Managed",
        url: String,
        flag: String = "aerion",
        userAgent: String? = null,
    ): JSONObject = withContext(Dispatchers.IO) {
        val params = JSONObject()
            .put("id", id)
            .put("name", name)
            .put("url", url)
            .put("flag", flag)
        if (!userAgent.isNullOrBlank()) {
            params.put("user_agent", userAgent)
        }
        val res = NetoBridge.call("sync_managed_subscription", params).getJSONObject("result")
        _snapshot.value = NetoSnapshot.fromJson(res.getJSONObject("snapshot"))
        res
    }

    fun updateDashboardWidget(id: String, enabled: Boolean? = null, moveDelta: Int? = null) {
        val params = JSONObject().put("id", id)
        if (enabled != null) params.put("enabled", enabled)
        if (moveDelta != null) params.put("move_delta", moveDelta)
        runCall("update_dashboard_widget", params)
    }

    fun resetDashboardWidgets() {
        runCall("reset_dashboard_widgets", JSONObject())
    }

    fun refreshLogs() {
        scope.launch(Dispatchers.IO) {
            runCatching {
                val arr = NetoBridge.call("logs", JSONObject().put("limit", 150)).getJSONArray("result")
                val items = (0 until arr.length()).mapNotNull { i ->
                    val o = arr.optJSONObject(i) ?: return@mapNotNull null
                    NetoLogEntry(
                        timestampMs = o.optLong("timestamp_ms", 0L),
                        level = o.optString("level", "info"),
                        message = o.optString("message", ""),
                    )
                }
                _logs.value = items
            }
        }
    }

    fun clearLogs() {
        scope.launch(Dispatchers.IO) {
            runCatching { NetoBridge.call("clear_logs") }
            _logs.value = emptyList()
        }
    }

    fun loadInstalledAppsIfNeeded() {
        if (_installedApps.value.isNotEmpty()) return
        scope.launch(Dispatchers.IO) {
            val pm = appContext.packageManager
            val byPkg = linkedMapOf<String, InstalledAppEntry>()
            val launcherIntent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
            for (ri in pm.queryIntentActivities(launcherIntent, 0)) {
                val ai = ri.activityInfo?.applicationInfo ?: continue
                if (ai.packageName == appContext.packageName) continue
                byPkg[ai.packageName] = InstalledAppEntry(
                    packageName = ai.packageName,
                    label = ai.loadLabel(pm).toString(),
                    systemApp = (ai.flags and ApplicationInfo.FLAG_SYSTEM) != 0,
                )
            }
            for (ai in pm.getInstalledApplications(0)) {
                if (ai.packageName == appContext.packageName || byPkg.containsKey(ai.packageName)) continue
                byPkg[ai.packageName] = InstalledAppEntry(
                    packageName = ai.packageName,
                    label = ai.loadLabel(pm).toString(),
                    systemApp = (ai.flags and ApplicationInfo.FLAG_SYSTEM) != 0,
                )
            }
            _installedApps.value = byPkg.values.sortedBy { it.label.lowercase() }
        }
    }

    private fun runCall(method: String, params: JSONObject, after: (() -> Unit)? = null) {
        scope.launch {
            _actionError.value = null
            try {
                val snap = withContext(Dispatchers.IO) {
                    val env = NetoBridge.call(method, params)
                    NetoSnapshot.fromJson(env.getJSONObject("result"))
                }
                _snapshot.value = snap
                after?.invoke()
            } catch (err: Throwable) {
                _actionError.value = err.message
            }
        }
    }
}
