using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Media = System.Windows.Media;

namespace RadialMenu1;

internal sealed class ChameleonMonitor : IDisposable
{
    internal readonly record struct Colors(Media.Color Background, Media.Color Accent);
    internal static Colors Current { get; private set; } =
        new(Media.Color.FromRgb(30, 30, 48), Media.Color.FromRgb(143, 173, 242));
    internal static event Action? PaletteChanged;
    internal static event Action? PaletteSettled;

    private readonly DispatcherTimer _sample = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly DispatcherTimer _blend = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(32) };
    private readonly Stopwatch _transition = new();
    private Colors _from, _target;
    private string _wallpaperPath = "";
    private DateTime _wallpaperTime;
    private Colors _wallpaperColors;
    private bool _busy, _disposed;

    public ChameleonMonitor()
    {
        _sample.Tick += (_, _) => Sample();
        _blend.Tick += (_, _) => BlendFrame();
        ConfigStore.Changed += Update;
        Update();
    }

    private void Update()
    {
        if (ConfigStore.Current.Theme == MenuTheme.Chameleon)
        {
            if (!_sample.IsEnabled) { _sample.Start(); Sample(); }
            if (!ConfigStore.Current.AnimationsEnabled && _blend.IsEnabled) BlendFrame();
        }
        else
        {
            _sample.Stop();
            _blend.Stop();
            _target = default;
        }
    }

    private async void Sample()
    {
        if (_busy || _disposed) return;
        nint window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint process);
        // Keep the last external palette while the wheel has focus.
        if (process == (uint)Environment.ProcessId && _target != default) return;

        _busy = true;
        try
        {
            var colors = await Task.Run(() => process == (uint)Environment.ProcessId ? ReadWallpaper() : ReadSource(window));
            if (_disposed || ConfigStore.Current.Theme != MenuTheme.Chameleon || colors is not { } next) return;
            if (_target != default && ChannelDifference(next, _target) < 18) return;
            _from = Current;
            _target = next;
            _transition.Restart();
            if (ConfigStore.Current.AnimationsEnabled) _blend.Start();
            else BlendFrame();
        }
        finally { _busy = false; }
    }

    private void BlendFrame()
    {
        double t = ConfigStore.Current.AnimationsEnabled ? Math.Min(1, _transition.Elapsed.TotalMilliseconds / 460) : 1;
        double eased = t * t * (3 - 2 * t);
        Current = new(Mix(_from.Background, _target.Background, eased), Mix(_from.Accent, _target.Accent, eased));
        PaletteChanged?.Invoke();
        if (t >= 1)
        {
            _blend.Stop();
            PaletteSettled?.Invoke();
        }
    }

    private Colors? ReadSource(nint window)
    {
        var name = new StringBuilder(96);
        GetClassName(window, name, name.Capacity);
        if (window == 0 || window == GetShellWindow() || name.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd")
            return ReadWallpaper();
        if (window != GetForegroundWindow()) return null;

        try
        {
            if (GetWindowRect(window, out var bounds))
            {
                var area = Drawing.Rectangle.Intersect(Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                    System.Windows.Forms.SystemInformation.VirtualScreen);
                if (area.Width >= 50 && area.Height >= 50)
                {
                    const int patch = 6;
                    using var image = new Drawing.Bitmap(5 * patch, 4 * patch);
                    using var graphics = Drawing.Graphics.FromImage(image);
                    for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 5; col++)
                    {
                        int x = area.X + area.Width * (col + 1) / 6;
                        int y = area.Y + area.Height * (row * 2 + 1) / 8;
                        graphics.CopyFromScreen(x, y, col * patch, row * patch, new Drawing.Size(patch, patch));
                    }
                    if (window != GetForegroundWindow()) return null;
                    return Extract(image);
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException)
        {
        }
        return ReadWallpaper();
    }

    private Colors ReadWallpaper()
    {
        var path = new StringBuilder(260);
        SystemParametersInfo(0x0073, (uint)path.Capacity, path, 0);
        string file = path.ToString();
        try
        {
            if (File.Exists(file))
            {
                DateTime stamp = File.GetLastWriteTimeUtc(file);
                if (file == _wallpaperPath && stamp == _wallpaperTime) return _wallpaperColors;
                using var source = Drawing.Image.FromFile(file);
                using var thumbnail = new Drawing.Bitmap(source, 40, 24);
                _wallpaperColors = Extract(thumbnail);
                _wallpaperPath = file;
                _wallpaperTime = stamp;
                return _wallpaperColors;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException or OutOfMemoryException)
        {
        }
        var desktop = Drawing.SystemColors.Desktop;
        return Create(ToMedia(desktop), ToMedia(desktop));
    }

    internal static Colors Extract(Drawing.Bitmap sample)
    {
        int r = 0, g = 0, b = 0;
        var bins = new (int Count, int R, int G, int B)[64];
        for (int y = 0; y < sample.Height; y++)
        for (int x = 0; x < sample.Width; x++)
        {
            var pixel = sample.GetPixel(x, y);
            r += pixel.R; g += pixel.G; b += pixel.B;
            int bin = (pixel.R >> 6) * 16 + (pixel.G >> 6) * 4 + (pixel.B >> 6);
            ref var color = ref bins[bin];
            color.Count++; color.R += pixel.R; color.G += pixel.G; color.B += pixel.B;
        }
        int count = sample.Width * sample.Height;
        var background = Media.Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
        Media.Color accent = background;
        double bestScore = 0;
        foreach (var bin in bins)
        {
            if (bin.Count < Math.Max(2, count / 48)) continue;
            var candidate = Media.Color.FromRgb((byte)(bin.R / bin.Count), (byte)(bin.G / bin.Count), (byte)(bin.B / bin.Count));
            int chroma = Math.Max(candidate.R, Math.Max(candidate.G, candidate.B)) - Math.Min(candidate.R, Math.Min(candidate.G, candidate.B));
            double score = chroma * Math.Sqrt(bin.Count);
            if (score <= bestScore) continue;
            accent = candidate;
            bestScore = score;
        }
        return Create(background, accent);
    }

    private static Colors Create(Media.Color background, Media.Color accent)
    {
        int chroma = Math.Max(accent.R, Math.Max(accent.G, accent.B)) - Math.Min(accent.R, Math.Min(accent.G, accent.B));
        if (chroma < 18) accent = Mix(accent, Media.Color.FromRgb(129, 159, 230), 0.65);
        return new(Mix(Media.Color.FromRgb(17, 19, 25), Mix(background, accent, 0.18), 0.28),
            Mix(accent, Media.Colors.White, 0.38));
    }

    internal static Media.Color Mix(Media.Color first, Media.Color second, double amount) => Media.Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private static Media.Color ToMedia(Drawing.Color color) => Media.Color.FromRgb(color.R, color.G, color.B);

    private static int ChannelDifference(Colors a, Colors b) =>
        Math.Abs(a.Accent.R - b.Accent.R) + Math.Abs(a.Accent.G - b.Accent.G) + Math.Abs(a.Accent.B - b.Accent.B) +
        Math.Abs(a.Background.R - b.Background.R) + Math.Abs(a.Background.G - b.Background.G) + Math.Abs(a.Background.B - b.Background.B);

    public void Dispose()
    {
        _disposed = true;
        _sample.Stop();
        _blend.Stop();
        ConfigStore.Changed -= Update;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint parameter, StringBuilder path, uint flags);
}
