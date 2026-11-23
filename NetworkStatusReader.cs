using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RadialMenu1;

internal sealed class NetworkStatusReader
{
    internal readonly record struct Sample(double Received, double Sent, double? Utilization);
    private readonly record struct InterfaceTotals(ulong Received, ulong Sent);
    private const byte HardwareInterface = 1, FilterInterface = 2;
    private const uint InterfaceUp = 1;
    private static readonly int TableOffset = Marshal.OffsetOf<InterfaceTable>(nameof(InterfaceTable.FirstRow)).ToInt32();
    private static readonly int RowSize = Marshal.SizeOf<InterfaceRow>();

    private Dictionary<ulong, InterfaceTotals> _previous = new();
    private long _previousTime;

    internal Sample? Read()
    {
        if (GetIfTable2(out nint table) != 0 || table == 0) return null;
        try
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = _previousTime == 0 ? 0 : Stopwatch.GetElapsedTime(_previousTime, now).TotalSeconds;
            var current = new Dictionary<ulong, InterfaceTotals>();
            double received = 0, sent = 0;
            double? utilization = null;
            bool hasSample = false;
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<InterfaceRow>(table + TableOffset + i * RowSize);
                if (row.OperStatus != InterfaceUp ||
                    (row.Flags & (HardwareInterface | FilterInterface)) != HardwareInterface)
                    continue;

                var totals = new InterfaceTotals(row.InOctets, row.OutOctets);
                current[row.Luid] = totals;
                if (elapsed is <= 0 or >= 5 || !_previous.TryGetValue(row.Luid, out var previous) ||
                    totals.Received < previous.Received || totals.Sent < previous.Sent)
                    continue;

                hasSample = true;
                double receiveRate = (totals.Received - previous.Received) / elapsed;
                double sendRate = (totals.Sent - previous.Sent) / elapsed;
                received += receiveRate;
                sent += sendRate;
                // Use the busiest direction. Simultaneous send/receive on a full-duplex link must not produce 200%.
                if (row.ReceiveSpeed > 0)
                    utilization = Math.Max(utilization ?? 0, receiveRate * 8 / row.ReceiveSpeed);
                if (row.TransmitSpeed > 0)
                    utilization = Math.Max(utilization ?? 0, sendRate * 8 / row.TransmitSpeed);
            }

            _previous = current;
            _previousTime = now;
            return hasSample
                ? new(received, sent, utilization is { } value ? Math.Clamp(value, 0, 1) : null)
                : null;
        }
        finally { FreeMibTable(table); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceTable
    {
        public uint Count;
        public InterfaceRow FirstRow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfaceRow
    {
        public ulong Luid;
        public uint Index;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitSpeed, ReceiveSpeed;
        public ulong InOctets, InUnicastPackets, InNonUnicastPackets, InDiscards, InErrors, InUnknownProtocols;
        public ulong InUnicastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUnicastPackets, OutNonUnicastPackets, OutDiscards, OutErrors;
        public ulong OutUnicastOctets, OutMulticastOctets, OutBroadcastOctets, OutQueueLength;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfTable2(out nint table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(nint table);
}
