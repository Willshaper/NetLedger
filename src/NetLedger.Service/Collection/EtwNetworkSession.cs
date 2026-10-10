using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NetLedger.Service.Collection;

/// <summary>
/// Real-time ETW session on Microsoft-Windows-Kernel-Network. Every TCP/UDP send and receive event carries the
/// owning process id and payload size, which is how traffic is attributed to apps. Requires administrator rights.
/// </summary>
internal sealed unsafe partial class EtwNetworkSession : IDisposable
{
    private const string SessionName = "NetLedger-KernelNetwork";
    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private const ulong KeywordIpv4 = 0x10;
    private const ulong KeywordIpv6 = 0x20;

    private const uint ErrorSuccess = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorAlreadyExists = 183;
    private const uint EventTraceControlQuery = 0;
    private const uint EventTraceControlStop = 1;
    private const uint EventControlCodeEnableProvider = 1;
    private const byte TraceLevelInformation = 4;
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const ulong InvalidProcessTraceHandle = ulong.MaxValue;

    // EVENT_TRACE_PROPERTIES is followed by room for the session and log file names, which ETW fills in.
    private const int PropertiesBufferSize = 120 + 2 * 1024;

    private static EtwNetworkSession? s_current;

    private readonly object _gate = new();
    private Dictionary<uint, ByteCounts> _active = new();
    private Dictionary<uint, ByteCounts> _drained = new();
    private readonly EVENT_TRACE_PROPERTIES* _properties;
    private readonly nint _loggerName;
    private ulong _sessionHandle;
    private ulong _traceHandle = InvalidProcessTraceHandle;
    private Thread? _thread;
    private bool _disposed;

    private EtwNetworkSession()
    {
        _properties = (EVENT_TRACE_PROPERTIES*)NativeMemory.AllocZeroed(PropertiesBufferSize);
        _loggerName = Marshal.StringToHGlobalUni(SessionName);
    }

    /// <summary>Starts the session; throws <see cref="System.ComponentModel.Win32Exception"/> on failure.</summary>
    public static EtwNetworkSession Start()
    {
        var session = new EtwNetworkSession();
        try
        {
            session.StartCore();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void StartCore()
    {
        ResetProperties();
        uint status = StartTrace(out _sessionHandle, SessionName, _properties);
        if (status == ErrorAlreadyExists)
        {
            // Left behind by a previous run that didn't shut down cleanly.
            ResetProperties();
            ControlTrace(0, SessionName, _properties, EventTraceControlStop);
            ResetProperties();
            status = StartTrace(out _sessionHandle, SessionName, _properties);
        }

        ThrowIfFailed(status, "StartTrace");

        Guid provider = KernelNetworkProvider;
        status = EnableTraceEx2(_sessionHandle, &provider, EventControlCodeEnableProvider, TraceLevelInformation,
            KeywordIpv4 | KeywordIpv6, 0, 0, null);
        ThrowIfFailed(status, "EnableTraceEx2");

        EVENT_TRACE_LOGFILEW logFile = default;
        logFile.LoggerName = _loggerName;
        logFile.ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord;
        logFile.EventRecordCallback = (nint)(delegate* unmanaged<EVENT_RECORD*, void>)&OnEventRecord;
        _traceHandle = OpenTrace(&logFile);
        if (_traceHandle == InvalidProcessTraceHandle)
        {
            ThrowIfFailed((uint)Marshal.GetLastPInvokeError(), "OpenTrace");
        }

        s_current = this;
        _thread = new Thread(Pump) { IsBackground = true, Name = "ETW kernel network" };
        _thread.Start();
    }

    private void Pump()
    {
        ulong handle = _traceHandle;
        uint status = ProcessTrace(&handle, 1, null, null);
        if (status != ErrorSuccess && !_disposed)
        {
            Log.Warn($"ETW ProcessTrace ended with error {status}.");
        }
    }

    /// <summary>Takes the per-process byte counts gathered since the previous call.</summary>
    public Dictionary<uint, ByteCounts> Drain()
    {
        lock (_gate)
        {
            _drained.Clear();
            (_active, _drained) = (_drained, _active);
            return _drained;
        }
    }

    /// <summary>Events ETW had to drop because buffers were full.</summary>
    public long EventsLost()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            ResetProperties();
            return ControlTrace(_sessionHandle, null, _properties, EventTraceControlQuery) == ErrorSuccess
                ? _properties->EventsLost + _properties->RealTimeBuffersLost
                : 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_sessionHandle != 0)
        {
            ResetProperties();
            ControlTrace(_sessionHandle, null, _properties, EventTraceControlStop);
        }

        if (_traceHandle != InvalidProcessTraceHandle)
        {
            CloseTrace(_traceHandle);
        }

        _thread?.Join(TimeSpan.FromSeconds(5));
        if (s_current == this)
        {
            s_current = null;
        }

        NativeMemory.Free(_properties);
        Marshal.FreeHGlobal(_loggerName);
    }

    private void ResetProperties()
    {
        NativeMemory.Clear(_properties, PropertiesBufferSize);
        _properties->Wnode.BufferSize = PropertiesBufferSize;
        _properties->Wnode.Flags = WnodeFlagTracedGuid;
        _properties->Wnode.ClientContext = 1; // QueryPerformanceCounter timestamps
        _properties->BufferSize = 64; // KB
        _properties->MinimumBuffers = 4;
        _properties->MaximumBuffers = 32;
        _properties->LogFileMode = EventTraceRealTimeMode;
        _properties->FlushTimer = 1;
        _properties->LoggerNameOffset = 120;
    }

    private static void ThrowIfFailed(uint status, string what)
    {
        if (status == ErrorAccessDenied)
        {
            throw new UnauthorizedAccessException("Per-app tracking needs administrator rights; it works when NetLedger runs as a service.");
        }

        if (status != ErrorSuccess)
        {
            throw new System.ComponentModel.Win32Exception((int)status, $"{what} failed with Windows error {status}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEventRecord(EVENT_RECORD* record)
    {
        EtwNetworkSession? self = s_current;
        if (self is null || record->ProviderId != KernelNetworkProvider)
        {
            return;
        }

        bool sent;
        bool v6;
        switch (record->EventId)
        {
            case 10 or 42: sent = true; v6 = false; break;   // TCPv4 / UDPv4 send
            case 11 or 43: sent = false; v6 = false; break;  // TCPv4 / UDPv4 receive
            case 26 or 58: sent = true; v6 = true; break;    // TCPv6 / UDPv6 send
            case 27 or 59: sent = false; v6 = true; break;   // TCPv6 / UDPv6 receive
            default: return;
        }

        // Payload: PID (u32), size (u32), daddr, saddr (4 or 16 bytes each), ports...
        int addressSize = v6 ? 16 : 4;
        if (record->UserDataLength < 8 + 2 * addressSize)
        {
            return;
        }

        byte* data = (byte*)record->UserData;
        if (IsLoopback(data + 8, v6) || IsLoopback(data + 8 + addressSize, v6))
        {
            return;
        }

        uint pid = *(uint*)data;
        uint size = *(uint*)(data + 4);
        lock (self._gate)
        {
            ref ByteCounts counts = ref CollectionsMarshal.GetValueRefOrAddDefault(self._active, pid, out _);
            if (sent)
            {
                counts.Sent += size;
            }
            else
            {
                counts.Received += size;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLoopback(byte* address, bool v6)
    {
        if (!v6)
        {
            return address[0] == 127;
        }

        // ::1, or an IPv4-mapped loopback (::ffff:127.x.x.x).
        ulong high = *(ulong*)address;
        uint mid = *(uint*)(address + 8);
        if (high != 0)
        {
            return false;
        }

        if (mid == 0 && *(uint*)(address + 12) == 0x01000000)
        {
            return true;
        }

        return mid == 0xFFFF0000 && address[12] == 127;
    }

    // ---- native layouts (64-bit) ----

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct WNODE_HEADER
    {
        [FieldOffset(0)] public uint BufferSize;
        [FieldOffset(40)] public uint ClientContext;
        [FieldOffset(44)] public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct EVENT_TRACE_PROPERTIES
    {
        [FieldOffset(0)] public WNODE_HEADER Wnode;
        [FieldOffset(48)] public uint BufferSize;
        [FieldOffset(52)] public uint MinimumBuffers;
        [FieldOffset(56)] public uint MaximumBuffers;
        [FieldOffset(64)] public uint LogFileMode;
        [FieldOffset(68)] public uint FlushTimer;
        [FieldOffset(88)] public uint EventsLost;
        [FieldOffset(100)] public uint RealTimeBuffersLost;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EVENT_TRACE_LOGFILEW
    {
        [FieldOffset(8)] public nint LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public nint EventRecordCallback;
    }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    private struct EVENT_RECORD
    {
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public nint UserData;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTrace(out ulong sessionHandle, string sessionName, EVENT_TRACE_PROPERTIES* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTrace(ulong sessionHandle, string? sessionName, EVENT_TRACE_PROPERTIES* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    private static partial uint EnableTraceEx2(
        ulong sessionHandle, Guid* providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, void* enableParameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    private static partial ulong OpenTrace(EVENT_TRACE_LOGFILEW* logFile);

    [LibraryImport("advapi32.dll")]
    private static partial uint ProcessTrace(ulong* handles, uint handleCount, void* startTime, void* endTime);

    [LibraryImport("advapi32.dll")]
    private static partial uint CloseTrace(ulong traceHandle);
}
