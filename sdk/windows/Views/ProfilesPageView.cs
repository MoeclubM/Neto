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
    private readonly StackPanel _rootPane;
    private readonly StackPanel _subPane;
    private readonly StackPanel _advImportPane;
    private readonly StackPanel _editPane;
    private readonly TextBox _quickUrlBox;
    private readonly Button _quickImportBtn;
    private readonly TextBox _advNameBox;
    private readonly TextBox _advUrlBox;
    private readonly ComboBox _flagCombo;
    private readonly ComboBox _autoUpdateCombo;
    private readonly Button _advImportBtn;
    private readonly TextBox _localNameBox;
    private readonly TextBox _yamlBox;
    private readonly Button _saveYamlBtn;
    private readonly Border _errCard;
    private readonly TextBlock _errLabel;
    private readonly Button _refreshAllBtn;
    private readonly TextBlock _editSubpageTitle;
    private readonly TextBox _editNameBox;
    private readonly TextBox _editUrlBox;
    private readonly ComboBox _editFlagCombo;
    private readonly ComboBox _editAutoCombo;
    private readonly Grid _editRemoteRow;
    private readonly TextBox _editYamlBox;
    private string? _editingProfileId;
    private bool _editingYamlMode;
    private readonly StackPanel _savedList;
    private string _lastSig = "__unset__";

    private static readonly (string Flag, string Label)[] FlagOptions =
    [
        ("aerion", "Aerion 格式"),
        ("meta", "Clash Meta 格式"),
    ];

    private static readonly (int Mins, string Label)[] AutoUpdateIntervals =
    [
        (15, "每 15 分钟更新"),
        (30, "每 30 分钟更新"),
        (60, "每 1 小时更新"),
        (360, "每 6 小时更新"),
        (1440, "每 24 小时更新"),
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
        _rootPane = new StackPanel { Spacing = 10 };
        _subPane = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };

        var quickRow = new Grid { ColumnSpacing = 8 };
        quickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        quickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        quickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        quickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _quickUrlBox = new TextBox { PlaceholderText = "https://... 订阅链接 URL" };
        var quickPasteBtn = new Button { Content = "粘贴" };
        quickPasteBtn.Click += (_, _) =>
        {
            var clip = NetoWindowHelper.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip)) _quickUrlBox.Text = clip.Trim();
        };
        _quickImportBtn = new Button { Content = "导入" };
        NetoUiFactory.ApplyPrimaryButton(_quickImportBtn, true);
        _quickImportBtn.Click += async (_, _) =>
        {
            var u = (_quickUrlBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(u)) return;
            await _client.AddRemoteProfileAsync("", u, 60, "aerion");
            if (_client.LastActionError == null) _quickUrlBox.Text = "";
        };
        var advBtn = new Button { Content = "高级 / 本地" };
        advBtn.Click += (_, _) =>
        {
            var typed = (_quickUrlBox.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(typed) && string.IsNullOrWhiteSpace(_advUrlBox.Text))
            {
                _advUrlBox.Text = typed;
            }
            OpenSubpage("advanced_import");
        };
        Grid.SetColumn(quickPasteBtn, 1);
        Grid.SetColumn(_quickImportBtn, 2);
        Grid.SetColumn(advBtn, 3);
        quickRow.Children.Add(_quickUrlBox);
        quickRow.Children.Add(quickPasteBtn);
        quickRow.Children.Add(_quickImportBtn);
        quickRow.Children.Add(advBtn);
        _rootPane.Children.Add(NetoUiFactory.CreateCard(quickRow));

        _errLabel = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 216, 82, 76)),
        };
        _errCard = NetoUiFactory.CreateCard(_errLabel, new Thickness(12, 8, 12, 8));
        _errCard.Visibility = Visibility.Collapsed;
        _rootPane.Children.Add(_errCard);

        _refreshAllBtn = new Button { Content = "全部更新", Padding = new Thickness(10, 4, 10, 4), Visibility = Visibility.Collapsed };
        _refreshAllBtn.Click += async (_, _) => await _client.RefreshAllProfilesAsync();
        _rootPane.Children.Add(NetoUiFactory.CreateInlineCardHeader("已保存订阅", _refreshAllBtn));

        _savedList = new StackPanel { Spacing = 8 };
        _rootPane.Children.Add(_savedList);

        _advImportPane = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        _advImportPane.Children.Add(NetoUiFactory.CreateSubpageHeader("高级与本地导入", ResetToRoot));

        var remoteStack = new StackPanel { Spacing = 8 };
        remoteStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("远程订阅参数"));
        var advUrlRow = new Grid { ColumnSpacing = 8 };
        advUrlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        advUrlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _advUrlBox = new TextBox { PlaceholderText = "https://... 订阅链接 URL" };
        var advPasteBtn = new Button { Content = "粘贴链接" };
        advPasteBtn.Click += (_, _) =>
        {
            var clip = NetoWindowHelper.GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clip)) _advUrlBox.Text = clip.Trim();
        };
        Grid.SetColumn(advPasteBtn, 1);
        advUrlRow.Children.Add(_advUrlBox);
        advUrlRow.Children.Add(advPasteBtn);

        var metaRow = new Grid { ColumnSpacing = 8 };
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _advNameBox = new TextBox { PlaceholderText = "备注名称（留空自动识别）" };
        _flagCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in FlagOptions) _flagCombo.Items.Add(label);
        _flagCombo.SelectedIndex = 0;
        _autoUpdateCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in AutoUpdateIntervals) _autoUpdateCombo.Items.Add(label);
        _autoUpdateCombo.SelectedIndex = 2;
        _advImportBtn = new Button { Content = "拉取添加", VerticalAlignment = VerticalAlignment.Center };
        NetoUiFactory.ApplyPrimaryButton(_advImportBtn, true);
        _advImportBtn.Click += async (_, _) =>
        {
            var u = (_advUrlBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(u)) return;
            var flagIdx = Math.Clamp(_flagCombo.SelectedIndex, 0, FlagOptions.Length - 1);
            var selIdx = Math.Clamp(_autoUpdateCombo.SelectedIndex, 0, AutoUpdateIntervals.Length - 1);
            var mins = AutoUpdateIntervals[selIdx].Mins;
            await _client.AddRemoteProfileAsync((_advNameBox.Text ?? "").Trim(), u, mins, FlagOptions[flagIdx].Flag);
            if (_client.LastActionError == null)
            {
                _advNameBox.Text = "";
                _advUrlBox.Text = "";
                _quickUrlBox.Text = "";
                ResetToRoot();
            }
        };
        Grid.SetColumn(_flagCombo, 1);
        Grid.SetColumn(_autoUpdateCombo, 2);
        Grid.SetColumn(_advImportBtn, 3);
        metaRow.Children.Add(_advNameBox);
        metaRow.Children.Add(_flagCombo);
        metaRow.Children.Add(_autoUpdateCombo);
        metaRow.Children.Add(_advImportBtn);
        remoteStack.Children.Add(advUrlRow);
        remoteStack.Children.Add(metaRow);
        _advImportPane.Children.Add(NetoUiFactory.CreateCard(remoteStack));

        var yamlActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var pickFileBtn = new Button { Content = "选择 YAML 文件" };
        pickFileBtn.Click += async (_, _) =>
        {
            var path = NetoWindowHelper.PickOpenYamlFile(_ownerHwnd);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var content = await File.ReadAllTextAsync(path);
            _yamlBox.Text = content;
            if (string.IsNullOrWhiteSpace(_localNameBox.Text))
            {
                _localNameBox.Text = Path.GetFileNameWithoutExtension(path);
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

        var yamlStack = new StackPanel { Spacing = 8 };
        yamlStack.Children.Add(NetoUiFactory.CreateInlineCardHeader("本地 YAML 配置", yamlActions));
        _localNameBox = new TextBox { PlaceholderText = "配置名称（可选）" };
        _yamlBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 136,
            MaxHeight = 340,
            FontSize = 12,
            PlaceholderText = "粘贴或从文件载入 Clash / Aerion YAML 配置",
            FontFamily = new FontFamily("Consolas"),
        };
        _saveYamlBtn = new Button { Content = "保存本地配置", HorizontalAlignment = HorizontalAlignment.Right };
        NetoUiFactory.ApplyPrimaryButton(_saveYamlBtn, true);
        _saveYamlBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_yamlBox.Text)) return;
            await _client.ImportLocalProfileAsync((_localNameBox.Text ?? "").Trim(), _yamlBox.Text);
            if (_client.LastActionError == null)
            {
                _localNameBox.Text = "";
                _yamlBox.Text = "";
                ResetToRoot();
            }
        };
        yamlStack.Children.Add(_localNameBox);
        yamlStack.Children.Add(_yamlBox);
        yamlStack.Children.Add(_saveYamlBtn);
        _advImportPane.Children.Add(NetoUiFactory.CreateCard(yamlStack));

        _editPane = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var saveEditBtn = new Button { Content = "保存更改" };
        NetoUiFactory.ApplyPrimaryButton(saveEditBtn, true);
        saveEditBtn.Click += async (_, _) => await SaveEditedProfileAsync(saveEditBtn);
        var editHeaderGrid = NetoUiFactory.CreateSubpageHeader("", ResetToRoot, saveEditBtn);
        _editSubpageTitle = (TextBlock)editHeaderGrid.Children[1];
        _editPane.Children.Add(editHeaderGrid);

        var editCardStack = new StackPanel { Spacing = 10 };
        _editNameBox = new TextBox { Header = "订阅名称" };
        _editUrlBox = new TextBox { Header = "订阅地址" };
        _editFlagCombo = new ComboBox { Header = "订阅格式", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in FlagOptions) _editFlagCombo.Items.Add(label);
        _editAutoCombo = new ComboBox { Header = "更新周期", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in AutoUpdateIntervals) _editAutoCombo.Items.Add(label);
        _editRemoteRow = new Grid { ColumnSpacing = 10 };
        _editRemoteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _editRemoteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_editAutoCombo, 1);
        _editRemoteRow.Children.Add(_editFlagCombo);
        _editRemoteRow.Children.Add(_editAutoCombo);

        _editYamlBox = new TextBox
        {
            Header = "YAML 内容",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 320,
            MaxHeight = 480,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
        };
        editCardStack.Children.Add(_editNameBox);
        editCardStack.Children.Add(_editUrlBox);
        editCardStack.Children.Add(_editRemoteRow);
        editCardStack.Children.Add(_editYamlBox);
        _editPane.Children.Add(NetoUiFactory.CreateCard(editCardStack));

        _subPane.Children.Add(_advImportPane);
        _subPane.Children.Add(_editPane);

        outer.Children.Add(_rootPane);
        outer.Children.Add(_subPane);
        Root = NetoUiFactory.CreateCenteredScrollViewer(outer);
    }

    public void ResetToRoot()
    {
        _editingProfileId = null;
        _subPane.Visibility = Visibility.Collapsed;
        _rootPane.Visibility = Visibility.Visible;
    }

    public void OpenSubpage(string key)
    {
        _advImportPane.Visibility = key == "advanced_import" ? Visibility.Visible : Visibility.Collapsed;
        _editPane.Visibility = key == "edit" ? Visibility.Visible : Visibility.Collapsed;
        _rootPane.Visibility = Visibility.Collapsed;
        _subPane.Visibility = Visibility.Visible;
        Root.ChangeView(null, 0, null, true);
    }

    private void OpenMetadataEditor(NetoProfileBrief prof)
    {
        _editingProfileId = prof.Id;
        _editingYamlMode = false;
        _editSubpageTitle.Text = $"编辑订阅 · {prof.Name}";
        _editNameBox.Text = prof.Name;
        _editNameBox.Visibility = Visibility.Visible;
        var hasRemote = !string.IsNullOrWhiteSpace(prof.Url);
        _editUrlBox.Text = prof.Url;
        _editUrlBox.Visibility = hasRemote ? Visibility.Visible : Visibility.Collapsed;
        _editRemoteRow.Visibility = hasRemote ? Visibility.Visible : Visibility.Collapsed;
        var flagIdx = Array.FindIndex(FlagOptions, f => string.Equals(f.Flag, prof.Flag, StringComparison.OrdinalIgnoreCase));
        _editFlagCombo.SelectedIndex = flagIdx >= 0 ? flagIdx : 0;
        var autoIdx = Array.FindIndex(AutoUpdateIntervals, a => a.Mins == (int)prof.AutoUpdateMins);
        _editAutoCombo.SelectedIndex = autoIdx >= 0 ? autoIdx : 2;
        _editYamlBox.Visibility = Visibility.Collapsed;
        OpenSubpage("edit");
    }

    private async Task OpenYamlEditorAsync(NetoProfileBrief prof)
    {
        _editingProfileId = prof.Id;
        _editingYamlMode = true;
        _editSubpageTitle.Text = $"编辑配置 YAML · {prof.Name}";
        _editNameBox.Visibility = Visibility.Collapsed;
        _editUrlBox.Visibility = Visibility.Collapsed;
        _editRemoteRow.Visibility = Visibility.Collapsed;
        _editYamlBox.Text = await _client.GetProfileYamlAsync(prof.Id);
        _editYamlBox.Visibility = Visibility.Visible;
        OpenSubpage("edit");
    }

    private async Task SaveEditedProfileAsync(Button btn)
    {
        if (string.IsNullOrEmpty(_editingProfileId)) return;
        btn.IsEnabled = false;
        try
        {
            if (_editingYamlMode)
            {
                await _client.UpdateProfileAsync(_editingProfileId, yaml: _editYamlBox.Text);
            }
            else
            {
                var flag = FlagOptions[Math.Clamp(_editFlagCombo.SelectedIndex, 0, FlagOptions.Length - 1)].Flag;
                var mins = AutoUpdateIntervals[Math.Clamp(_editAutoCombo.SelectedIndex, 0, AutoUpdateIntervals.Length - 1)].Mins;
                var url = _editUrlBox.Visibility == Visibility.Visible ? (_editUrlBox.Text ?? "").Trim() : null;
                await _client.UpdateProfileAsync(_editingProfileId, name: (_editNameBox.Text ?? "").Trim(), url: url, flag: flag, autoUpdateMins: mins);
            }
            if (_client.LastActionError == null)
            {
                ResetToRoot();
            }
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    public void ApplySnapshot(NetoSnapshot snap, bool busy, string? err)
    {
        _quickImportBtn.IsEnabled = !busy;
        _advImportBtn.IsEnabled = !busy;
        _saveYamlBtn.IsEnabled = !busy;
        _refreshAllBtn.IsEnabled = !busy;
        _refreshAllBtn.Visibility = snap.Profiles.Count(p => !string.IsNullOrWhiteSpace(p.Url)) > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        _quickImportBtn.Content = busy ? "导入中..." : "导入";
        _advImportBtn.Content = busy ? "拉取中..." : "拉取添加";
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
            string.Join(";", snap.Profiles.Select(p => $"{p.Id}:{p.Name}:{p.Url}:{p.Flag}:{p.AutoUpdateMins}:{p.Active}:{p.NodeCount}:{p.RuleCount}:{p.UsedBytes}:{p.TotalBytes}:{p.UpdatedAtUnix}:{p.ExpireUnix}")));
        if (_lastSig == sig) return;
        _lastSig = sig;

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
            if (!string.IsNullOrEmpty(prof.Url))
            {
                titleRow.Children.Add(NetoUiFactory.CreateTagBadge(string.Equals(prof.Flag, "meta", StringComparison.OrdinalIgnoreCase) ? "Meta" : "Aerion"));
            }
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
            var profSnap = prof;
            var editBtn = new Button { Content = "编辑" };
            editBtn.Click += (_, _) => OpenMetadataEditor(profSnap);
            actions.Children.Add(editBtn);

            var yamlBtn = new Button { Content = "YAML" };
            yamlBtn.Click += async (_, _) => await OpenYamlEditorAsync(profSnap);
            actions.Children.Add(yamlBtn);

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
