using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Neto.Sdk.Windows;
using Neto.Sdk.Windows.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace Neto.Windows;

public sealed partial class MainWindow : Window
{
    private readonly NetoClient _client;
    private readonly SingleInstanceGuard? _instanceGuard;
    private readonly IntPtr _hwnd;
    private readonly ConnectPageView _connectView;
    private readonly DashboardPageView _dashboardView;
    private readonly NodesPageView _nodesView;
    private readonly ProfilesPageView _profilesView;
    private readonly SettingsPageView _settingsView;
    private readonly AppWindow _appWindow;
    private readonly Win32TrayIcon _tray;
    private string _appliedTheme = "";
    private bool _syncingNav;
    private bool _quitting;

    public bool StartedHidden { get; }

    public MainWindow(bool startMinimized, bool isRelaunchElevated, SingleInstanceGuard? instanceGuard)
    {
        _instanceGuard = instanceGuard;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }
        NetoWindowHelper.ConfigureWindow(_appWindow, 920, 650);
        _appWindow.Closing += OnAppWindowClosing;

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neto");
        _client = new NetoClient(dataDir, "Neto", "Aerion", DispatcherQueue);

        _connectView = new ConnectPageView(_client, () => SwitchTab("nodes"), () => SwitchTab("profiles"), RequestElevation);
        _dashboardView = new DashboardPageView(_client, () => SwitchTab("nodes"), () => SwitchTab("profiles"), RequestElevation, detachHeaderActions: true);
        _nodesView = new NodesPageView(_client);
        _profilesView = new ProfilesPageView(_client, _hwnd);
        _settingsView = new SettingsPageView(_client, ApplyThemeMode, RequestElevation);

        ContentHost.Children.Add(_connectView.Root);
        ContentHost.Children.Add(_dashboardView.Root);
        ContentHost.Children.Add(_nodesView.Root);
        ContentHost.Children.Add(_profilesView.Root);
        ContentHost.Children.Add(_settingsView.Root);

        _tray = new Win32TrayIcon(_hwnd, "Neto");
        _tray.ShowRequested += ShowFromExternalActivation;
        _tray.PrimaryClickRequested += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (NetoWindowHelper.IsWindowForegroundAndVisible(_appWindow, _hwnd))
            {
                _appWindow.Hide();
            }
            else
            {
                NetoWindowHelper.RestoreAndActivate(this, _appWindow, _hwnd);
            }
        });
        _tray.ToggleConnectRequested += () => DispatcherQueue.TryEnqueue(async () => await _client.ToggleConnectAsync());
        _tray.SelectRoutingModeRequested += mode => DispatcherQueue.TryEnqueue(async () => await _client.SetRoutingModeAsync(mode));
        _tray.SelectNodeRequested += idx => DispatcherQueue.TryEnqueue(async () => await _client.SelectNodeAsync(idx));
        _tray.ToggleSystemProxyRequested += () => DispatcherQueue.TryEnqueue(async () => await _client.SetSystemProxyAsync(!_client.Snapshot.Settings.SystemProxyEnabled));
        _tray.ToggleTunRequested += () => DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_client.Snapshot.Settings.TunEnabled && !_client.Snapshot.Connection.CanUseTun)
            {
                RequestElevation();
                return;
            }
            await _client.SetTunEnabledAsync(!_client.Snapshot.Settings.TunEnabled);
        });
        _tray.CopyProxyCommandRequested += () => DispatcherQueue.TryEnqueue(() =>
        {
            NetoWindowHelper.SetClipboardText(NetoWindowHelper.BuildTerminalProxyCommand(_client.Snapshot));
        });
        _tray.RestartElevatedRequested += () => DispatcherQueue.TryEnqueue(RequestElevation);
        _tray.SessionEnding += () => NetoNativeBridge.Shutdown();
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);

        _client.SnapshotChanged += ApplyAllViews;
        var initialTab = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase))?[6..]?.ToLowerInvariant() ?? "connect";
        SwitchTab(initialTab);
        ApplyAllViews(_client.Snapshot);

        StartedHidden = (startMinimized || _client.Snapshot.Settings.SilentStart) && !isRelaunchElevated;
        if (StartedHidden)
        {
            _appWindow.Hide();
        }
    }

    public void ShowFromExternalActivation()
    {
        DispatcherQueue.TryEnqueue(() => NetoWindowHelper.RestoreAndActivate(this, _appWindow, _hwnd));
    }

    private void ApplyThemeMode(string themeMode)
    {
        if (_appliedTheme == themeMode) return;
        _appliedTheme = themeMode;
        NetoWindowHelper.ApplyTheme(RootGrid, _hwnd, themeMode);
    }

    private void RequestElevation()
    {
        if (NetoWindowHelper.TryRestartElevated(_instanceGuard))
        {
            ExitApplication();
        }
    }

    private void OnNavSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingNav) return;
        if (sender.SelectedItem is SelectorBarItem { Tag: string tag })
        {
            SwitchTab(tag);
        }
    }

    private void SwitchTab(string tab)
    {
        _syncingNav = true;
        BottomSelectorBar.SelectedItem = tab switch
        {
            "dashboard" => NavDashboardItem,
            "nodes" => NavNodesItem,
            "profiles" => NavProfilesItem,
            "settings" => NavSettingsItem,
            _ => NavConnectItem,
        };
        _syncingNav = false;

        if (tab == "connect")
        {
            TopHeaderBorder.Visibility = Visibility.Collapsed;
            PageHeaderActionHost.Content = null;
        }
        else
        {
            TopHeaderBorder.Visibility = Visibility.Visible;
            PageHeaderText.Text = tab switch
            {
                "dashboard" => "主页",
                "nodes" => "节点",
                "profiles" => "订阅",
                "settings" => "设置",
                _ => "",
            };
            PageHeaderActionHost.Content = tab == "dashboard" ? _dashboardView.HeaderActions : null;
        }

        _connectView.Root.Visibility = tab == "connect" ? Visibility.Visible : Visibility.Collapsed;
        _dashboardView.Root.Visibility = tab == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        _nodesView.Root.Visibility = tab == "nodes" ? Visibility.Visible : Visibility.Collapsed;
        _profilesView.Root.Visibility = tab == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        _settingsView.Root.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyAllViews(NetoSnapshot snap)
    {
        ApplyThemeMode(snap.Settings.ThemeMode);
        _connectView.ApplySnapshot(snap, _client.LastActionError);
        _dashboardView.ApplySnapshot(snap);
        _nodesView.ApplySnapshot(snap);
        _profilesView.ApplySnapshot(snap, _client.Busy, _client.LastActionError);
        _settingsView.ApplySnapshot(snap);

        _tray.SyncFromSnapshot(snap, "Neto");
        var portText = string.IsNullOrEmpty(snap.Connection.HttpProxyAddr) ? $"127.0.0.1:{snap.Settings.MixedPort}" : snap.Connection.HttpProxyAddr;
        BottomStatusText.Text = snap.Connection.CanUseTun ? $"管理员 · {portText}" : portText;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_quitting)
        {
            args.Cancel = true;
            _appWindow.Hide();
        }
    }

    private void ExitApplication()
    {
        _quitting = true;
        _tray.Dispose();
        _client.Dispose();
        _instanceGuard?.Dispose();
        Close();
    }
}
