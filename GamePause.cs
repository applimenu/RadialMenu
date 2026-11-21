using System.Runtime.InteropServices;

namespace RadialMenu1;

internal static class GamePause
{
    public static bool IsActive(AppConfig config)
    {
        if (!config.PauseInGames || config.GameExecutables is not { Count: > 0 }) return false;
        nint window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out int processId) == 0) return false;
        if (processId == Environment.ProcessId) return false;

        string? executable = RunningApp.ReadExecutable(processId);
        return executable != null && config.GameExecutables.Contains(executable, StringComparer.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);
}
