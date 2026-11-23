using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RadialMenu1;

// Coordinates are desktop pixels in the window's DPI-awareness context.
// Only the monitor name and integer cell coordinates go into config.json.
internal sealed record WidgetSnapGrid(string Device, Rectangle WorkArea, int Step, int Gap)
{
    internal const int PitchDip = 132;
    internal const int GapDip = 12;
    internal int Columns => Math.Max(1, WorkArea.Width / Step);
    internal int Rows => Math.Max(1, WorkArea.Height / Step);
    private int Left => WorkArea.Left + (WorkArea.Width - Columns * Step + Gap) / 2;
    private int Top => WorkArea.Top + (WorkArea.Height - Rows * Step + Gap) / 2;

    internal static WidgetSnapGrid For(Screen screen)
    {
        var area = screen.WorkingArea;
        var monitor = MonitorFromPoint(new Point(area.Left, area.Top), 2);
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96d : 1;
        int step = Math.Clamp((int)Math.Round(PitchDip * scale), 1, Math.Max(1, Math.Min(area.Width, area.Height)));
        int gap = Math.Clamp((int)Math.Round(GapDip * scale), 0, step - 1);
        return new(screen.DeviceName, area, step, gap);
    }

    internal Rectangle Bounds(Rectangle cells) => new(Left + cells.X * Step, Top + cells.Y * Step,
        cells.Width * Step - Gap, cells.Height * Step - Gap);

    internal Rectangle At(Point topLeft, Size span) => Clamp(
        (int)Math.Round((topLeft.X - Left) / (double)Step),
        (int)Math.Round((topLeft.Y - Top) / (double)Step), span);

    internal Rectangle Clamp(int x, int y, Size span)
    {
        int width = Math.Clamp(span.Width, 1, Columns), height = Math.Clamp(span.Height, 1, Rows);
        return new(Math.Clamp(x, 0, Columns - width), Math.Clamp(y, 0, Rows - height), width, height);
    }

    internal Rectangle? NearestFree(Rectangle target, IReadOnlyList<Rectangle> occupied)
    {
        bool Free(Rectangle cells)
        {
            foreach (var other in occupied)
                if (cells.IntersectsWith(other)) return false;
            return true;
        }
        if (Free(target)) return target;
        Rectangle? nearest = null;
        long best = long.MaxValue;
        // Equal distances resolve top to bottom, then left to right.
        for (int y = 0; y <= Rows - target.Height; y++)
            for (int x = 0; x <= Columns - target.Width; x++)
            {
                long dx = x - target.X, dy = y - target.Y, distance = dx * dx + dy * dy;
                if (distance >= best) continue;
                var cells = new Rectangle(new Point(x, y), target.Size);
                if (!Free(cells)) continue;
                nearest = cells;
                best = distance;
            }
        return nearest;
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
}

public sealed record WidgetPlacement(string Device, int Column, int Row, int Columns, int Rows, bool Open);
