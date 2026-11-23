using System.Windows;
using System.Windows.Media;

namespace RadialMenu1;

internal sealed class RadialEffects : FrameworkElement
{
    private const double BurstTime = 0.265;
    private static readonly Point Center = new(RadialView.ViewSize / 2, RadialView.ViewSize / 2);
    private double _burst = double.NegativeInfinity, _now;
    private MenuStyle _style;
    private Brush _particles = Brushes.Transparent;
    private Pen? _wave, _spark;
    private double _direction;
    private int _count;
    private bool _drawing;
    internal bool Enabled { get; set; }
    internal bool HasBurst => Enabled && _now - _burst < BurstTime;

    public RadialEffects() => IsHitTestVisible = false;

    internal void Configure(MenuStyle style, Color accent, int count)
    {
        _style = style;
        if (_particles is not SolidColorBrush brush || brush.Color != accent)
        {
            _particles = new SolidColorBrush(accent);
            _particles.Freeze();
            _wave = Stroke(_particles, 1.5);
            _spark = Stroke(_particles, 1.1);
        }
        _count = count;
        if (!Enabled) Reset();
    }

    internal void Burst(Point point, double now)
    {
        if (!Enabled || _wave == null) return;
        _direction = Math.Atan2(point.Y - Center.Y, point.X - Center.X);
        _now = _burst = now;
    }

    internal void Reset()
    {
        _burst = double.NegativeInfinity;
        _drawing = false;
        InvalidateVisual();
    }

    internal bool Tick(double now)
    {
        _now = now;
        bool active = HasBurst;
        if (active || _drawing) InvalidateVisual();
        return _drawing = active;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Enabled) return;
        double burst = (_now - _burst) / BurstTime;
        if (burst is >= 0 and < 1) DrawBurst(dc, burst);
    }

    private void DrawBurst(DrawingContext dc, double time)
    {
        double spread = 1 - (1 - time) * (1 - time);
        double radius = 31 + 114 * spread;
        double fade = Math.Pow(1 - time, 1.35);
        dc.PushOpacity(fade * .64);
        switch (_style)
        {
            case MenuStyle.Honeycomb:
                DrawPolygon(dc, Center, radius, 6, Math.PI / 6, null, _wave);
                break;
            case MenuStyle.Ring:
                int divisions = Math.Max(2, _count);
                for (int i = 0; i < divisions; i++)
                {
                    double angle = -Math.PI / 2 + i * 2 * Math.PI / divisions;
                    double half = Math.PI / divisions * 0.92;
                    DrawArc(dc, radius, angle - half, angle + half, _wave!);
                }
                break;
            case MenuStyle.Circles:
                for (int i = 0; i < 9; i++)
                    dc.DrawEllipse(null, _wave, Polar(radius, i * Math.Tau / 9), 3 + time * 7, 3 + time * 7);
                break;
            default:
                dc.DrawEllipse(null, _wave, Center, radius, radius);
                break;
        }
        for (int i = 0; i < 10; i++)
        {
            double angle = _direction + i * Math.Tau / 10 + 0.09 * Math.Sin(i * 2.9);
            Point point = Polar(radius * (0.82 + 0.035 * (i % 6)) + 5, angle);
            double size = (1.6 + i % 3 * 0.45) * (1 - time * 0.54);
            if (_style == MenuStyle.Honeycomb)
                DrawPolygon(dc, point, size, 4, 0, _particles, null);
            else if (_style == MenuStyle.Ring)
                dc.DrawLine(_spark!, point, Polar((point - Center).Length + size * 2.1, angle));
            else
                dc.DrawEllipse(_particles, null, point, size, size);
        }
        dc.Pop();
    }

    private static Point Polar(double radius, double angle) =>
        Center + new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius);

    private static void DrawArc(DrawingContext dc, double radius, double start, double end, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(radius, start), false, false);
            ctx.ArcTo(Polar(radius, end), new Size(radius, radius), 0, end - start > Math.PI, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawPolygon(DrawingContext dc, Point center, double radius, int sides, double orientation, Brush? fill, Pen? pen)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            Point Vertex(int i) => center + (Polar(radius, orientation + Math.Tau * i / sides) - Center);
            ctx.BeginFigure(Vertex(0), fill != null, true);
            for (int i = 1; i < sides; i++) ctx.LineTo(Vertex(i), true, false);
        }
        dc.DrawGeometry(fill, pen, geometry);
    }

    private static Pen Stroke(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
