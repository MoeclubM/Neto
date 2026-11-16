using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;

namespace Neto.Sdk.Windows.Views;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class ConnectionListItem : INotifyPropertyChanged
{
    private static readonly string[] Names =
        [nameof(Host), nameof(Download), nameof(Upload), nameof(DownloadSpeed),
         nameof(UploadSpeed), nameof(Outbound), nameof(Rule), nameof(Duration)];
    private readonly string[] _values = new string[8];
    public NetoActiveConnection Item { get; private set; }
    public bool IsClosed { get; private set; }
    public string Host => _values[0];
    public string Download => _values[1];
    public string Upload => _values[2];
    public string DownloadSpeed => _values[3];
    public string UploadSpeed => _values[4];
    public string Outbound => _values[5];
    public string Rule => _values[6];
    public string Duration => _values[7];
    public string CloseLabel => $"断开 {Item.Host}";
    public Visibility CloseVisibility => IsClosed ? Visibility.Collapsed : Visibility.Visible;
    public ICommand CloseCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public ConnectionListItem(NetoActiveConnection item, Action<ConnectionListItem> close)
    {
        Item = item;
        CloseCommand = new CloseAction(() => close(this));
    }

    public void Update(NetoActiveConnection item, bool isClosed, ulong nowMs)
    {
        var hostChanged = Item.Host != item.Host;
        Item = item;
        if (IsClosed != isClosed)
        {
            IsClosed = isClosed;
            PropertyChanged?.Invoke(this, new(nameof(CloseVisibility)));
        }
        if (hostChanged) PropertyChanged?.Invoke(this, new(nameof(CloseLabel)));
        var endMs = isClosed ? item.ClosedAtMs : nowMs;
        var values = new[]
        {
            $"{item.Host}  {item.Network.ToUpperInvariant()}",
            NetoFormat.FormatBytes(item.DownloadBytes), NetoFormat.FormatBytes(item.UploadBytes),
            NetoFormat.FormatSpeed(item.DownloadSpeedBps), NetoFormat.FormatSpeed(item.UploadSpeedBps),
            NetoFormat.FormatDisplayName(item.Outbound), item.Rule,
            NetoFormat.FormatDurationMs(endMs > item.StartedAtMs ? endMs - item.StartedAtMs : 0UL),
        };
        for (var i = 0; i < values.Length; i++)
        {
            if (_values[i] == values[i]) continue;
            _values[i] = values[i];
            PropertyChanged?.Invoke(this, new(Names[i]));
        }
    }

    private sealed class CloseAction(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
