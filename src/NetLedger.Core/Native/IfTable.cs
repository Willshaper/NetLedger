using System.Runtime.InteropServices;

namespace NetLedger.Core.Native;

/// <summary>One network interface as reported by GetIfTable2, minus the filter-driver duplicates.</summary>
public readonly record struct InterfaceSnapshot(
    Guid Guid,
    string Alias,
    string Description,
    uint IfType,
    bool IsHardware,
    bool IsUp,
    ulong LinkSpeedBitsPerSecond,
    ulong InOctets,
    ulong OutOctets);

/// <summary>Reads per-interface byte counters with GetIfTable2 (one syscall, no admin rights needed).</summary>
public static unsafe partial class IfTable
{
    // MIB_IF_ROW2 field offsets (netioapi.h) for 64-bit processes; sizeof(MIB_IF_ROW2) == 1352.
    private const int RowSize = 1352;
    private const int TableHeaderSize = 8; // ULONG NumEntries, padded to the row's 8-byte alignment
    private const int OffGuid = 12;
    private const int OffAlias = 28;
    private const int OffDescription = 542;
    private const int OffType = 1128;
    private const int OffFlags = 1152;
    private const int OffOperStatus = 1156;
    private const int OffTransmitLinkSpeed = 1192;
    private const int OffReceiveLinkSpeed = 1200;
    private const int OffInOctets = 1208;
    private const int OffOutOctets = 1280;

    private const byte FlagHardwareInterface = 0x01;
    private const byte FlagFilterInterface = 0x02;
    private const int IfOperStatusUp = 1;

    public const uint IfTypeEthernet = 6;
    public const uint IfTypePpp = 23;
    public const uint IfTypeSoftwareLoopback = 24;
    public const uint IfTypeTunnel = 131;
    public const uint IfTypeWifi = 71;

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetIfTable2(out nint table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(nint memory);

    /// <summary>
    /// Fills <paramref name="output"/> with every real interface. NDIS filter rows (WFP, QoS, ...) repeat the
    /// counters of the adapter they sit on, so they are skipped, as is the software loopback.
    /// </summary>
    public static void Read(List<InterfaceSnapshot> output)
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("NetLedger needs a 64-bit process.");
        }

        output.Clear();
        int error = GetIfTable2(out nint table);
        if (error != 0)
        {
            throw new System.ComponentModel.Win32Exception(error);
        }

        try
        {
            byte* basePtr = (byte*)table;
            uint count = *(uint*)basePtr;
            for (uint i = 0; i < count; i++)
            {
                byte* row = basePtr + TableHeaderSize + i * RowSize;
                byte flags = row[OffFlags];
                uint type = *(uint*)(row + OffType);
                if ((flags & FlagFilterInterface) != 0 || type == IfTypeSoftwareLoopback)
                {
                    continue;
                }

                ulong tx = *(ulong*)(row + OffTransmitLinkSpeed);
                ulong rx = *(ulong*)(row + OffReceiveLinkSpeed);
                output.Add(new InterfaceSnapshot(
                    Guid: *(Guid*)(row + OffGuid),
                    Alias: new string((char*)(row + OffAlias)),
                    Description: new string((char*)(row + OffDescription)),
                    IfType: type,
                    IsHardware: (flags & FlagHardwareInterface) != 0,
                    IsUp: *(int*)(row + OffOperStatus) == IfOperStatusUp,
                    LinkSpeedBitsPerSecond: Math.Max(tx == ulong.MaxValue ? 0 : tx, rx == ulong.MaxValue ? 0 : rx),
                    InOctets: *(ulong*)(row + OffInOctets),
                    OutOctets: *(ulong*)(row + OffOutOctets)));
            }
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>
    /// The automatic choice for "counts toward the internet total": physical adapters only. VPN, Hyper-V/WSL
    /// switches and tunnels carry traffic that is also counted on the physical adapter underneath.
    /// </summary>
    public static bool CountsByDefault(bool isHardware, uint ifType) =>
        isHardware && ifType != IfTypeSoftwareLoopback && ifType != IfTypeTunnel;

    public static string DescribeType(uint ifType) => ifType switch
    {
        IfTypeEthernet => "Ethernet",
        IfTypeWifi => "Wi-Fi",
        IfTypePpp => "PPP",
        IfTypeTunnel => "Tunnel",
        53 => "Virtual",
        243 or 244 => "Mobile broadband",
        _ => $"Type {ifType}",
    };
}
