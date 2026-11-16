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
    private readonly ComboBox _protoCombo;
    private readonly ComboBox _sortCombo;
    private readonly TextBlock _countLabel;
    private readonly Button _locateBtn;
    private readonly Button _testAllBtn;
    private readonly StackPanel _groupsPanel;
    private readonly Grid _nodesGrid;
    private readonly HashSet<int> _testingIndices = [];
    private List<string> _protoOptions = [""];
    private FrameworkElement? _selectedCardElement;
    private string _selectedGroupName = "";
    private bool _syncingProto;
    private bool _syncingSort;
    private string _filter = "";
    private string _protoFilter = "";
    private string _lastSig = "";
    private string _lastProtoListSig = "";

    private sealed record GroupMemberEntry(
        string Name,
        NetoProxyGroup? SubGroup,
        NetoNodeItem? Node,
        string? BuiltinLabel);

    private static readonly (string Key, string Label)[] SortOptions =
    [
        ("default", "默认排序"),
        ("latency", "延迟优先"),
        ("name", "名称排序"),
    ];

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
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
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
        _protoCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _protoCombo.Items.Add("全部协议");
        _protoCombo.SelectedIndex = 0;
        _protoCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingProto || _protoCombo.SelectedIndex < 0) return;
            var idx = Math.Clamp(_protoCombo.SelectedIndex, 0, _protoOptions.Count - 1);
            _protoFilter = _protoOptions[idx];
            _lastSig = "";
            ApplySnapshot(_client.Snapshot);
        };
        _sortCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in SortOptions) _sortCombo.Items.Add(label);
        _sortCombo.SelectedIndex = 0;
        _sortCombo.SelectionChanged += async (_, _) =>
        {
            if (_syncingSort || _sortCombo.SelectedIndex < 0) return;
            var key = SortOptions[Math.Clamp(_sortCombo.SelectedIndex, 0, SortOptions.Length - 1)].Key;
            await _client.SetNodeSortAsync(key);
        };
        _countLabel = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        _locateBtn = new Button { Content = "定位" };
        _testAllBtn = new Button { Content = "测速" };

        _locateBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_filter) || !string.IsNullOrEmpty(_protoFilter))
            {
                _searchBox.Text = "";
                _syncingProto = true;
                _protoCombo.SelectedIndex = 0;
                _syncingProto = false;
                _filter = "";
                _protoFilter = "";
                _lastSig = "";
                ApplySnapshot(_client.Snapshot);
            }
            _selectedCardElement?.StartBringIntoView();
        };
        _testAllBtn.Click += async (_, _) =>
        {
            var snap = _client.Snapshot;
            var activeGroup = ResolveActiveGroup(snap);
            if (activeGroup != null)
            {
                await _client.TestGroupNodesAsync(activeGroup.Name);
            }
            else
            {
                await _client.TestAllNodesAsync();
            }
        };

        Grid.SetColumn(_protoCombo, 1);
        Grid.SetColumn(_sortCombo, 2);
        Grid.SetColumn(_countLabel, 3);
        Grid.SetColumn(_locateBtn, 4);
        Grid.SetColumn(_testAllBtn, 5);
        bar.Children.Add(_searchBox);
        bar.Children.Add(_protoCombo);
        bar.Children.Add(_sortCombo);
        bar.Children.Add(_countLabel);
        bar.Children.Add(_locateBtn);
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
        var hasRealNodes = snap.Nodes.Any(n => n.Supported && !NetoFormat.IsInfoPseudoNode(n.Name));
        var isDirectMode = string.Equals(snap.Settings.RoutingMode, "direct", StringComparison.OrdinalIgnoreCase);
        var activeGroup = ResolveActiveGroup(snap);
        var isGroupMode = activeGroup != null;

        var sortIdx = Array.FindIndex(SortOptions, s => s.Key == snap.NodeSortMode);
        var targetSortIdx = sortIdx >= 0 ? sortIdx : 0;
        if (_sortCombo.SelectedIndex != targetSortIdx)
        {
            _syncingSort = true;
            _sortCombo.SelectedIndex = targetSortIdx;
            _syncingSort = false;
        }
        _protoCombo.Visibility = isGroupMode ? Visibility.Collapsed : Visibility.Visible;
        _testAllBtn.Content = snap.NodesTesting ? "测速中..." : "测速";
        _testAllBtn.IsEnabled = !snap.NodesTesting && snap.Nodes.Count > 0;
        _locateBtn.IsEnabled = snap.Nodes.Count > 0;

        var distinctProtos = snap.Nodes
            .Where(n => !hasRealNodes || !NetoFormat.IsInfoPseudoNode(n.Name))
            .Select(n => n.ProtocolLabel)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var protoSig = string.Join(",", distinctProtos);
        if (_lastProtoListSig != protoSig)
        {
            _lastProtoListSig = protoSig;
            _syncingProto = true;
            _protoOptions = [""];
            _protoCombo.Items.Clear();
            _protoCombo.Items.Add("全部协议");
            foreach (var p in distinctProtos)
            {
                _protoOptions.Add(p);
                _protoCombo.Items.Add(p);
            }
            var existingIdx = _protoOptions.FindIndex(p => string.Equals(p, _protoFilter, StringComparison.OrdinalIgnoreCase));
            if (existingIdx < 0)
            {
                _protoFilter = "";
                existingIdx = 0;
            }
            _protoCombo.SelectedIndex = existingIdx;
            _syncingProto = false;
        }

        var visible = snap.Nodes.Where(n =>
        {
            if (hasRealNodes && NetoFormat.IsInfoPseudoNode(n.Name))
                return false;
            if (!string.IsNullOrEmpty(_protoFilter) && !string.Equals(n.ProtocolLabel, _protoFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(_filter))
            {
                return n.Name.ToLowerInvariant().Contains(_filter) ||
                       NetoFormat.FormatDisplayName(n.Name).ToLowerInvariant().Contains(_filter) ||
                       n.ProtocolLabel.ToLowerInvariant().Contains(_filter) ||
                       n.Tags.Any(t => t.ToLowerInvariant().Contains(_filter));
            }
            return true;
        }).ToList();

        var totalRealCount = snap.Nodes.Count(n => !hasRealNodes || !NetoFormat.IsInfoPseudoNode(n.Name));
        var groupEntries = isGroupMode
            ? BuildGroupEntries(snap, activeGroup!, hasRealNodes)
            : [];
        if (isGroupMode)
        {
            _countLabel.Text = string.IsNullOrEmpty(_filter)
                ? $"{groupEntries.Count} 项"
                : $"{groupEntries.Count} / {activeGroup!.Proxies.Count(p => !hasRealNodes || !NetoFormat.IsInfoPseudoNode(p))}";
        }
        else
        {
            _countLabel.Text = string.IsNullOrEmpty(_filter) && string.IsNullOrEmpty(_protoFilter)
                ? $"{totalRealCount} 节点"
                : $"{visible.Count} / {totalRealCount}";
        }

        var sig = string.Join("|",
            snap.ActiveProfile?.Id ?? "",
            snap.Settings.RoutingMode,
            activeGroup?.Name ?? "",
            snap.NodeSortMode,
            snap.SelectedNodeIndex,
            _filter,
            _protoFilter,
            string.Join(",", _testingIndices.OrderBy(x => x)),
            string.Join(",", snap.ProxyGroups.Select(g => $"{g.Name}:{g.GroupType}:{g.Selected}:{g.Now}")),
            string.Join(",", snap.Nodes.Select(n => $"{n.Index}:{n.Name}:{n.LatencyMs}:{n.LastError != null}"))
        );
        if (_lastSig == sig) return;
        _lastSig = sig;

        _groupsPanel.Children.Clear();
        _selectedCardElement = null;

        if (isDirectMode)
        {
            var bannerGrid = new Grid { ColumnSpacing = 10 };
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bannerGrid.Children.Add(new TextBlock
            {
                Text = "当前为直连模式，节点暂不接管流量",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            });
            var switchRuleBtn = new Button
            {
                Content = "切到规则",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
            };
            switchRuleBtn.Click += async (_, _) => await _client.SetRoutingModeAsync("rule");
            Grid.SetColumn(switchRuleBtn, 1);
            bannerGrid.Children.Add(switchRuleBtn);
            _groupsPanel.Children.Add(NetoUiFactory.CreateCard(bannerGrid, new Thickness(12, 8, 12, 8)));
        }

        if (isGroupMode)
        {
            var nonEmptyGroups = snap.ProxyGroups.Where(g => g.Proxies.Count > 0).ToList();
            var tabsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var grp in nonEmptyGroups)
            {
                var isTabActive = grp.Name == activeGroup!.Name;
                var targetName = NetoFormat.FormatDisplayName(grp.Now ?? grp.Selected ?? "-");
                var tabContent = new StackPanel { Spacing = 2, MaxWidth = 200 };
                var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                titleRow.Children.Add(new TextBlock
                {
                    Text = grp.Name,
                    FontSize = 12,
                    FontWeight = isTabActive ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                titleRow.Children.Add(NetoUiFactory.CreateTagBadge(grp.ShortTypeLabel));
                tabContent.Children.Add(titleRow);
                tabContent.Children.Add(new TextBlock
                {
                    Text = targetName,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                });

                var tabBtn = new Button
                {
                    Content = tabContent,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 6, 10, 6),
                    CornerRadius = new CornerRadius(8),
                };
                if (isTabActive)
                {
                    tabBtn.BorderBrush = NetoUiFactory.BrushResource("AccentFillColorDefaultBrush", Color.FromArgb(255, 0, 120, 212));
                    tabBtn.BorderThickness = new Thickness(1.5);
                }
                var targetGroupName = grp.Name;
                tabBtn.Click += (_, _) =>
                {
                    _selectedGroupName = targetGroupName;
                    _lastSig = "";
                    ApplySnapshot(_client.Snapshot);
                };
                tabsRow.Children.Add(tabBtn);
            }
            var tabsScroll = new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 0, 4),
                Content = tabsRow,
            };
            _groupsPanel.Children.Add(tabsScroll);

            var currentHit = NetoFormat.FormatDisplayName(activeGroup!.Now ?? activeGroup.Selected ?? "-");
            var leafHit = snap.ResolveGroupLeafTarget(activeGroup.Name);
            var formattedLeaf = string.IsNullOrEmpty(leafHit) ? "" : NetoFormat.FormatDisplayName(leafHit);
            var summaryText = !string.IsNullOrEmpty(formattedLeaf) && formattedLeaf != currentHit
                ? $"{activeGroup.GroupTypeLabel} · {currentHit} → {formattedLeaf}"
                : $"{activeGroup.GroupTypeLabel} · {currentHit}";
            _groupsPanel.Children.Add(new TextBlock
            {
                Text = summaryText,
                FontSize = 12,
                Margin = new Thickness(2, 0, 2, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            });
        }

        _nodesGrid.Children.Clear();
        _nodesGrid.RowDefinitions.Clear();

        if (isGroupMode)
        {
            RenderGroupMembersGrid(snap, activeGroup!, groupEntries);
            return;
        }

        if (visible.Count == 0)
        {
            _nodesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var emptyCard = NetoUiFactory.CreateCard(new TextBlock
            {
                Text = totalRealCount == 0 ? "暂无可用节点" : "没有匹配的节点",
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

        var effectiveIdx = snap.EffectiveActiveNode?.Index ?? snap.SelectedNodeIndex;
        for (var i = 0; i < visible.Count; i++)
        {
            var node = visible[i];
            var isSelected = node.Index == effectiveIdx;
            var card = BuildNodeCard(
                node,
                isSelected,
                canSelect: node.Supported,
                onSelect: () => _client.SelectNodeAsync(node.Index));
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            _nodesGrid.Children.Add(card);
        }
    }

    private NetoProxyGroup? ResolveActiveGroup(NetoSnapshot snap)
    {
        if (!string.Equals(snap.Settings.RoutingMode, "rule", StringComparison.OrdinalIgnoreCase))
            return null;
        var nonEmpty = snap.ProxyGroups.Where(g => g.Proxies.Count > 0).ToList();
        if (nonEmpty.Count == 0) return null;
        if (!string.IsNullOrEmpty(_selectedGroupName))
        {
            var matched = nonEmpty.FirstOrDefault(g => g.Name == _selectedGroupName);
            if (matched != null) return matched;
        }
        return snap.PrimarySelectGroup is { Proxies.Count: > 0 } primary
            ? primary
            : nonEmpty[0];
    }

    private List<GroupMemberEntry> BuildGroupEntries(NetoSnapshot snap, NetoProxyGroup activeGroup, bool hasRealNodes)
    {
        var nodesByName = snap.Nodes
            .GroupBy(n => n.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var groupsByName = snap.ProxyGroups
            .GroupBy(g => g.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var raw = new List<GroupMemberEntry>();
        foreach (var memberName in activeGroup.Proxies)
        {
            groupsByName.TryGetValue(memberName, out var subGrp);
            var node = subGrp == null && nodesByName.TryGetValue(memberName, out var foundNode) ? foundNode : null;
            var builtin = memberName.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
                ? "直连出站"
                : (memberName.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
                   memberName.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase))
                    ? "拦截连接"
                    : null;
            if (hasRealNodes && subGrp == null && builtin == null && NetoFormat.IsInfoPseudoNode(memberName))
                continue;
            if (!string.IsNullOrEmpty(_filter))
            {
                var disp = NetoFormat.FormatDisplayName(memberName).ToLowerInvariant();
                var match = memberName.ToLowerInvariant().Contains(_filter) ||
                            disp.Contains(_filter) ||
                            (node != null && (node.ProtocolLabel.ToLowerInvariant().Contains(_filter) ||
                                              node.Tags.Any(t => t.ToLowerInvariant().Contains(_filter)))) ||
                            (subGrp != null && subGrp.GroupTypeLabel.ToLowerInvariant().Contains(_filter));
                if (!match) continue;
            }
            raw.Add(new GroupMemberEntry(memberName, subGrp, node, builtin));
        }

        var pinned = raw.Where(e => e.Node == null).ToList();
        var nodeEntries = raw.Where(e => e.Node != null).ToList();
        var sortedNodes = snap.NodeSortMode switch
        {
            "latency" => nodeEntries
                .OrderBy(e => NetoFormat.IsInfoPseudoNode(e.Name))
                .ThenBy(e => e.Node!.LatencyMs ?? ulong.MaxValue)
                .ThenBy(e => e.Node!.Index)
                .ToList(),
            "name" => nodeEntries
                .OrderBy(e => NetoFormat.IsInfoPseudoNode(e.Name))
                .ThenBy(e => NetoFormat.FormatDisplayName(e.Name), StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => nodeEntries,
        };
        pinned.AddRange(sortedNodes);
        return pinned;
    }

    private void RenderGroupMembersGrid(NetoSnapshot snap, NetoProxyGroup activeGroup, List<GroupMemberEntry> entries)
    {
        if (entries.Count == 0)
        {
            _nodesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var emptyCard = NetoUiFactory.CreateCard(new TextBlock
            {
                Text = "当前策略组下没有匹配项",
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            });
            Grid.SetColumnSpan(emptyCard, 2);
            _nodesGrid.Children.Add(emptyCard);
            return;
        }

        var totalRows = (entries.Count + 1) / 2;
        for (var r = 0; r < totalRows; r++)
        {
            _nodesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var currentTarget = activeGroup.Now ?? activeGroup.Selected ?? activeGroup.Proxies.FirstOrDefault() ?? "";
        var groupName = activeGroup.Name;
        var canManualPick = activeGroup.IsManualSelect;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var isSelected = entry.Name == currentTarget;
            Border card;
            if (entry.Node != null)
            {
                var memberName = entry.Name;
                var nodeIdx = entry.Node.Index;
                card = BuildNodeCard(
                    entry.Node,
                    isSelected,
                    canSelect: entry.Node.Supported && canManualPick,
                    onSelect: () => canManualPick
                        ? _client.SelectGroupProxyAsync(groupName, memberName)
                        : _client.SelectNodeAsync(nodeIdx));
            }
            else
            {
                var memberName = entry.Name;
                var subGrp = entry.SubGroup;
                var rowGrid = new Grid { ColumnSpacing = 6 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var infoStack = new StackPanel { Spacing = 4 };
                var dispTitle = NetoFormat.FormatDisplayName(memberName);
                infoStack.Children.Add(new TextBlock
                {
                    Text = isSelected ? $"● {dispTitle}" : dispTitle,
                    FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var badgesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                if (subGrp != null)
                {
                    badgesRow.Children.Add(NetoUiFactory.CreateTagBadge(subGrp.GroupTypeLabel));
                    var leaf = snap.ResolveGroupLeafTarget(subGrp.Name);
                    if (!string.IsNullOrEmpty(leaf))
                    {
                        badgesRow.Children.Add(NetoUiFactory.CreateTagBadge(NetoFormat.FormatDisplayName(leaf)));
                    }
                }
                else if (!string.IsNullOrEmpty(entry.BuiltinLabel))
                {
                    badgesRow.Children.Add(NetoUiFactory.CreateTagBadge(entry.BuiltinLabel));
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
                    IsEnabled = canManualPick || subGrp != null,
                };
                selectBtn.Click += async (_, _) =>
                {
                    if (canManualPick)
                    {
                        await _client.SelectGroupProxyAsync(groupName, memberName);
                    }
                    else if (subGrp != null)
                    {
                        _selectedGroupName = subGrp.Name;
                        _lastSig = "";
                        ApplySnapshot(_client.Snapshot);
                    }
                };
                rowGrid.Children.Add(selectBtn);

                if (subGrp != null)
                {
                    var jumpBtn = new Button
                    {
                        Content = new FontIcon { Glyph = "\uE76C", FontSize = 12 },
                        Padding = new Thickness(8, 6, 8, 6),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    ToolTipService.SetToolTip(jumpBtn, "查看子策略组");
                    var subName = subGrp.Name;
                    jumpBtn.Click += (_, _) =>
                    {
                        _selectedGroupName = subName;
                        _lastSig = "";
                        ApplySnapshot(_client.Snapshot);
                    };
                    Grid.SetColumn(jumpBtn, 1);
                    rowGrid.Children.Add(jumpBtn);
                }

                card = NetoUiFactory.CreateCard(rowGrid, new Thickness(8, 6, 10, 6));
                if (isSelected)
                {
                    card.BorderBrush = NetoUiFactory.BrushResource("AccentFillColorDefaultBrush", Color.FromArgb(255, 0, 120, 212));
                    card.BorderThickness = new Thickness(1.5);
                    _selectedCardElement = card;
                }
            }
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            _nodesGrid.Children.Add(card);
        }
    }

    private Border BuildNodeCard(NetoNodeItem node, bool isSelected, bool canSelect, Func<Task> onSelect)
    {
        var isTesting = _testingIndices.Contains(node.Index);
        var rowGrid = new Grid { ColumnSpacing = 6 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AutoSizeOrZero(true), GridUnitType.Auto) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dispName = NetoFormat.FormatDisplayName(node.Name);
        var infoStack = new StackPanel { Spacing = 4 };
        infoStack.Children.Add(new TextBlock
        {
            Text = isSelected ? $"● {dispName}" : dispName,
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
            IsEnabled = canSelect,
        };
        selectBtn.Click += async (_, _) => await onSelect();

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
        var idx = node.Index;
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
            _selectedCardElement = card;
        }
        return card;
    }

    private static double AutoSizeOrZero(bool _) => 0;
}
