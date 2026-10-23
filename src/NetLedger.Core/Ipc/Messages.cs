using System.Text.Json.Serialization;

namespace NetLedger.Core.Ipc;

/// <summary>Operation names understood by the service.</summary>
public static class Ops
{
    public const string Status = "status";
    public const string GetSettings = "getSettings";
    public const string SetSettings = "setSettings";
    public const string Adapters = "adapters";
    public const string SetAdapter = "setAdapter";
    public const string Usage = "usage";
    public const string Apps = "apps";
    public const string Outages = "outages";
    public const string Quota = "quota";
    public const string AckQuota = "ackQuota";
    public const string Export = "export";
    public const string ImportPreview = "importPreview";
    public const string Import = "import";
    public const string UndoImport = "undoImport";
}

public sealed class IpcRequest
{
    public string Op { get; set; } = "";
    public UsageQuery? Usage { get; set; }
    public RangeQuery? Range { get; set; }
    public ServiceSettings? Settings { get; set; }
    public AdapterUpdate? Adapter { get; set; }
    public QuotaAck? QuotaAck { get; set; }
    public BackupData? Backup { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ServiceStatus? Status { get; set; }
    public ServiceSettings? Settings { get; set; }
    public List<AdapterInfo>? Adapters { get; set; }
    public List<UsageBucket>? Buckets { get; set; }
    public List<AppUsageRow>? Apps { get; set; }
    public List<OutageRow>? Outages { get; set; }
    public QuotaStatus? Quota { get; set; }
    public BackupData? Backup { get; set; }
    public ImportPreview? Preview { get; set; }
    public ImportResult? Import { get; set; }

    public static IpcResponse Success() => new() { Ok = true };

    public static IpcResponse Failure(string error) => new() { Ok = false, Error = error };
}

public enum AdapterSelection
{
    /// <summary>The adapters that count toward the internet total.</summary>
    Counted,
    All,
    Single,
}

public sealed class UsageQuery
{
    public BucketKind Kind { get; set; }

    /// <summary>Inclusive local start. <c>default</c> means "from the first recorded data".</summary>
    public DateTime FromLocal { get; set; }

    /// <summary>Exclusive local end.</summary>
    public DateTime ToLocal { get; set; }

    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;
    public AdapterSelection Adapters { get; set; }
    public long AdapterId { get; set; }
}

public sealed class RangeQuery
{
    /// <summary>Inclusive local start; <c>default</c> means no lower bound.</summary>
    public DateTime FromLocal { get; set; }

    /// <summary>Exclusive local end; <c>default</c> means no upper bound.</summary>
    public DateTime ToLocal { get; set; }
}

public sealed class UsageBucket
{
    public DateTime LocalStart { get; set; }
    public long StartUtc { get; set; }
    public long Received { get; set; }
    public long Sent { get; set; }

    [JsonIgnore]
    public long Total => Received + Sent;
}

public sealed class AppUsageRow
{
    public long AppId { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public long Received { get; set; }
    public long Sent { get; set; }
}

public sealed class AdapterInfo
{
    public long Id { get; set; }
    public Guid Guid { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public uint IfType { get; set; }
    public bool IsHardware { get; set; }

    /// <summary>User override; <c>null</c> means the automatic rule decides.</summary>
    public bool? CountsOverride { get; set; }

    public bool CountsInTotal { get; set; }
    public bool Present { get; set; }
    public long LastSeenUtc { get; set; }
}

public sealed class AdapterUpdate
{
    public long Id { get; set; }
    public bool? CountsOverride { get; set; }
}

public sealed class ServiceSettings
{
    public bool AppTracking { get; set; } = true;
    public int MinuteRetentionDays { get; set; } = 30;
    public bool OutageLog { get; set; } = true;
    public string ProbeUrl { get; set; } = "http://www.msftconnecttest.com/connecttest.txt";
    public string ProbeExpect { get; set; } = "Microsoft Connect Test";
    public int ProbeIntervalSeconds { get; set; } = 60;
    public QuotaSettings Quota { get; set; } = new();
}

public sealed class QuotaSettings
{
    public bool Enabled { get; set; }
    public long LimitBytes { get; set; } = 1_000_000_000_000;
    public int CycleStartDay { get; set; } = 1;
    public bool CountUpload { get; set; } = true;
    public List<int> AlertPercents { get; set; } = [80, 100];
}

public sealed class QuotaStatus
{
    public bool Enabled { get; set; }
    public long LimitBytes { get; set; }
    public long UsedBytes { get; set; }
    public DateTime CycleStartLocal { get; set; }
    public DateTime CycleEndLocal { get; set; }

    /// <summary>Highest alert threshold already shown for this cycle.</summary>
    public int NotifiedPercent { get; set; }

    public List<int> AlertPercents { get; set; } = [];

    /// <summary>Usage at the end of the cycle if the average rate so far continues.</summary>
    public long ProjectedBytes { get; set; }

    [JsonIgnore]
    public double Percent => LimitBytes > 0 ? UsedBytes * 100.0 / LimitBytes : 0;
}

public sealed class QuotaAck
{
    public DateTime CycleStartLocal { get; set; }
    public int Percent { get; set; }
}

public sealed class OutageRow
{
    public long StartUtc { get; set; }

    /// <summary><c>null</c> while the outage is still going on.</summary>
    public long? EndUtc { get; set; }
}

public sealed class ServiceStatus
{
    public string Version { get; set; } = "";
    public bool AppTracking { get; set; }
    public string? AppTrackingError { get; set; }
    public long EventsLost { get; set; }

    /// <summary><c>null</c> when the outage log is off or no probe has finished yet.</summary>
    public bool? Online { get; set; }

    public long? OfflineSinceUtc { get; set; }
    public bool CanUndoImport { get; set; }
}

public sealed class ImportPreview
{
    public string? MachineName { get; set; }
    public long ExportedUtc { get; set; }
    public long? FirstUtc { get; set; }
    public long? LastUtc { get; set; }
    public int HourCount { get; set; }
    public int OverlappingHours { get; set; }
    public int AppRows { get; set; }
    public int Outages { get; set; }
    public long Received { get; set; }
    public long Sent { get; set; }
}

public sealed class ImportResult
{
    public int HoursReplaced { get; set; }
    public int RowsImported { get; set; }
}
