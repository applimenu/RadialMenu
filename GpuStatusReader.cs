using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RadialMenu1;

internal sealed class GpuStatusReader
{
    private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private const uint MoreData = 0x800007D2;
    private const uint NoData = 0x800007D5;
    private const uint FormatDoubleWithoutCap = 0x00008200;
    private const uint MaxCounterBufferSize = 16 * 1024 * 1024;
    private static readonly int ItemSize = Marshal.SizeOf<CounterItem>();

    private QueryHandle? _query;
    private nint _counter;
    private long _previousSample;
    private long _lastUnavailable;

    internal double? Read()
    {
        if (_query == null && !OpenQuery()) return null;

        uint result = PdhCollectQueryData(_query!);
        long now = Stopwatch.GetTimestamp();
        bool hasRecentSample = _previousSample != 0 &&
            Stopwatch.GetElapsedTime(_previousSample, now) < TimeSpan.FromSeconds(5);
        _previousSample = now;
        if (result == NoData) return null;
        if (result != 0)
        {
            _query!.Dispose();
            _query = null;
            _previousSample = 0;
            _lastUnavailable = now;
            return null;
        }
        return hasRecentSample ? ReadEngineLoad() : null;
    }

    private bool OpenQuery()
    {
        if (_lastUnavailable != 0 && Stopwatch.GetElapsedTime(_lastUnavailable) < TimeSpan.FromSeconds(10))
            return false;

        if (PdhOpenQueryW(null, 0, out var query) == 0 &&
            PdhAddEnglishCounterW(query, EnginePath, 0, out _counter) == 0)
        {
            _query = query;
            return true;
        }

        query?.Dispose();
        _lastUnavailable = Stopwatch.GetTimestamp();
        return false;
    }

    private double? ReadEngineLoad()
    {
        uint bytes = 0;
        if (PdhGetFormattedCounterArrayW(_counter, FormatDoubleWithoutCap, ref bytes, out _, 0) != MoreData ||
            bytes == 0 || bytes > MaxCounterBufferSize) return null;

        // Instances can be added between measuring the buffer and filling it.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint allocated = bytes;
            nint buffer = Marshal.AllocHGlobal((int)allocated);
            try
            {
                uint result = PdhGetFormattedCounterArrayW(_counter, FormatDoubleWithoutCap, ref bytes, out uint count, buffer);
                if (result == MoreData && bytes > allocated && bytes <= MaxCounterBufferSize) continue;
                if (result != 0 || count > allocated / ItemSize) return null;
                var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(buffer + i * ItemSize);
                    if (item.Value.Status > 1 || !double.IsFinite(item.Value.Percent)) continue;
                    string? instance = Marshal.PtrToStringUni(item.Name);
                    if (instance == null) continue;
                    string? engine = EngineName(instance);
                    if (engine != null)
                        engines[engine] = engines.GetValueOrDefault(engine) + Math.Max(0, item.Value.Percent);
                }

                // Counters are per process, so aggregate by hardware engine before finding the busiest one.
                return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max() / 100, 0, 1);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private static string? EngineName(string instance)
    {
        int first = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        int last = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
        return first >= 0 && last > first ? instance[first..last] : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public CounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Percent;
    }

    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle() : base(true) { }
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, nuint userData, out QueryHandle query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(QueryHandle query, string path, nuint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(QueryHandle query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes, out uint count, nint items);
}
