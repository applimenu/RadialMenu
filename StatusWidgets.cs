using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RadialMenu1;

internal static partial class StatusWidgets
{
    private static readonly Dictionary<StatusArc, MetricWidget> Windows = new();
    private static readonly DispatcherTimer ForegroundCheck =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
    private static nint _lastForeground;
    private static nint _foregroundHook;
    private static readonly WinEventCallback ForegroundChanged = OnForegroundChanged;
    private static bool _gameActive;
    private static bool _watching;
    internal static event Action? PinsChanged;

    internal static bool IsPinned(StatusArc metric) => Windows.ContainsKey(metric);

    internal static void SetPinned(StatusArc metric, bool pinned)
    {
        if (pinned) Show(metric);
        else if (Windows.TryGetValue(metric, out var widget)) Unpin(widget);
    }

    internal static void SetDetailVisible(StatusArc metric, bool visible)
    {
        var hidden = ConfigStore.Current.WidgetsWithoutDetail;
        if (visible) hidden.Remove(metric.ToString());
        else hidden.Add(metric.ToString());
        if (Windows.TryGetValue(metric, out var widget)) widget.ApplyDetailPreference();
        ConfigStore.Save();
    }

    static StatusWidgets() => ForegroundCheck.Tick += (_, _) => ApplyVisibility();

    internal static void Show(StatusArc metric)
    {
        if (Application.Current.Dispatcher.HasShutdownStarted || !Enum.IsDefined(metric)) return;
        if (!Windows.TryGetValue(metric, out var widget))
        {
            _dragging?.EndGesture(false);
            bool first = Windows.Count == 0;
            widget = new MetricWidget(metric);
            if (!Allocate(widget))
            {
                widget.Close();
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            widget.Closed += (_, _) => Remove(metric);
            Windows.Add(metric, widget);

            if (first)
            {
                SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
                SystemEvents.UserPreferenceChanged += OnPreferencesChanged;
                ConfigStore.Changed += Refresh;
                ChameleonMonitor.PaletteSettled += Refresh;
                const uint eventSystemForeground = 3;
                _foregroundHook = SetWinEventHook(eventSystemForeground, eventSystemForeground, 0,
                    ForegroundChanged, 0, 0, 0);
                ForegroundCheck.Start();
            }
            PinsChanged?.Invoke();
        }

        Refresh();
        if (widget.IsVisible) KeepAbove(new WindowInteropHelper(widget).Handle);
    }

    internal static void CloseAll()
    {
        foreach (var widget in Windows.Values.ToArray()) widget.Close();
    }

    private static void Remove(StatusArc metric)
    {
        _finishSnap?.Invoke();
        if (Windows.Remove(metric, out var widget)) Slots.Remove(widget);
        PinsChanged?.Invoke();
        if (Windows.Count == 0)
        {
            ForegroundCheck.Stop();
            if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
            _gameActive = false;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.UserPreferenceChanged -= OnPreferencesChanged;
            _lastForeground = 0;
            ConfigStore.Changed -= Refresh;
            ChameleonMonitor.PaletteSettled -= Refresh;
        }
        SynchronizeMonitor();
    }

    private static void Refresh()
    {
        ApplyVisibility();
        UpdateReadings();
    }

    private static void ApplyVisibility()
    {
        var config = ConfigStore.Current;
        nint foreground = GetForegroundWindow();
        bool changed = foreground != _lastForeground;
        _lastForeground = foreground;
        // Windows may briefly report no foreground window during Alt+Tab.
        bool inGame = foreground == 0 ? _gameActive : GamePause.IsGameWindow(foreground, config);
        if (inGame && !_gameActive) _finishSnap?.Invoke();
        _gameActive = inGame;
        bool hide = inGame && config.HideWidgetsInGames;
        bool raiseWidgets = changed && _dragging == null && !Windows.Values.Any(w => w.WidgetMenu.IsOpen);
        foreach (var widget in Windows.Values)
        {
            widget.SetGameInput(inGame && config.CompactWidgetsInGames);
            if (hide || !Slots.TryGetValue(widget, out var slot))
            {
                if (widget.IsVisible) widget.Hide();
                continue;
            }
            widget.PresentSlot(slot.Grid, slot.Cells, widget.DesktopCompactEnabled || (inGame && config.CompactWidgetsInGames));
            if (!widget.IsVisible) widget.Show();
            else if (raiseWidgets) KeepAbove(new WindowInteropHelper(widget).Handle);
        }
        SynchronizeMonitor();
    }

    private static void SynchronizeMonitor()
    {
        bool watch = Windows.Values.Any(widget => widget.IsVisible);
        if (watch == _watching) return;
        _watching = watch;
        var monitor = StatusRingMonitor.Instance;
        if (watch)
        {
            monitor.Watch(UpdateReadings);
            UpdateReadings();
        }
        else monitor.Unwatch(UpdateReadings);
    }

    private static void UpdateReadings()
    {
        var palette = MenuPalette.From(ConfigStore.Current);
        var monitor = StatusRingMonitor.Instance;
        foreach (var widget in Windows.Values)
        {
            widget.ApplyPalette(palette);
            if (widget.IsVisible) widget.Update(monitor.Readings, monitor.Countdown);
        }
    }

    private delegate void WinEventCallback(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time);

    private static void OnForegroundChanged(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (hook == _foregroundHook && dispatcher != null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ApplyVisibility));
    }

    internal static bool GameHasInput() => ConfigStore.Current.CompactWidgetsInGames &&
        GamePause.IsGameWindow(GetForegroundWindow(), ConfigStore.Current);

    internal static void SetWidgetInputStyle(nint hwnd, bool passThrough)
    {
        const int extendedStyle = -20, transparent = 0x20, noActivate = 0x08000000;
        int original = GetWindowLong(hwnd, extendedStyle);
        int desired = (passThrough ? original | transparent : original & ~transparent) | noActivate;
        if (desired != original) SetWindowLong(hwnd, extendedStyle, desired);
    }

    internal static void AvoidActivation(nint window)
    {
        const int extendedStyle = -20;
        const int noActivate = 0x08000000;
        SetWindowLong(window, extendedStyle, GetWindowLong(window, extendedStyle) | noActivate);
    }

    private static void KeepAbove(nint window)
    {
        const uint noSize = 0x0001, noMove = 0x0002, noActivate = 0x0010, noOwnerZOrder = 0x0200;
        if (window != 0) SetWindowPos(window, -1, 0, 0, 0, 0, noSize | noMove | noActivate | noOwnerZOrder);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
