using System.IO;
using System.Runtime.InteropServices;

namespace RadialMenu1;

internal sealed class ExtendedSystemStatus
{
    private readonly GpuStatusReader _gpu = new();
    private readonly NetworkStatusReader _network = new();

    internal StatusReadings Read(StatusReadings readings)
    {
        var (memoryUsed, memoryTotal) = ReadMemory();
        var (diskUsed, diskTotal) = ReadSystemDrive();
        var network = _network.Read();

        return readings with
        {
            Gpu = _gpu.Read(),
            Memory = Fraction(memoryUsed, memoryTotal),
            MemoryUsedBytes = memoryUsed,
            MemoryTotalBytes = memoryTotal,
            Disk = Fraction(diskUsed, diskTotal),
            DiskUsedBytes = diskUsed,
            DiskTotalBytes = diskTotal,
            Network = network?.Utilization,
            NetworkBytesPerSecond = network is { } link ? link.Received + link.Sent : null,
            NetworkReceivedBytesPerSecond = network?.Received,
            NetworkSentBytesPerSecond = network?.Sent
        };
    }

    private static double? Fraction(ulong? used, ulong? total) =>
        used is { } u && total is > 0 ? (double)u / total.Value : null;

    private static (ulong? Used, ulong? Total) ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0) return default;
        return (status.TotalPhysical - Math.Min(status.TotalPhysical, status.AvailablePhysical), status.TotalPhysical);
    }

    private static (ulong? Used, ulong? Total) ReadSystemDrive()
    {
        string? root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrEmpty(root)) return default;
        try
        {
            var drive = new DriveInfo(root);
            long total = drive.TotalSize;
            long available = drive.TotalFreeSpace;
            return total > 0 ? ((ulong)(total - Math.Clamp(available, 0, total)), (ulong)total) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
