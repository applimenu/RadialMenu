using System.Windows;
using System.Windows.Media;

namespace RadialMenu1;

internal sealed class WidgetGraph : FrameworkElement
{
    private readonly double?[] _samples = new double?[38];
    private int _count, _next;
    private Brush? _track, _area, _dot;
    private Pen? _line;

    internal bool AutoScale { get; set; }

    internal void AddSample(double? value)
    {
        _samples[_next] = value;
        _next = (_next + 1) % _samples.Length;
        _count = Math.Min(_count + 1, _samples.Length);
        if (IsVisible) InvalidateVisual();
    }

    internal void ApplyPalette(Color accent, Color outline)
    {
        _track = new SolidColorBrush(Color.FromArgb(48, outline.R, outline.G, outline.B));
        _area = new LinearGradientBrush(Color.FromArgb(65, accent.R, accent.G, accent.B),
            Color.FromArgb(8, accent.R, accent.G, accent.B), 90);
        _dot = new SolidColorBrush(accent);
        _line = new Pen(_dot, 1.15) { LineJoin = PenLineJoin.Round };
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        double width = ActualWidth, height = ActualHeight;
        if (_line == null || width <= 6 || height <= 4) return;
        context.DrawRoundedRectangle(_track, null, new Rect(0, 0, width, height), 5, 5);
        if (_count == 0) return;

        double maximum = AutoScale ? Math.Max(1024, _samples.Max(value => value ?? 0)) : 1;
        int start = (_next - _count + _samples.Length) % _samples.Length;
        double baseline = height - 3;
        var segment = new List<Point>(_count);
        Point? newest = null;
        double step = (width - 8) / (_samples.Length - 1);
        for (int i = 0; i < _count; i++)
        {
            var sample = _samples[(start + i) % _samples.Length];
            if (sample is not { } value)
            {
                DrawSegment(context, segment, baseline);
                segment.Clear();
                newest = null;
                continue;
            }

            var point = new Point(width - 4 - (_count - i - 1) * step,
                baseline - Math.Clamp(value / maximum, 0, 1) * (baseline - 3));
            segment.Add(point);
            newest = point;
        }
        DrawSegment(context, segment, baseline);
        if (newest is { } end) context.DrawEllipse(_dot, null, end, 1.8, 1.8);
    }

    private void DrawSegment(DrawingContext context, List<Point> points, double baseline)
    {
        if (points.Count < 2) return;
        var fill = new StreamGeometry();
        using (var path = fill.Open())
        {
            path.BeginFigure(new Point(points[0].X, baseline), true, true);
            path.PolyLineTo(points, false, false);
            path.LineTo(new Point(points[^1].X, baseline), false, false);
        }
        context.DrawGeometry(_area, null, fill);

        var line = new StreamGeometry();
        using (var path = line.Open())
        {
            path.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Count; i++) path.LineTo(points[i], true, false);
        }
        context.DrawGeometry(null, _line, line);
    }
}
