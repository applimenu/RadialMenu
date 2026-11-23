using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RadialMenu1;

internal readonly record struct StatusReadings(double? Cpu, double? Volume, bool Muted, double? Battery, bool OnAc)
{
    public double? Gpu { get; init; }
    public double? Memory { get; init; }
    public ulong? MemoryUsedBytes { get; init; }
    public ulong? MemoryTotalBytes { get; init; }
    public double? Disk { get; init; }
    public ulong? DiskUsedBytes { get; init; }
    public ulong? DiskTotalBytes { get; init; }
    public double? Network { get; init; }
    public double? NetworkBytesPerSecond { get; init; }
    public double? NetworkReceivedBytesPerSecond { get; init; }
    public double? NetworkSentBytesPerSecond { get; init; }
}
internal enum CountdownState { Ready, Running, Finished }
internal readonly record struct CountdownStatus(CountdownState State, TimeSpan Remaining, TimeSpan Duration)
{
    public double Fraction => State == CountdownState.Ready ? 1 : Math.Clamp(Remaining.TotalSeconds / Duration.TotalSeconds, 0, 1);
    public string Clock
    {
        get
        {
            int seconds = (int)Math.Ceiling(Remaining.TotalSeconds);
            return $"{seconds / 60:00}:{seconds % 60:00}";
        }
    }
}

internal sealed class StatusRingMonitor
{
    public static StatusRingMonitor Instance { get; } = new();
    private static readonly int[] Durations = [1, 5, 10, 15, 25, 45, 60];
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _alarm = new(DispatcherPriority.Background);
    private readonly SystemStatusReader _reader = new();
    private int _durationIndex = 1;
    private long _started;
    private bool _reading;
    private CountdownState _state;
    private event Action? Changed;
    public event Action? TimerFinished;

    internal StatusReadings Readings { get; private set; }
    internal CountdownStatus Countdown
    {
        get
        {
            var duration = TimeSpan.FromMinutes(Durations[_durationIndex]);
            var remaining = _state switch
            {
                CountdownState.Ready => duration,
                CountdownState.Running => duration - Stopwatch.GetElapsedTime(_started),
                _ => TimeSpan.Zero
            };
            return new(_state, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, duration);
        }
    }

    private StatusRingMonitor()
    {
        _poll.Tick += (_, _) => Poll();
        _alarm.Tick += (_, _) => FinishWhenDue();
    }

    internal void Watch(Action changed)
    {
        Changed += changed;
        if (_poll.IsEnabled) return;
        _poll.Start();
        Poll();
    }

    internal void Unwatch(Action changed)
    {
        Changed -= changed;
        if (Changed == null) _poll.Stop();
    }

    internal void ToggleTimer()
    {
        _alarm.Stop();
        if (_state == CountdownState.Running) _state = CountdownState.Ready;
        else
        {
            _state = CountdownState.Running;
            _started = Stopwatch.GetTimestamp();
            _alarm.Interval = Countdown.Duration;
            _alarm.Start();
        }
        Changed?.Invoke();
    }

    internal void ChangeDuration(int direction)
    {
        if (_state == CountdownState.Running || direction == 0) return;
        _durationIndex = Math.Clamp(_durationIndex + Math.Sign(direction), 0, Durations.Length - 1);
        _state = CountdownState.Ready;
        Changed?.Invoke();
    }

    private void FinishWhenDue()
    {
        _alarm.Stop();
        if (_state != CountdownState.Running) return;
        var remaining = Countdown.Remaining;
        if (remaining > TimeSpan.Zero)
        {
            _alarm.Interval = remaining;
            _alarm.Start();
            return;
        }
        _state = CountdownState.Finished;
        Changed?.Invoke();
        TimerFinished?.Invoke();
    }

    private async void Poll()
    {
        if (_reading) return;
        _reading = true;
        var readings = await Task.Run(_reader.Read).ConfigureAwait(false);
        if (_poll.Dispatcher.HasShutdownStarted) return;
        try
        {
            await _poll.Dispatcher.InvokeAsync(() =>
            {
                _reading = false;
                Readings = readings;
                Changed?.Invoke();
            }, DispatcherPriority.Background);
        }
        catch (TaskCanceledException) when (_poll.Dispatcher.HasShutdownStarted) { }
    }
}

internal sealed class SystemStatusReader
{
    private (ulong Idle, ulong Kernel, ulong User)? _previousCpu;
    private long _previousCpuTime;
    private readonly ExtendedSystemStatus _extended = new();

    internal StatusReadings Read()
    {
        double? cpu = null;
        if (GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            var current = (idle, kernel, user);
            long now = Stopwatch.GetTimestamp();
            if (_previousCpu is { } previous && Stopwatch.GetElapsedTime(_previousCpuTime, now).TotalSeconds < 2)
                cpu = CpuLoad(previous, current);
            _previousCpu = current;
            _previousCpuTime = now;
        }

        var (volume, muted) = ReadAudio();
        double? battery = null;
        bool onAc = false;
        if (GetSystemPowerStatus(out var power))
        {
            if (power.BatteryFlag != 255 && (power.BatteryFlag & 128) == 0 && power.BatteryLifePercent <= 100)
                battery = power.BatteryLifePercent / 100.0;
            onAc = power.AcLineStatus == 1;
        }
        return _extended.Read(new(cpu, volume, muted, battery, onAc));
    }

    internal static double? CpuLoad((ulong Idle, ulong Kernel, ulong User) previous, (ulong Idle, ulong Kernel, ulong User) current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User) return null;
        double total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        return total > 0 ? Math.Clamp(1 - (current.Idle - previous.Idle) / total, 0, 1) : null;
    }

    private static (double? Volume, bool Muted) ReadAudio()
    {
        const int render = 0, multimedia = 1;
        const uint allComContexts = 23;
        IDeviceEnumerator? enumerator = null;
        IAudioDevice? device = null;
        IEndpointVolume? endpoint = null;
        try
        {
            enumerator = (IDeviceEnumerator)new DeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(render, multimedia, out device) < 0) return default;
            var interfaceId = typeof(IEndpointVolume).GUID;
            if (device.Activate(ref interfaceId, allComContexts, 0, out endpoint) < 0 ||
                endpoint.GetMasterVolumeLevelScalar(out float volume) < 0 || !float.IsFinite(volume)) return default;
            bool muted = endpoint.GetMute(out int mute) >= 0 && mute != 0;
            return (Math.Clamp(volume, 0, 1), muted);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return default; }
        finally
        {
            if (endpoint != null) Marshal.ReleaseComObject(endpoint);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class DeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IAudioDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint context, nint activation,
            [MarshalAs(UnmanagedType.Interface)] out IEndpointVolume volume);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint channels);
        [PreserveSig] int SetMasterVolumeLevel(float level, nint context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, nint context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, nint context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, nint context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int mute, nint context);
        [PreserveSig] int GetMute(out int mute);
    }
}
