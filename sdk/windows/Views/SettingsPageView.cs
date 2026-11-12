using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neto.Sdk.Windows.Views;

public sealed class SettingsPageView
{
    public ScrollViewer Root { get; }
    private readonly NetoClient _client;
    private readonly NumberBox _portBox;
    private readonly ToggleSwitch _lanSwitch;
    private readonly ToggleSwitch _ipv6Switch;
    private readonly ToggleSwitch _autostartSwitch;
    private readonly ComboBox _dnsModeCombo;
    private readonly TextBox _nodeDnsBox;
    private readonly TextBox _overseasDnsBox;
    private readonly TextBox _directDnsBox;
    private readonly TextBox _testUrlBox;
    private readonly TextBox _customRouteBox;
    private readonly TextBox _logsBox;
    private bool _loaded;

    private static readonly (string Key, string Label)[] DnsModes =
    [
        ("virtual", "虚拟 DNS"),
        ("over_tcp", "TCP 转发"),
        ("direct", "直连 DNS"),
    ];

    public SettingsPageView(NetoClient client)
    {
        _client = client;
        var outer = new StackPanel
        {
            MaxWidth = 820,
            Spacing = 12,
            Padding = new Thickness(18, 12, 18, 24),
        };

        outer.Children.Add(NetoUiFactory.CreateSectionHeader("代理与启动"));
        var netStack = new StackPanel { Spacing = 10 };
        _portBox = new NumberBox { Header = "本地混合端口", Minimum = 1024, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var switchesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        _lanSwitch = new ToggleSwitch { Header = "允许局域网连接" };
        _ipv6Switch = new ToggleSwitch { Header = "启用 IPv6" };
        _autostartSwitch = new ToggleSwitch { Header = "开机自动启动" };
        switchesRow.Children.Add(_lanSwitch);
        switchesRow.Children.Add(_ipv6Switch);
        switchesRow.Children.Add(_autostartSwitch);
        netStack.Children.Add(_portBox);
        netStack.Children.Add(switchesRow);
        outer.Children.Add(NetoUiFactory.CreateCard(netStack));

        outer.Children.Add(NetoUiFactory.CreateSectionHeader("DNS 与连通测速"));
        var dnsStack = new StackPanel { Spacing = 8 };
        _dnsModeCombo = new ComboBox { Header = "TUN DNS 模式", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in DnsModes) _dnsModeCombo.Items.Add(label);
        _nodeDnsBox = new TextBox { Header = "节点解析 DoH" };
        _overseasDnsBox = new TextBox { Header = "代理 DNS" };
        _directDnsBox = new TextBox { Header = "直连 DNS" };
        _testUrlBox = new TextBox { Header = "连通测速地址" };
        _customRouteBox = new TextBox
        {
            Header = "自定义分流规则 YAML（留空使用订阅规则）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        var saveBtn = new Button { Content = "保存网络设置", HorizontalAlignment = HorizontalAlignment.Right };
        NetoUiFactory.ApplyPrimaryButton(saveBtn, true);
        saveBtn.Click += async (_, _) => await SaveAllAsync();
        dnsStack.Children.Add(_dnsModeCombo);
        dnsStack.Children.Add(_nodeDnsBox);
        dnsStack.Children.Add(_overseasDnsBox);
        dnsStack.Children.Add(_directDnsBox);
        dnsStack.Children.Add(_testUrlBox);
        dnsStack.Children.Add(_customRouteBox);
        dnsStack.Children.Add(saveBtn);
        outer.Children.Add(NetoUiFactory.CreateCard(dnsStack));

        outer.Children.Add(NetoUiFactory.CreateSectionHeader("运行日志"));
        var logStack = new StackPanel { Spacing = 8 };
        var logActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var refreshLogBtn = new Button { Content = "刷新" };
        var clearLogBtn = new Button { Content = "清空" };
        logActions.Children.Add(refreshLogBtn);
        logActions.Children.Add(clearLogBtn);
        _logsBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 140,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        refreshLogBtn.Click += async (_, _) => await RefreshLogsAsync();
        clearLogBtn.Click += async (_, _) => { await _client.ClearLogsAsync(); _logsBox.Text = ""; };
        logStack.Children.Add(logActions);
        logStack.Children.Add(_logsBox);
        outer.Children.Add(NetoUiFactory.CreateCard(logStack));

        Root = new ScrollViewer { Content = outer };
    }

    public void ApplySnapshot(NetoSnapshot snap)
    {
        if (_loaded) return;
        _loaded = true;
        var s = snap.Settings;
        _portBox.Value = s.MixedPort;
        _lanSwitch.IsOn = s.AllowLan;
        _ipv6Switch.IsOn = s.Ipv6Enabled;
        _autostartSwitch.IsOn = s.Autostart;
        var idx = Array.FindIndex(DnsModes, m => m.Key == s.VpnDnsMode);
        _dnsModeCombo.SelectedIndex = idx >= 0 ? idx : 0;
        _nodeDnsBox.Text = s.NodeDns;
        _overseasDnsBox.Text = s.OverseasDns;
        _directDnsBox.Text = s.DirectDns;
        _testUrlBox.Text = s.NodeTestUrl;
        _customRouteBox.Text = s.CustomRouteYaml;
        _ = RefreshLogsAsync();
    }

    private async Task SaveAllAsync()
    {
        var port = (ushort)Math.Clamp(double.IsNaN(_portBox.Value) ? 7890 : (int)_portBox.Value, 1024, 65535);
        var dnsMode = DnsModes[Math.Clamp(_dnsModeCombo.SelectedIndex, 0, DnsModes.Length - 1)].Key;
        var patch = new JsonObject
        {
            ["mixed_port"] = port,
            ["allow_lan"] = _lanSwitch.IsOn,
            ["ipv6_enabled"] = _ipv6Switch.IsOn,
            ["autostart"] = _autostartSwitch.IsOn,
            ["vpn_dns_mode"] = dnsMode,
            ["node_dns"] = _nodeDnsBox.Text.Trim(),
            ["overseas_dns"] = _overseasDnsBox.Text.Trim(),
            ["direct_dns"] = _directDnsBox.Text.Trim(),
            ["node_test_url"] = _testUrlBox.Text.Trim(),
            ["custom_route_yaml"] = _customRouteBox.Text,
        };
        await _client.UpdateSettingsAsync(patch);
    }

    public async Task RefreshLogsAsync()
    {
        var entries = await _client.GetLogsAsync(100);
        _logsBox.Text = string.Join(Environment.NewLine, entries.Select(e => $"[{e.Level}] {e.Message}"));
    }
}
