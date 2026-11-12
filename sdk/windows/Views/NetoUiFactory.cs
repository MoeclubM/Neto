using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Neto.Sdk.Windows.Views;

public static class NetoUiFactory
{
    public static Brush BrushResource(string key, Color fallback)
    {
        if (Application.Current?.Resources != null &&
            Application.Current.Resources.TryGetValue(key, out var val) &&
            val is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(fallback);
    }

    public static Style? StyleResource(string key)
    {
        if (Application.Current?.Resources != null &&
            Application.Current.Resources.TryGetValue(key, out var val) &&
            val is Style s)
        {
            return s;
        }
        return null;
    }

    public static Border CreateCard(UIElement child, Thickness? padding = null)
    {
        return new Border
        {
            Background = BrushResource("CardBackgroundFillColorDefaultBrush", Color.FromArgb(18, 128, 128, 128)),
            BorderBrush = BrushResource("CardStrokeColorDefaultBrush", Color.FromArgb(36, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = padding ?? new Thickness(14, 12, 14, 12),
            Child = child,
        };
    }

    public static TextBlock CreateSectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            Margin = new Thickness(2, 4, 0, 2),
        };
    }

    public static void ApplyPrimaryButton(Button btn, bool primary)
    {
        btn.Style = primary ? StyleResource("AccentButtonStyle") : StyleResource("DefaultButtonStyle");
    }
}

public sealed class NetoDualSeriesChart
{
    public Border Root { get; }
    private readonly TextBlock _header;
    private readonly Canvas _canvas;
    private readonly Polyline _primaryLine;
    private readonly Polyline _secondaryLine;
    private IReadOnlyList<double> _primary = [];
    private IReadOnlyList<double> _secondary = [];

    public NetoDualSeriesChart(Color primaryColor, Color? secondaryColor = null)
    {
        _header = new TextBlock
        {
            FontSize = 12,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        _canvas = new Canvas { Height = 64, HorizontalAlignment = HorizontalAlignment.Stretch };
        _primaryLine = new Polyline
        {
            Stroke = new SolidColorBrush(primaryColor),
            StrokeThickness = 2,
        };
        _secondaryLine = new Polyline
        {
            Stroke = new SolidColorBrush(secondaryColor ?? primaryColor),
            StrokeThickness = 2,
        };
        _canvas.Children.Add(_secondaryLine);
        _canvas.Children.Add(_primaryLine);
        _canvas.SizeChanged += (_, _) => Redraw();

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(_header);
        stack.Children.Add(_canvas);
        Root = NetoUiFactory.CreateCard(stack, new Thickness(12, 10, 12, 10));
    }

    public void Update(string header, IReadOnlyList<double> primary, IReadOnlyList<double>? secondary = null)
    {
        _header.Text = header;
        _primary = primary;
        _secondary = secondary ?? [];
        Redraw();
    }

    private void Redraw()
    {
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (w <= 4 || h <= 4) return;
        var peak = 1.0;
        foreach (var v in _primary) if (v > peak) peak = v;
        foreach (var v in _secondary) if (v > peak) peak = v;
        FillPoints(_primaryLine, _primary, w, h, peak);
        FillPoints(_secondaryLine, _secondary, w, h, peak);
    }

    private static void FillPoints(Polyline line, IReadOnlyList<double> vals, double w, double h, double peak)
    {
        line.Points.Clear();
        if (vals.Count < 2) return;
        for (var i = 0; i < vals.Count; i++)
        {
            var x = (double)i / (vals.Count - 1) * w;
            var ratio = Math.Clamp(vals[i] / peak, 0.0, 1.0);
            var y = (h - 4.0) * (1.0 - ratio) + 2.0;
            line.Points.Add(new Point(x, y));
        }
    }
}
