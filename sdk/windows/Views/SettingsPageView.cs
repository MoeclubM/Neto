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
    private readonly StackPanel _rootPane;
    private readonly StackPanel _subPane;
    private readonly StackPanel _dnsTunPane;
    private readonly StackPanel _customRoutePane;
    private readonly StackPanel _systemToolsPane;
    private readonly StackPanel _logsPane;
    private readonly ComboBox _themeCombo;
    private readonly ToggleSwitch _autostartSwitch;
    private readonly ToggleSwitch _silentStartSwitch;
    private readonly Border _adminBadge;
    private readonly Button _elevateBtn;
    private readonly NumberBox _portBox;
    private readonly TextBlock _portFeedbackText;
    private readonly ToggleSwitch _lanSwitch;
    private readonly TextBlock _dnsTunSummaryText;
    private readonly TextBlock _routeSummaryText;
    private readonly TextBlock _toolsSummaryText;
    private readonly TextBlock _logsSummaryText;
    private readonly ToggleSwitch _ipv6Switch;
    private readonly NumberBox _mtuBox;
    private readonly ComboBox _dnsModeCombo;
    private readonly TextBox _virtualDnsPoolBox;
    private readonly TextBox _nodeDnsBox;
    private readonly TextBox _overseasDnsBox;
    private readonly TextBox _directDnsBox;
    private readonly TextBox _testUrlBox;
    private readonly TextBlock _dnsSaveFeedbackText;
    private readonly TextBox _customRouteBox;
    private readonly TextBlock _routeSaveFeedbackText;
    private readonly TextBox _bypassBox;
    private readonly TextBlock _bypassFeedbackText;
    private readonly ComboBox _shellFormatCombo;
    private readonly TextBox _terminalCmdPreviewBox;
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
        _rootPane = new StackPanel { Spacing = 10 };
        _subPane = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };

        var generalStack = new StackPanel { Spacing = 4 };
        generalStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("常规"));
        _themeCombo = new ComboBox { Width = 148 };
        foreach (var (_, label) in ThemeModes) _themeCombo.Items.Add(label);
        _themeCombo.SelectionChanged += async (_, _) =>
        {
            if (_updating || _themeCombo.SelectedIndex < 0) return;
            var mode = ThemeModes[Math.Clamp(_themeCombo.SelectedIndex, 0, ThemeModes.Length - 1)].Key;
            _onThemeChanged?.Invoke(mode);
            await _client.UpdateSettingsAsync(new JsonObject { ["theme_mode"] = mode });
        };
        _autostartSwitch = new ToggleSwitch { OffContent = "关", OnContent = "开", MinWidth = 0 };
        _autostartSwitch.Toggled += async (_, _) =>
        {
            if (_updating) return;
            await _client.UpdateSettingsAsync(new JsonObject { ["autostart"] = _autostartSwitch.IsOn });
        };
        _silentStartSwitch = new ToggleSwitch { OffContent = "关", OnContent = "开", MinWidth = 0 };
        _silentStartSwitch.Toggled += async (_, _) =>
        {
            if (_updating) return;
            await _client.UpdateSettingsAsync(new JsonObject { ["silent_start"] = _silentStartSwitch.IsOn });
        };
        generalStack.Children.Add(NetoUiFactory.CreateSettingRow("界面主题", _themeCombo));
        generalStack.Children.Add(NetoUiFactory.CreateSettingRow("开机自动启动", _autostartSwitch));
        generalStack.Children.Add(NetoUiFactory.CreateSettingRow("启动时最小化到托盘", _silentStartSwitch));
        _rootPane.Children.Add(NetoUiFactory.CreateCard(generalStack));

        var privRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _adminBadge = NetoUiFactory.CreateTagBadge("已获管理员权限", Color.FromArgb(255, 42, 168, 118));
        _adminBadge.Visibility = Visibility.Collapsed;
        _elevateBtn = new Button { Content = "以管理员身份重启", Padding = new Thickness(10, 4, 10, 4), Visibility = Visibility.Collapsed };
        _elevateBtn.Click += (_, _) => _onRequestElevation?.Invoke();
        privRight.Children.Add(_adminBadge);
        privRight.Children.Add(_elevateBtn);

        var accessStack = new StackPanel { Spacing = 4 };
        accessStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("网络接入", privRight));
        _portBox = new NumberBox
        {
            Width = 124,
            Minimum = 1024,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        _portBox.ValueChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        _portFeedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 42, 168, 118)),
        };
        var applyPortBtn = new Button { Content = "应用" };
        applyPortBtn.Click += async (_, _) =>
        {
            var port = (ushort)Math.Clamp(double.IsNaN(_portBox.Value) ? 7890 : (int)_portBox.Value, 1024, 65535);
            _inputsDirty = false;
            await _client.UpdateNetworkSettingsAsync(new JsonObject { ["mixed_port"] = port });
            _portFeedbackText.Text = _client.LastActionError ?? "已应用";
            await Task.Delay(1800);
            if (_portFeedbackText.Text == "已应用") _portFeedbackText.Text = "";
        };
        var portRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        portRight.Children.Add(_portFeedbackText);
        portRight.Children.Add(_portBox);
        portRight.Children.Add(applyPortBtn);
        _lanSwitch = new ToggleSwitch { OffContent = "关", OnContent = "开", MinWidth = 0 };
        _lanSwitch.Toggled += async (_, _) =>
        {
            if (_updating) return;
            await _client.UpdateNetworkSettingsAsync(new JsonObject { ["allow_lan"] = _lanSwitch.IsOn });
        };
        accessStack.Children.Add(NetoUiFactory.CreateSettingRow("本地混合端口", portRight, "HTTP 与 SOCKS5 共用监听端口"));
        accessStack.Children.Add(NetoUiFactory.CreateSettingRow("允许局域网连接", _lanSwitch));
        _rootPane.Children.Add(NetoUiFactory.CreateCard(accessStack));

        _rootPane.Children.Add(NetoUiFactory.CreateSectionHeader("高级与诊断"));
        var navGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _dnsTunSummaryText = new TextBlock();
        _routeSummaryText = new TextBlock();
        _toolsSummaryText = new TextBlock { Text = "绕过地址、环境变量与数据目录" };
        _logsSummaryText = new TextBlock { Text = "筛选、搜索与复制运行日志" };
        PlaceGridCell(navGrid, NetoUiFactory.CreateNavEntryRow("DNS 与 TUN 参数", _dnsTunSummaryText, () => OpenSubpage("dns_tun")), 0, 0);
        PlaceGridCell(navGrid, NetoUiFactory.CreateNavEntryRow("分流规则覆写", _routeSummaryText, () => OpenSubpage("custom_route")), 0, 1);
        PlaceGridCell(navGrid, NetoUiFactory.CreateNavEntryRow("系统代理与外部工具", _toolsSummaryText, () => OpenSubpage("system_tools")), 1, 0);
        PlaceGridCell(navGrid, NetoUiFactory.CreateNavEntryRow("运行日志", _logsSummaryText, () =>
        {
            OpenSubpage("logs");
            _ = RefreshLogsAsync();
        }), 1, 1);
        _rootPane.Children.Add(navGrid);

        _extraFooterHost = new StackPanel { Spacing = 10 };
        _rootPane.Children.Add(_extraFooterHost);

        _dnsTunPane = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var dnsRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _dnsSaveFeedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 42, 168, 118)),
        };
        var saveDnsBtn = new Button { Content = "保存更改" };
        NetoUiFactory.ApplyPrimaryButton(saveDnsBtn, true);
        saveDnsBtn.Click += async (_, _) => await SaveDnsAndTunAsync();
        dnsRight.Children.Add(_dnsSaveFeedbackText);
        dnsRight.Children.Add(saveDnsBtn);
        _dnsTunPane.Children.Add(NetoUiFactory.CreateSubpageHeader("DNS 与 TUN 参数", ResetToRoot, dnsRight));

        var dnsCardStack = new StackPanel { Spacing = 10 };
        _ipv6Switch = new ToggleSwitch { OffContent = "关", OnContent = "开", MinWidth = 0 };
        _ipv6Switch.Toggled += async (_, _) =>
        {
            if (_updating) return;
            await _client.UpdateNetworkSettingsAsync(new JsonObject { ["ipv6_enabled"] = _ipv6Switch.IsOn });
        };
        dnsCardStack.Children.Add(NetoUiFactory.CreateSettingRow("启用 IPv6", _ipv6Switch));

        var formGrid = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < 4; r++) formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _mtuBox = new NumberBox { Header = "TUN MTU", Minimum = 1280, Maximum = 9000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _dnsModeCombo = new ComboBox { Header = "TUN DNS 模式", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in DnsModes) _dnsModeCombo.Items.Add(label);
        _virtualDnsPoolBox = new TextBox { Header = "虚拟 DNS 网段" };
        _testUrlBox = new TextBox { Header = "连通测速地址" };
        _nodeDnsBox = new TextBox { Header = "节点解析 DoH" };
        _overseasDnsBox = new TextBox { Header = "代理 DNS" };
        _directDnsBox = new TextBox { Header = "直连 DNS" };

        PlaceGridCell(formGrid, _mtuBox, 0, 0);
        PlaceGridCell(formGrid, _dnsModeCombo, 0, 1);
        PlaceGridCell(formGrid, _virtualDnsPoolBox, 1, 0);
        PlaceGridCell(formGrid, _testUrlBox, 1, 1);
        PlaceGridCell(formGrid, _nodeDnsBox, 2, 0);
        PlaceGridCell(formGrid, _overseasDnsBox, 2, 1);
        PlaceGridCell(formGrid, _directDnsBox, 3, 0);
        Grid.SetColumnSpan(_directDnsBox, 2);

        _mtuBox.ValueChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        _dnsModeCombo.SelectionChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        foreach (var tb in new[] { _virtualDnsPoolBox, _testUrlBox, _nodeDnsBox, _overseasDnsBox, _directDnsBox })
        {
            tb.TextChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        }
        dnsCardStack.Children.Add(formGrid);
        _dnsTunPane.Children.Add(NetoUiFactory.CreateCard(dnsCardStack));

        _customRoutePane = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var routeRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _routeSaveFeedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 42, 168, 118)),
        };
        var resetRouteBtn = new Button { Content = "恢复默认" };
        resetRouteBtn.Click += async (_, _) =>
        {
            _customRouteBox.Text = "";
            _inputsDirty = false;
            await _client.UpdateNetworkSettingsAsync(new JsonObject { ["custom_route_yaml"] = "" });
            _routeSaveFeedbackText.Text = "已恢复默认";
            await Task.Delay(1800);
            if (_routeSaveFeedbackText.Text == "已恢复默认") _routeSaveFeedbackText.Text = "";
        };
        var saveRouteBtn = new Button { Content = "保存规则" };
        NetoUiFactory.ApplyPrimaryButton(saveRouteBtn, true);
        saveRouteBtn.Click += async (_, _) =>
        {
            _inputsDirty = false;
            await _client.UpdateNetworkSettingsAsync(new JsonObject { ["custom_route_yaml"] = _customRouteBox.Text });
            _routeSaveFeedbackText.Text = _client.LastActionError ?? "已保存";
            await Task.Delay(1800);
            if (_routeSaveFeedbackText.Text == "已保存") _routeSaveFeedbackText.Text = "";
        };
        routeRight.Children.Add(_routeSaveFeedbackText);
        routeRight.Children.Add(resetRouteBtn);
        routeRight.Children.Add(saveRouteBtn);
        _customRoutePane.Children.Add(NetoUiFactory.CreateSubpageHeader("分流规则覆写", ResetToRoot, routeRight));
        _customRouteBox = new TextBox
        {
            Header = "自定义 YAML 规则（留空则使用当前订阅规则）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 300,
            MaxHeight = 460,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
        };
        _customRouteBox.TextChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        _customRoutePane.Children.Add(NetoUiFactory.CreateCard(_customRouteBox));

        _systemToolsPane = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        _systemToolsPane.Children.Add(NetoUiFactory.CreateSubpageHeader("系统代理与外部工具", ResetToRoot));

        var bypassRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _bypassFeedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 42, 168, 118)),
        };
        var saveBypassBtn = new Button { Content = "保存" };
        NetoUiFactory.ApplyPrimaryButton(saveBypassBtn, true);
        saveBypassBtn.Click += async (_, _) =>
        {
            _inputsDirty = false;
            await _client.UpdateNetworkSettingsAsync(new JsonObject { ["system_proxy_bypass"] = (_bypassBox.Text ?? "").Trim() });
            _bypassFeedbackText.Text = _client.LastActionError ?? "已保存";
            await Task.Delay(1800);
            if (_bypassFeedbackText.Text == "已保存") _bypassFeedbackText.Text = "";
        };
        bypassRight.Children.Add(_bypassFeedbackText);
        bypassRight.Children.Add(saveBypassBtn);
        var bypassStack = new StackPanel { Spacing = 8 };
        bypassStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("系统代理绕过列表（分号分隔）", bypassRight));
        _bypassBox = new TextBox { PlaceholderText = "localhost;127.*;10.*;192.168.*" };
        _bypassBox.TextChanged += (_, _) => { if (!_updating) _inputsDirty = true; };
        bypassStack.Children.Add(_bypassBox);
        _systemToolsPane.Children.Add(NetoUiFactory.CreateCard(bypassStack));

        var envControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _shellFormatCombo = new ComboBox { Width = 132 };
        foreach (var (_, label) in ShellFormats) _shellFormatCombo.Items.Add(label);
        _shellFormatCombo.SelectedIndex = 0;
        _shellFormatCombo.SelectionChanged += (_, _) => UpdateTerminalPreview();
        var copyCmdBtn = new Button { Content = "复制命令" };
        copyCmdBtn.Click += async (_, _) =>
        {
            if (NetoWindowHelper.SetClipboardText(_terminalCmdPreviewBox.Text))
            {
                copyCmdBtn.Content = "已复制";
                await Task.Delay(1200);
                copyCmdBtn.Content = "复制命令";
            }
        };
        envControls.Children.Add(_shellFormatCombo);
        envControls.Children.Add(copyCmdBtn);
        var envStack = new StackPanel { Spacing = 8 };
        envStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("终端环境变量", envControls));
        _terminalCmdPreviewBox = new TextBox
        {
            IsReadOnly = true,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
        };
        envStack.Children.Add(_terminalCmdPreviewBox);
        _systemToolsPane.Children.Add(NetoUiFactory.CreateCard(envStack));

        var openDataBtn = new Button { Content = "打开目录" };
        openDataBtn.Click += (_, _) => NetoWindowHelper.OpenFolder(_client.DataDir);
        var dataDirRow = NetoUiFactory.CreateSettingRow("本地数据目录", openDataBtn, _client.DataDir);
        _systemToolsPane.Children.Add(NetoUiFactory.CreateCard(dataDirRow));

        _logsPane = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var logActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var refreshLogBtn = new Button { Content = "刷新" };
        var copyLogBtn = new Button { Content = "复制" };
        var clearLogBtn = new Button { Content = "清空" };
        logActions.Children.Add(refreshLogBtn);
        logActions.Children.Add(copyLogBtn);
        logActions.Children.Add(clearLogBtn);
        _logsPane.Children.Add(NetoUiFactory.CreateSubpageHeader("运行日志", ResetToRoot, logActions));

        var logStack = new StackPanel { Spacing = 8 };
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
            MinHeight = 320,
            MaxHeight = 480,
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
        clearLogBtn.Click += async (_, _) =>
        {
            await _client.ClearLogsAsync();
            _logsBox.Text = "";
            _logsSummaryText.Text = "暂无记录";
        };
        logStack.Children.Add(_logsBox);
        _logsPane.Children.Add(NetoUiFactory.CreateCard(logStack));

        _subPane.Children.Add(_dnsTunPane);
        _subPane.Children.Add(_customRoutePane);
        _subPane.Children.Add(_systemToolsPane);
        _subPane.Children.Add(_logsPane);

        outer.Children.Add(_rootPane);
        outer.Children.Add(_subPane);
        Root = NetoUiFactory.CreateCenteredScrollViewer(outer);
    }

    public void ResetToRoot()
    {
        _subPane.Visibility = Visibility.Collapsed;
        _rootPane.Visibility = Visibility.Visible;
    }

    public void OpenSubpage(string key)
    {
        _dnsTunPane.Visibility = key == "dns_tun" ? Visibility.Visible : Visibility.Collapsed;
        _customRoutePane.Visibility = key == "custom_route" ? Visibility.Visible : Visibility.Collapsed;
        _systemToolsPane.Visibility = key == "system_tools" ? Visibility.Visible : Visibility.Collapsed;
        _logsPane.Visibility = key == "logs" ? Visibility.Visible : Visibility.Collapsed;
        _rootPane.Visibility = Visibility.Collapsed;
        _subPane.Visibility = Visibility.Visible;
        Root.ChangeView(null, 0, null, true);
    }

    private void UpdateTerminalPreview()
    {
        var fmt = ShellFormats[Math.Clamp(_shellFormatCombo.SelectedIndex, 0, ShellFormats.Length - 1)].Key;
        _terminalCmdPreviewBox.Text = NetoWindowHelper.BuildTerminalProxyCommand(_client.Snapshot, fmt);
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

        var dnsIdx = Array.FindIndex(DnsModes, m => m.Key == s.VpnDnsMode);
        var dnsLabel = DnsModes[dnsIdx >= 0 ? dnsIdx : 0].Label;
        _dnsTunSummaryText.Text = $"{dnsLabel} · MTU {s.Mtu} · IPv6 {(s.Ipv6Enabled ? "开" : "关")}";
        _routeSummaryText.Text = string.IsNullOrWhiteSpace(s.CustomRouteYaml)
            ? $"跟随订阅规则（共 {snap.RuleCount} 条）"
            : "已启用自定义规则";

        if (!_inputsInitialized || !_inputsDirty)
        {
            _portBox.Value = s.MixedPort;
            _mtuBox.Value = s.Mtu;
            _dnsModeCombo.SelectedIndex = dnsIdx >= 0 ? dnsIdx : 0;
            _virtualDnsPoolBox.Text = s.VirtualDnsPool;
            _bypassBox.Text = s.SystemProxyBypass;
            _nodeDnsBox.Text = s.NodeDns;
            _overseasDnsBox.Text = s.OverseasDns;
            _directDnsBox.Text = s.DirectDns;
            _testUrlBox.Text = s.NodeTestUrl;
            _customRouteBox.Text = s.CustomRouteYaml;
        }
        _updating = false;
        UpdateTerminalPreview();

        if (!_inputsInitialized)
        {
            _inputsInitialized = true;
            _ = RefreshLogsAsync();
        }
    }

    private async Task SaveDnsAndTunAsync()
    {
        var mtu = (ushort)Math.Clamp(double.IsNaN(_mtuBox.Value) ? 1500 : (int)_mtuBox.Value, 1280, 9000);
        var dnsMode = DnsModes[Math.Clamp(_dnsModeCombo.SelectedIndex, 0, DnsModes.Length - 1)].Key;
        var patch = new JsonObject
        {
            ["mtu"] = mtu,
            ["ipv6_enabled"] = _ipv6Switch.IsOn,
            ["vpn_dns_mode"] = dnsMode,
            ["virtual_dns_pool"] = (_virtualDnsPoolBox.Text ?? "").Trim(),
            ["node_dns"] = (_nodeDnsBox.Text ?? "").Trim(),
            ["overseas_dns"] = (_overseasDnsBox.Text ?? "").Trim(),
            ["direct_dns"] = (_directDnsBox.Text ?? "").Trim(),
            ["node_test_url"] = (_testUrlBox.Text ?? "").Trim(),
        };
        _inputsDirty = false;
        await _client.UpdateNetworkSettingsAsync(patch);
        _dnsSaveFeedbackText.Text = _client.LastActionError ?? "已保存";
        await Task.Delay(2000);
        if (_dnsSaveFeedbackText.Text == "已保存") _dnsSaveFeedbackText.Text = "";
    }

    public async Task RefreshLogsAsync()
    {
        var level = LogLevels[Math.Clamp(_logLevelCombo.SelectedIndex, 0, LogLevels.Length - 1)].Key;
        var filter = (_logSearchBox.Text ?? "").Trim();
        var entries = await _client.GetLogsAsync(200, level, filter);
        _logsSummaryText.Text = entries.Count == 0 ? "暂无记录" : $"最近 {entries.Count} 条记录";
        _logsBox.Text = string.Join(Environment.NewLine, entries.Select(e => $"{NetoFormat.FormatLogTimestamp(e.TimestampMs)} [{e.Level.ToUpperInvariant()}] {e.Message}"));
    }
}

