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
    private readonly HomePageView _homeView;
    private readonly NodesPageView _nodesView;
    private readonly ConnectionsPageView _connectionsView;
    private readonly RulesPageView _rulesView;
    private readonly ProfilesPageView _profilesView;
    private readonly SettingsPageView _settingsView;
    private readonly AppWindow _appWindow;
    private readonly Win32TrayIcon _tray;
    private string _appliedTheme = "";
    private bool _syncingNav;
    private bool _quitting;

    public bool StartedHidden { get; }

    public MainWindow(bool startMinimized, SingleInstanceGuard? instanceGuard)
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

        _homeView = new HomePageView(
            _client,
            () => SwitchTab("nodes"),
            () => SwitchTab("profiles"),
            detachHeaderActions: true,
            onOpenConnections: () => SwitchTab("connections"));
        _nodesView = new NodesPageView(_client);
        _connectionsView = new ConnectionsPageView(_client, detachHeaderActions: true);
        _rulesView = new RulesPageView(_client, detachHeaderActions: true, ownerHwnd: _hwnd);
        _profilesView = new ProfilesPageView(_client, _hwnd);
        _settingsView = new SettingsPageView(_client, ApplyThemeMode);
        _settingsView.ConfigureUpdater(
            "MoeclubM/Neto",
            NetoWindowHelper.GetAppVersion(),
            () => RootGrid.XamlRoot,
            ExitApplication);

        NavProfilesItem.Tapped += (_, _) => _profilesView.ResetToRoot();
        NavSettingsItem.Tapped += (_, _) => _settingsView.ResetToRoot();

        ContentHost.Children.Add(_homeView.Root);
        ContentHost.Children.Add(_nodesView.Root);
        ContentHost.Children.Add(_connectionsView.Root);
        ContentHost.Children.Add(_rulesView.Root);
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
        _tray.ToggleTunRequested += () => DispatcherQueue.TryEnqueue(async () => await _client.EnableTunAsync(!_client.Snapshot.Settings.TunEnabled));
        _tray.CopyProxyCommandRequested += () => DispatcherQueue.TryEnqueue(() =>
        {
            NetoWindowHelper.SetClipboardText(NetoWindowHelper.BuildTerminalProxyCommand(_client.Snapshot));
        });
        _tray.CloseAllConnectionsRequested += () => DispatcherQueue.TryEnqueue(async () => await _client.CloseAllConnectionsAsync());
        _tray.CheckUpdateRequested += () => DispatcherQueue.TryEnqueue(async () =>
        {
            NetoWindowHelper.RestoreAndActivate(this, _appWindow, _hwnd);
            SwitchTab("settings");
            await _settingsView.CheckForUpdatesAsync(true);
        });
        _tray.SessionEnding += () => NetoNativeBridge.Shutdown();
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);

        _client.SnapshotChanged += ApplyAllViews;
        var initialTab = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase))?[6..]?.ToLowerInvariant() ?? "home";
        SwitchTab(initialTab); var initialSub = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--subpage=", StringComparison.OrdinalIgnoreCase))?[10..]?.ToLowerInvariant(); if (!string.IsNullOrEmpty(initialSub)) { if (initialTab == "settings") _settingsView.OpenSubpage(initialSub); else if (initialTab == "profiles") _profilesView.OpenSubpage(initialSub); }
        ApplyAllViews(_client.Snapshot);

        StartedHidden = startMinimized || _client.Snapshot.Settings.SilentStart;
        if (StartedHidden)
        {
            _appWindow.Hide();
        }
        _ = TriggerStartupUpdateCheckAsync();
    }

    private async Task TriggerStartupUpdateCheckAsync()
    {
        await Task.Delay(4000);
        if (!_quitting)
        {
            DispatcherQueue.TryEnqueue(async () => await _settingsView.CheckForUpdatesAsync(false));
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
        if (tab is "connect" or "dashboard") tab = "home";
        _syncingNav = true;
        BottomSelectorBar.SelectedItem = tab switch
        {
            "nodes" => NavNodesItem,
            "connections" => NavConnectionsItem,
            "rules" => NavRulesItem,
            "profiles" => NavProfilesItem,
            "settings" => NavSettingsItem,
            _ => NavHomeItem,
        };
        _syncingNav = false;

        if (tab == "profiles") _profilesView.ResetToRoot();
        if (tab == "settings") _settingsView.ResetToRoot();

        TopHeaderBorder.Visibility = Visibility.Visible;
        PageHeaderText.Text = tab switch
        {
            "nodes" => "节点",
            "connections" => "会话",
            "rules" => "规则",
            "profiles" => "订阅",
            "settings" => "设置",
            _ => "",
        };
        PageHeaderActionHost.Content = tab switch
        {
            "home" => _homeView.HeaderActions,
            "connections" => _connectionsView.HeaderActions,
            "rules" => _rulesView.HeaderActions,
            _ => null,
        };

        _homeView.Root.Visibility = tab == "home" ? Visibility.Visible : Visibility.Collapsed;
        _nodesView.Root.Visibility = tab == "nodes" ? Visibility.Visible : Visibility.Collapsed;
        _connectionsView.Root.Visibility = tab == "connections" ? Visibility.Visible : Visibility.Collapsed;
        _rulesView.Root.Visibility = tab == "rules" ? Visibility.Visible : Visibility.Collapsed;
        _profilesView.Root.Visibility = tab == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        _settingsView.Root.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "connections")
        {
            _connectionsView.ApplySnapshot(_client.Snapshot);
        }
        else if (tab == "rules")
        {
            _rulesView.ApplySnapshot(_client.Snapshot);
        }
    }

    private void ApplyAllViews(NetoSnapshot snap)
    {
        ApplyThemeMode(snap.Settings.ThemeMode);
        _homeView.ApplySnapshot(snap, _client.LastActionError);
        _nodesView.ApplySnapshot(snap);
        _connectionsView.ApplySnapshot(snap);
        _rulesView.ApplySnapshot(snap);
        _profilesView.ApplySnapshot(snap, _client.Busy, _client.LastActionError);
        _settingsView.ApplySnapshot(snap);

        _tray.SyncFromSnapshot(snap, "Neto");
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
