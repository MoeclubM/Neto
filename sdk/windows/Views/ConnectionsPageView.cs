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
    private readonly TextBlock _connSummaryLabel;
    private readonly Button _pauseBtn;
    private readonly Button _actionAllBtn;
    private readonly StackPanel _connListPanel;
    private readonly TextBlock _emptyState;
    private readonly Dictionary<ulong, ConnectionRow> _rows = new();
    private readonly Dictionary<string, Button> _sortButtons = new();

    private string _subTab = "active";
    private string _connFilter = "";
    private string _connNetFilter = "all";
    private string _sortKey = "time";
    private bool _sortDescending = true;
    private bool _paused;
    private bool _fetchingConns;
    private bool _pendingRefresh;
    private bool _dialogOpen;

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
            await RefreshConnectionsAsync(forceRender: true);
        };
        _tabClosedBtn.Click += async (_, _) =>
        {
            SwitchSubTab("closed");
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
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _connSearchBox = new TextBox { PlaceholderText = "筛选主机、规则或出站" };
        _connSearchBox.TextChanged += (_, _) =>
        {
            _connFilter = (_connSearchBox.Text ?? "").Trim().ToLowerInvariant();
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
            await RefreshConnectionsAsync(forceRender: true);
        };

        Grid.SetColumn(_connNetCombo, 1);
        Grid.SetColumn(_pauseBtn, 2);
        Grid.SetColumn(_actionAllBtn, 3);
        connToolbar.Children.Add(_connSearchBox);
        connToolbar.Children.Add(_connNetCombo);
        connToolbar.Children.Add(_pauseBtn);
        connToolbar.Children.Add(_actionAllBtn);

        topStack.Children.Add(connToolbar);
        topStack.Children.Add(_connSummaryLabel);
        Grid.SetRow(topStack, 1);
        Root.Children.Add(topStack);

        _connListPanel = new StackPanel
        {
            MinWidth = 1100,
            Spacing = 0,
        };
        var listContent = new StackPanel { MinWidth = 1100 };
        var header = CreateColumns();
        var columns = new (string Key, string Title)[]
        {
            ("host", "主机"), ("download", "下载量"), ("upload", "上传量"),
            ("downSpeed", "下载速度"), ("upSpeed", "上传速度"), ("outbound", "链路"),
            ("rule", "规则"), ("time", "连接时间"),
        };
        for (var i = 0; i < columns.Length; i++)
        {
            var (key, title) = columns[i];
            var button = new Button
            {
                Content = title,
                Tag = title,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 5, 6, 5),
                FontSize = 12,
            };
            button.Click += (_, _) =>
            {
                _sortDescending = _sortKey == key ? !_sortDescending : key != "host" && key != "outbound" && key != "rule";
                _sortKey = key;
                UpdateSortHeaders();
                _ = RefreshConnectionsAsync(forceRender: true);
            };
            _sortButtons.Add(key, button);
            Grid.SetColumn(button, i);
            header.Children.Add(button);
        }
        UpdateSortHeaders();
        listContent.Children.Add(header);
        _emptyState = new TextBlock
        {
            Margin = new Thickness(6, 14, 6, 14),
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        listContent.Children.Add(_emptyState);
        listContent.Children.Add(_connListPanel);
        var connScroll = new ScrollViewer
        {
            Content = listContent,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(18, 0, 18, 24),
        };
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

    private sealed class ConnectionRow
    {
        public required Grid Element { get; init; }
        public required TextBlock[] Cells { get; init; }
        public required Button CloseButton { get; init; }
        public required NetoActiveConnection Item { get; set; }
        public bool IsClosed { get; set; }
    }

    private static Grid CreateColumns()
    {
        var grid = new Grid { ColumnSpacing = 2, MinWidth = 1100 };
        foreach (var width in new[] { 240d, 80, 80, 90, 90, 140, 190, 100, 70 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        return grid;
    }

    private void UpdateSortHeaders()
    {
        foreach (var (key, button) in _sortButtons)
            button.Content = $"{button.Tag}{(key == _sortKey ? (_sortDescending ? " ▼" : " ▲") : "")}";
    }

    private ConnectionRow CreateRow(NetoActiveConnection item, bool isClosed)
    {
        var grid = CreateColumns();
        var cells = new TextBlock[8];
        for (var i = 0; i < cells.Length; i++)
        {
            var text = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(6, 9, 6, 9),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
            cells[i] = text;
        }
        var closeButton = new Button
        {
            Content = "断开",
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(closeButton, 8);
        grid.Children.Add(closeButton);
        var row = new ConnectionRow { Element = grid, Cells = cells, CloseButton = closeButton, Item = item, IsClosed = isClosed };
        grid.Tapped += async (_, _) => await ShowConnectionDetailDialogAsync(row.Item, row.IsClosed);
        closeButton.Tapped += (_, e) => e.Handled = true;
        closeButton.Click += async (_, _) =>
        {
            await _client.CloseConnectionAsync(row.Item.Id);
            await RefreshConnectionsAsync(forceRender: true);
        };
        return row;
    }

    public async Task RefreshConnectionsAsync(bool forceRender = false)
    {
        if (_fetchingConns)
        {
            _pendingRefresh |= forceRender;
            return;
        }
        _fetchingConns = true;
        try
        {
            var snap = await _client.GetConnectionsAsync();
            var isClosed = _subTab == "closed";
            var source = isClosed ? snap.Closed : snap.Active;
            var matching = source.Where(c =>
                (_connNetFilter == "all" || string.Equals(c.Network, _connNetFilter, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(_connFilter) ||
                 c.Host.Contains(_connFilter, StringComparison.OrdinalIgnoreCase) ||
                 c.Rule.Contains(_connFilter, StringComparison.OrdinalIgnoreCase) ||
                 c.Outbound.Contains(_connFilter, StringComparison.OrdinalIgnoreCase)));
            Func<NetoActiveConnection, object> key = _sortKey switch
            {
                "host" => c => c.Host,
                "download" => c => c.DownloadBytes,
                "upload" => c => c.UploadBytes,
                "downSpeed" => c => c.DownloadSpeedBps,
                "upSpeed" => c => c.UploadSpeedBps,
                "outbound" => c => c.Outbound,
                "rule" => c => c.Rule,
                _ => c => isClosed ? c.ClosedAtMs : c.StartedAtMs,
            };
            var filtered = (_sortDescending ? matching.OrderByDescending(key) : matching.OrderBy(key))
                .ThenByDescending(c => c.Id).Take(200).ToList();

            _tabActiveBtn.Content = snap.Active.Count > 0 ? $"活跃会话 ({snap.Active.Count})" : "活跃会话";
            _tabClosedBtn.Content = snap.Closed.Count > 0 ? $"已关闭 ({snap.Closed.Count})" : "已关闭";
            _connSummaryLabel.Text = isClosed
                ? $"已关闭 {snap.Closed.Count} 条 · 累计 ↑ {NetoFormat.FormatBytes(snap.TotalUploadBytes)} ↓ {NetoFormat.FormatBytes(snap.TotalDownloadBytes)}"
                : $"活跃 {snap.Active.Count} · ↑ {NetoFormat.FormatSpeed(snap.TotalUploadSpeedBps)} ↓ {NetoFormat.FormatSpeed(snap.TotalDownloadSpeedBps)} · 累计 ↑ {NetoFormat.FormatBytes(snap.TotalUploadBytes)} ↓ {NetoFormat.FormatBytes(snap.TotalDownloadBytes)}";
            _actionAllBtn.IsEnabled = isClosed ? snap.Closed.Count > 0 : snap.Active.Count > 0;
            if (source.Count > 200) _connSummaryLabel.Text += " · 最多显示 200 条";
            _emptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _emptyState.Text = source.Count == 0 ? (isClosed ? "暂无已关闭会话" : "暂无活跃会话") : "无匹配会话";

            var ids = filtered.Select(c => c.Id).ToHashSet();
            foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _connListPanel.Children.Remove(_rows[id].Element);
                _rows.Remove(id);
            }
            var nowMs = NetoFormat.NowUnixMs();
            for (var index = 0; index < filtered.Count; index++)
            {
                var item = filtered[index];
                if (!_rows.TryGetValue(item.Id, out var row))
                {
                    row = CreateRow(item, isClosed);
                    _rows.Add(item.Id, row);
                    _connListPanel.Children.Insert(index, row.Element);
                }
                else
                {
                    var currentIndex = _connListPanel.Children.IndexOf(row.Element);
                    if (currentIndex != index)
                    {
                        _connListPanel.Children.RemoveAt(currentIndex);
                        _connListPanel.Children.Insert(index, row.Element);
                    }
                }
                row.Item = item;
                row.IsClosed = isClosed;
                row.CloseButton.Visibility = isClosed ? Visibility.Collapsed : Visibility.Visible;
                var durationMs = isClosed
                    ? (item.ClosedAtMs > item.StartedAtMs ? item.ClosedAtMs - item.StartedAtMs : 0UL)
                    : (nowMs > item.StartedAtMs ? nowMs - item.StartedAtMs : 0UL);
                var values = new[]
                {
                    $"{item.Host}  {item.Network.ToUpperInvariant()}",
                    NetoFormat.FormatBytes(item.DownloadBytes), NetoFormat.FormatBytes(item.UploadBytes),
                    NetoFormat.FormatSpeed(item.DownloadSpeedBps), NetoFormat.FormatSpeed(item.UploadSpeedBps),
                    NetoFormat.FormatDisplayName(item.Outbound), item.Rule, NetoFormat.FormatDurationMs(durationMs),
                };
                for (var i = 0; i < values.Length; i++)
                    if (row.Cells[i].Text != values[i]) row.Cells[i].Text = values[i];
            }
        }
        finally
        {
            _fetchingConns = false;
            if (_pendingRefresh)
            {
                _pendingRefresh = false;
                _ = RefreshConnectionsAsync(forceRender: true);
            }
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
