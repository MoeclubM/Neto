using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Text;
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
    private readonly StackPanel _rootPane;
    private readonly StackPanel _subPane;
    private readonly StackPanel _dnsTunPane;
    private readonly StackPanel _systemToolsPane;
    private readonly StackPanel _logsPane;
    private readonly ComboBox _themeCombo;
    private readonly ToggleSwitch _autostartSwitch;
    private readonly ToggleSwitch _silentStartSwitch;
    private readonly NumberBox _portBox;
    private readonly TextBlock _portFeedbackText;
    private readonly ToggleSwitch _lanSwitch;
    private readonly Grid _tunServiceRow;
    private readonly StackPanel _tunServiceStatusHost;
    private readonly Button _tunServiceActionBtn;
    private readonly TextBlock _tunServiceFeedbackText;
    private readonly TextBlock _dnsTunSummaryText;
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
    private readonly TextBox _bypassBox;
    private readonly TextBlock _bypassFeedbackText;
    private readonly ComboBox _shellFormatCombo;
    private readonly TextBox _terminalCmdPreviewBox;
    private readonly ComboBox _logLevelCombo;
    private readonly TextBox _logSearchBox;
    private readonly TextBox _logsBox;
    private readonly TextBlock _versionSubtitleText;
    private readonly StackPanel _updateBadgeHost;
    private readonly TextBlock _updateFeedbackText;
    private readonly Button _checkUpdateBtn;
    private readonly StackPanel _extraFooterHost;
    private string _updateRepo = "MoeclubM/Neto";
    private string _currentVersion = NetoWindowHelper.GetAppVersion();
    private Func<XamlRoot?>? _xamlRootProvider;
    private Action? _onQuitForInstaller;
    private NetoUpdateCheckResult? _cachedUpdate;
    private bool _checkingUpdate;
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

    public SettingsPageView(NetoClient client, Action<string>? onThemeChanged = null)
    {
        _client = client;
        _onThemeChanged = onThemeChanged;

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

        var accessStack = new StackPanel { Spacing = 4 };
        accessStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("网络接入"));
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
        _tunServiceStatusHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _tunServiceFeedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 216, 82, 76)),
            Visibility = Visibility.Collapsed,
        };
        _tunServiceActionBtn = new Button { Content = "安装服务" };
        _tunServiceActionBtn.Click += async (_, _) =>
        {
            _tunServiceActionBtn.IsEnabled = false;
            _tunServiceFeedbackText.Visibility = Visibility.Collapsed;
            try
            {
                if (string.Equals(_client.Snapshot.Connection.TunServiceStatus, "running", StringComparison.OrdinalIgnoreCase))
                {
                    await _client.UninstallTunServiceAsync();
                }
                else
                {
                    await _client.InstallTunServiceAsync();
                }
                if (!string.IsNullOrWhiteSpace(_client.LastActionError))
                {
                    _tunServiceFeedbackText.Text = _client.LastActionError;
                    _tunServiceFeedbackText.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _tunServiceActionBtn.IsEnabled = true;
            }
        };
        var tunServiceRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        tunServiceRight.Children.Add(_tunServiceFeedbackText);
        tunServiceRight.Children.Add(_tunServiceStatusHost);
        tunServiceRight.Children.Add(_tunServiceActionBtn);
        _tunServiceRow = NetoUiFactory.CreateSettingRow("TUN 服务模式", tunServiceRight, "通过系统服务管理虚拟网卡，界面保持普通权限运行");
        accessStack.Children.Add(NetoUiFactory.CreateSettingRow("本地混合端口", portRight, "HTTP 与 SOCKS5 共用监听端口"));
        accessStack.Children.Add(NetoUiFactory.CreateSettingRow("允许局域网连接", _lanSwitch));
        accessStack.Children.Add(_tunServiceRow);
        _rootPane.Children.Add(NetoUiFactory.CreateCard(accessStack));

        _rootPane.Children.Add(NetoUiFactory.CreateSectionHeader("高级与诊断"));
        var navGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _dnsTunSummaryText = new TextBlock();
        _toolsSummaryText = new TextBlock { Text = "绕过地址、环境变量与数据目录" };
        _logsSummaryText = new TextBlock { Text = "筛选、搜索与复制运行日志" };
        PlaceGridCell(navGrid, NetoUiFactory.CreateNavEntryRow("DNS 与 TUN 参数", _dnsTunSummaryText, () => OpenSubpage("dns_tun")), 0, 0);
        PlaceGridCell(navGrid, NetoUiFactory.CreateNavEntryRow("系统代理与外部工具", _toolsSummaryText, () => OpenSubpage("system_tools")), 0, 1);
        var logsNav = NetoUiFactory.CreateNavEntryRow("运行日志", _logsSummaryText, () =>
        {
            OpenSubpage("logs");
            _ = RefreshLogsAsync();
        });
        PlaceGridCell(navGrid, logsNav, 1, 0);
        Grid.SetColumnSpan(logsNav, 2);
        _rootPane.Children.Add(navGrid);

        var versionGrid = new Grid { ColumnSpacing = 12, MinHeight = 34 };
        versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var versionLeft = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        versionLeft.Children.Add(new TextBlock { Text = "客户端版本", FontSize = 13 });
        _versionSubtitleText = new TextBlock
        {
            Text = $"v{_currentVersion}",
            FontSize = 11,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        versionLeft.Children.Add(_versionSubtitleText);
        _updateFeedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _updateBadgeHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _checkUpdateBtn = new Button { Content = "检查更新" };
        _checkUpdateBtn.Click += async (_, _) => await CheckForUpdatesAsync(true);
        var versionRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        versionRight.Children.Add(_updateFeedbackText);
        versionRight.Children.Add(_updateBadgeHost);
        versionRight.Children.Add(_checkUpdateBtn);
        Grid.SetColumn(versionRight, 1);
        versionGrid.Children.Add(versionLeft);
        versionGrid.Children.Add(versionRight);
        _rootPane.Children.Add(NetoUiFactory.CreateCard(versionGrid));

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

    public void ConfigureUpdater(
        string repoSlug,
        string currentVersion,
        Func<XamlRoot?> xamlRootProvider,
        Action? onQuitForInstaller = null)
    {
        if (!string.IsNullOrWhiteSpace(repoSlug)) _updateRepo = repoSlug.Trim();
        if (!string.IsNullOrWhiteSpace(currentVersion)) _currentVersion = currentVersion.Trim().TrimStart('v', 'V');
        _xamlRootProvider = xamlRootProvider;
        _onQuitForInstaller = onQuitForInstaller;
        _versionSubtitleText.Text = $"v{_currentVersion}";
    }

    public async Task CheckForUpdatesAsync(bool manual = true)
    {
        if (_checkingUpdate || string.IsNullOrWhiteSpace(_updateRepo)) return;
        if (manual && _cachedUpdate is { HasUpdate: true } cached)
        {
            await ShowUpdateDialogAsync(cached);
            return;
        }
        _checkingUpdate = true;
        _checkUpdateBtn.IsEnabled = false;
        if (manual)
        {
            _checkUpdateBtn.Content = "检查中…";
            _updateFeedbackText.Visibility = Visibility.Collapsed;
        }
        try
        {
            var res = await _client.CheckUpdateAsync(_updateRepo, _currentVersion, NetoWindowHelper.IsInstallerSetup());
            _cachedUpdate = res;
            _updateBadgeHost.Children.Clear();
            if (res.HasUpdate)
            {
                _updateBadgeHost.Children.Add(NetoUiFactory.CreateTagBadge(
                    $"新版本 {res.LatestVersion}",
                    Color.FromArgb(255, 42, 168, 118)));
                _checkUpdateBtn.Content = "查看更新";
                NetoUiFactory.ApplyPrimaryButton(_checkUpdateBtn, true);
                if (manual)
                {
                    await ShowUpdateDialogAsync(res);
                }
            }
            else
            {
                _checkUpdateBtn.Content = "检查更新";
                NetoUiFactory.ApplyPrimaryButton(_checkUpdateBtn, false);
                if (manual)
                {
                    _updateFeedbackText.Foreground = new SolidColorBrush(Color.FromArgb(255, 42, 168, 118));
                    _updateFeedbackText.Text = "已是最新版本";
                    _updateFeedbackText.Visibility = Visibility.Visible;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2500);
                        Root.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_updateFeedbackText.Text == "已是最新版本")
                            {
                                _updateFeedbackText.Visibility = Visibility.Collapsed;
                            }
                        });
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _checkUpdateBtn.Content = "检查更新";
            if (manual)
            {
                _updateFeedbackText.Foreground = new SolidColorBrush(Color.FromArgb(255, 216, 82, 76));
                _updateFeedbackText.Text = ex.Message;
                _updateFeedbackText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _checkingUpdate = false;
            _checkUpdateBtn.IsEnabled = true;
        }
    }

    private async Task ShowUpdateDialogAsync(NetoUpdateCheckResult info)
    {
        var xamlRoot = _xamlRootProvider?.Invoke() ?? Root.XamlRoot;
        if (xamlRoot == null) return;

        var bodyStack = new StackPanel { Spacing = 10, MinWidth = 380 };
        bodyStack.Children.Add(new TextBlock
        {
            Text = $"当前版本 v{_currentVersion}  →  最新版本 {info.LatestVersion}",
            FontWeight = FontWeights.SemiBold,
        });
        if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
        {
            bodyStack.Children.Add(new ScrollViewer
            {
                MaxHeight = 240,
                Content = new TextBlock
                {
                    Text = info.ReleaseNotes,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                    Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                },
            });
        }
        var progressBar = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var statusText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        bodyStack.Children.Add(progressBar);
        bodyStack.Children.Add(statusText);

        var hasSetup = !string.IsNullOrWhiteSpace(info.InstallerUrl);
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"发现新版本 {info.LatestVersion}",
            PrimaryButtonText = hasSetup ? "下载并安装" : "前往下载页",
            SecondaryButtonText = hasSetup ? "打开发布页" : "",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            Content = bodyStack,
        };

        dialog.SecondaryButtonClick += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(info.ReleaseUrl))
            {
                NetoWindowHelper.OpenUrl(info.ReleaseUrl);
            }
        };

        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            if (!hasSetup)
            {
                var targetUrl = !string.IsNullOrWhiteSpace(info.DownloadUrl) ? info.DownloadUrl : info.ReleaseUrl;
                NetoWindowHelper.OpenUrl(targetUrl);
                return;
            }

            args.Cancel = true;
            sender.IsPrimaryButtonEnabled = false;
            sender.IsSecondaryButtonEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            statusText.Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray);
            statusText.Text = "正在下载安装包…";
            statusText.Visibility = Visibility.Visible;

            var fileName = string.IsNullOrWhiteSpace(info.InstallerName)
                ? $"update-{info.LatestVersion}-setup.exe"
                : Path.GetFileName(info.InstallerName);
            var destPath = Path.Combine(Path.GetTempPath(), "NetoUpdates", fileName);
            try
            {
                var dl = await _client.DownloadUpdateAsync(info.InstallerUrl, destPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = dl.Path,
                    UseShellExecute = true,
                });
                sender.Hide();
                _onQuitForInstaller?.Invoke();
            }
            catch (Exception ex)
            {
                progressBar.Visibility = Visibility.Collapsed;
                statusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 216, 82, 76));
                statusText.Text = $"下载失败：{ex.Message}";
                sender.IsPrimaryButtonEnabled = true;
                sender.IsSecondaryButtonEnabled = true;
            }
        };

        await dialog.ShowAsync();
    }

    public void ApplySnapshot(NetoSnapshot snap)
    {
        var s = snap.Settings;
        _updating = true;
        var svcStatus = (snap.Connection.TunServiceStatus ?? "unsupported").ToLowerInvariant();
        if (svcStatus == "unsupported")
        {
            _tunServiceRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            _tunServiceRow.Visibility = Visibility.Visible;
            _tunServiceStatusHost.Children.Clear();
            var (label, tint, btnLabel) = svcStatus switch
            {
                "running" => ("运行中", Color.FromArgb(255, 42, 168, 118), "卸载服务"),
                "stopped" => ("已停止", Color.FromArgb(255, 218, 152, 38), "启动服务"),
                _ => ("未安装", (Color?)null, "安装服务"),
            };
            _tunServiceStatusHost.Children.Add(NetoUiFactory.CreateTagBadge(label, tint));
            _tunServiceActionBtn.Content = btnLabel;
        }

        var themeIdx = Array.FindIndex(ThemeModes, m => m.Key == s.ThemeMode);
        _themeCombo.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;
        _autostartSwitch.IsOn = s.Autostart;
        _silentStartSwitch.IsOn = s.SilentStart;
        _lanSwitch.IsOn = s.AllowLan;
        _ipv6Switch.IsOn = s.Ipv6Enabled;

        var dnsIdx = Array.FindIndex(DnsModes, m => m.Key == s.VpnDnsMode);
        var dnsLabel = DnsModes[dnsIdx >= 0 ? dnsIdx : 0].Label;
        _dnsTunSummaryText.Text = $"{dnsLabel} · MTU {s.Mtu} · IPv6 {(s.Ipv6Enabled ? "开" : "关")}";

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

