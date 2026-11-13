using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public sealed class ProfilesPageView
{
    public ScrollViewer Root { get; }
    private readonly NetoClient _client;
    private readonly IntPtr _ownerHwnd;
    private readonly TextBox _nameBox;
    private readonly TextBox _urlBox;
    private readonly ComboBox _autoUpdateCombo;
    private readonly Button _importBtn;
    private readonly TextBox _yamlBox;
    private readonly Button _saveYamlBtn;
    private readonly Border _errCard;
    private readonly TextBlock _errLabel;
    private readonly StackPanel _savedList;
    private readonly Expander _rulesExpander;
    private readonly TextBox _rulesPreviewBox;
    private string _lastSig = "";

    private static readonly (int Mins, string Label)[] AutoUpdateIntervals =
    [
        (15, "自动更新：每 15 分钟"),
        (30, "自动更新：每 30 分钟"),
        (60, "自动更新：每 1 小时"),
        (360, "自动更新：每 6 小时"),
        (1440, "自动更新：每 24 小时"),
        (0, "仅手动更新"),
    ];

    public ProfilesPageView(NetoClient client, IntPtr ownerHwnd = default)
    {
        _client = client;
        _ownerHwnd = ownerHwnd;
        var outer = new StackPanel
        {
            MaxWidth = 860,
            Spacing = 10,
            Padding = new Thickness(18, 8, 18, 24),
        };

        var addStack = new StackPanel { Spacing = 8 };
        addStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("添加远程订阅"));
        var urlRow = new Grid { ColumnSpacing = 8 };
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _urlBox = new TextBox { PlaceholderText = "https://... 订阅链接 URL" };
        var pasteUrlBtn = new Button { Content = "粘贴链接" };
        pasteUrlBtn.Click += (_, _) =>
        {
            var clip = NetoWindowHelper.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip)) _urlBox.Text = clip.Trim();
        };
        Grid.SetColumn(pasteUrlBtn, 1);
        urlRow.Children.Add(_urlBox);
        urlRow.Children.Add(pasteUrlBtn);

        var metaRow = new Grid { ColumnSpacing = 8 };
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _nameBox = new TextBox { PlaceholderText = "备注名称（留空自动识别域名）" };
        _autoUpdateCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in AutoUpdateIntervals) _autoUpdateCombo.Items.Add(label);
        _autoUpdateCombo.SelectedIndex = 2;
        _importBtn = new Button { Content = "拉取添加", VerticalAlignment = VerticalAlignment.Center };
        NetoUiFactory.ApplyPrimaryButton(_importBtn, true);
        _importBtn.Click += async (_, _) =>
        {
            var u = _urlBox.Text.Trim();
            if (string.IsNullOrEmpty(u)) return;
            var selIdx = Math.Clamp(_autoUpdateCombo.SelectedIndex, 0, AutoUpdateIntervals.Length - 1);
            var mins = AutoUpdateIntervals[selIdx].Mins;
            await _client.AddRemoteProfileAsync(_nameBox.Text.Trim(), u, mins);
            if (_client.LastActionError == null)
            {
                _nameBox.Text = "";
                _urlBox.Text = "";
            }
        };
        Grid.SetColumn(_autoUpdateCombo, 1);
        Grid.SetColumn(_importBtn, 2);
        metaRow.Children.Add(_nameBox);
        metaRow.Children.Add(_autoUpdateCombo);
        metaRow.Children.Add(_importBtn);
        addStack.Children.Add(urlRow);
        addStack.Children.Add(metaRow);
        outer.Children.Add(NetoUiFactory.CreateCard(addStack));

        var yamlStack = new StackPanel { Spacing = 8 };
        _yamlBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            MaxHeight = 260,
            PlaceholderText = "粘贴或从文件载入 Clash / Aerion YAML 配置",
            FontFamily = new FontFamily("Consolas"),
        };
        var yamlActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var pickFileBtn = new Button { Content = "选择 YAML 文件" };
        pickFileBtn.Click += async (_, _) =>
        {
            var path = NetoWindowHelper.PickOpenYamlFile(_ownerHwnd);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var content = await File.ReadAllTextAsync(path);
            _yamlBox.Text = content;
            if (string.IsNullOrWhiteSpace(_nameBox.Text))
            {
                _nameBox.Text = Path.GetFileNameWithoutExtension(path);
            }
        };
        var pasteYamlBtn = new Button { Content = "粘贴剪贴板" };
        pasteYamlBtn.Click += (_, _) =>
        {
            var clip = NetoWindowHelper.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip)) _yamlBox.Text = clip;
        };
        yamlActions.Children.Add(pickFileBtn);
        yamlActions.Children.Add(pasteYamlBtn);
        _saveYamlBtn = new Button { Content = "保存本地配置", HorizontalAlignment = HorizontalAlignment.Right };
        NetoUiFactory.ApplyPrimaryButton(_saveYamlBtn, true);
        _saveYamlBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_yamlBox.Text)) return;
            await _client.ImportLocalProfileAsync(_nameBox.Text.Trim(), _yamlBox.Text);
            if (_client.LastActionError == null) _yamlBox.Text = "";
        };
        yamlStack.Children.Add(yamlActions);
        yamlStack.Children.Add(_yamlBox);
        yamlStack.Children.Add(_saveYamlBtn);
        var expander = new Expander
        {
            Header = "导入本地 YAML 配置",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = yamlStack,
        };
        outer.Children.Add(expander);

        _errLabel = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 216, 82, 76)),
        };
        _errCard = NetoUiFactory.CreateCard(_errLabel, new Thickness(12, 8, 12, 8));
        _errCard.Visibility = Visibility.Collapsed;
        outer.Children.Add(_errCard);

        _savedList = new StackPanel { Spacing = 8 };
        outer.Children.Add(_savedList);

        _rulesPreviewBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 220,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
        };
        _rulesExpander = new Expander
        {
            Header = "当前订阅分流规则预览",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Content = _rulesPreviewBox,
        };
        outer.Children.Add(_rulesExpander);

        Root = NetoUiFactory.CreateCenteredScrollViewer(outer);
    }

    public void ApplySnapshot(NetoSnapshot snap, bool busy, string? err)
    {
        _importBtn.IsEnabled = !busy;
        _saveYamlBtn.IsEnabled = !busy;
        _importBtn.Content = busy ? "拉取中..." : "拉取添加";
        if (!string.IsNullOrWhiteSpace(err))
        {
            _errLabel.Text = err;
            _errCard.Visibility = Visibility.Visible;
        }
        else
        {
            _errCard.Visibility = Visibility.Collapsed;
        }

        var sig = string.Join("|",
            snap.RuleCount,
            string.Join(",", snap.RulesPreview.Take(5)),
            string.Join(";", snap.Profiles.Select(p => $"{p.Id}:{p.Active}:{p.NodeCount}:{p.RuleCount}:{p.UsedBytes}:{p.TotalBytes}:{p.UpdatedAtUnix}:{p.ExpireUnix}")));
        if (_lastSig == sig) return;
        _lastSig = sig;

        if (snap.RulesPreview.Count > 0)
        {
            _rulesExpander.Visibility = Visibility.Visible;
            _rulesExpander.Header = $"当前订阅分流规则预览（共 {snap.RuleCount} 条）";
            _rulesPreviewBox.Text = string.Join(Environment.NewLine, snap.RulesPreview);
        }
        else
        {
            _rulesExpander.Visibility = Visibility.Collapsed;
        }

        _savedList.Children.Clear();
        if (snap.Profiles.Count == 0)
        {
            _savedList.Children.Add(NetoUiFactory.CreateCard(new TextBlock
            {
                Text = "暂无订阅配置",
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            }));
            return;
        }

        foreach (var prof in snap.Profiles)
        {
            var cardStack = new StackPanel { Spacing = 6 };
            var topGrid = new Grid { ColumnSpacing = 8 };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            titleRow.Children.Add(new TextBlock
            {
                Text = prof.Active ? $"● {prof.Name}" : prof.Name,
                FontWeight = prof.Active ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var kindText = prof.Kind switch { "local" => "本地", "managed" => "托管", _ => "远程" };
            titleRow.Children.Add(NetoUiFactory.CreateTagBadge(kindText));
            if (prof.AutoUpdateMins > 0 && !string.IsNullOrEmpty(prof.Url))
            {
                titleRow.Children.Add(NetoUiFactory.CreateTagBadge($"{prof.AutoUpdateMins}m 自动更新"));
            }
            topGrid.Children.Add(titleRow);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var pid = prof.Id;
            var useBtn = new Button
            {
                Content = prof.Active ? "当前使用" : "启用",
                IsEnabled = !prof.Active,
            };
            NetoUiFactory.ApplyPrimaryButton(useBtn, !prof.Active);
            useBtn.Click += async (_, _) => await _client.SelectProfileAsync(pid);
            actions.Children.Add(useBtn);

            if (!string.IsNullOrEmpty(prof.Url))
            {
                var copyUrlBtn = new Button { Content = "复制链接" };
                var rawUrl = prof.Url;
                copyUrlBtn.Click += async (_, _) =>
                {
                    if (NetoWindowHelper.SetClipboardText(rawUrl))
                    {
                        copyUrlBtn.Content = "已复制";
                        await Task.Delay(1000);
                        copyUrlBtn.Content = "复制链接";
                    }
                };
                actions.Children.Add(copyUrlBtn);

                var rBtn = new Button { Content = "更新" };
                rBtn.Click += async (_, _) => await _client.RefreshProfileAsync(pid);
                actions.Children.Add(rBtn);
            }
            var dBtn = new Button { Content = "删除" };
            dBtn.Click += async (_, _) => await _client.DeleteProfileAsync(pid);
            actions.Children.Add(dBtn);
            Grid.SetColumn(actions, 1);
            topGrid.Children.Add(actions);
            cardStack.Children.Add(topGrid);

            var metaParts = new List<string>
            {
                $"{prof.NodeCount} 节点",
                $"{prof.RuleCount} 规则",
                $"更新于 {NetoFormat.FormatUnixTimeShort(prof.UpdatedAtUnix)}",
            };
            if (prof.ExpireUnix.HasValue && prof.ExpireUnix.Value > 0)
            {
                metaParts.Add($"到期 {NetoFormat.FormatUnixDate(prof.ExpireUnix.Value)}");
            }
            cardStack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", metaParts),
                FontSize = 12,
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            });

            if (prof.TotalBytes > 0)
            {
                var quotaGrid = new Grid { ColumnSpacing = 10 };
                quotaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                quotaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var bar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = Math.Clamp((double)prof.UsedBytes / prof.TotalBytes * 100.0, 0.0, 100.0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var qLabel = new TextBlock
                {
                    Text = $"{NetoFormat.FormatBytes(prof.UsedBytes)} / {NetoFormat.FormatBytes(prof.TotalBytes)}",
                    FontSize = 12,
                    Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
                };
                Grid.SetColumn(qLabel, 1);
                quotaGrid.Children.Add(bar);
                quotaGrid.Children.Add(qLabel);
                cardStack.Children.Add(quotaGrid);
            }

            _savedList.Children.Add(NetoUiFactory.CreateCard(cardStack));
        }
    }
}
