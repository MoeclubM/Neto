using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public sealed class HomePageView
{
    public ScrollViewer Root { get; }
    public StackPanel HeaderActions { get; }
    private readonly NetoClient _client;
    private readonly Action _onOpenNodes;
    private readonly Action? _onOpenProfiles;
    private readonly Action? _onOpenConnections;
    private readonly bool _showProfilesSummaryWidget;
    private readonly StackPanel _extraHeaderHost;
    private readonly StackPanel _blockedHost;
    private readonly StackPanel _controlPanel;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusLabel;
    private readonly Border _sessionBadge;
    private readonly TextBlock _sessionBadgeText;
    private readonly TextBlock _durationLabel;
    private readonly TextBlock _endpointLabel;
    private readonly Button _connectButton;
    private readonly TextBlock _nodeNameLabel;
    private readonly TextBlock _nodeMetaLabel;
    private readonly Button _modeRuleBtn;
    private readonly Button _modeGlobalBtn;
    private readonly Button _modeDirectBtn;
    private readonly ToggleSwitch _sysProxySwitch;
    private readonly ToggleSwitch _tunSwitch;
    private readonly Border _errorCard;
    private readonly TextBlock _errorLabel;
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
    private TextBlock? _activeConnTitle;
    private TextBlock? _activeConnSub;
    private TextBlock? _trafficTitle;
    private TextBlock? _trafficSub;
    private ProgressBar? _quotaBar;
    private TextBlock? _quotaText;
    private TextBlock? _runtimeTitle;
    private TextBlock? _runtimeSub;

    public HomePageView(
        NetoClient client,
        Action onOpenNodes,
        Action? onOpenProfiles = null,
        bool detachHeaderActions = false,
        Action? onOpenConnections = null,
        bool showProfilesSummaryWidget = true)
    {
        _client = client;
        _onOpenNodes = onOpenNodes;
        _onOpenProfiles = onOpenProfiles;
        _onOpenConnections = onOpenConnections;
        _showProfilesSummaryWidget = showProfilesSummaryWidget;

        var outer = new StackPanel
        {
            MaxWidth = 860,
            Spacing = 10,
            Padding = new Thickness(18, 8, 18, 24),
        };

        _extraHeaderHost = new StackPanel { Spacing = 8 };
        _blockedHost = new StackPanel { Visibility = Visibility.Collapsed };
        _controlPanel = new StackPanel { Spacing = 10 };

        var topRow = new Grid { ColumnSpacing = 10 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var connGrid = new Grid { ColumnSpacing = 12 };
        connGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var statusLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusDot = new Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Colors.Gray),
        };
        _statusLabel = new TextBlock
        {
            Text = "未连接",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _sessionBadgeText = new TextBlock
        {
            FontSize = 11,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        _sessionBadge = new Border
        {
            Background = NetoUiFactory.BrushResource("SubtleFillColorSecondaryBrush", Color.FromArgb(24, 128, 128, 128)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _sessionBadgeText,
        };
        statusLine.Children.Add(_statusDot);
        statusLine.Children.Add(_statusLabel);
        statusLine.Children.Add(_sessionBadge);

        _durationLabel = new TextBlock
        {
            Text = "00:00:00",
            FontSize = 22,
            FontFamily = new FontFamily("Consolas"),
        };
        _endpointLabel = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };

        var connLeft = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        connLeft.Children.Add(statusLine);
        connLeft.Children.Add(_durationLabel);
        connLeft.Children.Add(_endpointLabel);

        _connectButton = new Button
        {
            Content = "连接",
            MinWidth = 112,
            Height = 36,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _connectButton.Click += async (_, _) => await _client.ToggleConnectAsync();
        Grid.SetColumn(_connectButton, 1);
        connGrid.Children.Add(connLeft);
        connGrid.Children.Add(_connectButton);
        topRow.Children.Add(NetoUiFactory.CreateCard(connGrid, new Thickness(14, 12, 14, 12)));

        var nodeGrid = new Grid { ColumnSpacing = 8 };
        nodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nodeLeft = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        nodeLeft.Children.Add(new TextBlock
        {
            Text = "当前节点",
            FontSize = 11,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        });
        _nodeNameLabel = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Text = "暂无可用节点",
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _nodeMetaLabel = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        nodeLeft.Children.Add(_nodeNameLabel);
        nodeLeft.Children.Add(_nodeMetaLabel);
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 1);
        nodeGrid.Children.Add(nodeLeft);
        nodeGrid.Children.Add(chevron);

        var nodeBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Content = nodeGrid,
        };
        nodeBtn.Click += (_, _) => _onOpenNodes();
        Grid.SetColumn(nodeBtn, 1);
        topRow.Children.Add(nodeBtn);
        _controlPanel.Children.Add(topRow);

        var controlsGrid = new Grid { ColumnSpacing = 10 };
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var modeStack = new StackPanel { Spacing = 8 };
        modeStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("出站模式"));
        var modeGrid = new Grid { ColumnSpacing = 6 };
        for (var i = 0; i < 3; i++) modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _modeRuleBtn = new Button { Content = "规则", HorizontalAlignment = HorizontalAlignment.Stretch };
        _modeGlobalBtn = new Button { Content = "全局", HorizontalAlignment = HorizontalAlignment.Stretch };
        _modeDirectBtn = new Button { Content = "直连", HorizontalAlignment = HorizontalAlignment.Stretch };
        _modeRuleBtn.Click += async (_, _) => await _client.SetRoutingModeAsync("rule");
        _modeGlobalBtn.Click += async (_, _) => await _client.SetRoutingModeAsync("global");
        _modeDirectBtn.Click += async (_, _) => await _client.SetRoutingModeAsync("direct");
        Grid.SetColumn(_modeGlobalBtn, 1);
        Grid.SetColumn(_modeDirectBtn, 2);
        modeGrid.Children.Add(_modeRuleBtn);
        modeGrid.Children.Add(_modeGlobalBtn);
        modeGrid.Children.Add(_modeDirectBtn);
        modeStack.Children.Add(modeGrid);
        controlsGrid.Children.Add(NetoUiFactory.CreateCard(modeStack, new Thickness(14, 10, 14, 12)));

        var takeoverStack = new StackPanel { Spacing = 6 };
        takeoverStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("网络接管"));
        var takeoverGrid = new Grid { ColumnSpacing = 8 };
        takeoverGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        takeoverGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sysProxySwitch = new ToggleSwitch { OffContent = "系统代理", OnContent = "系统代理", MinWidth = 0 };
        _tunSwitch = new ToggleSwitch { OffContent = "TUN 接管", OnContent = "TUN 接管", MinWidth = 0 };
        _sysProxySwitch.Toggled += async (_, _) => { if (!_updating) await _client.SetSystemProxyAsync(_sysProxySwitch.IsOn); };
        _tunSwitch.Toggled += async (_, _) => { if (!_updating) await _client.EnableTunAsync(_tunSwitch.IsOn); };
        Grid.SetColumn(_tunSwitch, 1);
        takeoverGrid.Children.Add(_sysProxySwitch);
        takeoverGrid.Children.Add(_tunSwitch);
        takeoverStack.Children.Add(takeoverGrid);
        var takeoverCard = NetoUiFactory.CreateCard(takeoverStack, new Thickness(14, 10, 14, 10));
        Grid.SetColumn(takeoverCard, 1);
        controlsGrid.Children.Add(takeoverCard);
        _controlPanel.Children.Add(controlsGrid);

        _errorLabel = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 216, 82, 76)),
        };
        _errorCard = NetoUiFactory.CreateCard(_errorLabel, new Thickness(12, 8, 12, 8));
        _errorCard.Visibility = Visibility.Collapsed;
        _controlPanel.Children.Add(_errorCard);

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
        outer.Children.Add(_blockedHost);
        outer.Children.Add(_controlPanel);
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

    public void SetBlockedElement(UIElement? element)
    {
        _blockedHost.Children.Clear();
        if (element != null)
        {
            _blockedHost.Children.Add(element);
            _blockedHost.Visibility = Visibility.Visible;
            _controlPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _blockedHost.Visibility = Visibility.Collapsed;
            _controlPanel.Visibility = Visibility.Visible;
        }
    }

    private List<NetoWidgetItem> VisibleWidgetDefs(NetoSnapshot snap) =>
        snap.Settings.DashboardWidgets
            .Where(w => _showProfilesSummaryWidget || w.Id != "profiles_summary")
            .ToList();

    public void ApplySnapshot(NetoSnapshot snap, string? actionError = null)
    {
        _updating = true;
        var conn = snap.Connection;
        _statusDot.Fill = new SolidColorBrush(conn.Connecting
            ? Color.FromArgb(255, 235, 168, 52)
            : conn.Connected ? Color.FromArgb(255, 46, 184, 128) : Colors.Gray);
        _statusLabel.Text = conn.Connecting ? "连接中" : conn.Connected ? "已连接" : "未连接";
        _durationLabel.Text = conn.Connected && conn.ConnectedAtMs > 0
            ? NetoFormat.FormatDurationMs(Math.Max(0UL, NetoFormat.NowUnixMs() - conn.ConnectedAtMs))
            : "00:00:00";
        _connectButton.Content = conn.Connecting ? "连接中..." : conn.Connected ? "断开" : "连接";
        _connectButton.IsEnabled = !conn.Connecting;
        NetoUiFactory.ApplyPrimaryButton(_connectButton, !conn.Connected);

        var addr = string.IsNullOrEmpty(conn.HttpProxyAddr) ? $"127.0.0.1:{snap.Settings.MixedPort}" : conn.HttpProxyAddr;
        if (conn.Connected)
        {
            _sessionBadge.Visibility = Visibility.Visible;
            _sessionBadgeText.Text = conn.SessionKind switch
            {
                "tun" => "TUN 接管",
                "route" => "规则分流",
                _ => "代理通道",
            };
            _endpointLabel.Text = $"本地监听 {addr}";
        }
        else
        {
            _sessionBadge.Visibility = Visibility.Collapsed;
            _endpointLabel.Text = $"预设监听 {addr}";
        }

        var sel = snap.SelectedNode;
        if (snap.Settings.RoutingMode == "direct")
        {
            _nodeNameLabel.Text = "DIRECT";
            _nodeMetaLabel.Text = "直连出站";
        }
        else if (sel != null)
        {
            _nodeNameLabel.Text = sel.Name;
            _nodeMetaLabel.Text = sel.LatencyMs.HasValue
                ? $"{sel.ProtocolLabel} · {sel.LatencyMs.Value} ms"
                : sel.ProtocolLabel;
        }
        else
        {
            _nodeNameLabel.Text = "暂无可用节点";
            _nodeMetaLabel.Text = "未选择";
        }

        NetoUiFactory.ApplyPrimaryButton(_modeRuleBtn, snap.Settings.RoutingMode == "rule");
        NetoUiFactory.ApplyPrimaryButton(_modeGlobalBtn, snap.Settings.RoutingMode == "global");
        NetoUiFactory.ApplyPrimaryButton(_modeDirectBtn, snap.Settings.RoutingMode == "direct");

        _sysProxySwitch.IsOn = snap.Settings.SystemProxyEnabled;
        _tunSwitch.IsOn = snap.Settings.TunEnabled;

        var err = actionError ?? conn.LastError;
        if (!string.IsNullOrWhiteSpace(err))
        {
            _errorLabel.Text = err;
            _errorCard.Visibility = Visibility.Visible;
        }
        else
        {
            _errorCard.Visibility = Visibility.Collapsed;
        }
        _updating = false;

        var defs = VisibleWidgetDefs(snap);
        var widgetSig = string.Join(",", defs.Select(w => $"{w.Id}:{w.Enabled}"));
        if (_customizing && _lastCustomizeSig != widgetSig)
        {
            _lastCustomizeSig = widgetSig;
            RebuildCustomizePanel(snap);
        }

        var prof = snap.ActiveProfile;
        var exitSig = snap.LastNetworkCheck?.CheckedAtMs.ToString() ?? "";
        var layoutSig = string.Join("|",
            widgetSig,
            snap.Connection.Connected,
            prof?.Id ?? "",
            prof?.NodeCount.ToString() ?? "",
            prof?.RuleCount.ToString() ?? "",
            prof?.TotalBytes.ToString() ?? "",
            exitSig,
            _checkingExit
        );
        if (_lastLayoutSig != layoutSig)
        {
            _lastLayoutSig = layoutSig;
            RebuildCards(snap, defs);
        }
        UpdateLiveValues(snap);
    }

    private void RebuildCustomizePanel(NetoSnapshot snap)
    {
        _customizePanel.Children.Clear();
        var rows = new StackPanel { Spacing = 4 };
        var list = VisibleWidgetDefs(snap);
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

    private void RebuildCards(NetoSnapshot snap, List<NetoWidgetItem> defs)
    {
        _cardsGrid.Children.Clear();
        _cardsGrid.RowDefinitions.Clear();
        _speedChart = null;
        _latencyChart = null;
        _activeConnTitle = null;
        _activeConnSub = null;
        _trafficTitle = null;
        _trafficSub = null;
        _quotaBar = null;
        _quotaText = null;
        _runtimeTitle = null;
        _runtimeSub = null;

        var builtCards = new List<FrameworkElement>();
        foreach (var w in defs.Where(x => x.Enabled))
        {
            switch (w.Id)
            {
                case "network_speed":
                    _speedChart = new NetoDualSeriesChart(Color.FromArgb(255, 46, 184, 128), Color.FromArgb(255, 56, 132, 235), "实时速率");
                    builtCards.Add(_speedChart.Root);
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
                        ApplySnapshot(_client.Snapshot, _client.LastActionError);
                        try { await _client.CheckConnectivityAsync(); }
                        finally
                        {
                            _checkingExit = false;
                            _lastLayoutSig = "";
                            ApplySnapshot(_client.Snapshot, _client.LastActionError);
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
            var tunInfo = snap.Connection.TunServiceStatus == "running" ? "TUN 服务就绪" : $"混合端口 {snap.Settings.MixedPort}";
            _runtimeSub.Text = $"{tunInfo} · 内存 {NetoFormat.FormatBytes(snap.Telemetry.MemoryBytes)}";
        }
    }
}
