using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rectangle = System.Drawing.Rectangle;

namespace RadialMenu1;

internal sealed class ScreenRecordingBadge : Window
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    internal ScreenRecordingBadge(Rectangle screen)
    {
        Title = "Запись экрана RadialMenu";
        Width = 196;
        Height = 44;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI");

        var panel = new DockPanel { LastChildFill = true };
        var stop = new Button
        {
            Content = "Стоп",
            Width = 53,
            Height = 26,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Остановить запись и сохранить видео"
        };
        AutomationProperties.SetName(stop, "Остановить запись экрана");
        stop.Click += async (_, _) => await ScreenRecording.StopAsync();
        DockPanel.SetDock(stop, Dock.Right);
        panel.Children.Add(stop);

        var level = new Ellipse
        {
            Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromRgb(255, 106, 122)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
        };
        DockPanel.SetDock(level, Dock.Left);
        panel.Children.Add(level);
        var text = new TextBlock
        {
            Text = "Запись 00:00",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(text);
        Content = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(131, 60, 73)),
            Background = new SolidColorBrush(Color.FromRgb(39, 24, 31)),
            Padding = new Thickness(11, 5, 7, 5)
        };
        _clock.Tick += (_, _) =>
        {
            long seconds = (long)_elapsed.Elapsed.TotalSeconds;
            text.Text = seconds < 3600 ? $"Запись {seconds / 60:00}:{seconds % 60:00}" :
                $"Запись {seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        };
        _clock.Start();
        Closed += (_, _) => _clock.Stop();
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            using var dpi = new ScreenRecordingFrames.PhysicalPixels();
            SetWindowPos(handle, -1, screen.Left + 14, screen.Top + 14, 0, 0, 0x11); // NOSIZE | NOACTIVATE
            SetWindowDisplayAffinity(handle, 0x11); // Exclude only the recorder badge, never the radial wheel.
        };
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
