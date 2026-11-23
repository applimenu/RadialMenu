using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RadialMenu1;

internal partial class MetricWidget
{
    private bool _gameInput, _compact, _transitioning;
    private Rectangle _frame, _start, _destination;
    private long _started;
    private double _initialMix, _mix;

    internal void SetGameInput(bool inGame)
    {
        if (_gameInput == inGame) return;
        _gameInput = inGame;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) StatusWidgets.SetWidgetInputStyle(hwnd, inGame);
        if (inGame)
        {
            WidgetMenu.IsOpen = false;
            EndGesture(false);
            if (IsMouseCaptureWithin) Mouse.Capture(null);
        }
        IsHitTestVisible = !inGame;
    }

    private nint WidgetWindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int wmNcHitTest = 0x84, wmMouseActivate = 0x21;
        if (message == wmNcHitTest && (_gameInput || StatusWidgets.GameHasInput()))
        {
            // Cover the interval before the foreground notification reaches the dispatcher.
            SetGameInput(true);
            handled = true;
            return -1; // HTTRANSPARENT
        }
        if (message == wmMouseActivate)
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        return 0;
    }

    private static Rectangle CompactBounds(WidgetSnapGrid grid, Rectangle normal)
    {
        double dpi = grid.Step / (double)WidgetSnapGrid.PitchDip;
        return new Rectangle(normal.X, normal.Y, Math.Min(normal.Width, (int)Math.Ceiling(152 * dpi)),
            Math.Min(normal.Height, (int)Math.Ceiling(58 * dpi)));
    }

    internal void PlaceDragFrame(WidgetSnapGrid grid, Rectangle bounds) =>
        StatusWidgets.PlaceWindow(this, _compact ? CompactBounds(grid, bounds) : bounds);

    internal void PresentSlot(WidgetSnapGrid grid, Rectangle cells, bool compact, bool placeImmediately = false)
    {
        if (!placeImmediately && compact == _compact) return;
        var normal = grid.Bounds(cells);
        var small = CompactBounds(grid, normal);
        _start = _transitioning ? _frame : _compact ? small : normal;
        _destination = compact ? small : normal;
        _compact = compact;
        if (placeImmediately || !IsVisible || !ConfigStore.Current.AnimationsEnabled)
        {
            CompletePresentation();
            return;
        }

        _initialMix = _mix;
        _started = Stopwatch.GetTimestamp();
        FullContent.Visibility = CompactContent.Visibility = Visibility.Visible;
        FullContent.IsHitTestVisible = !_compact;
        CompactContent.IsHitTestVisible = _compact;
        if (_transitioning) return;
        _transitioning = true;
        CompositionTarget.Rendering += RenderPresentation;
    }

    private void RenderPresentation(object? sender, EventArgs e)
    {
        double t = Math.Min(1, Stopwatch.GetElapsedTime(_started).TotalMilliseconds / 155);
        if (t >= 1) { CompletePresentation(); return; }
        double eased = 1 - Math.Pow(1 - t, 3);
        _mix = _initialMix + ((_compact ? 1 : 0) - _initialMix) * eased;
        FullContent.Opacity = 1 - _mix;
        CompactContent.Opacity = _mix;
        var rect = new Rectangle(_destination.X, _destination.Y,
            (int)Math.Round(_start.Width + (_destination.Width - _start.Width) * eased),
            (int)Math.Round(_start.Height + (_destination.Height - _start.Height) * eased));
        if (_frame != rect) StatusWidgets.PlaceWindow(this, rect);
        _frame = rect;
    }

    internal void FinishPresentation()
    {
        if (_transitioning) CompletePresentation();
    }

    private void CompletePresentation()
    {
        if (_transitioning) CompositionTarget.Rendering -= RenderPresentation;
        _transitioning = false;
        _mix = _compact ? 1 : 0;
        FullContent.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        CompactContent.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        CompactContent.IsHitTestVisible = _compact;
        FullContent.IsHitTestVisible = !_compact;
        FullContent.Opacity = 1 - _mix;
        CompactContent.Opacity = _mix;
        _frame = _destination;
        StatusWidgets.PlaceWindow(this, _destination);
    }
}
