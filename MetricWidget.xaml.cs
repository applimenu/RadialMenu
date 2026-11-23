using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using PixelPoint = System.Drawing.Point;

namespace RadialMenu1;

internal partial class MetricWidget : Window
{
    private readonly StatusArc _metric;
    private MenuPalette? _palette;
    private long _lastSample;
    internal StatusArc Metric => _metric;
    internal int MinimumRows => _metric == StatusArc.Timer ? 2 : 1;
    private PixelPoint? _press;
    private bool _dragActive;
    private readonly DispatcherTimer _escapeCheck = new(DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(25) };

    internal MetricWidget(StatusArc metric)
    {
        _metric = metric;
        InitializeComponent();

        bool isTimer = metric == StatusArc.Timer;
        Title = MetricTitle.Text = TitleFor(metric);
        ApplyDetailPreference();
        _escapeCheck.Tick += (_, _) =>
        {
            if ((StatusWidgets.GetAsyncKeyState(0x1B) & 0x8000) != 0) EndGesture(false);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            WidgetMenu.IsOpen = false;
            EndGesture(false);
            FinishPresentation();
        };
        Closing += (_, _) => { EndGesture(false); FinishPresentation(); };
        TimerControls.Visibility = isTimer ? Visibility.Visible : Visibility.Collapsed;
        History.Visibility = isTimer ? Visibility.Collapsed : Visibility.Visible;
        History.AutoScale = metric == StatusArc.Network;
        ApplyPalette(MenuPalette.From(ConfigStore.Current));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        StatusWidgets.SetWidgetInputStyle(hwnd, _gameInput);
        HwndSource.FromHwnd(hwnd)?.AddHook(WidgetWindowProc);
    }

    internal static string TitleFor(StatusArc metric) => metric switch
    {
        StatusArc.Cpu => "Процессор",
        StatusArc.Audio => "Громкость",
        StatusArc.Battery => "Заряд батареи",
        StatusArc.Gpu => "Видеокарта",
        StatusArc.Memory => "Оперативная память",
        StatusArc.Disk => "Системный диск",
        StatusArc.Network => "Сеть",
        StatusArc.Timer => "Таймер",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    internal void ApplyDetailPreference() => MetricDetail.Visibility =
        ConfigStore.Current.WidgetsWithoutDetail.Contains(_metric.ToString()) ? Visibility.Collapsed : Visibility.Visible;

    internal void ApplyPalette(MenuPalette palette)
    {
        if (_palette == palette) return;
        _palette = palette;

        Color top = palette.Control;
        Color bottom = Blend(top, palette.Accent, 0.11);
        Color text = ReadableText(top, bottom, palette.Text);
        Color accent = palette.Accent;
        for (int i = 1; Readability(accent, top, bottom) < 4.5 && i <= 10; i++)
            accent = Blend(palette.Accent, text, i / 10.0);
        Color outline = Blend(top, text, 0.22);

        Resources["WidgetSurface"] = new LinearGradientBrush(top, bottom, 75);
        Resources["WidgetText"] = new SolidColorBrush(text);
        Resources["WidgetAccent"] = new SolidColorBrush(accent);
        Resources["WidgetOutline"] = new SolidColorBrush(outline);
        Resources["WidgetButtonFill"] = new SolidColorBrush(Blend(top, text, 0.06));
        Resources["WidgetButtonHover"] = new SolidColorBrush(Blend(top, accent, 0.22));
        History.ApplyPalette(accent, outline);
    }

    internal void Update(StatusReadings status, CountdownStatus countdown)
    {
        if (_metric == StatusArc.Timer)
        {
            bool running = countdown.State == CountdownState.Running;
            MetricValue.Text = countdown.Clock;
            MetricDetail.Text = countdown.State switch
            {
                CountdownState.Running => "Идёт отсчёт",
                CountdownState.Finished => "Время вышло",
                _ => "До сигнала"
            };
            TimerProgress.Value = countdown.Fraction;
            TimerToggle.Content = running ? "Стоп" : "Старт";
            PreviousDuration.IsEnabled = NextDuration.IsEnabled = !running;
            return;
        }

        var (value, detail, trend) = _metric switch
        {
            StatusArc.Cpu => (Percentage(status.Cpu), "Загрузка процессора", status.Cpu),
            StatusArc.Audio when status.Muted => ("Без звука", $"Громкость {Percentage(status.Volume)}", 0.0),
            StatusArc.Audio => (Percentage(status.Volume), "Громкость устройства вывода", status.Volume),
            StatusArc.Battery => (Percentage(status.Battery), status.OnAc ? "От сети" : "От батареи", status.Battery),
            StatusArc.Gpu => (Percentage(status.Gpu), "Загрузка видеокарты", status.Gpu),
            StatusArc.Memory => (Percentage(status.Memory), Occupied(status.MemoryUsedBytes, status.MemoryTotalBytes), status.Memory),
            StatusArc.Disk => (Percentage(status.Disk), Occupied(status.DiskUsedBytes, status.DiskTotalBytes), status.Disk),
            StatusArc.Network => (Rate(status.NetworkBytesPerSecond), "Приём и отправка", status.NetworkBytesPerSecond),
            _ => ("—", "Нет данных", null)
        };
        MetricValue.Text = value;
        MetricDetail.Text = trend == null ? "Нет данных" : detail;

        if (_lastSample == 0 || Stopwatch.GetElapsedTime(_lastSample).TotalMilliseconds >= 380)
        {
            _lastSample = Stopwatch.GetTimestamp();
            History.AddSample(trend);
        }
    }

    private static string Percentage(double? value) => value is { } fraction ? $"{Math.Clamp(fraction, 0, 1) * 100:0} %" : "—";

    private static string Occupied(ulong? used, ulong? total) => used is { } occupied && total is { } capacity
        ? $"{occupied / (double)(1L << 30):0.0} из {capacity / (double)(1L << 30):0.0} ГиБ"
        : "Занято";

    private static string Rate(double? value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000:0.0} ГБ/с",
        >= 1_000_000 => $"{value / 1_000_000:0.0} МБ/с",
        >= 1_000 => $"{value / 1_000:0.0} КБ/с",
        { } bytes => $"{bytes:0} Б/с",
        _ => "—"
    };

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_gameInput || sender is not UIElement surface || !surface.CaptureMouse()) return;
        _press = Forms.Cursor.Position;
        _escapeCheck.Start();
        e.Handled = true;
    }

    private void HeaderMove(object sender, MouseEventArgs e)
    {
        if (_press is not { } down) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndGesture(false); return; }
        var cursor = Forms.Cursor.Position;
        if (!_dragActive)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (Math.Abs(cursor.X - down.X) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
                Math.Abs(cursor.Y - down.Y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
            _dragActive = true;
            StatusWidgets.BeginDrag(this, down);
            Card.Opacity = 0.91;
        }
        StatusWidgets.MoveDrag(this, cursor);
    }

    private void HeaderUp(object sender, MouseButtonEventArgs e)
    {
        if (_press == null) return;
        if (_dragActive) StatusWidgets.MoveDrag(this, Forms.Cursor.Position);
        EndGesture(true);
        e.Handled = true;
    }

    private void HeaderLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is UIElement surface && !surface.IsMouseCaptured) EndGesture(false);
    }

    internal void EndGesture(bool accept)
    {
        if (_press == null) return;
        _press = null;
        _escapeCheck.Stop();
        if (Mouse.Captured == Header || Mouse.Captured == CompactContent) Mouse.Capture(null);
        if (!_dragActive) return;
        _dragActive = false;
        Card.Opacity = 1;
        StatusWidgets.EndDrag(this, accept);
    }

    internal void Settle()
    {
        if (!ConfigStore.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation) return;
        var pulse = new DoubleAnimation(0.982, 1, TimeSpan.FromMilliseconds(140))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        Card.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        Card.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    internal bool DesktopCompactEnabled => ConfigStore.Current.CompactDesktopWidgets.Contains(Metric.ToString());

    private void ChangeDesktopCompact(object sender, RoutedEventArgs e)
    {
        var compact = ConfigStore.Current.CompactDesktopWidgets;
        if (DesktopCompact.IsChecked) compact.Add(Metric.ToString());
        else compact.Remove(Metric.ToString());
        ConfigStore.Commit();
    }

    private void WidgetMenuOpened(object sender, RoutedEventArgs e)
    {
        DesktopCompact.IsChecked = DesktopCompactEnabled;
        EndGesture(false);
        var span = StatusWidgets.CurrentSpan(this);
        SmallSize.IsChecked = span is { Width: 2, Height: 1 };
        MediumSize.IsChecked = span is { Width: 2, Height: 2 };
        LargeSize.IsChecked = span is { Width: 3, Height: 2 };
        SmallSize.IsEnabled = MinimumRows == 1;
        if (_metric == StatusArc.Timer)
            PrimaryAction.Header = StatusRingMonitor.Instance.Countdown.State == CountdownState.Running
                ? "Остановить таймер" : "Запустить таймер";
    }

    private void OpenMetric(object sender, RoutedEventArgs e)
    {
        if (_metric == StatusArc.Timer) { ToggleTimer(sender, e); return; }
        string target = _metric switch
        {
            StatusArc.Audio => "ms-settings:sound",
            StatusArc.Network => "ms-settings:network-status",
            StatusArc.Disk => "ms-settings:storagesense",
            StatusArc.Battery => "ms-settings:powersleep",
            _ => System.IO.Path.Combine(Environment.SystemDirectory, "Taskmgr.exe")
        };
        Launcher.Run(new Entry(Title, target));
    }

    private void ConfigureWidget(object sender, RoutedEventArgs e) =>
        (Application.Current.MainWindow as MainWindow)?.OpenWidgetSettings(_metric);

    private void ManageWidgets(object sender, RoutedEventArgs e) =>
        (Application.Current.MainWindow as MainWindow)?.OpenWidgetSettings(null);

    private void ResizeWidget(object sender, RoutedEventArgs e)
    {
        if (!StatusWidgets.Resize(this, sender == LargeSize ? 3 : 2, sender == SmallSize ? 1 : 2))
            System.Media.SystemSounds.Beep.Play();
    }

    private void CloseWidget(object sender, RoutedEventArgs e) => StatusWidgets.Unpin(this);

    private void ToggleTimer(object sender, RoutedEventArgs e) => StatusRingMonitor.Instance.ToggleTimer();

    private void PreviousTimerPreset(object sender, RoutedEventArgs e) => StatusRingMonitor.Instance.ChangeDuration(-1);

    private void NextTimerPreset(object sender, RoutedEventArgs e) => StatusRingMonitor.Instance.ChangeDuration(1);

    private void ChangeTimerByWheel(object sender, MouseWheelEventArgs e)
    {
        StatusRingMonitor.Instance.ChangeDuration(e.Delta);
        e.Handled = true;
    }

    private static Color ReadableText(Color top, Color bottom, Color preferred)
    {
        if (Readability(preferred, top, bottom) >= 4.5) return preferred;
        return Readability(Colors.White, top, bottom) >= Readability(Colors.Black, top, bottom) ? Colors.White : Colors.Black;
    }

    private static double Readability(Color foreground, Color top, Color bottom)
    {
        static double Linear(byte channel) => channel / 255.0 <= 0.04045
            ? channel / 255.0 / 12.92 : Math.Pow((channel / 255.0 + 0.055) / 1.055, 2.4);
        static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B) + 0.05;
        static double Ratio(double a, double b) => Math.Max(a, b) / Math.Min(a, b);
        double front = Luminance(foreground);
        return Math.Min(Ratio(front, Luminance(top)), Ratio(front, Luminance(bottom)));
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
