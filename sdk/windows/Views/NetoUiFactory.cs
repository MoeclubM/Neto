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

    public static ScrollViewer CreateCenteredScrollViewer(FrameworkElement child, bool centerVertically = false)
    {
        child.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (centerVertically)
        {
            child.VerticalAlignment = VerticalAlignment.Center;
        }
        var viewportHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        viewportHost.Children.Add(child);
        var scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = viewportHost,
        };
        if (centerVertically)
        {
            scroll.SizeChanged += (_, _) =>
            {
                viewportHost.MinHeight = Math.Max(0, scroll.ViewportHeight - 8);
            };
        }
        return scroll;
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

    public static Grid CreateInlineCardHeader(string title, FrameworkElement? rightElement = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(tb);
        if (rightElement != null)
        {
            Grid.SetColumn(rightElement, 1);
            grid.Children.Add(rightElement);
        }
        return grid;
    }

    public static Grid CreateSettingRow(string title, FrameworkElement control, string? subtitle = null)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
        });
        if (!string.IsNullOrEmpty(subtitle))
        {
            left.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
            });
        }
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(left);
        grid.Children.Add(control);
        return grid;
    }

    public static Button CreateNavEntryRow(string title, TextBlock summaryBlock, Action onClick)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        });
        summaryBlock.FontSize = 12;
        summaryBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        summaryBlock.Foreground = BrushResource("TextFillColorSecondaryBrush", Colors.Gray);
        left.Children.Add(summaryBlock);
        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(left);
        grid.Children.Add(chevron);

        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 9, 14, 9),
            Content = grid,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static Grid CreateSubpageHeader(string title, Action onBack, FrameworkElement? rightElement = null)
    {
        var grid = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var backInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        backInner.Children.Add(new FontIcon { Glyph = "\uE72B", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        backInner.Children.Add(new TextBlock { Text = "返回", VerticalAlignment = VerticalAlignment.Center });
        var backBtn = new Button
        {
            Content = backInner,
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        backBtn.Click += (_, _) => onBack();

        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(titleTb, 1);
        grid.Children.Add(backBtn);
        grid.Children.Add(titleTb);
        if (rightElement != null)
        {
            rightElement.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(rightElement, 2);
            grid.Children.Add(rightElement);
        }
        return grid;
    }

    public static Border CreateTagBadge(string text, Color? tint = null)
    {
        var fg = tint.HasValue
            ? new SolidColorBrush(tint.Value)
            : BrushResource("TextFillColorSecondaryBrush", Colors.Gray);
        var bg = tint.HasValue
            ? new SolidColorBrush(Color.FromArgb(28, tint.Value.R, tint.Value.G, tint.Value.B))
            : BrushResource("SubtleFillColorSecondaryBrush", Color.FromArgb(22, 128, 128, 128));
        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = fg,
            },
        };
    }

    public static Color LatencyColor(ulong? latencyMs, bool hasError)
    {
        if (hasError) return Color.FromArgb(255, 216, 82, 76);
        if (!latencyMs.HasValue) return Colors.Gray;
        if (latencyMs.Value < 160) return Color.FromArgb(255, 42, 168, 118);
        if (latencyMs.Value < 380) return Color.FromArgb(255, 218, 152, 38);
        return Color.FromArgb(255, 216, 82, 76);
    }
}

public sealed class NetoDualSeriesChart
{
    public Border Root { get; }
    private readonly TextBlock _titleLabel;
    private readonly TextBlock _header;
    private readonly Canvas _canvas;
    private readonly Line _baseLine;
    private readonly Polygon _primaryFill;
    private readonly Polyline _primaryLine;
    private readonly Polyline _secondaryLine;
    private IReadOnlyList<double> _primary = [];
    private IReadOnlyList<double> _secondary = [];

    public NetoDualSeriesChart(Color primaryColor, Color? secondaryColor = null, string title = "", FrameworkElement? actionElement = null)
    {
        _titleLabel = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible,
        };
        _header = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = NetoUiFactory.BrushResource("TextFillColorSecondaryBrush", Colors.Gray),
        };
        _canvas = new Canvas { Height = 56, HorizontalAlignment = HorizontalAlignment.Stretch };
        _baseLine = new Line
        {
            Stroke = NetoUiFactory.BrushResource("DividerStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128)),
            StrokeThickness = 1,
        };
        _primaryFill = new Polygon
        {
            Fill = new SolidColorBrush(Color.FromArgb(28, primaryColor.R, primaryColor.G, primaryColor.B)),
        };
        _primaryLine = new Polyline
        {
            Stroke = new SolidColorBrush(primaryColor),
            StrokeThickness = 1.75,
        };
        _secondaryLine = new Polyline
        {
            Stroke = new SolidColorBrush(secondaryColor ?? primaryColor),
            StrokeThickness = 1.75,
        };
        _canvas.Children.Add(_baseLine);
        _canvas.Children.Add(_primaryFill);
        _canvas.Children.Add(_secondaryLine);
        _canvas.Children.Add(_primaryLine);
        _canvas.SizeChanged += (_, _) => Redraw();

        var topRow = new Grid { ColumnSpacing = 8 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.HorizontalAlignment = string.IsNullOrEmpty(title) ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Grid.SetColumn(_header, 1);
        topRow.Children.Add(_titleLabel);
        topRow.Children.Add(_header);
        if (actionElement != null)
        {
            Grid.SetColumn(actionElement, 2);
            topRow.Children.Add(actionElement);
        }

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(topRow);
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
        _baseLine.X1 = 0;
        _baseLine.X2 = w;
        _baseLine.Y1 = h - 2;
        _baseLine.Y2 = h - 2;
        var peak = 1.0;
        foreach (var v in _primary) if (v > peak) peak = v;
        foreach (var v in _secondary) if (v > peak) peak = v;
        FillPoints(_primaryLine, _primaryFill, _primary, w, h, peak);
        FillPoints(_secondaryLine, null, _secondary, w, h, peak);
    }

    private static void FillPoints(Polyline line, Polygon? area, IReadOnlyList<double> vals, double w, double h, double peak)
    {
        line.Points.Clear();
        area?.Points.Clear();
        if (vals.Count < 2) return;
        area?.Points.Add(new Point(0, h - 2));
        for (var i = 0; i < vals.Count; i++)
        {
            var x = (double)i / (vals.Count - 1) * w;
            var ratio = Math.Clamp(vals[i] / peak, 0.0, 1.0);
            var y = (h - 4.0) * (1.0 - ratio) + 2.0;
            var pt = new Point(x, y);
            line.Points.Add(pt);
            area?.Points.Add(pt);
        }
        area?.Points.Add(new Point(w, h - 2));
    }
}
