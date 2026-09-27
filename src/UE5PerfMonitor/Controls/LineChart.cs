using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UE5PerfMonitor.Controls;

public sealed record ChartLine(string Name, IReadOnlyList<double?> Values, Color Color);

public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(nameof(Threshold), typeof(double?), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double? Threshold { get => (double?)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public IReadOnlyList<ChartLine> Lines { get; set; } = Array.Empty<ChartLine>();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 29, 43)), null, new Rect(0, 0, w, h));
        if (w < 80 || h < 70) return;
        var title = new FormattedText(Title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 13, Brush(Color.FromRgb(228, 237, 247)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(title, new Point(14, 10));
        var left = 42d; var right = w - 12; var top = 38d; var bottom = h - 24;
        var plotW = Math.Max(1, right - left); var plotH = Math.Max(1, bottom - top);
        var max = Math.Max(1, Maximum);
        for (var i = 0; i <= 4; i++)
        {
            var y = top + plotH * i / 4;
            dc.DrawLine(new Pen(Brush(Color.FromRgb(43, 55, 72)), 1), new Point(left, y), new Point(right, y));
            var label = new FormattedText((max * (4 - i) / 4).ToString("0.#", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, Brush(Color.FromRgb(137, 154, 173)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(4, y - 7));
        }
        if (Threshold is double threshold && threshold <= max)
        {
            var y = bottom - Math.Clamp(threshold / max, 0, 1) * plotH;
            var pen = new Pen(Brush(Color.FromRgb(255, 177, 88)), 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(pen, new Point(left, y), new Point(right, y));
        }
        foreach (var line in Lines)
        {
            var values = line.Values;
            if (values.Count < 2) continue;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var first = true;
                for (var i = 0; i < values.Count; i++)
                {
                    if (values[i] is not double value) { first = true; continue; }
                    var x = left + (values.Count == 1 ? 0 : (double)i / (values.Count - 1)) * plotW;
                    var y = bottom - Math.Clamp(value / max, 0, 1) * plotH;
                    if (first) { ctx.BeginFigure(new Point(x, y), false, false); first = false; }
                    else ctx.LineTo(new Point(x, y), true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(Brush(line.Color), 2), geometry);
        }
        if (Lines.Count > 1)
        {
            var x = 50d;
            foreach (var line in Lines)
            {
                dc.DrawEllipse(Brush(line.Color), null, new Point(x, h - 10), 3, 3);
                var legend = new FormattedText(line.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, Brush(Color.FromRgb(161, 177, 195)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(legend, new Point(x + 7, h - 17));
                x += legend.Width + 28;
            }
        }
        if (Unit.Length > 0)
        {
            var unit = new FormattedText(Unit, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, Brush(Color.FromRgb(111, 129, 149)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(unit, new Point(right - unit.Width, 10));
        }
    }

    private static SolidColorBrush Brush(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }
}
