using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neto.Sdk.Windows.Views;

public sealed class NodesPageView
{
    public Grid Root { get; }
    private readonly NetoClient _client;
    private readonly TextBox _searchBox;
    private readonly Button _sortDefaultBtn;
    private readonly Button _sortLatencyBtn;
    private readonly Button _sortNameBtn;
    private readonly Button _testAllBtn;
    private readonly StackPanel _groupsPanel;
    private readonly StackPanel _nodesListPanel;
    private string _filter = "";
    private string _lastSig = "";

    public NodesPageView(NetoClient client)
    {
        _client = client;
        Root = new Grid { Padding = new Thickness(18, 12, 18, 16), RowSpacing = 10 };
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new Grid { MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Stretch, ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _searchBox = new TextBox { PlaceholderText = "筛选节点" };
        _searchBox.TextChanged += (_, _) =>
        {
            _filter = _searchBox.Text.Trim().ToLowerInvariant();
            _lastSig = "";
            ApplySnapshot(_client.Snapshot);
        };
        _sortDefaultBtn = new Button { Content = "默认" };
        _sortLatencyBtn = new Button { Content = "延迟" };
        _sortNameBtn = new Button { Content = "名称" };
        _testAllBtn = new Button { Content = "测速" };

        _sortDefaultBtn.Click += async (_, _) => await _client.SetNodeSortAsync("default");
        _sortLatencyBtn.Click += async (_, _) => await _client.SetNodeSortAsync("latency");
        _sortNameBtn.Click += async (_, _) => await _client.SetNodeSortAsync("name");
        _testAllBtn.Click += async (_, _) => await _client.TestAllNodesAsync();

        Grid.SetColumn(_sortDefaultBtn, 1);
        Grid.SetColumn(_sortLatencyBtn, 2);
        Grid.SetColumn(_sortNameBtn, 3);
        Grid.SetColumn(_testAllBtn, 4);
        bar.Children.Add(_searchBox);
        bar.Children.Add(_sortDefaultBtn);
        bar.Children.Add(_sortLatencyBtn);
        bar.Children.Add(_sortNameBtn);
        bar.Children.Add(_testAllBtn);

        _groupsPanel = new StackPanel { Spacing = 8 };
        _nodesListPanel = new StackPanel { Spacing = 6 };
        var scrollInner = new StackPanel { MaxWidth = 820, Spacing = 10, Padding = new Thickness(0, 0, 0, 18) };
        scrollInner.Children.Add(_groupsPanel);
        scrollInner.Children.Add(_nodesListPanel);
        var scroll = new ScrollViewer { Content = scrollInner };
        Grid.SetRow(scroll, 1);

        Root.Children.Add(bar);
        Root.Children.Add(scroll);
    }

    public void ApplySnapshot(NetoSnapshot snap)
    {
        NetoUiFactory.ApplyPrimaryButton(_sortDefaultBtn, snap.NodeSortMode == "default");
        NetoUiFactory.ApplyPrimaryButton(_sortLatencyBtn, snap.NodeSortMode == "latency");
        NetoUiFactory.ApplyPrimaryButton(_sortNameBtn, snap.NodeSortMode == "name");
        _testAllBtn.Content = snap.NodesTesting ? "测速中" : "测速";
        _testAllBtn.IsEnabled = !snap.NodesTesting && snap.Nodes.Count > 0;

        var sig = string.Join("|",
            snap.NodeSortMode,
            snap.SelectedNodeIndex,
            _filter,
            string.Join(",", snap.ProxyGroups.Select(g => $"{g.Name}:{g.Selected}")),
            string.Join(",", snap.Nodes.Select(n => $"{n.Index}:{n.LatencyMs}:{n.LastError != null}"))
        );
        if (_lastSig == sig) return;
        _lastSig = sig;

        _groupsPanel.Children.Clear();
        if (snap.ProxyGroups.Count > 0)
        {
            _groupsPanel.Children.Add(NetoUiFactory.CreateSectionHeader("策略组"));
            var gStack = new StackPanel { Spacing = 8 };
            foreach (var grp in snap.ProxyGroups.Where(g => g.Proxies.Count > 0))
            {
                var combo = new ComboBox
                {
                    Header = grp.Name,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                foreach (var p in grp.Proxies) combo.Items.Add(p);
                var target = grp.Selected ?? grp.Proxies[0];
                combo.SelectedItem = combo.Items.Contains(target) ? target : combo.Items[0];
                var groupName = grp.Name;
                combo.SelectionChanged += async (_, _) =>
                {
                    if (combo.SelectedItem is string s && s != grp.Selected)
                    {
                        await _client.SelectGroupProxyAsync(groupName, s);
                    }
                };
                gStack.Children.Add(combo);
            }
            _groupsPanel.Children.Add(NetoUiFactory.CreateCard(gStack));
        }

        _nodesListPanel.Children.Clear();
        var visible = string.IsNullOrEmpty(_filter)
            ? snap.Nodes
            : snap.Nodes.Where(n => n.Name.ToLowerInvariant().Contains(_filter) || n.ProtocolLabel.ToLowerInvariant().Contains(_filter)).ToList();

        if (visible.Count == 0)
        {
            _nodesListPanel.Children.Add(NetoUiFactory.CreateCard(new TextBlock
            {
                Text = snap.Nodes.Count == 0 ? "当前暂无可用节点" : "没有匹配的节点",
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            }));
            return;
        }

        foreach (var node in visible)
        {
            var isSelected = node.Index == snap.SelectedNodeIndex;
            var rowGrid = new Grid { ColumnSpacing = 10 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel { Spacing = 2 };
            infoStack.Children.Add(new TextBlock
            {
                Text = isSelected ? $"● {node.Name}" : node.Name,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            });
            var subParts = new List<string> { node.ProtocolLabel };
            if (node.Tags.Count > 0) subParts.Add(string.Join(" · ", node.Tags));
            infoStack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", subParts),
                FontSize = 12,
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            });

            var selectBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = infoStack,
                IsEnabled = node.Supported,
            };
            NetoUiFactory.ApplyPrimaryButton(selectBtn, isSelected);
            var idx = node.Index;
            selectBtn.Click += async (_, _) => await _client.SelectNodeAsync(idx);

            var latText = new TextBlock
            {
                Text = node.LatencyMs.HasValue ? $"{node.LatencyMs.Value} ms" : (node.LastError != null ? "超时" : "-"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            };
            var testBtn = new Button { Content = "↻", VerticalAlignment = VerticalAlignment.Center };
            testBtn.Click += async (_, _) => await _client.TestNodeAsync(idx);

            Grid.SetColumn(latText, 1);
            Grid.SetColumn(testBtn, 2);
            rowGrid.Children.Add(selectBtn);
            rowGrid.Children.Add(latText);
            rowGrid.Children.Add(testBtn);
            _nodesListPanel.Children.Add(rowGrid);
        }
    }
}
