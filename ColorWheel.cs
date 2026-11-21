using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RadialMenu1;

public sealed class ColorWheel : FrameworkElement
{
    private const int BitmapSize = 176;
    private static readonly Lazy<BitmapSource> Palette = new(BuildPalette);
    private static readonly Pen MarkerBorder = new(Brushes.Black, 3);
    private static readonly Pen MarkerRim = new(Brushes.White, 1.5);
    private double _hue;
    private double _saturation;

    public event Action? SelectionChanged;

    public ColorWheel()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    public Color ColorAtBrightness(double brightness) => ToRgb(_hue, _saturation, brightness);

    public void SelectColor(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double chroma = max - min;
        _saturation = max > 0 ? chroma / max : 0;
        if (chroma > 0)
        {
            double sector = max == r ? (g - b) / chroma : max == g ? 2 + (b - r) / chroma : 4 + (r - g) / chroma;
            _hue = (sector / 6 + 1) % 1;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double radius = Radius;
        if (radius <= 0) return;
        Point center = Center;
        dc.DrawImage(Palette.Value, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
        double angle = _hue * Math.Tau;
        Point marker = center + new Vector(Math.Cos(angle), Math.Sin(angle)) * (radius * _saturation);
        dc.DrawEllipse(null, MarkerBorder, marker, 6, 6);
        dc.DrawEllipse(null, MarkerRim, marker, 6, 6);
        if (IsKeyboardFocused) dc.DrawEllipse(null, MarkerRim, center, radius + 2, radius + 2);
    }

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);
    private double Radius => Math.Min(ActualWidth, ActualHeight) / 2 - 8;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Radius <= 0) return;
        Focus();
        if (CaptureMouse()) SelectPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) SelectPoint(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        SelectPoint(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: _hue = (_hue + 0.98) % 1; break;
            case Key.Right: _hue = (_hue + 0.02) % 1; break;
            case Key.Up: _saturation = Math.Min(1, _saturation + 0.05); break;
            case Key.Down: _saturation = Math.Max(0, _saturation - 0.05); break;
            default: base.OnKeyDown(e); return;
        }
        SelectionChanged?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        InvalidateVisual();
    }

    private void SelectPoint(Point point)
    {
        Vector offset = point - Center;
        _hue = (Math.Atan2(offset.Y, offset.X) / Math.Tau + 1) % 1;
        _saturation = Math.Clamp(offset.Length / Radius, 0, 1);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    private static BitmapSource BuildPalette()
    {
        var pixels = new byte[BitmapSize * BitmapSize * 4];
        double radius = BitmapSize / 2.0;
        for (int y = 0; y < BitmapSize; y++)
        {
            for (int x = 0; x < BitmapSize; x++)
            {
                double dx = x + 0.5 - radius, dy = y + 0.5 - radius;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                var color = ToRgb((Math.Atan2(dy, dx) / Math.Tau + 1) % 1, Math.Min(1, distance / radius), 1);
                int index = (y * BitmapSize + x) * 4;
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = (byte)(255 * Math.Clamp(radius + 0.3 - distance, 0, 1));
            }
        }
        var image = BitmapSource.Create(BitmapSize, BitmapSize, 96, 96, PixelFormats.Bgra32, null, pixels, BitmapSize * 4);
        image.Freeze();
        return image;
    }

    private static Color ToRgb(double hue, double saturation, double value)
    {
        double c = value * saturation, h = hue * 6;
        double x = c * (1 - Math.Abs(h % 2 - 1)), m = value - c;
        (double r, double g, double b) = (int)h switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x),
            3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
