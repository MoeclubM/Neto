using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;

namespace Neto.Sdk.Windows;

public sealed class NetoClient : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private int _tickCount;
    private bool _autoRefreshing;
    private bool _disposed;

    public string DataDir { get; }
    public NetoSnapshot Snapshot { get; private set; } = new();
    public string? LastActionError { get; private set; }
    public bool Busy { get; private set; }

    public event Action<NetoSnapshot>? SnapshotChanged;

    public NetoClient(string dataDir, string appId, string userAgent, DispatcherQueue dispatcher)
    {
        DataDir = dataDir;
        _dispatcher = dispatcher;
        Directory.CreateDirectory(dataDir);
        var initNode = NetoNativeBridge.Init(dataDir, appId, userAgent);
        Snapshot = initNode.Deserialize<NetoSnapshot>() ?? new NetoSnapshot();

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1000);
        _timer.Tick += async (_, _) => await PollTickAsync();
        _timer.Start();
    }

    private async Task PollTickAsync()
    {
        if (_disposed) return;
        _tickCount++;
        try
        {
            var node = await NetoNativeBridge.CallAsync("snapshot");
            var snap = node.Deserialize<NetoSnapshot>();
            if (snap != null)
            {
                Snapshot = snap;
                SnapshotChanged?.Invoke(snap);
                if (snap.Connection.Connected && _tickCount % 4 == 0)
                {
                    _ = NetoNativeBridge.CallAsync("probe_tunnel");
                }
                if (_tickCount % 60 == 0 && !_autoRefreshing)
                {
                    var active = snap.ActiveProfile;
                    var nowUnix = NetoFormat.NowUnixMs() / 1000UL;
                    if (active != null && !string.IsNullOrWhiteSpace(active.Url) && active.AutoUpdateMins > 0 &&
                        nowUnix > active.UpdatedAtUnix && (nowUnix - active.UpdatedAtUnix) >= active.AutoUpdateMins * 60UL)
                    {
                        _ = AutoRefreshProfileAsync(active.Id);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private async Task AutoRefreshProfileAsync(string profileId)
    {
        _autoRefreshing = true;
        try
        {
            var res = await NetoNativeBridge.CallAsync("refresh_profile", new { id = profileId });
            var snap = res.Deserialize<NetoSnapshot>();
            if (snap != null)
            {
                EnqueueUi(() => { Snapshot = snap; SnapshotChanged?.Invoke(snap); });
            }
        }
        catch
        {
        }
        finally
        {
            _autoRefreshing = false;
        }
    }

    public async Task RefreshSnapshotAsync()
    {
        var node = await NetoNativeBridge.CallAsync("snapshot");
        var snap = node.Deserialize<NetoSnapshot>() ?? new NetoSnapshot();
        EnqueueUi(() =>
        {
            Snapshot = snap;
            SnapshotChanged?.Invoke(snap);
        });
    }

    public Task ToggleConnectAsync() => InvokeSnapshotCallAsync("toggle_connect", null);
    public Task ConnectAsync() => InvokeSnapshotCallAsync("connect", null);
    public Task DisconnectAsync() => InvokeSnapshotCallAsync("disconnect", null);
    public Task SelectNodeAsync(int index) => InvokeSnapshotCallAsync("select_node", new { index });
    public Task SelectGroupProxyAsync(string groupName, string proxyName) =>
        InvokeSnapshotCallAsync("select_group_proxy", new { group_name = groupName, proxy_name = proxyName });
    public Task SetNodeSortAsync(string mode) => InvokeSnapshotCallAsync("set_node_sort", new { mode });
    public Task SetRoutingModeAsync(string mode) => InvokeSnapshotCallAsync("set_routing_mode", new { mode });
    public Task SetSystemProxyAsync(bool enabled) => InvokeSnapshotCallAsync("set_system_proxy_enabled", new { enabled });
    public Task SetTunEnabledAsync(bool enabled) => InvokeSnapshotCallAsync("set_tun_enabled", new { enabled });
    public Task InstallTunServiceAsync() => InvokeSnapshotCallAsync("install_tun_service", null);
    public Task UninstallTunServiceAsync() => InvokeSnapshotCallAsync("uninstall_tun_service", null);
    public Task AddCustomRuleAsync(string rule) => InvokeSnapshotCallAsync("add_custom_rule", new { rule });

    public async Task EnableTunAsync(bool enabled)
    {
        if (enabled &&
            !Snapshot.Connection.CanUseTun &&
            !string.Equals(Snapshot.Connection.TunServiceStatus, "unsupported", StringComparison.OrdinalIgnoreCase))
        {
            await InstallTunServiceAsync();
            if (LastActionError != null || !Snapshot.Connection.CanUseTun)
            {
                EnqueueUi(() => SnapshotChanged?.Invoke(Snapshot));
                return;
            }
        }
        await SetTunEnabledAsync(enabled);
    }

    public Task UpdateSettingsAsync(JsonObject patch) => InvokeSnapshotCallAsync("update_settings", patch);
    public Task UpdateNetworkSettingsAsync(JsonObject patch) => InvokeSnapshotCallAsync("update_network_settings", patch);
    public Task RefreshProfileAsync(string? id = null) =>
        InvokeSnapshotCallAsync("refresh_profile", id is null ? new { } : new { id });
    public Task SelectProfileAsync(string id) => InvokeSnapshotCallAsync("select_profile", new { id });
    public Task DeleteProfileAsync(string id) => InvokeSnapshotCallAsync("delete_profile", new { id });
    public Task ResetDashboardWidgetsAsync() => InvokeSnapshotCallAsync("reset_dashboard_widgets", null);

    public Task UpdateDashboardWidgetAsync(string id, bool? enabled = null, int? moveDelta = null)
    {
        var obj = new JsonObject { ["id"] = id };
        if (enabled.HasValue) obj["enabled"] = enabled.Value;
        if (moveDelta.HasValue) obj["move_delta"] = moveDelta.Value;
        return InvokeSnapshotCallAsync("update_dashboard_widget", obj);
    }

    public async Task TestNodeAsync(int index)
    {
        try
        {
            await NetoNativeBridge.CallAsync("test_node", new { index });
        }
        catch
        {
        }
        await RefreshSnapshotAsync();
    }

    public Task TestAllNodesAsync() => InvokeSnapshotCallAsync("test_all_nodes", null);
    public Task TestGroupNodesAsync(string groupName) =>
        InvokeSnapshotCallAsync("test_group_nodes", new { group_name = groupName });

    public async Task ProbeTunnelAsync()
    {
        try
        {
            await NetoNativeBridge.CallAsync("probe_tunnel");
        }
        catch
        {
        }
        await RefreshSnapshotAsync();
    }

    public async Task<NetoNetworkCheckResult?> CheckConnectivityAsync()
    {
        EnqueueUi(() => { LastActionError = null; });
        try
        {
            var res = await NetoNativeBridge.CallAsync("check_connectivity");
            var parsed = res.Deserialize<NetoNetworkCheckResult>();
            await RefreshSnapshotAsync();
            return parsed;
        }
        catch (Exception ex)
        {
            EnqueueUi(() => { LastActionError = ex.Message; SnapshotChanged?.Invoke(Snapshot); });
            return null;
        }
    }

    public async Task<NetoConnectionsSnapshot> GetConnectionsAsync()
    {
        try
        {
            var res = await NetoNativeBridge.CallAsync("list_connections");
            return res.Deserialize<NetoConnectionsSnapshot>() ?? new NetoConnectionsSnapshot();
        }
        catch
        {
            return new NetoConnectionsSnapshot();
        }
    }

    public async Task CloseConnectionAsync(ulong id)
    {
        try
        {
            await NetoNativeBridge.CallAsync("close_connection", new { id });
        }
        catch
        {
        }
    }

    public async Task CloseAllConnectionsAsync()
    {
        try
        {
            await NetoNativeBridge.CallAsync("close_all_connections");
        }
        catch
        {
        }
        await RefreshSnapshotAsync();
    }

    public async Task ClearClosedConnectionsAsync()
    {
        try
        {
            await NetoNativeBridge.CallAsync("clear_closed_connections");
        }
        catch
        {
        }
    }

    public async Task<NetoRulesListResult> GetRulesAsync(string filter = "", int limit = 500)
    {
        try
        {
            var res = await NetoNativeBridge.CallAsync("list_rules", new { filter, limit });
            return res.Deserialize<NetoRulesListResult>() ?? new NetoRulesListResult();
        }
        catch
        {
            return new NetoRulesListResult();
        }
    }

    public async Task RefreshAllProfilesAsync()
    {
        EnqueueUi(() => { Busy = true; LastActionError = null; SnapshotChanged?.Invoke(Snapshot); });
        try
        {
            var res = await NetoNativeBridge.CallAsync("refresh_all_profiles");
            var snap = res["snapshot"].Deserialize<NetoSnapshot>();
            if (snap != null)
            {
                EnqueueUi(() => { Snapshot = snap; });
            }
        }
        catch (Exception ex)
        {
            EnqueueUi(() => { LastActionError = ex.Message; });
        }
        finally
        {
            EnqueueUi(() => { Busy = false; SnapshotChanged?.Invoke(Snapshot); });
        }
    }

    public Task UpdateProfileAsync(
        string id,
        string? name = null,
        string? url = null,
        string? flag = null,
        int? autoUpdateMins = null,
        string? yaml = null)
    {
        var obj = new JsonObject { ["id"] = id };
        if (name != null) obj["name"] = name;
        if (url != null) obj["url"] = url;
        if (flag != null) obj["flag"] = flag;
        if (autoUpdateMins.HasValue) obj["auto_update_mins"] = autoUpdateMins.Value;
        if (yaml != null) obj["yaml"] = yaml;
        return InvokeSnapshotCallAsync("update_profile", obj);
    }

    public async Task<string> GetProfileYamlAsync(string? id = null)
    {
        var res = await NetoNativeBridge.CallAsync("get_profile_yaml", id is null ? new { } : new { id });
        return res["yaml"]?.GetValue<string>() ?? "";
    }

    public async Task AddRemoteProfileAsync(string name, string url, int autoUpdateMins = 60, string flag = "aerion")
    {
        EnqueueUi(() => { Busy = true; LastActionError = null; SnapshotChanged?.Invoke(Snapshot); });
        try
        {
            var res = await NetoNativeBridge.CallAsync("add_remote_profile", new
            {
                name,
                url,
                flag = string.IsNullOrWhiteSpace(flag) ? "aerion" : flag,
                auto_update_mins = autoUpdateMins,
            });
            var snap = res["snapshot"].Deserialize<NetoSnapshot>() ?? new NetoSnapshot();
            EnqueueUi(() => { Snapshot = snap; });
        }
        catch (Exception ex)
        {
            EnqueueUi(() => { LastActionError = ex.Message; });
        }
        finally
        {
            EnqueueUi(() => { Busy = false; SnapshotChanged?.Invoke(Snapshot); });
        }
    }

    public async Task ImportLocalProfileAsync(string name, string yaml)
    {
        EnqueueUi(() => { Busy = true; LastActionError = null; SnapshotChanged?.Invoke(Snapshot); });
        try
        {
            var res = await NetoNativeBridge.CallAsync("import_local_profile", new { name, yaml });
            var snap = res["snapshot"].Deserialize<NetoSnapshot>() ?? new NetoSnapshot();
            EnqueueUi(() => { Snapshot = snap; });
        }
        catch (Exception ex)
        {
            EnqueueUi(() => { LastActionError = ex.Message; });
        }
        finally
        {
            EnqueueUi(() => { Busy = false; SnapshotChanged?.Invoke(Snapshot); });
        }
    }

    public Task UpsertManagedProfileAsync(JsonObject payload) =>
        InvokeSnapshotCallAsync("upsert_managed_profile", payload);

    public async Task<JsonNode?> SyncManagedSubscriptionAsync(
        string url,
        string name = "Managed",
        string id = "managed-default",
        string flag = "aerion",
        string? userAgent = null)
    {
        var payload = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["url"] = url,
            ["flag"] = flag,
        };
        if (!string.IsNullOrWhiteSpace(userAgent)) payload["user_agent"] = userAgent;
        var res = await NetoNativeBridge.CallAsync("sync_managed_subscription", payload);
        var snap = res["snapshot"].Deserialize<NetoSnapshot>();
        if (snap != null)
        {
            EnqueueUi(() =>
            {
                Snapshot = snap;
                SnapshotChanged?.Invoke(snap);
            });
        }
        return res["user_info"];
    }

    public async Task<List<NetoLogEntry>> GetLogsAsync(int limit = 200, string? level = null, string? filter = null)
    {
        var obj = new JsonObject { ["limit"] = limit };
        if (!string.IsNullOrWhiteSpace(level) && !string.Equals(level, "all", StringComparison.OrdinalIgnoreCase))
        {
            obj["level"] = level;
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            obj["filter"] = filter;
        }
        var res = await NetoNativeBridge.CallAsync("logs", obj);
        return res.Deserialize<List<NetoLogEntry>>() ?? [];
    }

    public Task ClearLogsAsync() => NetoNativeBridge.CallAsync("clear_logs");

    public async Task<NetoRouteTestResult> TestRouteAsync(string target, string network = "tcp")
    {
        var res = await NetoNativeBridge.CallAsync("test_route", new { target, network });
        return res.Deserialize<NetoRouteTestResult>() ?? new NetoRouteTestResult();
    }

    public async Task<NetoUpdateCheckResult> CheckUpdateAsync(string repo, string currentVersion, bool preferInstaller = true)
    {
        var res = await NetoNativeBridge.CallAsync("check_update", new
        {
            repo,
            current_version = currentVersion,
            prefer_installer = preferInstaller,
        });
        return res.Deserialize<NetoUpdateCheckResult>() ?? new NetoUpdateCheckResult();
    }

    public async Task<NetoDownloadUpdateResult> DownloadUpdateAsync(string url, string destPath)
    {
        var res = await NetoNativeBridge.CallAsync("download_update", new
        {
            url,
            dest_path = destPath,
        });
        return res.Deserialize<NetoDownloadUpdateResult>() ?? new NetoDownloadUpdateResult();
    }

    private async Task InvokeSnapshotCallAsync(string method, object? parameters)
    {
        EnqueueUi(() => { LastActionError = null; });
        try
        {
            var res = await NetoNativeBridge.CallAsync(method, parameters);
            var snap = res.Deserialize<NetoSnapshot>();
            if (snap != null)
            {
                EnqueueUi(() =>
                {
                    Snapshot = snap;
                    SnapshotChanged?.Invoke(snap);
                });
            }
        }
        catch (Exception ex)
        {
            EnqueueUi(() =>
            {
                LastActionError = ex.Message;
                SnapshotChanged?.Invoke(Snapshot);
            });
        }
    }

    private void EnqueueUi(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        NetoNativeBridge.Shutdown();
    }
}
