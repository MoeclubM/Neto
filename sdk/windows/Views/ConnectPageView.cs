using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public sealed class ConnectPageView
{
    public ScrollViewer Root { get; }
    private readonly NetoClient _client;
    private readonly Action? _onRequestElevation;
    private readonly StackPanel _blockedHost;
    private readonly StackPanel _mainContent;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusLabel;
    private readonly Border _sessionBadge;
    private readonly TextBlock _sessionBadgeText;
    private readonly TextBlock _durationLabel;
    private readonly TextBlock _endpointLabel;
    private readonly Button _connectButton;
    private readonly Button _probeButton;
    private readonly TextBlock _nodeNameLabel;
    private readonly TextBlock _nodeMetaLabel;
    private readonly TextBlock _profileNameLabel;
    private readonly TextBlock _profileMetaLabel;
    private readonly Button _refreshProfileBtn;
    private readonly Button _modeRuleBtn;
    private readonly Button _modeGlobalBtn;
    private readonly Button _modeDirectBtn;
    private readonly ToggleSwitch _sysProxySwitch;
    private readonly ToggleSwitch _tunSwitch;
    private readonly Button _elevateBtn;
    private readonly NetoDualSeriesChart _speedChart;
    private readonly NetoDualSeriesChart _latencyChart;
    private readonly Border _errorCard;
    private readonly TextBlock _errorLabel;
    private bool _updating;

    public ConnectPageView(NetoClient client, Action onOpenNodes, Action? onOpenProfiles = null, Action? onRequestElevation = null)
    {
        _client = client;
        _onRequestElevation = onRequestElevation;

        var outer = new StackPanel
        {
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10,
            Padding = new Thickness(20, 16, 20, 16),
        };

        _blockedHost = new StackPanel { Visibility = Visibility.Collapsed };
        _mainContent = new StackPanel { Spacing = 10 };

        var heroGrid = new Grid { ColumnSpacing = 16 };
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _sessionBadgeText = new TextBlock { FontSize = 11, Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray) };
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
            FontSize = 24,
            FontFamily = new FontFamily("Consolas"),
        };
        _endpointLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        var heroLeft = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        heroLeft.Children.Add(statusLine);
        heroLeft.Children.Add(_durationLabel);
        heroLeft.Children.Add(_endpointLabel);

        _connectButton = new Button
        {
            Content = "连接",
            MinWidth = 132,
            Height = 38,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _connectButton.Click += async (_, _) => await _client.ToggleConnectAsync();

        _probeButton = new Button
        {
            Content = "测速",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        _probeButton.Click += async (_, _) =>
        {
            _probeButton.IsEnabled = false;
            try { await _client.ProbeTunnelAsync(); }
            finally { _probeButton.IsEnabled = true; }
        };

        var heroRight = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        heroRight.Children.Add(_connectButton);
        heroRight.Children.Add(_probeButton);
        Grid.SetColumn(heroRight, 1);
        heroGrid.Children.Add(heroLeft);
        heroGrid.Children.Add(heroRight);
        _mainContent.Children.Add(NetoUiFactory.CreateCard(heroGrid, new Thickness(16, 14, 16, 14)));

        var contextGrid = new Grid { ColumnSpacing = 10 };
        contextGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contextGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nodeGrid = new Grid();
        nodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nodeLeft = new StackPanel { Spacing = 2 };
        nodeLeft.Children.Add(new TextBlock
        {
            Text = "当前节点",
            FontSize = 11,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        });
        _nodeNameLabel = new TextBlock { FontWeight = FontWeights.SemiBold, Text = "暂无可用节点", TextTrimming = TextTrimming.CharacterEllipsis };
        _nodeMetaLabel = new TextBlock
        {
            FontSize = 12,
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
            Padding = new Thickness(14, 10, 14, 10),
            Content = nodeGrid,
        };
        nodeBtn.Click += (_, _) => onOpenNodes();
        contextGrid.Children.Add(nodeBtn);

        var profCardGrid = new Grid();
        profCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        profCardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var profBtnContent = new Grid();
        profBtnContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        profBtnContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var profLeft = new StackPanel { Spacing = 2 };
        profLeft.Children.Add(new TextBlock
        {
            Text = "当前订阅",
            FontSize = 11,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        });
        _profileNameLabel = new TextBlock { FontWeight = FontWeights.SemiBold, Text = "未配置订阅", TextTrimming = TextTrimming.CharacterEllipsis };
        _profileMetaLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        profLeft.Children.Add(_profileNameLabel);
        profLeft.Children.Add(_profileMetaLabel);
        var profChevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = onOpenProfiles != null ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(profChevron, 1);
        profBtnContent.Children.Add(profLeft);
        profBtnContent.Children.Add(profChevron);
        var profJumpBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10, 14, 10),
            IsEnabled = onOpenProfiles != null,
            Content = profBtnContent,
        };
        if (onOpenProfiles != null) profJumpBtn.Click += (_, _) => onOpenProfiles();
        _refreshProfileBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE72C", FontSize = 13 },
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(6, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(_refreshProfileBtn, "更新当前订阅");
        _refreshProfileBtn.Click += async (_, _) =>
        {
            _refreshProfileBtn.IsEnabled = false;
            try { await _client.RefreshProfileAsync(); }
            finally { _refreshProfileBtn.IsEnabled = true; }
        };
        Grid.SetColumn(_refreshProfileBtn, 1);
        profCardGrid.Children.Add(profJumpBtn);
        profCardGrid.Children.Add(_refreshProfileBtn);
        Grid.SetColumn(profCardGrid, 1);
        contextGrid.Children.Add(profCardGrid);
        _mainContent.Children.Add(contextGrid);

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

        _elevateBtn = new Button
        {
            Content = "提权启用 TUN",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Visibility = Visibility.Collapsed,
        };
        _elevateBtn.Click += (_, _) => _onRequestElevation?.Invoke();
        var takeoverStack = new StackPanel { Spacing = 6 };
        takeoverStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("网络接管", _elevateBtn));
        var takeoverGrid = new Grid { ColumnSpacing = 8 };
        takeoverGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        takeoverGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sysProxySwitch = new ToggleSwitch { OffContent = "系统代理", OnContent = "系统代理", MinWidth = 0 };
        _tunSwitch = new ToggleSwitch { OffContent = "TUN 接管", OnContent = "TUN 接管", MinWidth = 0 };
        _sysProxySwitch.Toggled += async (_, _) => { if (!_updating) await _client.SetSystemProxyAsync(_sysProxySwitch.IsOn); };
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
        takeoverGrid.Children.Add(_sysProxySwitch);
        takeoverGrid.Children.Add(_tunSwitch);
        takeoverStack.Children.Add(takeoverGrid);
        var takeoverCard = NetoUiFactory.CreateCard(takeoverStack, new Thickness(14, 10, 14, 10));
        Grid.SetColumn(takeoverCard, 1);
        controlsGrid.Children.Add(takeoverCard);
        _mainContent.Children.Add(controlsGrid);

        var chartsRow = new Grid { ColumnSpacing = 10 };
        chartsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chartsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _speedChart = new NetoDualSeriesChart(Color.FromArgb(255, 46, 184, 128), Color.FromArgb(255, 56, 132, 235), "实时吞吐");
        _latencyChart = new NetoDualSeriesChart(Color.FromArgb(255, 56, 132, 235), null, "连通延迟");
        Grid.SetColumn(_latencyChart.Root, 1);
        chartsRow.Children.Add(_speedChart.Root);
        chartsRow.Children.Add(_latencyChart.Root);
        _mainContent.Children.Add(chartsRow);

        _errorLabel = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 216, 82, 76)),
        };
        _errorCard = NetoUiFactory.CreateCard(_errorLabel, new Thickness(12, 8, 12, 8));
        _errorCard.Visibility = Visibility.Collapsed;
        _mainContent.Children.Add(_errorCard);

        outer.Children.Add(_blockedHost);
        outer.Children.Add(_mainContent);
        Root = NetoUiFactory.CreateCenteredScrollViewer(outer, centerVertically: true);
    }

    public void SetBlockedElement(UIElement? element)
    {
        _blockedHost.Children.Clear();
        if (element != null)
        {
            _blockedHost.Children.Add(element);
            _blockedHost.Visibility = Visibility.Visible;
            _mainContent.Visibility = Visibility.Collapsed;
        }
        else
        {
            _blockedHost.Visibility = Visibility.Collapsed;
            _mainContent.Visibility = Visibility.Visible;
        }
    }

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
        _probeButton.Visibility = conn.Connected ? Visibility.Visible : Visibility.Collapsed;

        if (conn.Connected)
        {
            _sessionBadge.Visibility = Visibility.Visible;
            _sessionBadgeText.Text = conn.SessionKind switch
            {
                "tun" => "TUN 全局网卡",
                "route" => "规则分流",
                _ => "代理通道",
            };
            var addr = string.IsNullOrEmpty(conn.HttpProxyAddr) ? $"127.0.0.1:{snap.Settings.MixedPort}" : conn.HttpProxyAddr;
            _endpointLabel.Text = $"本地混合端口 {addr}" + (conn.SystemProxyActive ? " · 系统代理已启用" : "");
        }
        else
        {
            _sessionBadge.Visibility = Visibility.Collapsed;
            _endpointLabel.Text = $"预设混合端口 127.0.0.1:{snap.Settings.MixedPort}";
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

        var prof = snap.ActiveProfile;
        if (prof != null)
        {
            _profileNameLabel.Text = prof.Name;
            _profileMetaLabel.Text = prof.TotalBytes > 0
                ? $"{prof.NodeCount} 节点 · 已用 {NetoFormat.FormatBytes(prof.UsedBytes)} / {NetoFormat.FormatBytes(prof.TotalBytes)}"
                : $"{prof.NodeCount} 节点 · {prof.RuleCount} 规则";
            _refreshProfileBtn.Visibility = !string.IsNullOrWhiteSpace(prof.Url) ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _profileNameLabel.Text = "未配置订阅";
            _profileMetaLabel.Text = "暂无订阅";
            _refreshProfileBtn.Visibility = Visibility.Collapsed;
        }

        NetoUiFactory.ApplyPrimaryButton(_modeRuleBtn, snap.Settings.RoutingMode == "rule");
        NetoUiFactory.ApplyPrimaryButton(_modeGlobalBtn, snap.Settings.RoutingMode == "global");
        NetoUiFactory.ApplyPrimaryButton(_modeDirectBtn, snap.Settings.RoutingMode == "direct");

        _sysProxySwitch.IsOn = snap.Settings.SystemProxyEnabled;
        _tunSwitch.IsOn = snap.Settings.TunEnabled;
        _elevateBtn.Visibility = !conn.CanUseTun && _onRequestElevation != null ? Visibility.Visible : Visibility.Collapsed;

        _speedChart.Update(
            $"↑ {NetoFormat.FormatSpeed(snap.Telemetry.UploadSpeedBps)}  ↓ {NetoFormat.FormatSpeed(snap.Telemetry.DownloadSpeedBps)}",
            snap.Telemetry.UploadHistory.Select(v => (double)v).ToList(),
            snap.Telemetry.DownloadHistory.Select(v => (double)v).ToList()
        );
        var validLat = snap.Telemetry.LatencyHistory.Where(v => v >= 0).Select(v => (double)v).ToList();
        _latencyChart.Update(
            snap.Telemetry.LatencyMs.HasValue ? $"{snap.Telemetry.LatencyMs.Value} ms" : "- ms",
            validLat
        );

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
    }
}
