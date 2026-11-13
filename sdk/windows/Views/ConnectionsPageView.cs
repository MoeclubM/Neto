using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public sealed class ConnectionsPageView
{
    public Grid Root { get; }
    public StackPanel HeaderActions { get; }
    private readonly NetoClient _client;
    private readonly Button _tabActiveBtn;
    private readonly Button _tabClosedBtn;
    private readonly TextBox _connSearchBox;
    private readonly ComboBox _connNetCombo;
    private readonly ComboBox _connSortCombo;
    private readonly TextBlock _connSummaryLabel;
    private readonly Button _pauseBtn;
    private readonly Button _actionAllBtn;
    private readonly StackPanel _connListPanel;

    private string _subTab = "active";
    private string _connFilter = "";
    private string _connNetFilter = "all";
    private int _sortIndex;
    private bool _paused;
    private bool _fetchingConns;
    private bool _dialogOpen;
    private string _lastConnSig = "";

    public ConnectionsPageView(NetoClient client, bool detachHeaderActions = false)
    {
        _client = client;
        Root = new Grid { RowSpacing = detachHeaderActions ? 0 : 8 };
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        HeaderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
        };
        _tabActiveBtn = new Button { Content = "活跃会话" };
        _tabClosedBtn = new Button { Content = "已关闭" };
        _tabActiveBtn.Click += async (_, _) =>
        {
            SwitchSubTab("active");
            _lastConnSig = "";
            await RefreshConnectionsAsync(forceRender: true);
        };
        _tabClosedBtn.Click += async (_, _) =>
        {
            SwitchSubTab("closed");
            _lastConnSig = "";
            await RefreshConnectionsAsync(forceRender: true);
        };
        HeaderActions.Children.Add(_tabActiveBtn);
        HeaderActions.Children.Add(_tabClosedBtn);

        if (!detachHeaderActions)
        {
            var inlineHeader = new Grid
            {
                MaxWidth = 860,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(18, 6, 18, 0),
            };
            inlineHeader.Children.Add(HeaderActions);
            Root.Children.Add(inlineHeader);
        }

        var topStack = new StackPanel
        {
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(18, 6, 18, 4),
            Spacing = 6,
        };

        var connToolbar = new Grid
        {
            ColumnSpacing = 8,
        };
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(102) });
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _connSearchBox = new TextBox { PlaceholderText = "筛选主机、规则或出站" };
        _connSearchBox.TextChanged += (_, _) =>
        {
            _connFilter = (_connSearchBox.Text ?? "").Trim().ToLowerInvariant();
            _lastConnSig = "";
            _ = RefreshConnectionsAsync(forceRender: true);
        };
        _connNetCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _connNetCombo.Items.Add("全部协议");
        _connNetCombo.Items.Add("TCP");
        _connNetCombo.Items.Add("UDP");
        _connNetCombo.SelectedIndex = 0;
        _connNetCombo.SelectionChanged += (_, _) =>
        {
            _connNetFilter = _connNetCombo.SelectedIndex switch { 1 => "tcp", 2 => "udp", _ => "all" };
            _lastConnSig = "";
            _ = RefreshConnectionsAsync(forceRender: true);
        };
        _connSortCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _connSortCombo.Items.Add("最新");
        _connSortCombo.Items.Add("下载速率");
        _connSortCombo.Items.Add("上传速率");
        _connSortCombo.Items.Add("总流量");
        _connSortCombo.Items.Add("域名");
        _connSortCombo.SelectedIndex = 0;
        _connSortCombo.SelectionChanged += (_, _) =>
        {
            _sortIndex = Math.Clamp(_connSortCombo.SelectedIndex, 0, 4);
            _lastConnSig = "";
            _ = RefreshConnectionsAsync(forceRender: true);
        };

        _connSummaryLabel = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        _pauseBtn = new Button { Content = "暂停" };
        _pauseBtn.Click += (_, _) =>
        {
            _paused = !_paused;
            _pauseBtn.Content = _paused ? "继续" : "暂停";
            NetoUiFactory.ApplyPrimaryButton(_pauseBtn, _paused);
        };
        _actionAllBtn = new Button { Content = "断开全部" };
        _actionAllBtn.Click += async (_, _) =>
        {
            if (_subTab == "closed")
            {
                await _client.ClearClosedConnectionsAsync();
            }
            else
            {
                await _client.CloseAllConnectionsAsync();
            }
            _lastConnSig = "";
            await RefreshConnectionsAsync(forceRender: true);
        };

        Grid.SetColumn(_connNetCombo, 1);
        Grid.SetColumn(_connSortCombo, 2);
        Grid.SetColumn(_pauseBtn, 3);
        Grid.SetColumn(_actionAllBtn, 4);
        connToolbar.Children.Add(_connSearchBox);
        connToolbar.Children.Add(_connNetCombo);
        connToolbar.Children.Add(_connSortCombo);
        connToolbar.Children.Add(_pauseBtn);
        connToolbar.Children.Add(_actionAllBtn);

        topStack.Children.Add(connToolbar);
        topStack.Children.Add(_connSummaryLabel);
        Grid.SetRow(topStack, 1);
        Root.Children.Add(topStack);

        _connListPanel = new StackPanel
        {
            MaxWidth = 860,
            Spacing = 6,
            Padding = new Thickness(18, 0, 18, 24),
        };
        var connScroll = NetoUiFactory.CreateCenteredScrollViewer(_connListPanel);
        Grid.SetRow(connScroll, 2);
        Root.Children.Add(connScroll);

        SwitchSubTab("active");
    }

    public void SwitchSubTab(string subTab)
    {
        _subTab = subTab == "closed" ? "closed" : "active";
        NetoUiFactory.ApplyPrimaryButton(_tabActiveBtn, _subTab == "active");
        NetoUiFactory.ApplyPrimaryButton(_tabClosedBtn, _subTab == "closed");
        _actionAllBtn.Content = _subTab == "closed" ? "清空记录" : "断开全部";
    }

    public void ApplySnapshot(NetoSnapshot snap)
    {
        if (Root.Visibility != Visibility.Visible) return;
        if (!_paused)
        {
            _ = RefreshConnectionsAsync();
        }
    }

    public async Task RefreshConnectionsAsync(bool forceRender = false)
    {
        if (_fetchingConns) return;
        _fetchingConns = true;
        try
        {
            var snap = await _client.GetConnectionsAsync();
            var isClosed = _subTab == "closed";
            var source = isClosed ? snap.Closed : snap.Active;
            var matching = source.Where(c =>
            {
                if (_connNetFilter != "all" && !string.Equals(c.Network, _connNetFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.IsNullOrEmpty(_connFilter))
                {
                    var hit = c.Host.ToLowerInvariant().Contains(_connFilter) ||
                              c.Rule.ToLowerInvariant().Contains(_connFilter) ||
                              c.Outbound.ToLowerInvariant().Contains(_connFilter);
                    if (!hit) return false;
                }
                return true;
            });

            var filtered = (_sortIndex switch
            {
                1 => matching.OrderByDescending(c => c.DownloadSpeedBps).ThenByDescending(c => c.DownloadBytes),
                2 => matching.OrderByDescending(c => c.UploadSpeedBps).ThenByDescending(c => c.UploadBytes),
                3 => matching.OrderByDescending(c => c.UploadBytes + c.DownloadBytes),
                4 => matching.OrderBy(c => c.Host, StringComparer.OrdinalIgnoreCase),
                _ => isClosed
                    ? matching.OrderByDescending(c => c.ClosedAtMs).ThenByDescending(c => c.Id)
                    : matching.OrderByDescending(c => c.StartedAtMs).ThenByDescending(c => c.Id),
            }).ToList();

            _tabActiveBtn.Content = snap.Active.Count > 0 ? $"活跃会话 ({snap.Active.Count})" : "活跃会话";
            _tabClosedBtn.Content = snap.Closed.Count > 0 ? $"已关闭 ({snap.Closed.Count})" : "已关闭";
            _connSummaryLabel.Text = isClosed
                ? $"已关闭 {snap.Closed.Count} 条 · 累计 ↑ {NetoFormat.FormatBytes(snap.TotalUploadBytes)} ↓ {NetoFormat.FormatBytes(snap.TotalDownloadBytes)}"
                : $"活跃 {snap.Active.Count} · ↑ {NetoFormat.FormatSpeed(snap.TotalUploadSpeedBps)} ↓ {NetoFormat.FormatSpeed(snap.TotalDownloadSpeedBps)} · 累计 ↑ {NetoFormat.FormatBytes(snap.TotalUploadBytes)} ↓ {NetoFormat.FormatBytes(snap.TotalDownloadBytes)}";
            _actionAllBtn.IsEnabled = isClosed ? snap.Closed.Count > 0 : snap.Active.Count > 0;

            var nowMs = NetoFormat.NowUnixMs();
            var sig = string.Join("|",
                _subTab,
                _connNetFilter,
                _connFilter,
                _sortIndex,
                snap.Active.Count,
                snap.Closed.Count,
                string.Join(";", filtered.Take(200).Select(c =>
                    $"{c.Id}:{c.UploadBytes / 1024}:{c.DownloadBytes / 1024}:{c.UploadSpeedBps / 512}:{c.DownloadSpeedBps / 512}:{(isClosed ? c.ClosedAtMs : (nowMs - Math.Min(nowMs, c.StartedAtMs)) / 1000)}")));
            if (!forceRender && _lastConnSig == sig) return;
            _lastConnSig = sig;

            _connListPanel.Children.Clear();
            if (filtered.Count == 0)
            {
                _connListPanel.Children.Add(NetoUiFactory.CreateCard(new TextBlock
                {
                    Text = source.Count == 0 ? (isClosed ? "暂无已关闭会话" : "暂无活跃会话") : "无匹配会话",
                    Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                }));
                return;
            }

            foreach (var item in filtered.Take(200))
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (!isClosed)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                }

                var contentGrid = new Grid { ColumnSpacing = 10 };
                contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { Spacing = 4 };
                left.Children.Add(new TextBlock
                {
                    Text = item.Host,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                badges.Children.Add(NetoUiFactory.CreateTagBadge(item.Network.ToUpperInvariant()));
                if (!string.IsNullOrWhiteSpace(item.Outbound))
                {
                    badges.Children.Add(NetoUiFactory.CreateTagBadge(item.Outbound, Color.FromArgb(255, 56, 132, 235)));
                }
                if (!string.IsNullOrWhiteSpace(item.Rule))
                {
                    badges.Children.Add(NetoUiFactory.CreateTagBadge(item.Rule));
                }
                left.Children.Add(badges);

                var durationMs = isClosed
                    ? (item.ClosedAtMs > item.StartedAtMs ? item.ClosedAtMs - item.StartedAtMs : 0UL)
                    : (nowMs > item.StartedAtMs ? nowMs - item.StartedAtMs : 0UL);
                var stats = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                stats.Children.Add(new TextBlock
                {
                    Text = isClosed
                        ? $"↑ {NetoFormat.FormatBytes(item.UploadBytes)} · ↓ {NetoFormat.FormatBytes(item.DownloadBytes)}"
                        : $"↑ {NetoFormat.FormatSpeed(item.UploadSpeedBps)} · ↓ {NetoFormat.FormatSpeed(item.DownloadSpeedBps)}",
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
                stats.Children.Add(new TextBlock
                {
                    Text = isClosed
                        ? $"{NetoFormat.FormatDurationMs(durationMs)} · {NetoFormat.FormatLogTimestamp(item.ClosedAtMs)}"
                        : $"↑ {NetoFormat.FormatBytes(item.UploadBytes)} ↓ {NetoFormat.FormatBytes(item.DownloadBytes)} · {NetoFormat.FormatDurationMs(durationMs)}",
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                });

                Grid.SetColumn(stats, 1);
                contentGrid.Children.Add(left);
                contentGrid.Children.Add(stats);

                var itemSnapshot = item;
                var openDetailBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(12, 8, 12, 8),
                    Content = contentGrid,
                };
                openDetailBtn.Click += async (_, _) => await ShowConnectionDetailDialogAsync(itemSnapshot, isClosed);
                row.Children.Add(openDetailBtn);

                if (!isClosed)
                {
                    var cid = item.Id;
                    var closeBtn = new Button
                    {
                        Content = "断开",
                        Padding = new Thickness(10, 4, 10, 4),
                        Margin = new Thickness(0, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    closeBtn.Click += async (_, _) =>
                    {
                        await _client.CloseConnectionAsync(cid);
                        _lastConnSig = "";
                        await RefreshConnectionsAsync(forceRender: true);
                    };
                    Grid.SetColumn(closeBtn, 1);
                    row.Children.Add(closeBtn);
                }
                _connListPanel.Children.Add(NetoUiFactory.CreateCard(row, new Thickness(0)));
            }
        }
        finally
        {
            _fetchingConns = false;
        }
    }

    private async Task ShowConnectionDetailDialogAsync(NetoActiveConnection item, bool isClosed)
    {
        if (_dialogOpen || Root.XamlRoot == null) return;
        _dialogOpen = true;
        try
        {
            var nowMs = NetoFormat.NowUnixMs();
            var durationMs = isClosed
                ? (item.ClosedAtMs > item.StartedAtMs ? item.ClosedAtMs - item.StartedAtMs : 0UL)
                : (nowMs > item.StartedAtMs ? nowMs - item.StartedAtMs : 0UL);
            var body = new StackPanel { Spacing = 8, MinWidth = 360 };
            AddDetailRow(body, "目标主机", item.Host);
            AddDetailRow(body, "传输协议", item.Network.ToUpperInvariant());
            AddDetailRow(body, "出站策略", string.IsNullOrWhiteSpace(item.Outbound) ? "-" : item.Outbound);
            AddDetailRow(body, "命中规则", string.IsNullOrWhiteSpace(item.Rule) ? "-" : item.Rule);
            AddDetailRow(body, "会话状态", isClosed ? "已关闭" : "活跃");
            if (!isClosed)
            {
                AddDetailRow(body, "实时速率", $"↑ {NetoFormat.FormatSpeed(item.UploadSpeedBps)} · ↓ {NetoFormat.FormatSpeed(item.DownloadSpeedBps)}");
            }
            AddDetailRow(body, "累计流量", $"↑ {NetoFormat.FormatBytes(item.UploadBytes)} · ↓ {NetoFormat.FormatBytes(item.DownloadBytes)}");
            AddDetailRow(body, "建立时间", NetoFormat.FormatLogTimestamp(item.StartedAtMs));
            AddDetailRow(body, "持续时长", NetoFormat.FormatDurationMs(durationMs));
            if (isClosed)
            {
                AddDetailRow(body, "关闭时间", NetoFormat.FormatLogTimestamp(item.ClosedAtMs));
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "会话详情",
                Content = body,
                PrimaryButtonText = "复制主机",
                SecondaryButtonText = isClosed ? "" : "断开连接",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };
            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                NetoWindowHelper.SetClipboardText(item.Host);
            }
            else if (res == ContentDialogResult.Secondary && !isClosed)
            {
                await _client.CloseConnectionAsync(item.Id);
                _lastConnSig = "";
                await RefreshConnectionsAsync(forceRender: true);
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static void AddDetailRow(StackPanel host, string label, string value)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        });
        var valBlock = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(valBlock, 1);
        row.Children.Add(valBlock);
        host.Children.Add(row);
    }
}
