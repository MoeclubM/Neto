using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public sealed class SettingsPageView
{
    public ScrollViewer Root { get; }
    private readonly NetoClient _client;
    private readonly Action<string>? _onThemeChanged;
    private readonly Action? _onRequestElevation;
    private readonly ComboBox _themeCombo;
    private readonly ToggleSwitch _autostartSwitch;
    private readonly ToggleSwitch _silentStartSwitch;
    private readonly Border _adminBadge;
    private readonly Button _elevateBtn;
    private readonly NumberBox _portBox;
    private readonly NumberBox _mtuBox;
    private readonly ToggleSwitch _lanSwitch;
    private readonly ToggleSwitch _ipv6Switch;
    private readonly ComboBox _dnsModeCombo;
    private readonly TextBox _virtualDnsPoolBox;
    private readonly TextBox _bypassBox;
    private readonly TextBox _nodeDnsBox;
    private readonly TextBox _overseasDnsBox;
    private readonly TextBox _directDnsBox;
    private readonly TextBox _testUrlBox;
    private readonly TextBox _customRouteBox;
    private readonly TextBlock _saveFeedbackText;
    private readonly ComboBox _shellFormatCombo;
    private readonly ComboBox _logLevelCombo;
    private readonly TextBox _logSearchBox;
    private readonly TextBox _logsBox;
    private readonly StackPanel _extraFooterHost;
    private bool _updating;
    private bool _inputsInitialized;
    private bool _inputsDirty;

    private static readonly (string Key, string Label)[] ShellFormats =
    [
        ("powershell", "PowerShell"),
        ("cmd", "CMD"),
        ("bash", "Bash"),
    ];

    private static readonly (string Key, string Label)[] LogLevels =
    [
        ("all", "全部级别"),
        ("info", "INFO"),
        ("warn", "WARN"),
        ("error", "ERROR"),
    ];

    private static readonly (string Key, string Label)[] ThemeModes =
    [
        ("system", "跟随系统"),
        ("light", "浅色模式"),
        ("dark", "深色模式"),
    ];

    private static readonly (string Key, string Label)[] DnsModes =
    [
        ("virtual", "虚拟 DNS"),
        ("over_tcp", "TCP 转发"),
        ("direct", "直连 DNS"),
    ];

    public SettingsPageView(NetoClient client, Action<string>? onThemeChanged = null, Action? onRequestElevation = null)
    {
        _client = client;
        _onThemeChanged = onThemeChanged;
        _onRequestElevation = onRequestElevation;
        var outer = new StackPanel
        {
            MaxWidth = 860,
            Spacing = 10,
            Padding = new Thickness(18, 8, 18, 24),
        };

        var privRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _adminBadge = NetoUiFactory.CreateTagBadge("已获管理员权限", Color.FromArgb(255, 42, 168, 118));
        _adminBadge.Visibility = Visibility.Collapsed;
        _elevateBtn = new Button { Content = "以管理员身份重启", Padding = new Thickness(10, 4, 10, 4), Visibility = Visibility.Collapsed };
        _elevateBtn.Click += (_, _) => _onRequestElevation?.Invoke();
        privRight.Children.Add(_adminBadge);
        privRight.Children.Add(_elevateBtn);

        var generalStack = new StackPanel { Spacing = 10 };
        generalStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("常规与系统", privRight));
        var generalGrid = new Grid { ColumnSpacing = 16 };
        generalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        generalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        generalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _themeCombo = new ComboBox { Header = "界面主题", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in ThemeModes) _themeCombo.Items.Add(label);
        _themeCombo.SelectionChanged += async (_, _) =>
        {
            if (_updating || _themeCombo.SelectedIndex < 0) return;
            var mode = ThemeModes[Math.Clamp(_themeCombo.SelectedIndex, 0, ThemeModes.Length - 1)].Key;
            _onThemeChanged?.Invoke(mode);
            await _client.UpdateSettingsAsync(new JsonObject { ["theme_mode"] = mode });
        };
        _autostartSwitch = new ToggleSwitch { Header = "开机自动启动", OffContent = "关", OnContent = "开", MinWidth = 0 };
        _autostartSwitch.Toggled += async (_, _) =>
        {
            if (_updating) return;
            await _client.UpdateSettingsAsync(new JsonObject { ["autostart"] = _autostartSwitch.IsOn });
        };
        _silentStartSwitch = new ToggleSwitch { Header = "启动时最小化到托盘", OffContent = "关", OnContent = "开", MinWidth = 0 };
        _silentStartSwitch.Toggled += async (_, _) =>
        {
            if (_updating) return;
            await _client.UpdateSettingsAsync(new JsonObject { ["silent_start"] = _silentStartSwitch.IsOn });
        };
        Grid.SetColumn(_autostartSwitch, 1);
        Grid.SetColumn(_silentStartSwitch, 2);
        generalGrid.Children.Add(_themeCombo);
        generalGrid.Children.Add(_autostartSwitch);
        generalGrid.Children.Add(_silentStartSwitch);

        var utilRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var openDataBtn = new Button { Content = "打开数据目录" };
        openDataBtn.Click += (_, _) => NetoWindowHelper.OpenFolder(_client.DataDir);
        _shellFormatCombo = new ComboBox { Width = 132 };
        foreach (var (_, label) in ShellFormats) _shellFormatCombo.Items.Add(label);
        _shellFormatCombo.SelectedIndex = 0;
        var copyCmdBtn = new Button { Content = "复制终端环境变量" };
        copyCmdBtn.Click += async (_, _) =>
        {
            var fmt = ShellFormats[Math.Clamp(_shellFormatCombo.SelectedIndex, 0, ShellFormats.Length - 1)].Key;
            if (NetoWindowHelper.SetClipboardText(NetoWindowHelper.BuildTerminalProxyCommand(_client.Snapshot, fmt)))
            {
                copyCmdBtn.Content = "已复制";
                await Task.Delay(1200);
                copyCmdBtn.Content = "复制终端环境变量";
            }
        };
        utilRow.Children.Add(openDataBtn);
        utilRow.Children.Add(_shellFormatCombo);
        utilRow.Children.Add(copyCmdBtn);
        generalStack.Children.Add(generalGrid);
        generalStack.Children.Add(utilRow);
        outer.Children.Add(NetoUiFactory.CreateCard(generalStack));

        var saveRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _saveFeedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 42, 168, 118)),
        };
        var saveBtn = new Button { Content = "保存网络配置" };
        NetoUiFactory.ApplyPrimaryButton(saveBtn, true);
        saveBtn.Click += async (_, _) => await SaveNetworkSettingsAsync();
        saveRight.Children.Add(_saveFeedbackText);
        saveRight.Children.Add(saveBtn);

        var netStack = new StackPanel { Spacing = 10 };
        netStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("网络、TUN 与 DNS", saveRight));

        var netSwitches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        _lanSwitch = new ToggleSwitch { Header = "允许局域网连接", OffContent = "关", OnContent = "开", MinWidth = 0 };
        _lanSwitch.Toggled += async (_, _) =>
        {
            if (_updating) return;
            await _client.UpdateNetworkSettingsAsync(new JsonObject { ["allow_lan"] = _lanSwitch.IsOn });
        };
        _ipv6Switch = new ToggleSwitch { Header = "启用 IPv6", OffContent = "关", OnContent = "开", MinWidth = 0 };
        _ipv6Switch.Toggled += async (_, _) =>
        {
            if (_updating) return;
            await _client.UpdateNetworkSettingsAsync(new JsonObject { ["ipv6_enabled"] = _ipv6Switch.IsOn });
        };
        netSwitches.Children.Add(_lanSwitch);
        netSwitches.Children.Add(_ipv6Switch);
        netStack.Children.Add(netSwitches);

        var formGrid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < 4; r++) formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _portBox = new NumberBox { Header = "本地混合端口", Minimum = 1024, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _mtuBox = new NumberBox { Header = "TUN MTU", Minimum = 1280, Maximum = 9000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _dnsModeCombo = new ComboBox { Header = "TUN DNS 模式", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in DnsModes) _dnsModeCombo.Items.Add(label);
        _virtualDnsPoolBox = new TextBox { Header = "虚拟 DNS 网段" };
        _nodeDnsBox = new TextBox { Header = "节点解析 DoH" };
        _testUrlBox = new TextBox { Header = "连通测速地址" };
        _overseasDnsBox = new TextBox { Header = "代理 DNS" };
        _directDnsBox = new TextBox { Header = "直连 DNS" };
        _bypassBox = new TextBox { Header = "系统代理绕过列表（分号分隔）" };
        _customRouteBox = new TextBox
        {
            Header = "自定义分流规则 YAML（留空使用订阅规则）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 88,
            MaxHeight = 220,
            FontFamily = new FontFamily("Consolas"),
        };

        PlaceGridCell(formGrid, _portBox, 0, 0);
        PlaceGridCell(formGrid, _mtuBox, 0, 1);
        PlaceGridCell(formGrid, _dnsModeCombo, 1, 0);
        PlaceGridCell(formGrid, _virtualDnsPoolBox, 1, 1);
        PlaceGridCell(formGrid, _nodeDnsBox, 2, 0);
        PlaceGridCell(formGrid, _testUrlBox, 2, 1);
        PlaceGridCell(formGrid, _overseasDnsBox, 3, 0);
        PlaceGridCell(formGrid, _directDnsBox, 3, 1);

        _portBox.ValueChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        _mtuBox.ValueChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        _dnsModeCombo.SelectionChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        foreach (var tb in new[] { _virtualDnsPoolBox, _nodeDnsBox, _testUrlBox, _overseasDnsBox, _directDnsBox, _bypassBox, _customRouteBox })
        {
            tb.TextChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        }

        netStack.Children.Add(formGrid);
        netStack.Children.Add(_bypassBox);
        netStack.Children.Add(_customRouteBox);
        outer.Children.Add(NetoUiFactory.CreateCard(netStack));

        var logActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var refreshLogBtn = new Button { Content = "刷新" };
        var copyLogBtn = new Button { Content = "复制" };
        var clearLogBtn = new Button { Content = "清空" };
        logActions.Children.Add(refreshLogBtn);
        logActions.Children.Add(copyLogBtn);
        logActions.Children.Add(clearLogBtn);
        var logStack = new StackPanel { Spacing = 8 };
        logStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("运行日志", logActions));
        var logFilterRow = new Grid { ColumnSpacing = 8 };
        logFilterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        logFilterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _logLevelCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in LogLevels) _logLevelCombo.Items.Add(label);
        _logLevelCombo.SelectedIndex = 0;
        _logLevelCombo.SelectionChanged += async (_, _) => { if (_inputsInitialized) await RefreshLogsAsync(); };
        _logSearchBox = new TextBox { PlaceholderText = "筛选日志内容" };
        _logSearchBox.TextChanged += async (_, _) => { if (_inputsInitialized) await RefreshLogsAsync(); };
        Grid.SetColumn(_logSearchBox, 1);
        logFilterRow.Children.Add(_logLevelCombo);
        logFilterRow.Children.Add(_logSearchBox);
        logStack.Children.Add(logFilterRow);
        _logsBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 130,
            MaxHeight = 240,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
        };
        refreshLogBtn.Click += async (_, _) => await RefreshLogsAsync();
        copyLogBtn.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(_logsBox.Text) && NetoWindowHelper.SetClipboardText(_logsBox.Text))
            {
                copyLogBtn.Content = "已复制";
                await Task.Delay(1000);
                copyLogBtn.Content = "复制";
            }
        };
        clearLogBtn.Click += async (_, _) => { await _client.ClearLogsAsync(); _logsBox.Text = ""; };
        logStack.Children.Add(_logsBox);
        outer.Children.Add(NetoUiFactory.CreateCard(logStack));

        _extraFooterHost = new StackPanel { Spacing = 12 };
        outer.Children.Add(_extraFooterHost);

        Root = NetoUiFactory.CreateCenteredScrollViewer(outer);
    }

    private static void PlaceGridCell(Grid grid, FrameworkElement child, int row, int col)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, col);
        grid.Children.Add(child);
    }

    public void SetExtraFooter(UIElement? element)
    {
        _extraFooterHost.Children.Clear();
        if (element != null) _extraFooterHost.Children.Add(element);
    }

    public void ApplySnapshot(NetoSnapshot snap)
    {
        var s = snap.Settings;
        _updating = true;
        _adminBadge.Visibility = snap.Connection.CanUseTun ? Visibility.Visible : Visibility.Collapsed;
        _elevateBtn.Visibility = !snap.Connection.CanUseTun && _onRequestElevation != null ? Visibility.Visible : Visibility.Collapsed;

        var themeIdx = Array.FindIndex(ThemeModes, m => m.Key == s.ThemeMode);
        _themeCombo.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;
        _autostartSwitch.IsOn = s.Autostart;
        _silentStartSwitch.IsOn = s.SilentStart;
        _lanSwitch.IsOn = s.AllowLan;
        _ipv6Switch.IsOn = s.Ipv6Enabled;

        if (!_inputsInitialized || !_inputsDirty)
        {
            _portBox.Value = s.MixedPort;
            _mtuBox.Value = s.Mtu;
            var idx = Array.FindIndex(DnsModes, m => m.Key == s.VpnDnsMode);
            _dnsModeCombo.SelectedIndex = idx >= 0 ? idx : 0;
            _virtualDnsPoolBox.Text = s.VirtualDnsPool;
            _bypassBox.Text = s.SystemProxyBypass;
            _nodeDnsBox.Text = s.NodeDns;
            _overseasDnsBox.Text = s.OverseasDns;
            _directDnsBox.Text = s.DirectDns;
            _testUrlBox.Text = s.NodeTestUrl;
            _customRouteBox.Text = s.CustomRouteYaml;
        }
        _updating = false;

        if (!_inputsInitialized)
        {
            _inputsInitialized = true;
            _ = RefreshLogsAsync();
        }
    }

    private async Task SaveNetworkSettingsAsync()
    {
        var port = (ushort)Math.Clamp(double.IsNaN(_portBox.Value) ? 7890 : (int)_portBox.Value, 1024, 65535);
        var mtu = (ushort)Math.Clamp(double.IsNaN(_mtuBox.Value) ? 1500 : (int)_mtuBox.Value, 1280, 9000);
        var dnsMode = DnsModes[Math.Clamp(_dnsModeCombo.SelectedIndex, 0, DnsModes.Length - 1)].Key;
        var patch = new JsonObject
        {
            ["mixed_port"] = port,
            ["mtu"] = mtu,
            ["allow_lan"] = _lanSwitch.IsOn,
            ["ipv6_enabled"] = _ipv6Switch.IsOn,
            ["vpn_dns_mode"] = dnsMode,
            ["virtual_dns_pool"] = _virtualDnsPoolBox.Text.Trim(),
            ["system_proxy_bypass"] = _bypassBox.Text.Trim(),
            ["node_dns"] = _nodeDnsBox.Text.Trim(),
            ["overseas_dns"] = _overseasDnsBox.Text.Trim(),
            ["direct_dns"] = _directDnsBox.Text.Trim(),
            ["node_test_url"] = _testUrlBox.Text.Trim(),
            ["custom_route_yaml"] = _customRouteBox.Text,
        };
        _inputsDirty = false;
        await _client.UpdateNetworkSettingsAsync(patch);
        _saveFeedbackText.Text = _client.LastActionError == null ? "已保存" : _client.LastActionError;
        await Task.Delay(2000);
        if (_saveFeedbackText.Text == "已保存") _saveFeedbackText.Text = "";
    }

    public async Task RefreshLogsAsync()
    {
        var level = LogLevels[Math.Clamp(_logLevelCombo.SelectedIndex, 0, LogLevels.Length - 1)].Key;
        var filter = _logSearchBox.Text.Trim();
        var entries = await _client.GetLogsAsync(200, level, filter);
        _logsBox.Text = string.Join(Environment.NewLine, entries.Select(e => $"{NetoFormat.FormatLogTimestamp(e.TimestampMs)} [{e.Level.ToUpperInvariant()}] {e.Message}"));
    }
}
