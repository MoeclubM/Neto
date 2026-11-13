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
    private readonly Button _tabRulesBtn;
    private readonly Grid _activePane;
    private readonly Grid _rulesPane;

    private readonly TextBox _connSearchBox;
    private readonly ComboBox _connNetCombo;
    private readonly TextBlock _connSummaryLabel;
    private readonly Button _pauseBtn;
    private readonly Button _closeAllBtn;
    private readonly StackPanel _connListPanel;

    private readonly TextBox _routeTargetBox;
    private readonly ComboBox _routeNetCombo;
    private readonly TextBlock _routeResultText;
    private readonly TextBox _ruleSearchBox;
    private readonly TextBlock _ruleCountLabel;
    private readonly StackPanel _rulesListPanel;

    private string _subTab = "active";
    private string _connFilter = "";
    private string _connNetFilter = "all";
    private bool _paused;
    private bool _fetchingConns;
    private bool _fetchingRules;
    private bool _rulesLoadedOnce;
    private string _lastConnSig = "";
    private string _lastRuleAccountSig = "";

    public ConnectionsPageView(NetoClient client, bool detachHeaderActions = false)
    {
        _client = client;
        Root = new Grid { RowSpacing = detachHeaderActions ? 0 : 8 };
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        HeaderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
        };
        _tabActiveBtn = new Button { Content = "活跃会话" };
        _tabRulesBtn = new Button { Content = "分流规则" };
        _tabActiveBtn.Click += async (_, _) =>
        {
            SwitchSubTab("active");
            if (!_paused) await RefreshConnectionsAsync();
        };
        _tabRulesBtn.Click += async (_, _) =>
        {
            SwitchSubTab("rules");
            if (!_rulesLoadedOnce) await RefreshRulesAsync();
        };
        HeaderActions.Children.Add(_tabActiveBtn);
        HeaderActions.Children.Add(_tabRulesBtn);

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

        _activePane = new Grid { RowSpacing = 8 };
        _activePane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _activePane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var connToolbar = new Grid
        {
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(18, 6, 18, 0),
            ColumnSpacing = 8,
        };
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        connToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _connSearchBox = new TextBox { PlaceholderText = "筛选主机、规则或出站" };
        _connSearchBox.TextChanged += (_, _) =>
        {
            _connFilter = _connSearchBox.Text.Trim().ToLowerInvariant();
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
        _connSummaryLabel = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        _pauseBtn = new Button { Content = "暂停" };
        _pauseBtn.Click += (_, _) =>
        {
            _paused = !_paused;
            _pauseBtn.Content = _paused ? "继续" : "暂停";
            NetoUiFactory.ApplyPrimaryButton(_pauseBtn, _paused);
        };
        _closeAllBtn = new Button { Content = "断开全部" };
        _closeAllBtn.Click += async (_, _) =>
        {
            await _client.CloseAllConnectionsAsync();
            _lastConnSig = "";
            await RefreshConnectionsAsync(forceRender: true);
        };

        Grid.SetColumn(_connNetCombo, 1);
        Grid.SetColumn(_connSummaryLabel, 2);
        Grid.SetColumn(_pauseBtn, 3);
        Grid.SetColumn(_closeAllBtn, 4);
        connToolbar.Children.Add(_connSearchBox);
        connToolbar.Children.Add(_connNetCombo);
        connToolbar.Children.Add(_connSummaryLabel);
        connToolbar.Children.Add(_pauseBtn);
        connToolbar.Children.Add(_closeAllBtn);

        _connListPanel = new StackPanel
        {
            MaxWidth = 860,
            Spacing = 6,
            Padding = new Thickness(18, 0, 18, 24),
        };
        var connScroll = NetoUiFactory.CreateCenteredScrollViewer(_connListPanel);
        Grid.SetRow(connScroll, 1);
        _activePane.Children.Add(connToolbar);
        _activePane.Children.Add(connScroll);

        _rulesPane = new Grid { RowSpacing = 8, Visibility = Visibility.Collapsed };
        _rulesPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rulesPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var rulesTopStack = new StackPanel
        {
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 8,
            Padding = new Thickness(18, 6, 18, 0),
        };

        var testerStack = new StackPanel { Spacing = 8 };
        testerStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("规则匹配测试"));
        var testerRow = new Grid { ColumnSpacing = 8 };
        testerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        testerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        testerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _routeTargetBox = new TextBox { PlaceholderText = "域名或 IP（如 www.google.com 或 223.5.5.5:53）" };
        _routeNetCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _routeNetCombo.Items.Add("TCP");
        _routeNetCombo.Items.Add("UDP");
        _routeNetCombo.SelectedIndex = 0;
        var runTestBtn = new Button { Content = "匹配测试" };
        NetoUiFactory.ApplyPrimaryButton(runTestBtn, true);
        runTestBtn.Click += async (_, _) => await RunRouteTestAsync(runTestBtn);
        Grid.SetColumn(_routeNetCombo, 1);
        Grid.SetColumn(runTestBtn, 2);
        testerRow.Children.Add(_routeTargetBox);
        testerRow.Children.Add(_routeNetCombo);
        testerRow.Children.Add(runTestBtn);
        _routeResultText = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        testerStack.Children.Add(testerRow);
        testerStack.Children.Add(_routeResultText);
        rulesTopStack.Children.Add(NetoUiFactory.CreateCard(testerStack, new Thickness(14, 10, 14, 10)));

        var ruleFilterRow = new Grid { ColumnSpacing = 8 };
        ruleFilterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ruleFilterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ruleFilterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _ruleSearchBox = new TextBox { PlaceholderText = "筛选规则类型、匹配内容或目标策略" };
        _ruleSearchBox.TextChanged += async (_, _) => await RefreshRulesAsync();
        _ruleCountLabel = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        var refreshRulesBtn = new Button { Content = "刷新" };
        refreshRulesBtn.Click += async (_, _) => await RefreshRulesAsync();
        Grid.SetColumn(_ruleCountLabel, 1);
        Grid.SetColumn(refreshRulesBtn, 2);
        ruleFilterRow.Children.Add(_ruleSearchBox);
        ruleFilterRow.Children.Add(_ruleCountLabel);
        ruleFilterRow.Children.Add(refreshRulesBtn);
        rulesTopStack.Children.Add(ruleFilterRow);

        _rulesListPanel = new StackPanel
        {
            MaxWidth = 860,
            Spacing = 4,
            Padding = new Thickness(18, 0, 18, 24),
        };
        var rulesScroll = NetoUiFactory.CreateCenteredScrollViewer(_rulesListPanel);
        Grid.SetRow(rulesScroll, 1);
        _rulesPane.Children.Add(rulesTopStack);
        _rulesPane.Children.Add(rulesScroll);

        Grid.SetRow(_activePane, 1);
        Grid.SetRow(_rulesPane, 1);
        Root.Children.Add(_activePane);
        Root.Children.Add(_rulesPane);

        SwitchSubTab("active");
    }

    public void SwitchSubTab(string subTab)
    {
        _subTab = subTab == "rules" ? "rules" : "active";
        NetoUiFactory.ApplyPrimaryButton(_tabActiveBtn, _subTab == "active");
        NetoUiFactory.ApplyPrimaryButton(_tabRulesBtn, _subTab == "rules");
        _activePane.Visibility = _subTab == "active" ? Visibility.Visible : Visibility.Collapsed;
        _rulesPane.Visibility = _subTab == "rules" ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ApplySnapshot(NetoSnapshot snap)
    {
        var ruleAccountSig = $"{snap.ActiveProfileId}:{snap.RuleCount}:{snap.Settings.CustomRouteYaml.Length}";
        if (_lastRuleAccountSig != ruleAccountSig)
        {
            _lastRuleAccountSig = ruleAccountSig;
            _rulesLoadedOnce = false;
            if (Root.Visibility == Visibility.Visible && _subTab == "rules")
            {
                _ = RefreshRulesAsync();
            }
        }

        if (Root.Visibility != Visibility.Visible) return;
        if (_subTab == "active" && !_paused)
        {
            _ = RefreshConnectionsAsync();
        }
        else if (_subTab == "rules" && !_rulesLoadedOnce)
        {
            _ = RefreshRulesAsync();
        }
    }

    public async Task RefreshConnectionsAsync(bool forceRender = false)
    {
        if (_fetchingConns) return;
        _fetchingConns = true;
        try
        {
            var snap = await _client.GetConnectionsAsync();
            var filtered = snap.Active.Where(c =>
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
            }).OrderByDescending(c => c.StartedAtMs).ToList();

            _connSummaryLabel.Text = $"活跃 {snap.Active.Count} · ↑ {NetoFormat.FormatBytes(snap.TotalUploadBytes)} ↓ {NetoFormat.FormatBytes(snap.TotalDownloadBytes)}";
            _closeAllBtn.IsEnabled = snap.Active.Count > 0;

            var nowMs = NetoFormat.NowUnixMs();
            var sig = string.Join("|",
                _connNetFilter,
                _connFilter,
                string.Join(";", filtered.Select(c => $"{c.Id}:{c.UploadBytes / 1024}:{c.DownloadBytes / 1024}:{(nowMs - Math.Min(nowMs, c.StartedAtMs)) / 1000}")));
            if (!forceRender && _lastConnSig == sig) return;
            _lastConnSig = sig;

            _connListPanel.Children.Clear();
            if (filtered.Count == 0)
            {
                _connListPanel.Children.Add(NetoUiFactory.CreateCard(new TextBlock
                {
                    Text = snap.Active.Count == 0 ? "暂无活跃会话" : "无匹配会话",
                    Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                }));
                return;
            }

            foreach (var item in filtered.Take(200))
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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

                var ageSec = nowMs > item.StartedAtMs ? (nowMs - item.StartedAtMs) / 1000UL : 0UL;
                var stats = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                stats.Children.Add(new TextBlock
                {
                    Text = $"↑ {NetoFormat.FormatBytes(item.UploadBytes)} · ↓ {NetoFormat.FormatBytes(item.DownloadBytes)}",
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
                stats.Children.Add(new TextBlock
                {
                    Text = $"{ageSec}s",
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                });

                var cid = item.Id;
                var closeBtn = new Button
                {
                    Content = "断开",
                    Padding = new Thickness(10, 4, 10, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                closeBtn.Click += async (_, _) =>
                {
                    await _client.CloseConnectionAsync(cid);
                    _lastConnSig = "";
                    await RefreshConnectionsAsync(forceRender: true);
                };

                Grid.SetColumn(stats, 1);
                Grid.SetColumn(closeBtn, 2);
                row.Children.Add(left);
                row.Children.Add(stats);
                row.Children.Add(closeBtn);
                _connListPanel.Children.Add(NetoUiFactory.CreateCard(row, new Thickness(12, 8, 12, 8)));
            }
        }
        finally
        {
            _fetchingConns = false;
        }
    }

    public async Task RefreshRulesAsync()
    {
        if (_fetchingRules) return;
        _fetchingRules = true;
        try
        {
            var filter = _ruleSearchBox.Text.Trim();
            var res = await _client.GetRulesAsync(filter, 300);
            _rulesLoadedOnce = true;
            _ruleCountLabel.Text = string.IsNullOrEmpty(filter)
                ? $"共 {res.Total} 条"
                : $"{res.Items.Count} / {res.Total}";

            _rulesListPanel.Children.Clear();
            if (res.Items.Count == 0)
            {
                _rulesListPanel.Children.Add(NetoUiFactory.CreateCard(new TextBlock
                {
                    Text = res.Total == 0 ? "暂无分流规则" : "无匹配规则",
                    Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                }));
                return;
            }

            foreach (var r in res.Items)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var typeBadge = NetoUiFactory.CreateTagBadge(r.RuleType);
                var payloadText = new TextBlock
                {
                    Text = string.IsNullOrEmpty(r.Payload) ? "全匹配" : r.Payload,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var targetTint = r.Target.ToUpperInvariant() switch
                {
                    "DIRECT" => Color.FromArgb(255, 42, 168, 118),
                    "REJECT" => Color.FromArgb(255, 216, 82, 76),
                    _ => Color.FromArgb(255, 56, 132, 235),
                };
                var targetBadge = NetoUiFactory.CreateTagBadge(r.Target, targetTint);

                Grid.SetColumn(payloadText, 1);
                Grid.SetColumn(targetBadge, 2);
                row.Children.Add(typeBadge);
                row.Children.Add(payloadText);
                row.Children.Add(targetBadge);
                _rulesListPanel.Children.Add(NetoUiFactory.CreateCard(row, new Thickness(10, 6, 10, 6)));
            }
        }
        finally
        {
            _fetchingRules = false;
        }
    }

    private async Task RunRouteTestAsync(Button triggerBtn)
    {
        var target = _routeTargetBox.Text.Trim();
        if (string.IsNullOrEmpty(target)) return;
        var net = _routeNetCombo.SelectedIndex == 1 ? "udp" : "tcp";
        triggerBtn.IsEnabled = false;
        try
        {
            var res = await _client.TestRouteAsync(target, net);
            var outbound = string.IsNullOrEmpty(res.Outbound) ? res.Decision : $"{res.Decision} ({res.Outbound})";
            _routeResultText.Text = $"{res.Target} [{res.Network.ToUpperInvariant()}] -> {outbound}  |  {res.MatchedRule}";
            _routeResultText.Foreground = NetoUiFactory.BrushResource("TextFillColorPrimaryBrush", Colors.White);
            _routeResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _routeResultText.Text = ex.Message;
            _routeResultText.Foreground = new SolidColorBrush(Color.FromArgb(255, 216, 82, 76));
            _routeResultText.Visibility = Visibility.Visible;
        }
        finally
        {
            triggerBtn.IsEnabled = true;
        }
    }
}
