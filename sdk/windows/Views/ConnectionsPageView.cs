using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
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
    private readonly ListView _connList;
    private readonly TextBlock _emptyState;
    private readonly InfoBar _feedback;
    private readonly Dictionary<ulong, ConnectionListItem> _rows = new();
    private readonly ObservableCollection<ConnectionListItem> _items = new();
    private readonly Dictionary<string, Button> _sortButtons = new();
    private NetoConnectionsSnapshot _snapshot = new();

    private string _subTab = "active";
    private string _connFilter = "";
    private string _connNetFilter = "all";
    private string _sortKey = "time";
    private bool _sortDescending;
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
            RenderConnections();
            if (!_paused) await RefreshConnectionsAsync();
        };
        _tabClosedBtn.Click += async (_, _) =>
        {
            SwitchSubTab("closed");
            RenderConnections();
            if (!_paused) await RefreshConnectionsAsync();
        };
        HeaderActions.Children.Add(_tabActiveBtn);
        HeaderActions.Children.Add(_tabClosedBtn);

        if (!detachHeaderActions)
        {
            var inlineHeader = new Grid
            {
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
            RenderConnections();
        };
        _connNetCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _connNetCombo.Items.Add("全部协议");
        _connNetCombo.Items.Add("TCP");
        _connNetCombo.Items.Add("UDP");
        _connNetCombo.SelectedIndex = 0;
        _connNetCombo.SelectionChanged += (_, _) =>
        {
            _connNetFilter = _connNetCombo.SelectedIndex switch { 1 => "tcp", 2 => "udp", _ => "all" };
            RenderConnections();
        };
        _connSummaryLabel = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Style = NetoUiFactory.StyleResource("NetoSecondaryTextStyle"),
        };
        _pauseBtn = new Button { Content = "暂停" };
        _pauseBtn.Click += (_, _) =>
        {
            _paused = !_paused;
            _pauseBtn.Content = _paused ? "继续" : "暂停";
            NetoUiFactory.ApplyPrimaryButton(_pauseBtn, _paused);
            if (!_paused) _ = RefreshConnectionsAsync();
        };
        _actionAllBtn = new Button { Content = "断开全部" };
        _actionAllBtn.Click += async (_, _) =>
        {
            await RunActionAsync(async () =>
            {
                if (_subTab == "closed") await _client.ClearClosedConnectionsAsync();
                else await _client.CloseAllConnectionsAsync();
            });
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
        _feedback = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true };
        topStack.Children.Add(_feedback);
        Grid.SetRow(topStack, 1);
        Root.Children.Add(topStack);

        var listContent = new Grid { MinWidth = 1100 };
        listContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = CreateColumns();
        var columns = new (string Key, string Title)[]
        {
            ("host", "主机"), ("download", "下载量"), ("upload", "上传量"),
            ("downSpeed", "下载速度"), ("upSpeed", "上传速度"), ("outbound", "链路"),
            ("rule", "规则"), ("time", "持续时长"),
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
                RenderConnections();
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
            Style = NetoUiFactory.StyleResource("NetoSecondaryTextStyle"),
        };
        Grid.SetRow(_emptyState, 1);
        listContent.Children.Add(_emptyState);
        var itemStyle = new Style { TargetType = typeof(ListViewItem) };
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        _connList = new ListView
        {
            ItemsSource = _items,
            ItemTemplate = (DataTemplate)Application.Current.Resources["NetoConnectionTemplate"],
            ItemContainerStyle = itemStyle,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
        };
        _connList.ItemClick += async (_, e) =>
        {
            var row = (ConnectionListItem)e.ClickedItem;
            await ShowConnectionDetailDialogAsync(row.Item, row.IsClosed);
        };
        ScrollViewer.SetHorizontalScrollMode(_connList, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_connList, ScrollBarVisibility.Disabled);
        Grid.SetRow(_connList, 1);
        listContent.Children.Add(_connList);
        var connScroll = new ScrollViewer
        {
            Content = listContent,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 0, 18, 18),
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

    private IOrderedEnumerable<NetoActiveConnection> SortBy<T>(
        IEnumerable<NetoActiveConnection> source, Func<NetoActiveConnection, T> key, IComparer<T>? comparer = null) =>
        _sortDescending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

    private void RenderConnections()
    {
        var snap = _snapshot;
        var nowMs = NetoFormat.NowUnixMs();
        var isClosed = _subTab == "closed";
        var source = isClosed ? snap.Closed : snap.Active;
        var matching = source.Where(c =>
            (_connNetFilter == "all" || string.Equals(c.Network, _connNetFilter, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(_connFilter) ||
             c.Host.Contains(_connFilter, StringComparison.OrdinalIgnoreCase) ||
             c.Rule.Contains(_connFilter, StringComparison.OrdinalIgnoreCase) ||
             c.Outbound.Contains(_connFilter, StringComparison.OrdinalIgnoreCase)));
        var ordered = _sortKey switch
        {
            "host" => SortBy(matching, c => c.Host, StringComparer.OrdinalIgnoreCase),
            "download" => SortBy(matching, c => c.DownloadBytes),
            "upload" => SortBy(matching, c => c.UploadBytes),
            "downSpeed" => SortBy(matching, c => c.DownloadSpeedBps),
            "upSpeed" => SortBy(matching, c => c.UploadSpeedBps),
            "outbound" => SortBy(matching, c => c.Outbound, StringComparer.OrdinalIgnoreCase),
            "rule" => SortBy(matching, c => c.Rule, StringComparer.OrdinalIgnoreCase),
            _ => SortBy(matching, c =>
                (isClosed ? c.ClosedAtMs : nowMs) > c.StartedAtMs
                    ? (isClosed ? c.ClosedAtMs : nowMs) - c.StartedAtMs : 0UL),
        };
        var filtered = (_sortDescending ? ordered.ThenByDescending(c => c.Id) : ordered.ThenBy(c => c.Id)).ToList();

        _tabActiveBtn.Content = snap.Active.Count > 0 ? $"活跃会话 ({snap.Active.Count})" : "活跃会话";
        _tabClosedBtn.Content = snap.Closed.Count > 0 ? $"已关闭 ({snap.Closed.Count})" : "已关闭";
        _connSummaryLabel.Text = isClosed
            ? $"已关闭 {snap.Closed.Count} 条 · 累计 ↑ {NetoFormat.FormatBytes(snap.TotalUploadBytes)} ↓ {NetoFormat.FormatBytes(snap.TotalDownloadBytes)}"
            : $"活跃 {snap.Active.Count} · ↑ {NetoFormat.FormatSpeed(snap.TotalUploadSpeedBps)} ↓ {NetoFormat.FormatSpeed(snap.TotalDownloadSpeedBps)} · 累计 ↑ {NetoFormat.FormatBytes(snap.TotalUploadBytes)} ↓ {NetoFormat.FormatBytes(snap.TotalDownloadBytes)}";
        _actionAllBtn.IsEnabled = source.Count > 0;
        _emptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyState.Text = source.Count == 0 ? (isClosed ? "暂无已关闭会话" : "暂无活跃会话") : "无匹配会话";
        _connList.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var ids = filtered.Select(c => c.Id).ToHashSet();
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (ids.Contains(_items[i].Item.Id)) continue;
            _rows.Remove(_items[i].Item.Id);
            _items.RemoveAt(i);
        }
        for (var index = 0; index < filtered.Count; index++)
        {
            var item = filtered[index];
            if (!_rows.TryGetValue(item.Id, out var row))
            {
                row = new ConnectionListItem(item, entry =>
                    _ = RunActionAsync(() => _client.CloseConnectionAsync(entry.Item.Id)));
                row.Update(item, isClosed, nowMs);
                _rows.Add(item.Id, row);
                _items.Insert(index, row);
            }
            else
            {
                row.Update(item, isClosed, nowMs);
                if (!ReferenceEquals(_items[index], row)) _items.Move(_items.IndexOf(row), index);
            }
        }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        _feedback.IsOpen = false;
        try
        {
            await action();
            await RefreshConnectionsAsync(forceRender: true);
        }
        catch (Exception ex)
        {
            _feedback.Title = "连接操作失败";
            _feedback.Message = ex.Message;
            _feedback.IsOpen = true;
        }
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
            _snapshot = await _client.GetConnectionsAsync();
            if (_feedback.Title == "读取连接失败") _feedback.IsOpen = false;
            RenderConnections();
        }
        catch (Exception ex)
        {
            _feedback.Title = "读取连接失败";
            _feedback.Message = ex.Message;
            _feedback.IsOpen = true;
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
                await RunActionAsync(() => _client.CloseConnectionAsync(item.Id));
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
            Style = NetoUiFactory.StyleResource("NetoSecondaryTextStyle"),
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
