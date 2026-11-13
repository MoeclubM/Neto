using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public sealed class NodesPageView
{
    public Grid Root { get; }
    private readonly NetoClient _client;
    private readonly TextBox _searchBox;
    private readonly TextBlock _countLabel;
    private readonly Button _sortDefaultBtn;
    private readonly Button _sortLatencyBtn;
    private readonly Button _sortNameBtn;
    private readonly Button _testAllBtn;
    private readonly StackPanel _groupsPanel;
    private readonly Grid _nodesGrid;
    private readonly HashSet<int> _testingIndices = [];
    private string _filter = "";
    private string _lastSig = "";

    public NodesPageView(NetoClient client)
    {
        _client = client;
        Root = new Grid { RowSpacing = 10 };
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new Grid
        {
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(18, 6, 18, 0),
            ColumnSpacing = 8,
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _searchBox = new TextBox { PlaceholderText = "搜索节点名称、标签或协议" };
        _searchBox.TextChanged += (_, _) =>
        {
            _filter = _searchBox.Text.Trim().ToLowerInvariant();
            _lastSig = "";
            ApplySnapshot(_client.Snapshot);
        };
        _countLabel = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        _sortDefaultBtn = new Button { Content = "默认" };
        _sortLatencyBtn = new Button { Content = "延迟" };
        _sortNameBtn = new Button { Content = "名称" };
        _testAllBtn = new Button { Content = "批量测速" };

        _sortDefaultBtn.Click += async (_, _) => await _client.SetNodeSortAsync("default");
        _sortLatencyBtn.Click += async (_, _) => await _client.SetNodeSortAsync("latency");
        _sortNameBtn.Click += async (_, _) => await _client.SetNodeSortAsync("name");
        _testAllBtn.Click += async (_, _) => await _client.TestAllNodesAsync();

        Grid.SetColumn(_countLabel, 1);
        Grid.SetColumn(_sortDefaultBtn, 2);
        Grid.SetColumn(_sortLatencyBtn, 3);
        Grid.SetColumn(_sortNameBtn, 4);
        Grid.SetColumn(_testAllBtn, 5);
        bar.Children.Add(_searchBox);
        bar.Children.Add(_countLabel);
        bar.Children.Add(_sortDefaultBtn);
        bar.Children.Add(_sortLatencyBtn);
        bar.Children.Add(_sortNameBtn);
        bar.Children.Add(_testAllBtn);

        _groupsPanel = new StackPanel { Spacing = 8 };
        _nodesGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        _nodesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _nodesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scrollInner = new StackPanel
        {
            MaxWidth = 860,
            Spacing = 10,
            Padding = new Thickness(18, 0, 18, 24),
        };
        scrollInner.Children.Add(_groupsPanel);
        scrollInner.Children.Add(_nodesGrid);
        var scroll = NetoUiFactory.CreateCenteredScrollViewer(scrollInner);
        Grid.SetRow(scroll, 1);

        Root.Children.Add(bar);
        Root.Children.Add(scroll);
    }

    public void ApplySnapshot(NetoSnapshot snap)
    {
        NetoUiFactory.ApplyPrimaryButton(_sortDefaultBtn, snap.NodeSortMode == "default");
        NetoUiFactory.ApplyPrimaryButton(_sortLatencyBtn, snap.NodeSortMode == "latency");
        NetoUiFactory.ApplyPrimaryButton(_sortNameBtn, snap.NodeSortMode == "name");
        _testAllBtn.Content = snap.NodesTesting ? "测速中..." : "批量测速";
        _testAllBtn.IsEnabled = !snap.NodesTesting && snap.Nodes.Count > 0;

        var visible = string.IsNullOrEmpty(_filter)
            ? snap.Nodes
            : snap.Nodes.Where(n =>
                n.Name.ToLowerInvariant().Contains(_filter) ||
                n.ProtocolLabel.ToLowerInvariant().Contains(_filter) ||
                n.Tags.Any(t => t.ToLowerInvariant().Contains(_filter))).ToList();
        _countLabel.Text = string.IsNullOrEmpty(_filter)
            ? $"{snap.Nodes.Count} 节点"
            : $"{visible.Count} / {snap.Nodes.Count}";

        var sig = string.Join("|",
            snap.NodeSortMode,
            snap.SelectedNodeIndex,
            _filter,
            string.Join(",", _testingIndices.OrderBy(x => x)),
            string.Join(",", snap.ProxyGroups.Select(g => $"{g.Name}:{g.Selected}")),
            string.Join(",", snap.Nodes.Select(n => $"{n.Index}:{n.LatencyMs}:{n.LastError != null}"))
        );
        if (_lastSig == sig) return;
        _lastSig = sig;

        _groupsPanel.Children.Clear();
        var nonEmptyGroups = snap.ProxyGroups.Where(g => g.Proxies.Count > 0).ToList();
        if (nonEmptyGroups.Count > 0)
        {
            var gCardStack = new StackPanel { Spacing = 8 };
            gCardStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("策略组"));
            var gGrid = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
            gGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var gRows = (nonEmptyGroups.Count + 1) / 2;
            for (var r = 0; r < gRows; r++) gGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < nonEmptyGroups.Count; i++)
            {
                var grp = nonEmptyGroups[i];
                var combo = new ComboBox
                {
                    Header = grp.Name,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                foreach (var p in grp.Proxies) combo.Items.Add(p);
                var target = grp.Selected ?? grp.Proxies[0];
                var currentSelected = target;
                combo.SelectedItem = combo.Items.Contains(target) ? target : combo.Items[0];
                var groupName = grp.Name;
                combo.SelectionChanged += async (_, _) =>
                {
                    if (combo.SelectedItem is string s && s != currentSelected)
                    {
                        currentSelected = s;
                        await _client.SelectGroupProxyAsync(groupName, s);
                    }
                };
                Grid.SetRow(combo, i / 2);
                Grid.SetColumn(combo, i % 2);
                if (i == nonEmptyGroups.Count - 1 && nonEmptyGroups.Count % 2 == 1) Grid.SetColumnSpan(combo, 2);
                gGrid.Children.Add(combo);
            }
            gCardStack.Children.Add(gGrid);
            _groupsPanel.Children.Add(NetoUiFactory.CreateCard(gCardStack));
        }

        _nodesGrid.Children.Clear();
        _nodesGrid.RowDefinitions.Clear();

        if (visible.Count == 0)
        {
            _nodesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var emptyCard = NetoUiFactory.CreateCard(new TextBlock
            {
                Text = snap.Nodes.Count == 0 ? "暂无可用节点" : "没有匹配的节点",
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            });
            Grid.SetColumnSpan(emptyCard, 2);
            _nodesGrid.Children.Add(emptyCard);
            return;
        }

        var totalRows = (visible.Count + 1) / 2;
        for (var r = 0; r < totalRows; r++)
        {
            _nodesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var node = visible[i];
            var isSelected = node.Index == snap.SelectedNodeIndex;
            var isTesting = _testingIndices.Contains(node.Index);
            var rowGrid = new Grid { ColumnSpacing = 6 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel { Spacing = 4 };
            infoStack.Children.Add(new TextBlock
            {
                Text = isSelected ? $"● {node.Name}" : node.Name,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var badgesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            badgesRow.Children.Add(NetoUiFactory.CreateTagBadge(node.ProtocolLabel));
            if (node.Udp) badgesRow.Children.Add(NetoUiFactory.CreateTagBadge("UDP"));
            foreach (var tag in node.Tags.Take(2))
            {
                badgesRow.Children.Add(NetoUiFactory.CreateTagBadge(tag));
            }
            infoStack.Children.Add(badgesRow);

            var selectBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                Content = infoStack,
                IsEnabled = node.Supported,
            };
            var idx = node.Index;
            selectBtn.Click += async (_, _) => await _client.SelectNodeAsync(idx);

            var latColor = NetoUiFactory.LatencyColor(node.LatencyMs, node.LastError != null);
            var latText = new TextBlock
            {
                Text = isTesting ? "..." : (node.LatencyMs.HasValue ? $"{node.LatencyMs.Value} ms" : (node.LastError != null ? "超时" : "-")),
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                MinWidth = 48,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(latColor),
            };
            var testBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE72C", FontSize = 12 },
                Padding = new Thickness(8, 6, 8, 6),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !isTesting && node.Supported,
            };
            ToolTipService.SetToolTip(testBtn, "测试延迟");
            testBtn.Click += async (_, _) =>
            {
                _testingIndices.Add(idx);
                _lastSig = "";
                ApplySnapshot(_client.Snapshot);
                try { await _client.TestNodeAsync(idx); }
                finally
                {
                    _testingIndices.Remove(idx);
                    _lastSig = "";
                    ApplySnapshot(_client.Snapshot);
                }
            };

            Grid.SetColumn(latText, 1);
            Grid.SetColumn(testBtn, 2);
            rowGrid.Children.Add(selectBtn);
            rowGrid.Children.Add(latText);
            rowGrid.Children.Add(testBtn);
            var card = NetoUiFactory.CreateCard(rowGrid, new Thickness(8, 6, 10, 6));
            if (isSelected)
            {
                card.BorderBrush = NetoUiFactory.BrushResource("AccentFillColorDefaultBrush", Color.FromArgb(255, 0, 120, 212));
                card.BorderThickness = new Thickness(1.5);
            }
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            _nodesGrid.Children.Add(card);
        }
    }
}
