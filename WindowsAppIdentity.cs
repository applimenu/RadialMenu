using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace RadialMenu1;

internal static class WindowsAppIdentity
{
    public static string? AppIdForTarget(string target, bool includeActions = false)
    {
        if (target.StartsWith(RunningApp.PackagedPrefix, StringComparison.OrdinalIgnoreCase))
            return target[RunningApp.PackagedPrefix.Length..];

        return target.ToLowerInvariant() switch
        {
            "ms-settings:" => "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
            "ms-screenclip:" when includeActions => "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
            "calc.exe" => "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            "notepad.exe" => "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            _ => null
        };
    }

    public static string? FileForTarget(string target)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string path = target.ToLowerInvariant() switch
        {
            "ms-settings:" => Path.Combine(windows, "ImmersiveControlPanel", "SystemSettings.exe"),
            "ms-screenclip:" => Path.Combine(Environment.SystemDirectory, "SnippingTool.exe"),
            "explorer.exe" => Path.Combine(windows, "explorer.exe"),
            "taskmgr.exe" or "calc.exe" or "notepad.exe" => Path.Combine(Environment.SystemDirectory, target),
            _ => Environment.ExpandEnvironmentVariables(target)
        };
        return File.Exists(path) || Directory.Exists(path) ? path : null;
    }

    private static string? ShortcutExecutable(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            if (shell == null) return null;
            shortcut = ((dynamic)shell).CreateShortcut(shortcutPath);
            string arguments = ((dynamic)shortcut!).Arguments;
            // Ярлыки веб-приложений и ссылки должны открывать свою страницу.
            if (arguments.Contains("://", StringComparison.Ordinal) ||
                arguments.Contains("--app", StringComparison.OrdinalIgnoreCase)) return null;
            string target = ((dynamic)shortcut).TargetPath;
            return FileForTarget(target);
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    public static bool BringRunningAppToFront(string target)
    {
        if (target.Equals("ms-screenclip:", StringComparison.OrdinalIgnoreCase)) return false;

        string? appId = AppIdForTarget(target);
        string? executable = FileForTarget(target);
        if (executable == null && appId != null && !appId.Contains('!'))
            executable = InstalledApps.ExecutableFor(appId);
        if (executable?.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) == true)
            executable = ShortcutExecutable(executable);
        if (executable?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) != true) executable = null;
        if (executable == null && appId == null) return false;
        bool explorer = Path.GetFileName(executable)?.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) == true;

        var processMatches = new Dictionary<int, bool>();
        bool Matches(nint window)
        {
            GetWindowThreadProcessId(window, out int processId);
            if (!processMatches.TryGetValue(processId, out bool match))
            {
                match = appId != null && string.Equals(RunningApp.ReadAppId(processId), appId, StringComparison.OrdinalIgnoreCase);
                if (!match && executable != null)
                {
                    try
                    {
                        using var process = Process.GetProcessById(processId);
                        if (process.ProcessName.Equals(Path.GetFileNameWithoutExtension(executable), StringComparison.OrdinalIgnoreCase))
                            match = string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                    {
                        // Окно могло закрыться, а системный процесс может быть защищён.
                    }
                }
                processMatches[processId] = match;
            }
            return match;
        }

        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLengthW(window) == 0) return true;
            bool match = Matches(window);
            if (explorer || (!match && appId != null))
            {
                var windowClass = new System.Text.StringBuilder(64);
                GetClassNameW(window, windowClass, windowClass.Capacity);
                string name = windowClass.ToString();
                if (explorer && name is not ("CabinetWClass" or "ExploreWClass")) return true;
                if (!match && name == "ApplicationFrameWindow")
                    EnumChildWindows(window, (child, _) => !(match = Matches(child)), 0);
            }
            if (!match) return true;
            found = window;
            return false;
        }, 0);

        if (found == 0) return false;
        ActivateWindow(found);
        return true;
    }

    internal static void ActivateWindow(nint window)
    {
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        if (!SetForegroundWindow(window)) FlashWindow(window, true);
    }

    private delegate bool WindowCallback(nint window, nint parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint window, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool FlashWindow(nint window, bool invert);
}
