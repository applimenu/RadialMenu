using System.Runtime.InteropServices;
using System.Text;

namespace RadialMenu1;

internal static class GamePause
{
    public static bool IsActive(AppConfig config) =>
        config.PauseInGames && config.GameExecutables is { Count: > 0 } &&
        IsGameWindow(GetForegroundWindow(), config, includeFullscreen: false);

    internal static bool IsGameWindow(nint window, AppConfig config, bool includeFullscreen = true)
    {
        if (window == 0 || window == GetShellWindow() || IsIconic(window) ||
            GetWindowThreadProcessId(window, out int processId) == 0 || processId == Environment.ProcessId) return false;
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (config.GameExecutables is { Count: > 0 } games && RunningApp.ReadExecutable(processId) is { } path &&
            games.Contains(path, StringComparer.OrdinalIgnoreCase)) return true;
        if (!includeFullscreen || (GetWindowLong(window, -16) & 0x00C00000) != 0) return false;

        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        nint handle = MonitorFromWindow(window, 0);
        if (handle == 0 || !GetMonitorInfo(handle, ref monitor)) return false;
        if (DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<NativeRect>()) != 0 &&
            !GetWindowRect(window, out rect)) return false;
        const int tolerance = 3;
        return rect.Left <= monitor.Bounds.Left + tolerance && rect.Top <= monitor.Bounds.Top + tolerance &&
            rect.Right >= monitor.Bounds.Right - tolerance && rect.Bottom >= monitor.Bounds.Bottom - tolerance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Bounds, WorkArea; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);
}
