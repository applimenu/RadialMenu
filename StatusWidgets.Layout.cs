using Microsoft.Win32;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace RadialMenu1;

internal static partial class StatusWidgets
{
    private sealed record Slot(WidgetSnapGrid Grid, Rectangle Cells);
    private static readonly Dictionary<MetricWidget, Slot> Slots = new();
    private static MetricWidget? _dragging;
    private static WidgetGridOverlay? _overlay;
    private static Slot? _preview;
    private static Rectangle? _wanted, _dragBounds;
    private static Rectangle[] _occupied = [];
    private static Action? _finishSnap;
    private static double _anchorX, _anchorY;
    private static bool _restored;

    internal static void RestorePinned()
    {
        if (_restored) return;
        _restored = true;
        foreach (var pair in ConfigStore.Current.WidgetPlacements.ToArray())
            if (pair.Value is { Open: true } && Enum.TryParse<StatusArc>(pair.Key, out var metric)) Show(metric);
    }

    private static Rectangle[] Occupied(MetricWidget widget, WidgetSnapGrid grid) => Slots
        .Where(p => p.Key != widget && p.Value.Grid.Device == grid.Device)
        .Select(p => p.Value.Cells).ToArray();

    private static bool Allocate(MetricWidget widget)
    {
        _finishSnap?.Invoke();
        var cursor = Forms.Cursor.Position;
        ConfigStore.Current.WidgetPlacements.TryGetValue(widget.Metric.ToString(), out var saved);
        var screens = Forms.Screen.AllScreens;
        var first = screens.FirstOrDefault(s => s.DeviceName == saved?.Device) ?? Forms.Screen.FromPoint(cursor);
        var size = new Size(Math.Clamp(saved?.Columns ?? 2, 2, 3),
            Math.Clamp(saved?.Rows ?? widget.MinimumRows, widget.MinimumRows, 2));
        foreach (var screen in screens.OrderByDescending(s => s.DeviceName == first.DeviceName))
        {
            var grid = WidgetSnapGrid.For(screen);
            if (grid.Columns < size.Width || grid.Rows < size.Height) continue;
            var target = saved == null ? grid.At(cursor, size) : grid.Clamp(saved.Column, saved.Row, size);
            if (grid.NearestFree(target, Occupied(widget, grid)) is not { } cells) continue;
            CommitSlot(widget, new(grid, cells));
            return true;
        }
        return false;
    }

    private static void CommitSlot(MetricWidget widget, Slot slot, bool moveNow = true)
    {
        Slots[widget] = slot;
        if (moveNow) widget.PresentSlot(slot.Grid, slot.Cells,
            widget.DesktopCompactEnabled || (_gameActive && ConfigStore.Current.CompactWidgetsInGames), placeImmediately: true);
        var cells = slot.Cells;
        var placement = new WidgetPlacement(slot.Grid.Device, cells.X, cells.Y, cells.Width, cells.Height, true);
        var layouts = ConfigStore.Current.WidgetPlacements;
        if (layouts.TryGetValue(widget.Metric.ToString(), out var old) && old == placement) return;
        layouts[widget.Metric.ToString()] = placement;
        ConfigStore.Save();
    }

    internal static void Unpin(MetricWidget widget)
    {
        var layouts = ConfigStore.Current.WidgetPlacements;
        if (layouts.TryGetValue(widget.Metric.ToString(), out var place))
        {
            layouts[widget.Metric.ToString()] = place with { Open = false };
            ConfigStore.Save();
        }
        widget.Close();
    }

    internal static Size? CurrentSpan(MetricWidget widget) =>
        Slots.TryGetValue(widget, out var slot) ? slot.Cells.Size : null;

    internal static bool Resize(MetricWidget widget, int columns, int rows)
    {
        _finishSnap?.Invoke();
        _dragging?.EndGesture(false);
        if (rows < widget.MinimumRows || !Slots.TryGetValue(widget, out var old) ||
            columns > old.Grid.Columns || rows > old.Grid.Rows) return false;
        var target = old.Grid.Clamp(old.Cells.X, old.Cells.Y, new(columns, rows));
        if (old.Grid.NearestFree(target, Occupied(widget, old.Grid)) is not { } free) return false;
        CommitSlot(widget, new(old.Grid, free));
        widget.Settle();
        return true;
    }

    internal static void BeginDrag(MetricWidget widget, Point down)
    {
        widget.FinishPresentation();
        _finishSnap?.Invoke();
        if (!Slots.TryGetValue(widget, out var slot)) return;
        _dragging = widget;
        _preview = slot;
        var bounds = slot.Grid.Bounds(slot.Cells);
        _dragBounds = bounds;
        _wanted = null;
        _anchorX = Math.Clamp((down.X - bounds.X) / (double)bounds.Width, 0, 1);
        _anchorY = Math.Clamp((down.Y - bounds.Y) / (double)bounds.Height, 0, 1);
    }

    internal static void MoveDrag(MetricWidget widget, Point cursor)
    {
        if (_dragging != widget) return;
        var source = Slots[widget];
        var screen = Forms.Screen.FromPoint(cursor);
        if (_overlay == null || _overlay.Grid.Device != screen.DeviceName)
        {
            _overlay?.Close();
            _overlay = new WidgetGridOverlay(WidgetSnapGrid.For(screen), MenuPalette.From(ConfigStore.Current).Accent);
            _occupied = Occupied(widget, _overlay.Grid);
            _wanted = null;
            _overlay.Show();
            KeepAbove(new WindowInteropHelper(widget).Handle);
        }
        var grid = _overlay.Grid;
        var span = source.Cells.Size;
        var size = grid.Bounds(new Rectangle(Point.Empty, span)).Size;
        var topLeft = new Point(cursor.X - (int)Math.Round(_anchorX * size.Width),
                                cursor.Y - (int)Math.Round(_anchorY * size.Height));
        var target = grid.At(topLeft, span);
        if (_wanted != target)
        {
            _wanted = target;
            var free = target.Size != span ? null : grid.NearestFree(target, _occupied);
            _overlay.Preview(target, free);
            _preview = free is { } cells ? new Slot(grid, cells) : null;
        }
        var display = _preview ?? source;
        var bounds = display.Grid.Bounds(display.Cells);
        if (_preview != null)
        {
            var area = grid.WorkArea;
            var raw = new Rectangle(Math.Clamp(topLeft.X, area.Left, area.Right - size.Width),
                Math.Clamp(topLeft.Y, area.Top, area.Bottom - size.Height), size.Width, size.Height);
            if (HasClearPath(grid, raw)) bounds = raw;
        }
        if (_dragBounds == bounds) return;
        _dragBounds = bounds;
        widget.PlaceDragFrame(display.Grid, bounds);
    }

    private static bool HasClearPath(WidgetSnapGrid grid, Rectangle bounds)
    {
        foreach (var occupied in _occupied)
            if (grid.Bounds(occupied).IntersectsWith(bounds)) return false;
        return true;
    }

    internal static void EndDrag(MetricWidget widget, bool accept)
    {
        if (_dragging != widget) return;
        _dragging = null;
        _overlay?.Close();
        _overlay = null;
        var preview = _preview;
        _preview = null;
        if (accept && preview != null)
        {
            var destination = preview.Grid.Bounds(preview.Cells);
            var start = _dragBounds ?? destination;
            bool animate = ConfigStore.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation &&
                start != destination && start.Size == destination.Size &&
                HasClearPath(preview.Grid, Rectangle.Union(start, destination));
            CommitSlot(widget, preview, moveNow: !animate);
            if (animate) AnimateSnap(widget, start, destination);
        }
        else if (Slots.TryGetValue(widget, out var old)) widget.PlaceDragFrame(old.Grid, old.Grid.Bounds(old.Cells));
        _dragBounds = null;
        _occupied = [];
        if (accept) widget.Settle();
    }

    private static void AnimateSnap(MetricWidget widget, Rectangle start, Rectangle end)
    {
        long began = Stopwatch.GetTimestamp();
        void Finish()
        {
            CompositionTarget.Rendering -= Frame;
            _finishSnap = null;
            if (new WindowInteropHelper(widget).Handle != 0 && Slots.TryGetValue(widget, out var slot))
                widget.PlaceDragFrame(slot.Grid, end);
        }
        void Frame(object? sender, EventArgs e)
        {
            double t = Stopwatch.GetElapsedTime(began).TotalMilliseconds / 120;
            if (t >= 1 || !widget.IsVisible) { Finish(); return; }
            double remaining = Math.Pow(1 - t, 3);
            var frame = new Rectangle(end.X + (int)Math.Round((start.X - end.X) * remaining),
                end.Y + (int)Math.Round((start.Y - end.Y) * remaining), end.Width, end.Height);
            widget.PlaceDragFrame(Slots[widget].Grid, frame);
        }
        _finishSnap = Finish;
        CompositionTarget.Rendering += Frame;
    }
    private static void OnDisplayChanged(object? sender, EventArgs e) => QueueReflow();
    private static void OnPreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General) QueueReflow();
    }

    private static void QueueReflow()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            _dragging?.EndGesture(false);
            Slots.Clear();
            foreach (var widget in Windows.Values)
                if (!Allocate(widget)) widget.Hide();
            ApplyVisibility();
        }));
    }

    internal static void PlaceWindow(Window window, Rectangle rect)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        const uint noZOrder = 4, noActivate = 16;
        SetWindowPos(handle, 0, rect.X, rect.Y, rect.Width, rect.Height, noZOrder | noActivate);
    }

    internal static void MakeOverlayTransparent(nint hwnd)
    {
        AvoidActivation(hwnd);
        const int extendedStyle = -20, transparent = 0x20, toolWindow = 0x80;
        SetWindowLong(hwnd, extendedStyle, GetWindowLong(hwnd, extendedStyle) | transparent | toolWindow);
    }

    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
}
