
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RadialMenu.UpdateHost;

internal static class Program
{
    private static readonly string Installer = Path.Combine(AppContext.BaseDirectory, "RadialMenu-Setup.exe");

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 8 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId) ||
            !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out long parentStart) ||
            !Version.TryParse(args[3], out var version) || !Path.IsPathRooted(args[2]) || !File.Exists(Installer))
            return;

        string executable = args[2];
        string displayVersion = version.ToString(version.Revision > 0 ? 4 : 3);
        var app = new Application();
        var window = CreateWindow(displayVersion, ReadBrush(args[4], Colors.Black),
            ReadBrush(args[5], Colors.White), ReadBrush(args[6], Colors.MediumPurple), args[7] == "1");

        bool finished = false;
        window.Closing += (_, e) => e.Cancel = !finished;
        window.Loaded += async (_, _) =>
        {
            try
            {
                var outcome = await Task.Run(() => Install(parentId, parentStart, executable, version));
                if (outcome is not null)
                    MessageBox.Show(window, outcome, window.Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
            {
                MessageBox.Show(window, ex.Message, window.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                try { File.Delete(Installer); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finished = true;
                window.Close();
            }
        };
        app.Run(window);
    }

    private static string? Install(int parentId, long parentStart, string executable, Version expectedVersion)
    {
        using var mutex = new Mutex(false, @"LocalRadialMenu.UpdateHost");
        try
        {
            if (!mutex.WaitOne(0)) return "Обновление RadialMenu уже выполняется.";
        }
        catch (AbandonedMutexException) { }

        try { return RunInstaller(parentId, parentStart, executable, expectedVersion); }
        finally { mutex.ReleaseMutex(); }
    }

    private static string? RunInstaller(int parentId, long parentStart, string executable, Version expectedVersion)
    {
        if (!WaitForAppToClose(parentId, parentStart))
            return "RadialMenu ещё работает. Закрой его через трей и повтори обновление.";

        string installDirectory = Path.GetDirectoryName(executable)!;
        string directoryArgument = installDirectory.EndsWith("\\", StringComparison.Ordinal) ? installDirectory + "\\" : installDirectory;
        var original = ReadVersion(installDirectory);
        var setup = new ProcessStartInfo(Installer,
            "/VERYSILENT /SUPPRESSMSGBOXES /SP- /NOCANCEL /NORESTART /RESTARTEXITCODE=3010 /NOFORCECLOSEAPPLICATIONS" +
            " /DIR=\"" + directoryArgument + "\"")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        int exitCode;
        try
        {
            using var process = Process.Start(setup) ?? throw new InvalidOperationException("Не удалось запустить обновление.");
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Win32Exception)
        {
            Restart(executable);
            return "Не удалось запустить установку. Попробуй обновиться ещё раз.";
        }

        if (exitCode == 3010)
            return "Чтобы завершить обновление RadialMenu, перезапусти Windows.";

        var installed = ReadVersion(installDirectory);
        if (exitCode == 0 && installed == expectedVersion)
        {
            Restart(executable);
            return null;
        }

        if (original is not null && original == installed) Restart(executable);
        return exitCode == 0
            ? "Установщик завершился, но версия RadialMenu не изменилась как ожидалось."
            : $"Установщик не смог завершить обновление (код {exitCode}).";
    }

    private static bool WaitForAppToClose(int id, long start)
    {
        try
        {
            using var parent = Process.GetProcessById(id);
            return parent.StartTime.ToUniversalTime().Ticks != start || parent.WaitForExit(30000);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    private static Version? ReadVersion(string directory)
    {
        string assembly = Path.Combine(directory, "RadialMenu1.dll");
        if (!File.Exists(assembly)) return null;
        var info = FileVersionInfo.GetVersionInfo(assembly);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    private static void Restart(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Не удалось снова открыть RadialMenu.");
    }

    private static Brush ReadBrush(string value, Color fallback)
    {
        return new SolidColorBrush(value.Length == 9 && value[0] == '#' &&
            uint.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)
            ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
            : fallback);
    }

    private static Window CreateWindow(string version, Brush background, Brush foreground, Brush accent, bool animate)
    {
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock
        {
            Text = $"Обновляем RadialMenu до {version}",
            FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = foreground, TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new ProgressBar
        {
            Height = 7, Margin = new Thickness(0, 14, 0, 12), IsIndeterminate = animate,
            Minimum = 0, Maximum = 100, Value = animate ? 0 : 100, Foreground = accent,
            Background = background, BorderBrush = accent
        });
        content.Children.Add(new TextBlock
        {
            Text = "RadialMenu откроется после установки.",
            Foreground = foreground, TextWrapping = TextWrapping.Wrap
        });

        return new Window
        {
            Title = "Обновление RadialMenu", Width = 400, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = true,
            Content = new Border
            {
                CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = accent,
                Background = background, Child = content
            }
        };
    }
}
