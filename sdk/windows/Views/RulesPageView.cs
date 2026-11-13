using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public sealed class RulesPageView
{
    public Grid Root { get; }
    public StackPanel HeaderActions { get; }
    private readonly NetoClient _client;
    private readonly IntPtr _ownerHwnd;
    private readonly Button _tabRulesBtn;
    private readonly Button _tabOverrideBtn;
    private readonly Grid _rulesPane;
    private readonly ScrollViewer _overrideScroll;
    private readonly Border _testerCard;
    private readonly Button _toggleTesterBtn;
    private readonly TextBox _routeTargetBox;
    private readonly ComboBox _routeNetCombo;
    private readonly TextBlock _routeResultText;
    private readonly TextBox _ruleSearchBox;
    private readonly TextBlock _ruleCountLabel;
    private readonly StackPanel _rulesListPanel;

    private readonly ComboBox _quickTypeCombo;
    private readonly TextBox _quickMatchBox;
    private readonly ComboBox _quickTargetCombo;
    private readonly TextBlock _quickFeedbackText;
    private readonly TextBox _overrideBox;
    private readonly TextBlock _overrideFeedbackText;

    private string _subTab = "rules";
    private bool _fetchingRules;
    private bool _rulesLoadedOnce;
    private bool _updating;
    private bool _overrideInitialized;
    private bool _overrideDirty;
    private string _lastRuleAccountSig = "";
    private string _lastTargetsSig = "";

    private static readonly string[] RuleTypes =
    [
        "DOMAIN-SUFFIX",
        "DOMAIN",
        "DOMAIN-KEYWORD",
        "IP-CIDR",
        "DST-PORT",
    ];

    public RulesPageView(NetoClient client, bool detachHeaderActions = false, IntPtr ownerHwnd = default)
    {
        _client = client;
        _ownerHwnd = ownerHwnd;

        Root = new Grid { RowSpacing = detachHeaderActions ? 0 : 8 };
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        HeaderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
        };
        _tabRulesBtn = new Button { Content = "分流规则" };
        _tabOverrideBtn = new Button { Content = "规则覆写" };
        _tabRulesBtn.Click += async (_, _) =>
        {
            SwitchSubTab("rules");
            if (!_rulesLoadedOnce) await RefreshRulesAsync();
        };
        _tabOverrideBtn.Click += (_, _) => SwitchSubTab("override");
        HeaderActions.Children.Add(_tabRulesBtn);
        HeaderActions.Children.Add(_tabOverrideBtn);

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

        _rulesPane = new Grid { RowSpacing = 8 };
        _rulesPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rulesPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var rulesTopStack = new StackPanel
        {
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 8,
            Padding = new Thickness(18, 6, 18, 0),
        };

        var ruleFilterRow = new Grid { ColumnSpacing = 8 };
        ruleFilterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ruleFilterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
        _toggleTesterBtn = new Button { Content = "匹配测试" };
        _toggleTesterBtn.Click += (_, _) =>
        {
            var show = _testerCard.Visibility != Visibility.Visible;
            _testerCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            NetoUiFactory.ApplyPrimaryButton(_toggleTesterBtn, show);
        };
        var refreshRulesBtn = new Button { Content = "刷新" };
        refreshRulesBtn.Click += async (_, _) => await RefreshRulesAsync();
        Grid.SetColumn(_ruleCountLabel, 1);
        Grid.SetColumn(_toggleTesterBtn, 2);
        Grid.SetColumn(refreshRulesBtn, 3);
        ruleFilterRow.Children.Add(_ruleSearchBox);
        ruleFilterRow.Children.Add(_ruleCountLabel);
        ruleFilterRow.Children.Add(_toggleTesterBtn);
        ruleFilterRow.Children.Add(refreshRulesBtn);
        rulesTopStack.Children.Add(ruleFilterRow);

        var testerStack = new StackPanel { Spacing = 8 };
        var testerRow = new Grid { ColumnSpacing = 8 };
        testerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        testerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        testerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _routeTargetBox = new TextBox { PlaceholderText = "输入目标域名或 IP（如 www.google.com 或 223.5.5.5:53）" };
        _routeNetCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _routeNetCombo.Items.Add("TCP");
        _routeNetCombo.Items.Add("UDP");
        _routeNetCombo.SelectedIndex = 0;
        var runTestBtn = new Button { Content = "测试" };
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
        _testerCard = NetoUiFactory.CreateCard(testerStack, new Thickness(14, 10, 14, 10));
        _testerCard.Visibility = Visibility.Collapsed;
        rulesTopStack.Children.Add(_testerCard);

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

        var overrideOuter = new StackPanel
        {
            MaxWidth = 860,
            Spacing = 10,
            Padding = new Thickness(18, 6, 18, 24),
        };

        _quickFeedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var quickStack = new StackPanel { Spacing = 8 };
        quickStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("快速添加规则", _quickFeedbackText));
        var quickGrid = new Grid { ColumnSpacing = 8 };
        quickGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        quickGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        quickGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(152) });
        quickGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _quickTypeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var rt in RuleTypes) _quickTypeCombo.Items.Add(rt);
        _quickTypeCombo.SelectedIndex = 0;
        _quickMatchBox = new TextBox { PlaceholderText = "example.com 或 1.2.3.0/24" };
        _quickTargetCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _quickTargetCombo.Items.Add("PROXY");
        _quickTargetCombo.Items.Add("DIRECT");
        _quickTargetCombo.Items.Add("REJECT");
        _quickTargetCombo.SelectedIndex = 0;
        var addQuickBtn = new Button { Content = "添加" };
        NetoUiFactory.ApplyPrimaryButton(addQuickBtn, true);
        addQuickBtn.Click += async (_, _) => await AddQuickRuleAsync(addQuickBtn);
        Grid.SetColumn(_quickMatchBox, 1);
        Grid.SetColumn(_quickTargetCombo, 2);
        Grid.SetColumn(addQuickBtn, 3);
        quickGrid.Children.Add(_quickTypeCombo);
        quickGrid.Children.Add(_quickMatchBox);
        quickGrid.Children.Add(_quickTargetCombo);
        quickGrid.Children.Add(addQuickBtn);
        quickStack.Children.Add(quickGrid);
        overrideOuter.Children.Add(NetoUiFactory.CreateCard(quickStack));

        var editorActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _overrideFeedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        editorActions.Children.Add(_overrideFeedbackText);
        if (_ownerHwnd != IntPtr.Zero)
        {
            var importBtn = new Button { Content = "导入 YAML" };
            importBtn.Click += async (_, _) =>
            {
                var path = NetoWindowHelper.PickOpenYamlFile(_ownerHwnd);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                _overrideDirty = true;
                _overrideBox.Text = await File.ReadAllTextAsync(path);
            };
            editorActions.Children.Add(importBtn);
        }
        var resetBtn = new Button { Content = "恢复默认" };
        resetBtn.Click += async (_, _) => await ResetOverrideYamlAsync();
        var saveBtn = new Button { Content = "保存规则" };
        NetoUiFactory.ApplyPrimaryButton(saveBtn, true);
        saveBtn.Click += async (_, _) => await SaveOverrideYamlAsync();
        editorActions.Children.Add(resetBtn);
        editorActions.Children.Add(saveBtn);

        var editorStack = new StackPanel { Spacing = 8 };
        editorStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("规则覆写 YAML（留空则使用当前订阅规则）", editorActions));
        _overrideBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 300,
            MaxHeight = 460,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
        };
        _overrideBox.TextChanged += (_, _) => { if (!_updating) _overrideDirty = true; };
        editorStack.Children.Add(_overrideBox);
        overrideOuter.Children.Add(NetoUiFactory.CreateCard(editorStack));

        _overrideScroll = NetoUiFactory.CreateCenteredScrollViewer(overrideOuter);
        _overrideScroll.Visibility = Visibility.Collapsed;

        Grid.SetRow(_rulesPane, 1);
        Grid.SetRow(_overrideScroll, 1);
        Root.Children.Add(_rulesPane);
        Root.Children.Add(_overrideScroll);

        SwitchSubTab("rules");
    }

    public void SwitchSubTab(string subTab)
    {
        _subTab = subTab == "override" ? "override" : "rules";
        NetoUiFactory.ApplyPrimaryButton(_tabRulesBtn, _subTab == "rules");
        NetoUiFactory.ApplyPrimaryButton(_tabOverrideBtn, _subTab == "override");
        _rulesPane.Visibility = _subTab == "rules" ? Visibility.Visible : Visibility.Collapsed;
        _overrideScroll.Visibility = _subTab == "override" ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ApplySnapshot(NetoSnapshot snap)
    {
        SyncTargetOptions(snap);

        if (!_overrideInitialized || !_overrideDirty)
        {
            _updating = true;
            _overrideBox.Text = snap.Settings.CustomRouteYaml;
            _updating = false;
            _overrideInitialized = true;
        }

        var ruleAccountSig = $"{snap.ActiveProfileId}:{snap.RuleCount}:{snap.Settings.CustomRouteYaml.Length}:{snap.Settings.CustomRouteYaml.GetHashCode()}";
        if (_lastRuleAccountSig != ruleAccountSig)
        {
            _lastRuleAccountSig = ruleAccountSig;
            _rulesLoadedOnce = false;
            if (Root.Visibility == Visibility.Visible && _subTab == "rules")
            {
                _ = RefreshRulesAsync();
            }
        }

        if (Root.Visibility == Visibility.Visible && _subTab == "rules" && !_rulesLoadedOnce)
        {
            _ = RefreshRulesAsync();
        }
    }

    private void SyncTargetOptions(NetoSnapshot snap)
    {
        var options = new List<string> { "PROXY", "DIRECT", "REJECT" };
        var seen = new HashSet<string>(options, StringComparer.OrdinalIgnoreCase);
        foreach (var grp in snap.ProxyGroups)
        {
            if (!string.IsNullOrWhiteSpace(grp.Name) && seen.Add(grp.Name.Trim()))
            {
                options.Add(grp.Name.Trim());
            }
        }
        var sig = string.Join("|", options);
        if (_lastTargetsSig == sig) return;
        _lastTargetsSig = sig;

        var selected = _quickTargetCombo.SelectedItem as string;
        _quickTargetCombo.Items.Clear();
        foreach (var opt in options) _quickTargetCombo.Items.Add(opt);
        var nextIdx = !string.IsNullOrEmpty(selected) ? options.FindIndex(o => string.Equals(o, selected, StringComparison.Ordinal)) : 0;
        _quickTargetCombo.SelectedIndex = nextIdx >= 0 ? nextIdx : 0;
    }

    private async Task AddQuickRuleAsync(Button triggerBtn)
    {
        var pattern = (_quickMatchBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(pattern)) return;

        var ruleType = RuleTypes[Math.Clamp(_quickTypeCombo.SelectedIndex, 0, RuleTypes.Length - 1)];
        if (ruleType == "IP-CIDR" && !pattern.Contains('/'))
        {
            pattern += pattern.Contains(':') ? "/128" : "/32";
        }
        var target = _quickTargetCombo.SelectedItem as string ?? "PROXY";
        var ruleLine = $"{ruleType},{pattern},{target}";

        triggerBtn.IsEnabled = false;
        try
        {
            await _client.AddCustomRuleAsync(ruleLine);
            if (_client.LastActionError == null)
            {
                _quickMatchBox.Text = "";
                _overrideDirty = false;
                _updating = true;
                _overrideBox.Text = _client.Snapshot.Settings.CustomRouteYaml;
                _updating = false;
                _lastRuleAccountSig = "";
                await RefreshRulesAsync();
                await ShowFeedbackAsync(_quickFeedbackText, "已添加到顶部", false);
            }
            else
            {
                await ShowFeedbackAsync(_quickFeedbackText, _client.LastActionError, true);
            }
        }
        finally
        {
            triggerBtn.IsEnabled = true;
        }
    }

    private async Task SaveOverrideYamlAsync()
    {
        _overrideDirty = false;
        await _client.UpdateNetworkSettingsAsync(new JsonObject { ["custom_route_yaml"] = _overrideBox.Text });
        if (_client.LastActionError == null)
        {
            _lastRuleAccountSig = "";
            await RefreshRulesAsync();
            await ShowFeedbackAsync(_overrideFeedbackText, "已保存", false);
        }
        else
        {
            await ShowFeedbackAsync(_overrideFeedbackText, _client.LastActionError, true);
        }
    }

    private async Task ResetOverrideYamlAsync()
    {
        _updating = true;
        _overrideBox.Text = "";
        _updating = false;
        _overrideDirty = false;
        await _client.UpdateNetworkSettingsAsync(new JsonObject { ["custom_route_yaml"] = "" });
        if (_client.LastActionError == null)
        {
            _lastRuleAccountSig = "";
            await RefreshRulesAsync();
            await ShowFeedbackAsync(_overrideFeedbackText, "已恢复默认", false);
        }
        else
        {
            await ShowFeedbackAsync(_overrideFeedbackText, _client.LastActionError, true);
        }
    }

    private static async Task ShowFeedbackAsync(TextBlock target, string message, bool isError)
    {
        target.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 216, 82, 76)
            : Color.FromArgb(255, 42, 168, 118));
        target.Text = message;
        if (!isError)
        {
            await Task.Delay(1800);
            if (target.Text == message) target.Text = "";
        }
    }

    public async Task RefreshRulesAsync()
    {
        if (_fetchingRules) return;
        _fetchingRules = true;
        try
        {
            var filter = (_ruleSearchBox.Text ?? "").Trim();
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
        var target = (_routeTargetBox.Text ?? "").Trim();
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
