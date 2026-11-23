using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace RadialMenu1;

internal static class ScreenRecording
{
    private const int FramesPerSecond = 30;
    private const long FrameDuration = TimeSpan.TicksPerSecond / FramesPerSecond;
    private static CancellationTokenSource? _stop;
    private static Task? _session;
    private static ScreenRecordingBadge? _badge;

    internal static bool IsRecording { get; private set; }
    internal static bool IsBusy { get; private set; }
    internal static string? LastFile { get; private set; }
    internal static string StatusText { get; private set; } = "Запись экрана остановлена.";
    internal static event Action? Changed;
    internal static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify), "RadialMenu");

    internal static async Task ToggleAsync()
    {
        if (IsBusy) return;
        if (IsRecording) await StopAsync();
        else await StartAsync(FolderPath);
    }

    private static async Task StartAsync(string destinationFolder)
    {
        Rectangle source;
        try { source = ScreenRecordingFrames.MonitorAtCursor(); }
        catch (Exception ex) when (IsRecordingError(ex))
        {
            StatusText = Describe(ex);
            Changed?.Invoke();
            return;
        }

        var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stop = stop;
        IsBusy = true;
        StatusText = "Подготовка записи…";
        _session = RunSessionAsync(source, destinationFolder, stop, started);
        Changed?.Invoke();
        if (await started.Task && ReferenceEquals(_stop, stop) && !stop.IsCancellationRequested)
        {
            IsRecording = true;
            IsBusy = false;
            StatusText = "Идёт запись экрана.";
            ShowBadge(source);
            Changed?.Invoke();
        }
    }

    internal static async Task StopAsync()
    {
        if (_session is not { } session || _stop is not { } stop) return;
        if (!stop.IsCancellationRequested)
        {
            IsRecording = false;
            IsBusy = true;
            StatusText = "Сохранение видео…";
            HideBadge();
            stop.Cancel();
            Changed?.Invoke();
        }
        await session;
    }

    private static async Task RunSessionAsync(Rectangle source, string destinationFolder,
        CancellationTokenSource stop, TaskCompletionSource<bool> started)
    {
        await Task.Yield();
        try
        {
            Directory.CreateDirectory(destinationFolder);
            string destination = Path.Combine(destinationFolder, $"RadialMenu {DateTime.Now:yyyy-MM-dd HH-mm-ss-fff}.mp4");
            var result = await Task.Run(() => Record(source, destination, stop.Token, started));
            if (result.File is { } file)
            {
                LastFile = file;
                StatusText = result.Error == null ?
                    $"Видео сохранено в папке «Видео → RadialMenu». {Path.GetFileName(file)}" :
                    $"Запись прервана. Доступная часть сохранена. {Describe(result.Error)}";
            }
            else StatusText = "Запись экрана остановлена.";
        }
        catch (Exception ex) when (IsRecordingError(ex)) { StatusText = Describe(ex); }
        finally
        {
            started.TrySetResult(false);
            HideBadge();
            IsRecording = IsBusy = false;
            _session = null;
            _stop = null;
            stop.Dispose();
            Changed?.Invoke();
        }
    }

    private static (string? File, Exception? Error) Record(Rectangle source, string destination, CancellationToken stop,
        TaskCompletionSource<bool> started)
    {
        if (stop.IsCancellationRequested) return default;
        string staging = Path.ChangeExtension(destination, $"{Guid.NewGuid():N}.recording.mp4");
        Exception? error = null;
        try
        {
            using var dpi = new ScreenRecordingFrames.PhysicalPixels();
            using var desktop = new ScreenRecordingFrames(source);
            using (var writer = new ScreenMp4Writer(staging, desktop.Width, desktop.Height, FramesPerSecond))
            {
                var clock = Stopwatch.StartNew();
                long next = 0;
                int frames = 0;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        long time = frames == 0 ? 0 : clock.Elapsed.Ticks;
                        desktop.Capture();
                        writer.WriteFrame(desktop.Pixels, time, FrameDuration);
                    }
                    catch (Exception ex) when (frames > 0 && IsRecordingError(ex))
                    {
                        error = ex;
                        break;
                    }
                    if (frames++ == 0) started.TrySetResult(true);

                    long elapsed = clock.Elapsed.Ticks;
                    next += FrameDuration;
                    if (next <= elapsed) next = (elapsed / FrameDuration + 1) * FrameDuration;
                    if (stop.WaitHandle.WaitOne(TimeSpan.FromTicks(next - elapsed))) break;
                }
                if (frames == 0) return default;
                writer.Finish();
            }
            File.Move(staging, destination);
            return (destination, error);
        }
        finally
        {
            try { File.Delete(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            using var explorer = Process.Start(new ProcessStartInfo(FolderPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (IsRecordingError(ex))
        {
            StatusText = Describe(ex);
            Changed?.Invoke();
        }
    }

    private static void ShowBadge(Rectangle screen)
    {
        if (Application.Current == null) return;
        try
        {
            var badge = new ScreenRecordingBadge(screen);
            _badge = badge;
            badge.Closed += async (_, _) =>
            {
                if (!ReferenceEquals(_badge, badge)) return;
                _badge = null;
                await StopAsync();
            };
            badge.Show();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _badge = null;
        }
    }

    private static void HideBadge()
    {
        var badge = _badge;
        _badge = null;
        badge?.Close();
    }

    private static bool IsRecordingError(Exception error) => error is IOException or UnauthorizedAccessException or COMException
        or Win32Exception or ArgumentException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException;

    private static string Describe(Exception error) => error switch
    {
        DllNotFoundException or EntryPointNotFoundException => "Windows Media Foundation недоступна. Проверьте компонент мультимедиа Windows.",
        COMException => $"Не удалось записать MP4. Код Windows 0x{error.HResult:X8}.",
        _ => $"Ошибка записи экрана. {error.Message}"
    };
}
