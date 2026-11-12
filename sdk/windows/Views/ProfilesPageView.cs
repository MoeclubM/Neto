using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neto.Sdk.Windows.Views;

public sealed class ProfilesPageView
{
    public ScrollViewer Root { get; }
    private readonly NetoClient _client;
    private readonly TextBox _nameBox;
    private readonly TextBox _urlBox;
    private readonly Button _importBtn;
    private readonly TextBox _yamlBox;
    private readonly Button _saveYamlBtn;
    private readonly TextBlock _errLabel;
    private readonly StackPanel _savedList;
    private string _lastSig = "";

    public ProfilesPageView(NetoClient client)
    {
        _client = client;
        var outer = new StackPanel
        {
            MaxWidth = 820,
            Spacing = 12,
            Padding = new Thickness(18, 12, 18, 24),
        };

        outer.Children.Add(NetoUiFactory.CreateSectionHeader("导入订阅"));
        var addStack = new StackPanel { Spacing = 8 };
        _nameBox = new TextBox { PlaceholderText = "名称（可选）" };
        _urlBox = new TextBox { PlaceholderText = "订阅链接 URL" };
        _importBtn = new Button { Content = "拉取并添加", HorizontalAlignment = HorizontalAlignment.Right };
        NetoUiFactory.ApplyPrimaryButton(_importBtn, true);
        _importBtn.Click += async (_, _) =>
        {
            var u = _urlBox.Text.Trim();
            if (string.IsNullOrEmpty(u)) return;
            await _client.AddRemoteProfileAsync(_nameBox.Text.Trim(), u);
            if (_client.LastActionError == null)
            {
                _nameBox.Text = "";
                _urlBox.Text = "";
            }
        };
        addStack.Children.Add(_nameBox);
        addStack.Children.Add(_urlBox);
        addStack.Children.Add(_importBtn);
        outer.Children.Add(NetoUiFactory.CreateCard(addStack));

        var yamlStack = new StackPanel { Spacing = 8 };
        _yamlBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110,
            PlaceholderText = "粘贴 Clash / Aerion YAML 配置",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        _saveYamlBtn = new Button { Content = "保存本地配置", HorizontalAlignment = HorizontalAlignment.Right };
        _saveYamlBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_yamlBox.Text)) return;
            await _client.ImportLocalProfileAsync(_nameBox.Text.Trim(), _yamlBox.Text);
            if (_client.LastActionError == null) _yamlBox.Text = "";
        };
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

        _errLabel = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        outer.Children.Add(_errLabel);

        _savedList = new StackPanel { Spacing = 8 };
        outer.Children.Add(_savedList);
        Root = new ScrollViewer { Content = outer };
    }

    public void ApplySnapshot(NetoSnapshot snap, bool busy, string? err)
    {
        _importBtn.IsEnabled = !busy;
        _saveYamlBtn.IsEnabled = !busy;
        _importBtn.Content = busy ? "导入中" : "拉取并添加";
        if (!string.IsNullOrWhiteSpace(err))
        {
            _errLabel.Text = err;
            _errLabel.Visibility = Visibility.Visible;
        }
        else
        {
            _errLabel.Visibility = Visibility.Collapsed;
        }

        var sig = string.Join("|", snap.Profiles.Select(p => $"{p.Id}:{p.Active}:{p.NodeCount}:{p.RuleCount}:{p.UsedBytes}:{p.TotalBytes}"));
        if (_lastSig == sig) return;
        _lastSig = sig;

        _savedList.Children.Clear();
        if (snap.Profiles.Count == 0) return;
        _savedList.Children.Add(NetoUiFactory.CreateSectionHeader("已保存订阅"));
        foreach (var prof in snap.Profiles)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock
            {
                Text = prof.Active ? $"● {prof.Name}" : prof.Name,
                FontWeight = prof.Active ? FontWeights.SemiBold : FontWeights.Normal,
            });
            var quota = prof.TotalBytes > 0
                ? $" · {NetoFormat.FormatBytes(prof.UsedBytes)} / {NetoFormat.FormatBytes(prof.TotalBytes)}"
                : "";
            info.Children.Add(new TextBlock
            {
                Text = $"{prof.NodeCount} 节点 · {prof.RuleCount} 规则{quota}",
                FontSize = 12,
                Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            });

            var selBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = info,
            };
            NetoUiFactory.ApplyPrimaryButton(selBtn, prof.Active);
            var pid = prof.Id;
            selBtn.Click += async (_, _) => await _client.SelectProfileAsync(pid);
            grid.Children.Add(selBtn);

            if (!string.IsNullOrEmpty(prof.Url))
            {
                var rBtn = new Button { Content = "↻", VerticalAlignment = VerticalAlignment.Center };
                rBtn.Click += async (_, _) => await _client.RefreshProfileAsync(pid);
                Grid.SetColumn(rBtn, 1);
                grid.Children.Add(rBtn);
            }
            var dBtn = new Button { Content = "✕", VerticalAlignment = VerticalAlignment.Center };
            dBtn.Click += async (_, _) => await _client.DeleteProfileAsync(pid);
            Grid.SetColumn(dBtn, 2);
            grid.Children.Add(dBtn);
            _savedList.Children.Add(grid);
        }
    }
}
