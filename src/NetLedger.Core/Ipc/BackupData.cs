using System.Text.Json.Serialization;

namespace NetLedger.Core.Ipc;

/// <summary>Everything NetLedger has recorded, as written to a <c>.netledger</c> backup file.</summary>
public sealed class BackupData
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string? MachineName { get; set; }
    public long ExportedUtc { get; set; }
    public List<BackupAdapter> Adapters { get; set; } = [];
    public List<BackupApp> Apps { get; set; } = [];

    /// <summary>Per-adapter hourly totals; <see cref="UsageRecord.Id"/> is an adapter id.</summary>
    public List<UsageRecord> Hours { get; set; } = [];

    /// <summary>Per-adapter minute totals (only the recent retention window is kept).</summary>
    public List<UsageRecord> Minutes { get; set; } = [];

    /// <summary>Per-app hourly totals; <see cref="UsageRecord.Id"/> is an app id.</summary>
    public List<UsageRecord> AppHours { get; set; } = [];

    public List<OutageRow> Outages { get; set; } = [];
}

public sealed class BackupAdapter
{
    public long Id { get; set; }
    public Guid Guid { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public uint IfType { get; set; }
    public bool IsHardware { get; set; }
    public bool? CountsOverride { get; set; }
}

public sealed class BackupApp
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Path { get; set; }
}

/// <summary>One bucket row. Short JSON names keep multi-year backups small.</summary>
public struct UsageRecord
{
    public UsageRecord(long time, long id, long received, long sent)
    {
        Time = time;
        Id = id;
        Received = received;
        Sent = sent;
    }

    /// <summary>Bucket start, UTC Unix seconds.</summary>
    [JsonPropertyName("t")]
    public long Time { get; set; }

    [JsonPropertyName("i")]
    public long Id { get; set; }

    [JsonPropertyName("r")]
    public long Received { get; set; }

    [JsonPropertyName("s")]
    public long Sent { get; set; }
}
