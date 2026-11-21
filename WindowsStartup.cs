using System.IO;
using Microsoft.Win32;

namespace RadialMenu1;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RadialMenu";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public static void SetRegistered(bool enabled)
    {
        if (!enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string? executable = Environment.ProcessPath;
        if (executable is null || string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для автозапуска открой RadialMenu через его EXE-файл.");

        string command = $"\"{executable}\"";
        if (command.Length > 260)
            throw new InvalidOperationException("Для автозапуска перемести RadialMenu в папку с более коротким путём.");

        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
