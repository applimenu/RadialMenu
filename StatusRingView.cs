using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Drawing = System.Drawing;

namespace RadialMenu1;

internal enum StatusArc { Cpu, Audio, Battery, Timer, Gpu, Memory, Disk, Network }

internal sealed class StatusRingView : FrameworkElement
{
    private const double Radius = 166;
    private static readonly Point Center = new(RadialView.ViewSize / 2, RadialView.ViewSize / 2);
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly string[] Labels = ["ЦП", "Звук", "Заряд", "Таймер", "ГП", "ОЗУ", "Диск", "Сеть"];
    private static readonly string[] CompactLabels = ["ЦП", "♪", "АКБ", "◷", "ГП", "ОЗУ", "SSD", "↕"];
    private sealed class ArcLayout(StatusArc metric, double angle, double halfSpan, Geometry label, Rect bounds)
    {
        public StatusArc Metric { get; } = metric;
        public double Angle { get; } = angle;
        public double HalfSpan { get; } = halfSpan;
        public Geometry Label { get; } = label;
        public Rect Bounds { get; } = bounds;
        public Geometry Track { get; } = Arc(angle - halfSpan, angle + halfSpan);
        public ArcSegment Tip { get; } = new() { Size = new Size(Radius, Radius), SweepDirection = SweepDirection.Clockwise };
        public PathGeometry Progress { get; } = new();
    }
    private sealed record LabelInk(Brush Text, Brush Surface, Brush ActiveSurface, Pen Edge);
    private ArcLayout[] _layout = [];
    private Dictionary<StatusArc, Color> _screenColors = new();
    private readonly LabelInk[] _labels = new LabelInk[Labels.Length];
    private readonly double[] _levels = new double[Labels.Length], _highlights = new double[Labels.Length];
    private readonly double[] _targets = new double[Labels.Length], _from = new double[Labels.Length], _elapsed = new double[Labels.Length];
    private double _scale = 1;
    private bool _glow;
    private StatusMetrics _metrics;
    private MenuPalette? _palette;
    private StatusReadings _status;
    private CountdownStatus _countdown;
    private StatusArc? _hover;
    private Pen? _trackInk, _levelInk, _activeInk, _haloInk, _badgeAccent;

    public StatusRingView()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
    }

    internal void ApplyPalette(MenuPalette palette)
    {
        if (_palette == palette) return;
        _palette = palette;
        _trackInk = MakePen(palette.Text, 75, 2);
        _levelInk = MakePen(MenuPalette.Blend(palette.Accent, palette.Text, .14), 250, 3);
        _activeInk = MakePen(palette.Accent, 155, 3.6);
        _badgeAccent = MakePen(palette.Focus, 175, 1);
        _haloInk = MakePen(palette.Accent, 46, 7);
        RefreshInk();
        InvalidateVisual();
    }

    internal bool Contains(StatusArc metric) => (_metrics & Mask(metric)) != 0;

    internal void SetScale(double scale)
    {
        if (_scale == scale) return;
        _scale = scale;
        BuildLayout();
    }

    internal void Configure(StatusMetrics metrics)
    {
        metrics &= StatusMetrics.All;
        if (_metrics == metrics) return;
        _metrics = metrics;
        BuildLayout();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        BuildLayout();
    }

    private void BuildLayout()
    {
        var arcs = Enum.GetValues<StatusArc>().Where(Contains).ToArray();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _layout = new ArcLayout[arcs.Length];
        for (int i = 0; i < arcs.Length; i++)
        {
            double step = Math.Tau / arcs.Length;
            double angle = (i + 0.5) * step - Math.PI / 2;
            double half = Math.Min(Math.PI * 33 / 180, step * 0.40);
            double font = Math.Clamp(12 / Math.Sqrt(_scale), 12, 20);
            var label = LayoutLabel(arcs[i], angle, step, dpi, font, out var badge);
            var arc = new ArcLayout(arcs[i], angle, half, label, badge);
            var figure = new PathFigure { StartPoint = Polar(Radius, angle - half), IsFilled = false };
            figure.Segments.Add(arc.Tip);
            arc.Progress.Figures.Add(figure);
            arc.Tip.Point = Polar(Radius, angle - half + 2 * half * _levels[(int)arc.Metric]);
            _layout[i] = arc;
        }
        InvalidateVisual();
    }

    private static Geometry LayoutLabel(StatusArc metric, double angle, double step, double dpi, double size, out Rect badge)
    {
        string title = Labels[(int)metric];
        for (int pass = 0; ; pass++)
        {
            var text = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface, size, Brushes.White, dpi);
            double width = Math.Ceiling(text.Width) + 10, height = Math.Ceiling(size * 1.5);
            // Keep every pill in its metric's angular sector, including the padding.
            int attempts = pass == 0 ? 1 : 17;
            for (int n = 0; n < attempts; n++)
            {
                double offset = ((n + 1) / 2) * .05 * (n % 2 == 0 ? 1 : -1);
                Point center = Polar(Radius + 26, angle + offset);
                badge = new Rect(Math.Clamp(center.X - width / 2, 2, RadialView.ViewSize - width - 2),
                    Math.Clamp(center.Y - height / 2, 2, RadialView.ViewSize - height - 2), width, height);
                bool Within(Point p) => Math.Abs(Math.IEEERemainder(Math.Atan2(p.X - Center.X, Center.Y - p.Y) - angle, Math.Tau)) < step * .49;
                if (size <= 8 || DistanceToCenter(badge) >= Radius + 5 && Within(badge.TopLeft) && Within(badge.TopRight) &&
                    Within(badge.BottomLeft) && Within(badge.BottomRight))
                {
                    var geometry = text.BuildGeometry(new Point());
                    var ink = geometry.Bounds;
                    geometry.Transform = new TranslateTransform(badge.X + (badge.Width - ink.Width) / 2 - ink.X,
                        badge.Y + (badge.Height - ink.Height) / 2 - ink.Y);
                    geometry.Freeze();
                    return geometry;
                }
            }
            title = CompactLabels[(int)metric];
            if (pass > 0) size -= 1;
        }
    }

    private static double DistanceToCenter(Rect bounds) =>
        (new Point(Math.Clamp(Center.X, bounds.Left, bounds.Right), Math.Clamp(Center.Y, bounds.Top, bounds.Bottom)) - Center).Length;

    private static StatusMetrics Mask(StatusArc metric) => (StatusMetrics)(1 << (int)metric);

    internal IEnumerable<(StatusArc Metric, Point Point)> SamplePoints(bool besideLabels)
    {
        foreach (var arc in _layout)
        {
            Point center = arc.Bounds.TopLeft + new Vector(arc.Bounds.Width / 2, arc.Bounds.Height / 2);
            if (!besideLabels)
            {
                yield return (arc.Metric, center);
                continue;
            }
            yield return (arc.Metric, center + new Vector(0, -arc.Bounds.Height / 2 - 6));
            yield return (arc.Metric, center + new Vector(0, arc.Bounds.Height / 2 + 6));
        }
    }

    internal static Dictionary<StatusArc, Color> ReadScreenColors(IEnumerable<(StatusArc Metric, Point Point)> samples)
    {
        var result = new Dictionary<StatusArc, Color>();
        try
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bitmap = new Drawing.Bitmap(5, 5);
            using var graphics = Drawing.Graphics.FromImage(bitmap);
            foreach (var group in samples.GroupBy(sample => sample.Metric))
            {
                int r = 0, g = 0, b = 0, count = 0;
                foreach (var sample in group)
                {
                    var bounds = new Drawing.Rectangle((int)Math.Round(sample.Point.X) - 2, (int)Math.Round(sample.Point.Y) - 2, 5, 5);
                    if (!screen.Contains(bounds)) continue;
                    graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size);
                    for (int y = 0; y < 5; y++)
                    for (int x = 0; x < 5; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        r += pixel.R; g += pixel.G; b += pixel.B; count++;
                    }
                }
                if (count > 0) result[group.Key] = Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException)
        {
        }
        return result;
    }

    internal void ApplyScreenColors(Dictionary<StatusArc, Color> colors, bool replace)
    {
        if ((!replace || colors.Count == _screenColors.Count) &&
            colors.All(pair => _screenColors.TryGetValue(pair.Key, out var old) && old == pair.Value)) return;
        if (replace) _screenColors = colors;
        else foreach (var (metric, color) in colors) _screenColors[metric] = color;
        RefreshInk();
        InvalidateVisual();
    }

    internal bool Update(StatusReadings status, CountdownStatus countdown)
    {
        _status = status;
        _countdown = countdown;
        bool changed = false;
        foreach (var arc in _layout)
        {
            int i = (int)arc.Metric;
            double next = Math.Clamp(Value(arc.Metric) ?? 0, 0, 1);
            if (next == _targets[i]) continue;
            _from[i] = _levels[i];
            _targets[i] = next;
            _elapsed[i] = 0;
            changed = true;
        }
        return changed;
    }

    internal void Highlight(StatusArc? metric) => _hover = metric;

    internal bool Advance(double dt, bool animate)
    {
        if (Visibility != Visibility.Visible) return false;
        bool redraw = _glow != animate, busy = false;
        _glow = animate;
        double hoverBlend = animate ? 1 - Math.Exp(-dt * 25) : 1;
        foreach (var arc in _layout)
        {
            int i = (int)arc.Metric;
            double level = _targets[i];
            double focus = _hover == arc.Metric || arc.Metric == StatusArc.Timer && _countdown.State == CountdownState.Finished ? 1 : 0;
            if (_levels[i] != level)
            {
                const double duration = .34;
                _elapsed[i] += dt;
                double t = animate ? Math.Min(1, _elapsed[i] / duration) : 1;
                double eased = t * t * (3 - 2 * t);
                _levels[i] = t == 1 ? level : _from[i] + (level - _from[i]) * eased;
                arc.Tip.Point = Polar(Radius, arc.Angle + (2 * _levels[i] - 1) * arc.HalfSpan);
                redraw = true;
            }
            redraw |= Approach(ref _highlights[i], focus, hoverBlend);
            busy |= _levels[i] != level || _highlights[i] != focus;
        }
        if (redraw) InvalidateVisual();
        return busy;
    }

    private static bool Approach(ref double value, double target, double blend)
    {
        if (value == target) return false;
        value += (target - value) * blend;
        if (Math.Abs(target - value) < .006) value = target;
        return true;
    }

    internal string Caption(StatusArc metric) => metric switch
    {
        StatusArc.Cpu => "Процессор\n" + Percentage(_status.Cpu),
        StatusArc.Audio => "Громкость\n" + (_status.Muted ? "Без звука" : Percentage(_status.Volume)),
        StatusArc.Battery => _status.Battery is not { } battery ? "Заряд\nНет данных" :
            "Заряд\n" + Percentage(battery) + (_status.OnAc ? " · сеть" : ""),
        StatusArc.Gpu => "Видеокарта\n" + Percentage(_status.Gpu),
        StatusArc.Memory => "Память\n" + Percentage(_status.Memory),
        StatusArc.Disk => "Системный диск\n" + Percentage(_status.Disk),
        StatusArc.Network => "Сеть\n" + NetworkRate(_status.NetworkBytesPerSecond),
        _ => _countdown.State switch
        {
            CountdownState.Running => $"Таймер\n{_countdown.Clock} · стоп",
            CountdownState.Finished => "Таймер\nИстёк",
            _ => $"Таймер\n{_countdown.Clock} · старт"
        }
    };

    private static string Percentage(double? value) => value is { } fraction ? $"{fraction:P0}" : "Нет данных";
    private static string NetworkRate(double? value) => value switch
    {
        >= 1024 * 1024 => $"{value.Value / (1024 * 1024):0.#} МБ/с",
        { } rate => $"{rate / 1024:0.#} КБ/с",
        _ => "Нет данных"
    };

    internal StatusArc? At(Point point)
    {
        if (Visibility != Visibility.Visible) return null;
        var offset = point - Center;
        double angle = Math.Atan2(offset.X, -offset.Y);
        bool nearTrack = offset.LengthSquared is >= (Radius - 7) * (Radius - 7) and <= (Radius + 16) * (Radius + 16);
        foreach (var arc in _layout)
        {
            var bounds = arc.Bounds;
            bounds.Inflate(1.5, 1.5);
            if (bounds.Contains(point) || nearTrack && Math.Abs(Math.IEEERemainder(angle - arc.Angle, Math.Tau)) <= arc.HalfSpan)
                return arc.Metric;
        }
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_palette == null) return;
        foreach (var arc in _layout)
        {
            int i = (int)arc.Metric;
            var ink = _labels[i];
            dc.DrawGeometry(null, _trackInk, arc.Track);
            var badge = arc.Bounds;
            double corner = badge.Height * .5;
            dc.DrawRoundedRectangle(ink.Surface, ink.Edge, badge, corner, corner);
            double hover = _highlights[i];
            if (hover > 0)
            {
                dc.PushOpacity(hover * .8);
                if (_glow) dc.DrawGeometry(null, _haloInk, arc.Track);
                dc.DrawGeometry(null, _activeInk, arc.Track);
                dc.DrawRoundedRectangle(ink.ActiveSurface, _badgeAccent, badge, corner, corner);
                dc.Pop();
            }
            double value = _levels[i];
            if (value > 0)
            {
                Geometry progress = value >= 1 ? arc.Track : arc.Progress;
                if (_glow) dc.DrawGeometry(null, _haloInk, progress);
                dc.DrawGeometry(null, _levelInk, progress);
            }
            dc.DrawGeometry(ink.Text, null, arc.Label);
        }
    }

    private void RefreshInk()
    {
        if (_palette is not { } palette) return;
        foreach (var metric in Enum.GetValues<StatusArc>())
        {
            Color background = _screenColors.GetValueOrDefault(metric, palette.Panel);
            Color text = LabelColor(background, palette.Text);
            Color contrast = MenuPalette.Contrast(text, Colors.Black) >= 4.5 ? Colors.Black : Colors.White;
            Color surface = MenuPalette.Blend(MenuPalette.Blend(background, palette.Disc, .24), contrast, .52);
            Color tint = MenuPalette.Blend(surface, palette.Accent, .16);
            var foreground = new SolidColorBrush(text);
            var resting = new LinearGradientBrush(MenuPalette.Blend(surface, text, .075) with { A = 239 },
                surface with { A = 227 }, 85);
            var active = new SolidColorBrush(tint);
            foreground.Freeze();
            resting.Freeze();
            active.Freeze();
            _labels[(int)metric] = new(foreground, resting, active, MakePen(text, 63, .65));
        }
    }

    internal static Color LabelColor(Color background, Color preferred)
    {
        if (MenuPalette.Contrast(background, preferred) >= 4.5) return preferred;
        return MenuPalette.Contrast(background, Colors.Black) >= MenuPalette.Contrast(background, Colors.White) ? Colors.Black : Colors.White;
    }

    private double? Value(StatusArc metric) => metric switch
    {
        StatusArc.Cpu => _status.Cpu,
        StatusArc.Audio => _status.Muted ? 0 : _status.Volume,
        StatusArc.Battery => _status.Battery,
        StatusArc.Timer => _countdown.Fraction,
        StatusArc.Gpu => _status.Gpu,
        StatusArc.Memory => _status.Memory,
        StatusArc.Disk => _status.Disk,
        _ => _status.Network
    };

    private static Point Polar(double radius, double angle) =>
        Center + new Vector(radius * Math.Sin(angle), -radius * Math.Cos(angle));

    private static Pen MakePen(Color color, byte alpha, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color with { A = alpha }), thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static Geometry Arc(double start, double end)
    {
        var shape = new StreamGeometry();
        using (var context = shape.Open())
        {
            context.BeginFigure(Polar(Radius, start), false, false);
            context.ArcTo(Polar(Radius, end), new Size(Radius, Radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        shape.Freeze();
        return shape;
    }
}
