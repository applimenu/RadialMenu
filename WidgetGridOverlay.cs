using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PixelRect = System.Drawing.Rectangle;

namespace RadialMenu1;

internal sealed class WidgetGridOverlay : Window
{
    private readonly Canvas _canvas;
    private readonly Border _slot, _warning;
    private (PixelRect, PixelRect?)? _last;
    internal WidgetSnapGrid Grid { get; }

    internal WidgetGridOverlay(WidgetSnapGrid grid, Color accent)
    {
        Grid = grid;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = ShowActivated = Focusable = IsHitTestVisible = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = grid.WorkArea;
        _canvas = new Canvas { Width = area.Width, Height = area.Height, ClipToBounds = true };
        Content = new Viewbox { Stretch = Stretch.Fill, Child = _canvas };
        var drawing = new DrawingGroup();
        double scale = grid.Step / (double)WidgetSnapGrid.PitchDip;
        var rim = new Pen(new SolidColorBrush(Color.FromArgb(45, 238, 245, 255)), scale);
        var fill = new SolidColorBrush(Color.FromArgb(9, 230, 239, 255));
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, area.Width, area.Height));
            for (int row = 0; row < grid.Rows; row++)
                for (int col = 0; col < grid.Columns; col++)
                    dc.DrawRoundedRectangle(fill, rim, Local(new(col, row, 1, 1)), 14 * scale, 14 * scale);
        }
        drawing.Freeze();
        _canvas.Children.Add(new Image { Width = area.Width, Height = area.Height, Source = new DrawingImage(drawing) });
        _warning = Highlight(Color.FromRgb(234, 155, 118), scale);
        _slot = Highlight(accent, scale);
        SourceInitialized += (_, _) => StatusWidgets.MakeOverlayTransparent(new WindowInteropHelper(this).Handle);
        StatusWidgets.PlaceWindow(this, area);
    }

    private Border Highlight(Color tint, double scale)
    {
        Color Light(byte alpha) => Color.FromArgb(alpha, tint.R, tint.G, tint.B);
        var border = new Border
        {
            Visibility = Visibility.Hidden, BorderThickness = new Thickness(2.5 * scale),
            CornerRadius = new CornerRadius(17 * scale), BorderBrush = new SolidColorBrush(Light(48)),
            Child = new Border
            {
                CornerRadius = new CornerRadius(15 * scale), BorderThickness = new Thickness(1.4 * scale),
                BorderBrush = new SolidColorBrush(Light(195)),
                Background = new LinearGradientBrush(Color.FromArgb(65, 255, 255, 255), Light(30), 75)
            },
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform()
        };
        _canvas.Children.Add(border);
        return border;
    }

    private Rect Local(PixelRect cells)
    {
        var r = Grid.Bounds(cells);
        return new(r.X - Grid.WorkArea.X, r.Y - Grid.WorkArea.Y, r.Width, r.Height);
    }

    internal void Preview(PixelRect wanted, PixelRect? free)
    {
        if (_last == (wanted, free)) return;
        _last = (wanted, free);
        Paint(_warning, free == wanted ? null : wanted);
        Paint(_slot, free);
    }

    private void Paint(Border border, PixelRect? cells)
    {
        if (cells == null) { border.Visibility = Visibility.Hidden; return; }
        var rect = Local(cells.Value);
        rect.Inflate(Grid.Gap * 0.32, Grid.Gap * 0.32);
        Canvas.SetLeft(border, rect.X);
        Canvas.SetTop(border, rect.Y);
        border.Width = rect.Width;
        border.Height = rect.Height;
        border.Visibility = Visibility.Visible;
        if (!ConfigStore.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation) return;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var pulse = new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(115)) { EasingFunction = ease };
        border.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        border.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        border.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, pulse.Duration) { EasingFunction = ease });
    }
}
