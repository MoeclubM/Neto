using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;

namespace Neto.Sdk.Windows;

public sealed class NetoClient : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private int _tickCount;
    private bool _disposed;

    public NetoSnapshot Snapshot { get; private set; } = new();
    public string? LastActionError { get; private set; }
    public bool Busy { get; private set; }

    public event Action<NetoSnapshot>? SnapshotChanged;

    public NetoClient(string dataDir, string appId, string userAgent, DispatcherQueue dispatcher)
    {
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
            }
        }
        catch
        {
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
    public Task UpdateSettingsAsync(JsonObject patch) => InvokeSnapshotCallAsync("update_settings", patch);
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

    public async Task AddRemoteProfileAsync(string name, string url, int autoUpdateMins = 60)
    {
        EnqueueUi(() => { Busy = true; LastActionError = null; SnapshotChanged?.Invoke(Snapshot); });
        try
        {
            var res = await NetoNativeBridge.CallAsync("add_remote_profile", new
            {
                name,
                url,
                flag = "aerion",
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

    public async Task<List<NetoLogEntry>> GetLogsAsync(int limit = 150)
    {
        var res = await NetoNativeBridge.CallAsync("logs", new { limit });
        return res.Deserialize<List<NetoLogEntry>>() ?? [];
    }

    public Task ClearLogsAsync() => NetoNativeBridge.CallAsync("clear_logs");

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
