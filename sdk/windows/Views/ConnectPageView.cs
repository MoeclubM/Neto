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
    private readonly StackPanel _blockedHost;
    private readonly StackPanel _mainContent;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _durationLabel;
    private readonly Button _connectButton;
    private readonly TextBlock _nodeNameLabel;
    private readonly TextBlock _nodeMetaLabel;
    private readonly Button _modeRuleBtn;
    private readonly Button _modeGlobalBtn;
    private readonly Button _modeDirectBtn;
    private readonly ToggleSwitch _sysProxySwitch;
    private readonly ToggleSwitch _tunSwitch;
    private readonly Grid _chartsRow;
    private readonly NetoDualSeriesChart _speedChart;
    private readonly NetoDualSeriesChart _latencyChart;
    private readonly Border _errorCard;
    private readonly TextBlock _errorLabel;
    private bool _updating;

    public ConnectPageView(NetoClient client, Action onOpenNodes)
    {
        _client = client;

        var outer = new StackPanel
        {
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            Padding = new Thickness(20, 24, 20, 24),
        };

        _blockedHost = new StackPanel { Visibility = Visibility.Collapsed };
        _mainContent = new StackPanel { Spacing = 12 };

        // Titleless Connect hero card
        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8,
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
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
        };
        statusRow.Children.Add(_statusDot);
        statusRow.Children.Add(_statusLabel);

        _durationLabel = new TextBlock
        {
            Text = "00:00:00",
            FontSize = 28,
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _connectButton = new Button
        {
            Content = "连接",
            MinWidth = 180,
            Height = 42,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _connectButton.Click += async (_, _) => await _client.ToggleConnectAsync();

        var heroStack = new StackPanel { Spacing = 12, Padding = new Thickness(8, 10, 8, 10) };
        heroStack.Children.Add(statusRow);
        heroStack.Children.Add(_durationLabel);
        heroStack.Children.Add(_connectButton);
        _mainContent.Children.Add(NetoUiFactory.CreateCard(heroStack));

        // Current node jump button
        var nodeGrid = new Grid();
        nodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nodeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nodeLeft = new StackPanel { Spacing = 2 };
        _nodeNameLabel = new TextBlock { FontWeight = FontWeights.SemiBold, Text = "暂无可用节点" };
        _nodeMetaLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        nodeLeft.Children.Add(_nodeNameLabel);
        nodeLeft.Children.Add(_nodeMetaLabel);
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 1);
        nodeGrid.Children.Add(nodeLeft);
        nodeGrid.Children.Add(chevron);

        var nodeBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10, 14, 10),
            Content = nodeGrid,
        };
        nodeBtn.Click += (_, _) => onOpenNodes();
        _mainContent.Children.Add(nodeBtn);

        // Outbound mode bar
        var modeGrid = new Grid { ColumnSpacing = 8 };
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
        _mainContent.Children.Add(modeGrid);

        // Quick takeover switches
        var takeoverGrid = new Grid { ColumnSpacing = 12 };
        takeoverGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        takeoverGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sysProxySwitch = new ToggleSwitch { Header = "系统代理", OffContent = "关", OnContent = "开" };
        _tunSwitch = new ToggleSwitch { Header = "TUN 接管", OffContent = "关", OnContent = "开" };
        _sysProxySwitch.Toggled += async (_, _) => { if (!_updating) await _client.SetSystemProxyAsync(_sysProxySwitch.IsOn); };
        _tunSwitch.Toggled += async (_, _) => { if (!_updating) await _client.SetTunEnabledAsync(_tunSwitch.IsOn); };
        Grid.SetColumn(_tunSwitch, 1);
        takeoverGrid.Children.Add(_sysProxySwitch);
        takeoverGrid.Children.Add(_tunSwitch);
        _mainContent.Children.Add(NetoUiFactory.CreateCard(takeoverGrid, new Thickness(14, 8, 14, 8)));

        // Live speed & latency charts
        _chartsRow = new Grid { ColumnSpacing = 10, Visibility = Visibility.Collapsed };
        _chartsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _chartsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _speedChart = new NetoDualSeriesChart(Color.FromArgb(255, 46, 184, 128), Color.FromArgb(255, 56, 132, 235));
        _latencyChart = new NetoDualSeriesChart(Color.FromArgb(255, 56, 132, 235));
        Grid.SetColumn(_latencyChart.Root, 1);
        _chartsRow.Children.Add(_speedChart.Root);
        _chartsRow.Children.Add(_latencyChart.Root);
        _mainContent.Children.Add(_chartsRow);

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
        Root = new ScrollViewer { Content = outer };
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
        _connectButton.Content = conn.Connecting ? "连接中" : conn.Connected ? "断开" : "连接";
        _connectButton.IsEnabled = !conn.Connecting;
        NetoUiFactory.ApplyPrimaryButton(_connectButton, !conn.Connected);

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
            _nodeMetaLabel.Text = "前往节点页选择";
        }

        NetoUiFactory.ApplyPrimaryButton(_modeRuleBtn, snap.Settings.RoutingMode == "rule");
        NetoUiFactory.ApplyPrimaryButton(_modeGlobalBtn, snap.Settings.RoutingMode == "global");
        NetoUiFactory.ApplyPrimaryButton(_modeDirectBtn, snap.Settings.RoutingMode == "direct");

        _sysProxySwitch.IsOn = snap.Settings.SystemProxyEnabled;
        _tunSwitch.IsOn = snap.Settings.TunEnabled;

        _chartsRow.Visibility = conn.Connected ? Visibility.Visible : Visibility.Collapsed;
        if (conn.Connected)
        {
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
        }

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
