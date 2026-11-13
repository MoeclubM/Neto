using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public sealed class DashboardPageView
{
    public ScrollViewer Root { get; }
    public StackPanel HeaderActions { get; }
    private readonly NetoClient _client;
    private readonly Action _onOpenNodes;
    private readonly Action? _onOpenProfiles;
    private readonly Action? _onRequestElevation;
    private readonly Action? _onOpenConnections;
    private readonly StackPanel _extraHeaderHost;
    private readonly StackPanel _customizePanel;
    private readonly Grid _cardsGrid;
    private readonly Button _customizeBtn;
    private readonly Button _resetBtn;
    private bool _customizing;
    private bool _checkingExit;
    private bool _updating;
    private string _lastLayoutSig = "";
    private string _lastCustomizeSig = "";

    private NetoDualSeriesChart? _speedChart;
    private NetoDualSeriesChart? _latencyChart;
    private ToggleSwitch? _spSwitch;
    private ToggleSwitch? _tunSwitch;
    private TextBlock? _activeConnTitle;
    private TextBlock? _activeConnSub;
    private TextBlock? _trafficTitle;
    private TextBlock? _trafficSub;
    private ProgressBar? _quotaBar;
    private TextBlock? _quotaText;
    private TextBlock? _runtimeTitle;
    private TextBlock? _runtimeSub;

    public DashboardPageView(
        NetoClient client,
        Action onOpenNodes,
        Action? onOpenProfiles = null,
        Action? onRequestElevation = null,
        bool detachHeaderActions = false,
        Action? onOpenConnections = null)
    {
        _client = client;
        _onOpenNodes = onOpenNodes;
        _onOpenProfiles = onOpenProfiles;
        _onRequestElevation = onRequestElevation;
        _onOpenConnections = onOpenConnections;

        var outer = new StackPanel
        {
            MaxWidth = 860,
            Spacing = 10,
            Padding = new Thickness(18, 8, 18, 24),
        };

        _extraHeaderHost = new StackPanel { Spacing = 8 };
        _customizePanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        _cardsGrid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        _cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        HeaderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        _resetBtn = new Button { Content = "恢复默认", Visibility = Visibility.Collapsed };
        _resetBtn.Click += async (_, _) =>
        {
            _lastLayoutSig = "";
            _lastCustomizeSig = "";
            await _client.ResetDashboardWidgetsAsync();
        };
        _customizeBtn = new Button { Content = "自定义" };
        _customizeBtn.Click += (_, _) =>
        {
            _customizing = !_customizing;
            _customizeBtn.Content = _customizing ? "完成" : "自定义";
            NetoUiFactory.ApplyPrimaryButton(_customizeBtn, _customizing);
            _resetBtn.Visibility = _customizing ? Visibility.Visible : Visibility.Collapsed;
            _customizePanel.Visibility = _customizing ? Visibility.Visible : Visibility.Collapsed;
            if (_customizing) RebuildCustomizePanel(_client.Snapshot);
        };
        HeaderActions.Children.Add(_resetBtn);
        HeaderActions.Children.Add(_customizeBtn);

        outer.Children.Add(_extraHeaderHost);
        if (!detachHeaderActions)
        {
            outer.Children.Add(HeaderActions);
        }
        outer.Children.Add(_customizePanel);
        outer.Children.Add(_cardsGrid);
        Root = NetoUiFactory.CreateCenteredScrollViewer(outer);
    }

    public void SetExtraHeader(UIElement? element)
    {
        _extraHeaderHost.Children.Clear();
        if (element != null) _extraHeaderHost.Children.Add(element);
    }

    public void ApplySnapshot(NetoSnapshot snap)
    {
        var widgetSig = string.Join(",", snap.Settings.DashboardWidgets.Select(w => $"{w.Id}:{w.Enabled}"));
        if (_customizing && _lastCustomizeSig != widgetSig)
        {
            _lastCustomizeSig = widgetSig;
            RebuildCustomizePanel(snap);
        }

        var sel = snap.SelectedNode;
        var prof = snap.ActiveProfile;
        var exitSig = snap.LastNetworkCheck?.CheckedAtMs.ToString() ?? "";
        var layoutSig = string.Join("|",
            widgetSig,
            snap.Settings.RoutingMode,
            snap.Connection.HttpProxyAddr,
            snap.Connection.Connected,
            snap.Connection.CanUseTun,
            sel?.Name ?? "",
            sel?.LatencyMs?.ToString() ?? "",
            prof?.Id ?? "",
            prof?.NodeCount.ToString() ?? "",
            prof?.TotalBytes.ToString() ?? "",
            exitSig,
            _checkingExit
        );
        if (_lastLayoutSig != layoutSig)
        {
            _lastLayoutSig = layoutSig;
            RebuildCards(snap);
        }
        UpdateLiveValues(snap);
    }

    private void RebuildCustomizePanel(NetoSnapshot snap)
    {
        _customizePanel.Children.Clear();
        var rows = new StackPanel { Spacing = 4 };
        var list = snap.Settings.DashboardWidgets;
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            var idx = i;
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock { Text = item.TitleZh, VerticalAlignment = VerticalAlignment.Center };
            var upBtn = new Button { Content = "↑", IsEnabled = idx > 0 };
            var downBtn = new Button { Content = "↓", IsEnabled = idx + 1 < list.Count };
            var sw = new ToggleSwitch { IsOn = item.Enabled, OffContent = "", OnContent = "", MinWidth = 44 };

            upBtn.Click += async (_, _) => await _client.UpdateDashboardWidgetAsync(item.Id, moveDelta: -1);
            downBtn.Click += async (_, _) => await _client.UpdateDashboardWidgetAsync(item.Id, moveDelta: 1);
            sw.Toggled += async (_, _) => await _client.UpdateDashboardWidgetAsync(item.Id, enabled: sw.IsOn);

            Grid.SetColumn(upBtn, 1);
            Grid.SetColumn(downBtn, 2);
            Grid.SetColumn(sw, 3);
            grid.Children.Add(title);
            grid.Children.Add(upBtn);
            grid.Children.Add(downBtn);
            grid.Children.Add(sw);
            rows.Children.Add(grid);
        }
        _customizePanel.Children.Add(NetoUiFactory.CreateCard(rows));
    }

    private void RebuildCards(NetoSnapshot snap)
    {
        _cardsGrid.Children.Clear();
        _cardsGrid.RowDefinitions.Clear();
        _speedChart = null;
        _latencyChart = null;
        _spSwitch = null;
        _tunSwitch = null;
        _activeConnTitle = null;
        _activeConnSub = null;
        _trafficTitle = null;
        _trafficSub = null;
        _quotaBar = null;
        _quotaText = null;
        _runtimeTitle = null;
        _runtimeSub = null;

        var builtCards = new List<FrameworkElement>();
        foreach (var w in snap.Settings.DashboardWidgets.Where(x => x.Enabled))
        {
            switch (w.Id)
            {
                case "network_speed":
                    _speedChart = new NetoDualSeriesChart(Color.FromArgb(255, 46, 184, 128), Color.FromArgb(255, 56, 132, 235), "实时速率");
                    builtCards.Add(_speedChart.Root);
                    break;

                case "outbound_mode":
                    var mStack = new StackPanel { Spacing = 8 };
                    mStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("出站模式"));
                    var mGrid = new Grid { ColumnSpacing = 6 };
                    for (var i = 0; i < 3; i++) mGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var modes = new[] { ("rule", "规则"), ("global", "全局"), ("direct", "直连") };
                    for (var i = 0; i < modes.Length; i++)
                    {
                        var (key, label) = modes[i];
                        var btn = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
                        NetoUiFactory.ApplyPrimaryButton(btn, snap.Settings.RoutingMode == key);
                        btn.Click += async (_, _) => await _client.SetRoutingModeAsync(key);
                        Grid.SetColumn(btn, i);
                        mGrid.Children.Add(btn);
                    }
                    mStack.Children.Add(mGrid);
                    builtCards.Add(NetoUiFactory.CreateCard(mStack));
                    break;

                case "current_outbound":
                    var sel = snap.SelectedNode;
                    var isDirect = snap.Settings.RoutingMode == "direct";
                    var switchNodeBtn = new Button { Content = "切换", Padding = new Thickness(10, 3, 10, 3) };
                    switchNodeBtn.Click += (_, _) => _onOpenNodes();
                    var nStack = new StackPanel { Spacing = 4 };
                    nStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("当前节点", switchNodeBtn));
                    nStack.Children.Add(new TextBlock
                    {
                        Text = isDirect ? "DIRECT" : (sel?.Name ?? "暂无可用节点"),
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    nStack.Children.Add(new TextBlock
                    {
                        Text = isDirect ? "直连出站" : (sel != null ? (sel.LatencyMs.HasValue ? $"{sel.ProtocolLabel} · {sel.LatencyMs.Value} ms" : sel.ProtocolLabel) : "未选择"),
                        FontSize = 12,
                        Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                    });
                    builtCards.Add(NetoUiFactory.CreateCard(nStack));
                    break;

                case "active_connections":
                    var connActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    if (snap.Connection.Connected)
                    {
                        var closeAllBtn = new Button { Content = "断开全部", Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
                        closeAllBtn.Click += async (_, _) => await _client.CloseAllConnectionsAsync();
                        connActions.Children.Add(closeAllBtn);
                    }
                    if (_onOpenConnections != null)
                    {
                        var viewConnBtn = new Button { Content = "查看", Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
                        viewConnBtn.Click += (_, _) => _onOpenConnections();
                        connActions.Children.Add(viewConnBtn);
                    }
                    var acStack = new StackPanel { Spacing = 4 };
                    acStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("活跃会话", connActions));
                    _activeConnTitle = new TextBlock { FontWeight = FontWeights.SemiBold };
                    _activeConnSub = new TextBlock
                    {
                        FontSize = 12,
                        Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                    };
                    acStack.Children.Add(_activeConnTitle);
                    acStack.Children.Add(_activeConnSub);
                    builtCards.Add(NetoUiFactory.CreateCard(acStack));
                    break;

                case "exit_check":
                    var checkBtn = new Button
                    {
                        Content = _checkingExit ? "检测中..." : "检测",
                        IsEnabled = !_checkingExit,
                        Padding = new Thickness(8, 2, 8, 2),
                        FontSize = 11,
                    };
                    checkBtn.Click += async (_, _) =>
                    {
                        _checkingExit = true;
                        _lastLayoutSig = "";
                        ApplySnapshot(_client.Snapshot);
                        try { await _client.CheckConnectivityAsync(); }
                        finally
                        {
                            _checkingExit = false;
                            _lastLayoutSig = "";
                            ApplySnapshot(_client.Snapshot);
                        }
                    };
                    var exStack = new StackPanel { Spacing = 6 };
                    exStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("出口检测", checkBtn));
                    var checkRes = snap.LastNetworkCheck;
                    if (checkRes == null)
                    {
                        exStack.Children.Add(new TextBlock { Text = "未检测", FontWeight = FontWeights.SemiBold });
                        exStack.Children.Add(new TextBlock
                        {
                            Text = "检测出口 IP 与站点连通延迟",
                            FontSize = 12,
                            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                        });
                    }
                    else
                    {
                        var ipParts = new[] { checkRes.ExitIp, checkRes.ExitCountry, checkRes.ExitIsp }
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToList();
                        exStack.Children.Add(new TextBlock
                        {
                            Text = ipParts.Count > 0 ? string.Join(" · ", ipParts) : "未识别出口 IP",
                            FontWeight = FontWeights.SemiBold,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });
                        var badgeWrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                        foreach (var t in checkRes.Targets)
                        {
                            var label = t.LatencyMs.HasValue ? $"{t.Name} {t.LatencyMs.Value}ms" : $"{t.Name} 超时";
                            var tint = NetoUiFactory.LatencyColor(t.LatencyMs, !t.Ok);
                            badgeWrap.Children.Add(NetoUiFactory.CreateTagBadge(label, tint));
                        }
                        exStack.Children.Add(badgeWrap);
                    }
                    builtCards.Add(NetoUiFactory.CreateCard(exStack));
                    break;

                case "network_takeover":
                    FrameworkElement? elevHeaderAction = null;
                    if (!snap.Connection.CanUseTun && _onRequestElevation != null)
                    {
                        var eb = new Button { Content = "提权", Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
                        eb.Click += (_, _) => _onRequestElevation();
                        elevHeaderAction = eb;
                    }
                    var tkStack = new StackPanel { Spacing = 6 };
                    var portStr = string.IsNullOrEmpty(snap.Connection.HttpProxyAddr) ? $"端口 {snap.Settings.MixedPort}" : snap.Connection.HttpProxyAddr;
                    tkStack.Children.Add(NetoUiFactory.CreateInlineCardHeader($"网络接管 · {portStr}", elevHeaderAction));
                    var tGrid = new Grid { ColumnSpacing = 8 };
                    tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    _spSwitch = new ToggleSwitch { OffContent = "系统代理", OnContent = "系统代理", IsOn = snap.Settings.SystemProxyEnabled, MinWidth = 0 };
                    _spSwitch.Toggled += async (_, _) => { if (!_updating) await _client.SetSystemProxyAsync(_spSwitch.IsOn); };
                    _tunSwitch = new ToggleSwitch { OffContent = "TUN 接管", OnContent = "TUN 接管", IsOn = snap.Settings.TunEnabled, MinWidth = 0 };
                    _tunSwitch.Toggled += async (_, _) =>
                    {
                        if (_updating) return;
                        if (_tunSwitch.IsOn && !_client.Snapshot.Connection.CanUseTun && _onRequestElevation != null)
                        {
                            _updating = true;
                            _tunSwitch.IsOn = false;
                            _updating = false;
                            _onRequestElevation();
                            return;
                        }
                        await _client.SetTunEnabledAsync(_tunSwitch.IsOn);
                    };
                    Grid.SetColumn(_tunSwitch, 1);
                    tGrid.Children.Add(_spSwitch);
                    tGrid.Children.Add(_tunSwitch);
                    tkStack.Children.Add(tGrid);
                    builtCards.Add(NetoUiFactory.CreateCard(tkStack));
                    break;

                case "traffic_usage":
                    var trStack = new StackPanel { Spacing = 4 };
                    trStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("流量统计"));
                    _trafficTitle = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                    _trafficSub = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray) };
                    trStack.Children.Add(_trafficTitle);
                    trStack.Children.Add(_trafficSub);
                    if (snap.ActiveProfile is { TotalBytes: > 0 })
                    {
                        _quotaText = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 2) };
                        _quotaBar = new ProgressBar { Minimum = 0, Maximum = 100 };
                        trStack.Children.Add(_quotaText);
                        trStack.Children.Add(_quotaBar);
                    }
                    builtCards.Add(NetoUiFactory.CreateCard(trStack));
                    break;

                case "profiles_summary":
                    var prof = snap.ActiveProfile;
                    var pActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    if (prof != null && !string.IsNullOrEmpty(prof.Url))
                    {
                        var rBtn = new Button { Content = "刷新", Padding = new Thickness(8, 3, 8, 3) };
                        rBtn.Click += async (_, _) => await _client.RefreshProfileAsync(prof.Id);
                        pActions.Children.Add(rBtn);
                    }
                    if (_onOpenProfiles != null)
                    {
                        var mBtn = new Button { Content = "管理", Padding = new Thickness(8, 3, 8, 3) };
                        mBtn.Click += (_, _) => _onOpenProfiles();
                        pActions.Children.Add(mBtn);
                    }
                    var pStack = new StackPanel { Spacing = 4 };
                    pStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("订阅概览", pActions));
                    pStack.Children.Add(new TextBlock { Text = prof?.Name ?? "未配置订阅", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    pStack.Children.Add(new TextBlock
                    {
                        Text = prof != null ? $"{prof.NodeCount} 节点 · {prof.RuleCount} 规则" : "暂无订阅",
                        FontSize = 12,
                        Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                    });
                    builtCards.Add(NetoUiFactory.CreateCard(pStack));
                    break;

                case "latency_probe":
                    var probeBtn = new Button { Content = "探测", Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
                    probeBtn.Click += async (_, _) =>
                    {
                        probeBtn.IsEnabled = false;
                        try { await _client.ProbeTunnelAsync(); }
                        finally { probeBtn.IsEnabled = true; }
                    };
                    _latencyChart = new NetoDualSeriesChart(Color.FromArgb(255, 56, 132, 235), null, "连通延迟", probeBtn);
                    builtCards.Add(_latencyChart.Root);
                    break;

                case "runtime_info":
                    var rStack = new StackPanel { Spacing = 4 };
                    rStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("运行信息"));
                    _runtimeTitle = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                    _runtimeSub = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray) };
                    rStack.Children.Add(_runtimeTitle);
                    rStack.Children.Add(_runtimeSub);
                    builtCards.Add(NetoUiFactory.CreateCard(rStack));
                    break;
            }
        }

        var totalRows = (builtCards.Count + 1) / 2;
        for (var r = 0; r < totalRows; r++)
        {
            _cardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var i = 0; i < builtCards.Count; i++)
        {
            var card = builtCards[i];
            card.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            if (i == builtCards.Count - 1 && builtCards.Count % 2 == 1)
            {
                Grid.SetColumnSpan(card, 2);
            }
            _cardsGrid.Children.Add(card);
        }
    }

    private void UpdateLiveValues(NetoSnapshot snap)
    {
        _updating = true;
        if (_spSwitch != null) _spSwitch.IsOn = snap.Settings.SystemProxyEnabled;
        if (_tunSwitch != null) _tunSwitch.IsOn = snap.Settings.TunEnabled;
        _updating = false;

        _speedChart?.Update(
            $"↑ {NetoFormat.FormatSpeed(snap.Telemetry.UploadSpeedBps)}  ↓ {NetoFormat.FormatSpeed(snap.Telemetry.DownloadSpeedBps)}",
            snap.Telemetry.UploadHistory.Select(v => (double)v).ToList(),
            snap.Telemetry.DownloadHistory.Select(v => (double)v).ToList()
        );
        if (_latencyChart != null)
        {
            var valid = snap.Telemetry.LatencyHistory.Where(v => v >= 0).Select(v => (double)v).ToList();
            _latencyChart.Update(
                snap.Telemetry.LatencyMs.HasValue ? $"{snap.Telemetry.LatencyMs.Value} ms" : "- ms",
                valid
            );
        }
        if (_activeConnTitle != null && _activeConnSub != null)
        {
            _activeConnTitle.Text = $"{snap.Telemetry.ActiveSessions} 个并发会话";
            _activeConnSub.Text = $"累计 ↑ {NetoFormat.FormatBytes(snap.Telemetry.UploadBytes)} · ↓ {NetoFormat.FormatBytes(snap.Telemetry.DownloadBytes)}";
        }
        if (_trafficTitle != null && _trafficSub != null)
        {
            var total = snap.Telemetry.UploadBytes + snap.Telemetry.DownloadBytes;
            _trafficTitle.Text = $"会话流量 {NetoFormat.FormatBytes(total)}";
            _trafficSub.Text = $"上传 {NetoFormat.FormatBytes(snap.Telemetry.UploadBytes)} · 下载 {NetoFormat.FormatBytes(snap.Telemetry.DownloadBytes)}";
        }
        if (_quotaBar != null && _quotaText != null && snap.ActiveProfile is { TotalBytes: > 0 } prof)
        {
            _quotaText.Text = $"配额 {NetoFormat.FormatBytes(prof.UsedBytes)} / {NetoFormat.FormatBytes(prof.TotalBytes)}";
            _quotaBar.Value = Math.Clamp((double)prof.UsedBytes / prof.TotalBytes * 100.0, 0.0, 100.0);
        }
        if (_runtimeTitle != null && _runtimeSub != null)
        {
            var ips = string.Join(" · ", new[] { snap.Telemetry.IntranetIpv4, snap.Telemetry.IntranetIpv6 }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var fullAddr = $"内网地址 {(string.IsNullOrEmpty(ips) ? "-" : ips)}";
            _runtimeTitle.Text = fullAddr;
            ToolTipService.SetToolTip(_runtimeTitle, fullAddr);
            _runtimeSub.Text = $"活跃会话 {snap.Telemetry.ActiveSessions} · 内存 {NetoFormat.FormatBytes(snap.Telemetry.MemoryBytes)}";
        }
    }
}
