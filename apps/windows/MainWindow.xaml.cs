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
    private readonly ConnectPageView _connectView;
    private readonly DashboardPageView _dashboardView;
    private readonly NodesPageView _nodesView;
    private readonly ProfilesPageView _profilesView;
    private readonly SettingsPageView _settingsView;
    private readonly AppWindow _appWindow;
    private readonly Win32TrayIcon _tray;
    private string _currentTab = "connect";
    private bool _quitting;

    public MainWindow(bool startMinimized)
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }
        _appWindow.Resize(new SizeInt32(880, 640));
        _appWindow.Closing += OnAppWindowClosing;

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neto");
        _client = new NetoClient(dataDir, "Neto", "Aerion", DispatcherQueue);

        _connectView = new ConnectPageView(_client, () => SwitchTab("nodes"));
        _dashboardView = new DashboardPageView(_client, () => SwitchTab("nodes"), () => SwitchTab("profiles"));
        _nodesView = new NodesPageView(_client);
        _profilesView = new ProfilesPageView(_client);
        _settingsView = new SettingsPageView(_client);

        ContentHost.Children.Add(_connectView.Root);
        ContentHost.Children.Add(_dashboardView.Root);
        ContentHost.Children.Add(_nodesView.Root);
        ContentHost.Children.Add(_profilesView.Root);
        ContentHost.Children.Add(_settingsView.Root);

        _tray = new Win32TrayIcon(hwnd, "Neto");
        _tray.ShowRequested += () => DispatcherQueue.TryEnqueue(() => { _appWindow.Show(); Activate(); });
        _tray.ToggleConnectRequested += () => DispatcherQueue.TryEnqueue(async () => await _client.ToggleConnectAsync());
        _tray.ToggleSystemProxyRequested += () => DispatcherQueue.TryEnqueue(async () => await _client.SetSystemProxyAsync(!_client.Snapshot.Settings.SystemProxyEnabled));
        _tray.ToggleTunRequested += () => DispatcherQueue.TryEnqueue(async () => await _client.SetTunEnabledAsync(!_client.Snapshot.Settings.TunEnabled));
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);

        _client.SnapshotChanged += ApplyAllViews;
        SwitchTab("connect");
        ApplyAllViews(_client.Snapshot);

        if (startMinimized)
        {
            _appWindow.Hide();
        }
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            SwitchTab(tag);
        }
    }

    private void SwitchTab(string tab)
    {
        _currentTab = tab;
        // Connect page has NO title header; other pages display a compact header
        if (tab == "connect")
        {
            TopHeaderBorder.Visibility = Visibility.Collapsed;
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
        }

        _connectView.Root.Visibility = tab == "connect" ? Visibility.Visible : Visibility.Collapsed;
        _dashboardView.Root.Visibility = tab == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        _nodesView.Root.Visibility = tab == "nodes" ? Visibility.Visible : Visibility.Collapsed;
        _profilesView.Root.Visibility = tab == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        _settingsView.Root.Visibility = tab == "settings" ? Visibility.Visible : Visibility.Collapsed;

        NetoUiFactory.ApplyPrimaryButton(NavConnectBtn, tab == "connect");
        NetoUiFactory.ApplyPrimaryButton(NavDashboardBtn, tab == "dashboard");
        NetoUiFactory.ApplyPrimaryButton(NavNodesBtn, tab == "nodes");
        NetoUiFactory.ApplyPrimaryButton(NavProfilesBtn, tab == "profiles");
        NetoUiFactory.ApplyPrimaryButton(NavSettingsBtn, tab == "settings");
    }

    private void ApplyAllViews(NetoSnapshot snap)
    {
        _connectView.ApplySnapshot(snap, _client.LastActionError);
        _dashboardView.ApplySnapshot(snap);
        _nodesView.ApplySnapshot(snap);
        _profilesView.ApplySnapshot(snap, _client.Busy, _client.LastActionError);
        _settingsView.ApplySnapshot(snap);

        _tray.Connected = snap.Connection.Connected;
        _tray.SystemProxyEnabled = snap.Settings.SystemProxyEnabled;
        _tray.TunEnabled = snap.Settings.TunEnabled;
        _tray.UpdateTooltip(snap.Connection.Connected && !string.IsNullOrEmpty(snap.Connection.ActiveNodeName)
            ? $"Neto · {snap.Connection.ActiveNodeName}"
            : "Neto");
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
        Close();
    }
}
