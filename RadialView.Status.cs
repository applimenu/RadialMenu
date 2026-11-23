using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace RadialMenu1;

public sealed partial class RadialView
{
    private readonly StatusRingView _statusRing = new() { Width = ViewSize, Height = ViewSize };
    private StatusArc? _statusHover;
    private bool _watchingStatus;
    private bool _checkingStatusBackground;
    private long _lastStatusBackground;
    private int _statusBackgroundGeneration;

    internal event Action<StatusArc>? StatusWidgetRequested;

    private double PointerRadius => _statusRing.Visibility == Visibility.Visible ? 196 : CursorR;

    private void SynchronizeStatusRing()
    {
        var config = ConfigStore.Current;
        var metrics = config.VisibleStatusMetrics & StatusMetrics.All;
        _statusRing.Configure(metrics);
        bool show = config.StatusRingEnabled && metrics != StatusMetrics.None && _slots.Count != 0;
        _statusRing.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) SetStatusHover(null);

        bool watch = show && _open && IsLoaded && IsVisible;
        if (watch != _watchingStatus)
        {
            _watchingStatus = watch;
            if (watch) StatusRingMonitor.Instance.Watch(UpdateStatusRing);
            else StatusRingMonitor.Instance.Unwatch(UpdateStatusRing);
        }
        if (show) UpdateStatusRing();
    }

    private void StopWatchingStatus()
    {
        if (!_watchingStatus) return;
        _watchingStatus = false;
        StatusRingMonitor.Instance.Unwatch(UpdateStatusRing);
    }

    private void UpdateStatusRing()
    {
        var status = StatusRingMonitor.Instance;
        bool changed = _statusRing.Update(status.Readings, status.Countdown);
        if (_open && IsVisible && _statusRing.Visibility == Visibility.Visible)
        {
            if (changed) StartRendering();
        }
        else _statusRing.Advance(0, animate: false);
        if (_statusHover.HasValue) UpdateCenterText();
        RefreshStatusBackground();
    }

    internal void PrepareStatusBackground(Point anchor, double scaleX, double scaleY)
    {
        var config = ConfigStore.Current;
        _statusBackgroundGeneration++;
        if (!config.StatusRingEnabled) return;
        _statusRing.Configure(config.VisibleStatusMetrics);
        var samples = _statusRing.SamplePoints(false).Select(sample => (sample.Metric,
            new Point(anchor.X + (sample.Point.X - C) * scaleX, anchor.Y + (sample.Point.Y - C) * scaleY)));
        _statusRing.ApplyScreenColors(StatusRingView.ReadScreenColors(samples), replace: true);
        _lastStatusBackground = Stopwatch.GetTimestamp();
    }

    private async void RefreshStatusBackground()
    {
        if (IsPreview || !_watchingStatus || _checkingStatusBackground ||
            Stopwatch.GetElapsedTime(_lastStatusBackground).TotalSeconds < 1.2) return;

        _lastStatusBackground = Stopwatch.GetTimestamp();
        var samples = _statusRing.SamplePoints(true).Select(sample => (sample.Metric, PointToScreen(sample.Point))).ToArray();
        int generation = _statusBackgroundGeneration;
        _checkingStatusBackground = true;
        try
        {
            var colors = await Task.Run(() => StatusRingView.ReadScreenColors(samples)).ConfigureAwait(false);
            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                _checkingStatusBackground = false;
                if (_watchingStatus && generation == _statusBackgroundGeneration)
                    _statusRing.ApplyScreenColors(colors, replace: false);
            });
        }
        catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted) { }
    }

    private void SetStatusHover(StatusArc? metric)
    {
        if (_statusHover == metric) return;
        _statusHover = metric;
        _statusRing.Highlight(metric);
        UpdateCenterText();
        if (_open && _statusRing.Visibility == Visibility.Visible) StartRendering();
    }

    private bool HighlightStatusAt(Point point)
    {
        var metric = _statusRing.At(point);
        SetStatusHover(metric);
        if (!metric.HasValue) return false;

        SetPointer(-1, false);
        Cursor = Cursors.Hand;
        return true;
    }

    private bool ClickStatusAt(Point point)
    {
        if (_statusRing.At(point) is not { } metric) return false;
        if (metric == StatusArc.Timer && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ToggleStatusTimer();
        else StatusWidgetRequested?.Invoke(metric);
        return true;
    }

    internal bool ToggleStatusTimer()
    {
        if (!_open || _statusRing.Visibility != Visibility.Visible || !_statusRing.Contains(StatusArc.Timer)) return false;
        StatusRingMonitor.Instance.ToggleTimer();
        UpdateStatusRing();
        return true;
    }
}
