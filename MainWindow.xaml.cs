using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RadialMenu1;

public partial class MainWindow : Window
{
    private const int FirstHotkeyId = 9000;
    private const int SecondHotkeyId = 9001;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private int? _registeredId;
    private MenuHotkey _registeredHotkey;
    private readonly MouseHotkeyHook _mouseHotkey;
    private bool _hasHotkey;
    private bool _mouseSelectOnRelease;
    private int _heldKey;
    private readonly DispatcherTimer _keyRelease = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _glassRefresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(125) };
    private (int X, int Y, int Span) _glassSample;
    private int _glassGeneration;
    private bool _glassBusy;
    private bool _captureExcluded;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private Action? _afterClose;
    private bool _openingTelegram;
    private SettingsWindow? _settings;
    private TrayMenu? _tray;

    public MainWindow()
    {
        _mouseHotkey = new(MouseHotkeyPressed, MouseHotkeyReleased);
        InitializeComponent();
        _glassRefresh.Tick += RefreshGlass;
        _keyRelease.Tick += CheckKeyRelease;

        View.ItemInvoked += entry =>
        {
            string? webAppId = TelegramWebTabs.InstalledAppId(entry.Target);
            if (webAppId != null || TelegramWebTabs.IsWebLink(entry.Target)) CloseMenu(() => OpenTelegram(entry, webAppId));
            else if (WindowsAppIdentity.BringRunningAppToFront(entry.Target)) CloseMenu();
            else CloseMenu(() => Launcher.Run(entry));
        };
        View.CloseRequested += () => CloseMenu();
        View.SettingsRequested += () => CloseMenu(OpenSettings);
        View.CloseFinished += OnCloseFinished;

        SourceInitialized += OnSourceInitialized;
        Loaded += (s, e) =>
        {
            Hide();
            _tray ??= new TrayMenu(OpenSettings, OpenUpdates, OpenReport, SelectTrayTheme,
                () => Dispatcher.BeginInvoke(new Action(Application.Current.Shutdown)));
            if (!_hasHotkey) OpenSettings();
        };
        Deactivated += (s, e) => { if (View.IsOpen) CloseMenu(); }; // клик в другое место закрывает меню
        KeyDown += HandleKey;
    }

    // ---------------------------------------------------------------------
    //  Горячая клавиша
    // ---------------------------------------------------------------------
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);

        TryRegisterHotkey(ConfigStore.Current.Hotkey);
    }

    internal bool TryRegisterHotkey(MenuHotkey hotkey)
    {
        if (!hotkey.CanRegister) return false;
        if (_hasHotkey && _registeredHotkey == hotkey) return true;

        var handle = new WindowInteropHelper(this).Handle;
        if (hotkey.MouseButton != 0)
        {
            if (!_mouseHotkey.Start()) return false;
            if (_registeredId is int keyboardId) UnregisterHotKey(handle, keyboardId);
            _registeredId = null;
            _registeredHotkey = hotkey;
            _hasHotkey = true;
            return true;
        }

        int id = _registeredId == FirstHotkeyId ? SecondHotkeyId : FirstHotkeyId;
        // Сначала регистрируем новое сочетание, чтобы при конфликте старое продолжало работать.
        if (!RegisterHotKey(handle, id, (uint)hotkey.Modifiers | MOD_NOREPEAT,
                (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key))) return false;

        if (_registeredId is int oldId) UnregisterHotKey(handle, oldId);
        _registeredId = id;
        _registeredHotkey = hotkey;
        _hasHotkey = true;
        _mouseHotkey.Dispose();
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _registeredId)
        {
            handled = true;
            if (_settings?.UseCurrentHotkeyWhileRecording() == true) return IntPtr.Zero;
            if (GamePause.IsActive(ConfigStore.Current)) return IntPtr.Zero;
            if (ConfigStore.Current.SelectOnRelease)
            {
                if (!View.IsOpen) OpenMenu();
                if (View.IsOpen)
                {
                    _heldKey = KeyInterop.VirtualKeyFromKey(_registeredHotkey.Key);
                    _keyRelease.Start();
                }
            }
            else ToggleMenu();
        }
        return IntPtr.Zero;
    }

    private bool MouseHotkeyPressed(MenuHotkey hotkey, Point point)
    {
        if (hotkey != _registeredHotkey || _settings?.IsRecordingHotkey == true ||
            GamePause.IsActive(ConfigStore.Current)) return false;

        _mouseSelectOnRelease = ConfigStore.Current.SelectOnRelease;
        if (_mouseSelectOnRelease)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!View.IsOpen) OpenMenu(point);
            }));
        return true;
    }

    private void MouseHotkeyReleased(Point point)
    {
        bool select = _mouseSelectOnRelease;
        _mouseSelectOnRelease = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (select) SelectAtRelease(point);
            else if (!GamePause.IsActive(ConfigStore.Current)) ToggleMenu();
        }));
    }

    private void CheckKeyRelease(object? sender, EventArgs e)
    {
        if (GetAsyncKeyState(_heldKey) < 0) return;
        _keyRelease.Stop();
        if (GetCursorPos(out var cursor)) SelectAtRelease(new Point(cursor.X, cursor.Y));
        else CloseMenu();
    }

    private void SelectAtRelease(Point point)
    {
        if (View.IsOpen) View.SelectAtRelease(View.PointFromScreen(point));
    }

    private void ToggleMenu()
    {
        if (IsVisible && View.IsOpen) CloseMenu();
        else OpenMenu();
    }

    // ---------------------------------------------------------------------
    //  Показать у курсора / спрятать
    // ---------------------------------------------------------------------
    private void OpenMenu(Point? screenPoint = null)
    {
        if (_afterClose != null) return;
        if (screenPoint == null)
        {
            if (!GetCursorPos(out var cursor)) return;
            screenPoint = new Point(cursor.X, cursor.Y);
        }
        var p = screenPoint.Value;
        double size = View.SetMenuScale(ConfigStore.Current.MenuScalePercent);
        Width = Height = size;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = p.X / dpi.DpiScaleX - Width / 2;
        Top = p.Y / dpi.DpiScaleY - Height / 2;

        bool glass = ConfigStore.Current is { Theme: MenuTheme.Glass, AnimationsEnabled: true, TransparencyPercent: < 100 };
        if (glass)
        {
            _glassSample = ((int)p.X, (int)p.Y, (int)Math.Round(RadialView.GlassDiameter * size / RadialView.ViewSize * dpi.DpiScaleX));
            if (!IsVisible)
                View.SetGlassBackdrop(GlassBackdrop.Capture(_glassSample.X, _glassSample.Y, _glassSample.Span));
        }

        if (!IsVisible) Show();
        Activate();
        Focus();
        View.Open();
        View.CaptureMouse();
        if (glass && RenderCapability.Tier >= 0x10000 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            // Windows excludes only this overlay. If unsupported, the opening image stays as a fallback.
            _captureExcluded = SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11);
            if (_captureExcluded) _glassRefresh.Start();
        }
    }

    private async void RefreshGlass(object? sender, EventArgs e)
    {
        if (_glassBusy || !View.IsFullyOpen) return;
        var (x, y, span) = _glassSample;
        int generation = _glassGeneration;
        _glassBusy = true;
        try
        {
            var image = await Task.Run(() => GlassBackdrop.Capture(x, y, span));
            if (generation == _glassGeneration && image != null && View.IsOpen)
                View.SetGlassBackdrop(image);
        }
        finally { _glassBusy = false; }
    }

    private void CloseMenu(Action? after = null)
    {
        _keyRelease.Stop();
        if (!IsVisible)
        {
            after?.Invoke();
            return;
        }

        if (!View.IsOpen)
        {
            // уже закрывается: запоминаем действие, если оно есть
            if (after != null) _afterClose = after;
            return;
        }

        _afterClose = after;
        _glassRefresh.Stop();
        _glassGeneration++;
        View.BeginClose();
    }

    private void OnCloseFinished()
    {
        Hide();
        if (_captureExcluded)
        {
            SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0);
            _captureExcluded = false;
        }
        View.SetGlassBackdrop(null);
        var after = _afterClose;
        _afterClose = null;
        after?.Invoke();
    }

    private async void OpenTelegram(Entry entry, string? webAppId)
    {
        if (_openingTelegram) return;
        _openingTelegram = true;
        try
        {
            nint window = await Task.Run(() => TelegramWebTabs.FindExistingWindow(webAppId));
            if (window != 0) WindowsAppIdentity.ActivateWindow(window);
            else Launcher.Run(entry);
        }
        finally { _openingTelegram = false; }
    }

    private void OpenSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(TryRegisterHotkey, _hasHotkey);
            _settings.Closed += (s, e) => _settings = null;
            _settings.Show();
        }
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    private async void OpenUpdates()
    {
        OpenSettings();
        if (_settings != null) await _settings.CheckUpdatesAsync();
    }

    private void OpenReport()
    {
        OpenSettings();
        _settings?.ShowBugReport();
    }

    private void SelectTrayTheme(MenuTheme theme)
    {
        if (ConfigStore.Current.Theme == theme) return;
        ConfigStore.Current.Theme = theme;
        _settings?.SynchronizeTheme();
        ConfigStore.Commit();
    }

    // ---------------------------------------------------------------------
    //  Клавиши: Esc, Tab, стрелки, цифры 1-9 (режимы)
    // ---------------------------------------------------------------------
    private void HandleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_keyRelease.IsEnabled || _mouseSelectOnRelease || !View.TryGoBack()) CloseMenu();
        }
        else if (e.Key == Key.Tab)
        {
            View.SwitchMode(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        }
        else if (e.Key == Key.F2)
        {
            CloseMenu(OpenSettings);
        }
        else if (e.Key == Key.Right)
        {
            View.SwitchMode(1);
        }
        else if (e.Key == Key.Left)
        {
            View.SwitchMode(-1);
        }
        else if (e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            View.SetMode(e.Key - Key.D1);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_registeredId is int id) UnregisterHotKey(new WindowInteropHelper(this).Handle, id);
        _mouseHotkey.Dispose();
        _keyRelease.Stop();
        _tray?.Dispose();
        _glassRefresh.Stop();
        _glassGeneration++;
        base.OnClosed(e);
    }
}
