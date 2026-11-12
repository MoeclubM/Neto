using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public sealed class DashboardPageView
{
    public ScrollViewer Root { get; }
    private readonly NetoClient _client;
    private readonly Action _onOpenNodes;
    private readonly Action? _onOpenProfiles;
    private readonly StackPanel _extraHeaderHost;
    private readonly StackPanel _customizePanel;
    private readonly StackPanel _cardsPanel;
    private readonly Button _customizeBtn;
    private readonly Button _resetBtn;
    private bool _customizing;
    private string _lastLayoutSig = "";
    private string _lastCustomizeSig = "";

    private NetoDualSeriesChart? _speedChart;
    private NetoDualSeriesChart? _latencyChart;
    private TextBlock? _trafficTitle;
    private TextBlock? _trafficSub;
    private ProgressBar? _quotaBar;
    private TextBlock? _quotaText;
    private TextBlock? _runtimeTitle;
    private TextBlock? _runtimeSub;

    public DashboardPageView(NetoClient client, Action onOpenNodes, Action? onOpenProfiles = null)
    {
        _client = client;
        _onOpenNodes = onOpenNodes;
        _onOpenProfiles = onOpenProfiles;

        var outer = new StackPanel
        {
            MaxWidth = 820,
            Spacing = 10,
            Padding = new Thickness(18, 12, 18, 24),
        };

        _extraHeaderHost = new StackPanel { Spacing = 8 };

        var toolbar = new StackPanel
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
        toolbar.Children.Add(_resetBtn);
        toolbar.Children.Add(_customizeBtn);

        _customizePanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        _cardsPanel = new StackPanel { Spacing = 10 };

        outer.Children.Add(_extraHeaderHost);
        outer.Children.Add(toolbar);
        outer.Children.Add(_customizePanel);
        outer.Children.Add(_cardsPanel);
        Root = new ScrollViewer { Content = outer };
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
        var layoutSig = string.Join("|",
            widgetSig,
            snap.Settings.RoutingMode,
            snap.Settings.SystemProxyEnabled,
            snap.Settings.TunEnabled,
            snap.Connection.HttpProxyAddr,
            snap.Connection.Connected,
            sel?.Name ?? "",
            sel?.LatencyMs?.ToString() ?? "",
            prof?.Id ?? "",
            prof?.NodeCount.ToString() ?? "",
            prof?.TotalBytes.ToString() ?? ""
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
        _cardsPanel.Children.Clear();
        _speedChart = null;
        _latencyChart = null;
        _trafficTitle = null;
        _trafficSub = null;
        _quotaBar = null;
        _quotaText = null;
        _runtimeTitle = null;
        _runtimeSub = null;

        foreach (var w in snap.Settings.DashboardWidgets.Where(x => x.Enabled))
        {
            switch (w.Id)
            {
                case "network_speed":
                    _cardsPanel.Children.Add(NetoUiFactory.CreateSectionHeader("实时速率"));
                    _speedChart = new NetoDualSeriesChart(Color.FromArgb(255, 46, 184, 128), Color.FromArgb(255, 56, 132, 235));
                    _cardsPanel.Children.Add(_speedChart.Root);
                    break;

                case "outbound_mode":
                    _cardsPanel.Children.Add(NetoUiFactory.CreateSectionHeader("出站模式"));
                    var mGrid = new Grid { ColumnSpacing = 8 };
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
                    _cardsPanel.Children.Add(mGrid);
                    break;

                case "current_outbound":
                    _cardsPanel.Children.Add(NetoUiFactory.CreateSectionHeader("当前节点"));
                    var sel = snap.SelectedNode;
                    var nStack = new StackPanel { Spacing = 2 };
                    nStack.Children.Add(new TextBlock { Text = sel?.Name ?? "暂无可用节点", FontWeight = FontWeights.SemiBold });
                    nStack.Children.Add(new TextBlock
                    {
                        Text = sel != null ? (sel.LatencyMs.HasValue ? $"{sel.ProtocolLabel} · {sel.LatencyMs.Value} ms" : sel.ProtocolLabel) : "前往节点页选择",
                        FontSize = 12,
                        Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                    });
                    var nBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Thickness(14, 10, 14, 10),
                        Content = nStack,
                    };
                    nBtn.Click += (_, _) => _onOpenNodes();
                    _cardsPanel.Children.Add(nBtn);
                    break;

                case "network_takeover":
                    _cardsPanel.Children.Add(NetoUiFactory.CreateSectionHeader("网络接管"));
                    var tGrid = new Grid { ColumnSpacing = 16 };
                    tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var spSw = new ToggleSwitch
                    {
                        Header = string.IsNullOrEmpty(snap.Connection.HttpProxyAddr) ? $"系统代理 ({snap.Settings.MixedPort})" : $"系统代理 ({snap.Connection.HttpProxyAddr})",
                        IsOn = snap.Settings.SystemProxyEnabled,
                    };
                    spSw.Toggled += async (_, _) => await _client.SetSystemProxyAsync(spSw.IsOn);
                    var tunSw = new ToggleSwitch
                    {
                        Header = $"TUN 网卡 (MTU {snap.Settings.Mtu})",
                        IsOn = snap.Settings.TunEnabled,
                    };
                    tunSw.Toggled += async (_, _) => await _client.SetTunEnabledAsync(tunSw.IsOn);
                    Grid.SetColumn(tunSw, 1);
                    tGrid.Children.Add(spSw);
                    tGrid.Children.Add(tunSw);
                    _cardsPanel.Children.Add(NetoUiFactory.CreateCard(tGrid));
                    break;

                case "traffic_usage":
                    _cardsPanel.Children.Add(NetoUiFactory.CreateSectionHeader("流量统计"));
                    var trStack = new StackPanel { Spacing = 4 };
                    _trafficTitle = new TextBlock { FontWeight = FontWeights.SemiBold };
                    _trafficSub = new TextBlock { FontSize = 12, Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray) };
                    trStack.Children.Add(_trafficTitle);
                    trStack.Children.Add(_trafficSub);
                    if (snap.ActiveProfile is { TotalBytes: > 0 })
                    {
                        _quotaText = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 2) };
                        _quotaBar = new ProgressBar { Minimum = 0, Maximum = 100 };
                        trStack.Children.Add(_quotaText);
                        trStack.Children.Add(_quotaBar);
                    }
                    _cardsPanel.Children.Add(NetoUiFactory.CreateCard(trStack));
                    break;

                case "profiles_summary":
                    _cardsPanel.Children.Add(NetoUiFactory.CreateSectionHeader("订阅概览"));
                    var prof = snap.ActiveProfile;
                    var pGrid = new Grid();
                    pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var pInfo = new StackPanel { Spacing = 2 };
                    pInfo.Children.Add(new TextBlock { Text = prof?.Name ?? "未配置订阅", FontWeight = FontWeights.SemiBold });
                    pInfo.Children.Add(new TextBlock
                    {
                        Text = prof != null ? $"{prof.NodeCount} 节点 · {prof.RuleCount} 规则" : "前往订阅页添加",
                        FontSize = 12,
                        Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                    });
                    pGrid.Children.Add(pInfo);
                    var pActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    if (prof != null && !string.IsNullOrEmpty(prof.Url))
                    {
                        var rBtn = new Button { Content = "刷新" };
                        rBtn.Click += async (_, _) => await _client.RefreshProfileAsync(prof.Id);
                        pActions.Children.Add(rBtn);
                    }
                    if (_onOpenProfiles != null)
                    {
                        var mBtn = new Button { Content = "管理" };
                        mBtn.Click += (_, _) => _onOpenProfiles();
                        pActions.Children.Add(mBtn);
                    }
                    Grid.SetColumn(pActions, 1);
                    pGrid.Children.Add(pActions);
                    _cardsPanel.Children.Add(NetoUiFactory.CreateCard(pGrid));
                    break;

                case "latency_probe":
                    _cardsPanel.Children.Add(NetoUiFactory.CreateSectionHeader("连通延迟"));
                    _latencyChart = new NetoDualSeriesChart(Color.FromArgb(255, 56, 132, 235));
                    _cardsPanel.Children.Add(_latencyChart.Root);
                    break;

                case "runtime_info":
                    _cardsPanel.Children.Add(NetoUiFactory.CreateSectionHeader("运行信息"));
                    var rStack = new StackPanel { Spacing = 4 };
                    _runtimeTitle = new TextBlock { FontWeight = FontWeights.SemiBold };
                    _runtimeSub = new TextBlock { FontSize = 12, Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray) };
                    rStack.Children.Add(_runtimeTitle);
                    rStack.Children.Add(_runtimeSub);
                    _cardsPanel.Children.Add(NetoUiFactory.CreateCard(rStack));
                    break;
            }
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
            _runtimeTitle.Text = $"内网地址 {(string.IsNullOrEmpty(ips) ? "-" : ips)}";
            _runtimeSub.Text = $"活跃会话 {snap.Telemetry.ActiveSessions} · 内存 {NetoFormat.FormatBytes(snap.Telemetry.MemoryBytes)}";
        }
    }
}
