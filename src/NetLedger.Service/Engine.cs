using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using NetLedger.Core;
using NetLedger.Core.Ipc;
using NetLedger.Core.Native;
using NetLedger.Service.Collection;
using NetLedger.Service.Ipc;
using NetLedger.Service.Storage;

namespace NetLedger.Service;

/// <summary>
/// Samples adapter counters every second, attributes traffic to apps, watches connectivity, flushes to SQLite
/// once a minute and answers the tray app. Every piece of state is guarded by <see cref="_sync"/>.
/// </summary>
internal sealed class Engine : IDisposable
{
    private const int FlushIntervalSeconds = 60;
    private const int MaintenanceIntervalSeconds = 3600;
    private const int MaxBuckets = 200_000;
    private const int SnapshotsKept = 5;
    private const string SettingsKey = "settings";
    private const string QuotaNotifiedKey = "quotaNotified";
    private const string LastImportSnapshotKey = "lastImportSnapshot";

    private readonly string _dataDirectory;
    private readonly bool _secureDataDirectory;
    private readonly object _sync = new();
    private readonly CounterTracker _counters = new();
    private readonly List<InterfaceSnapshot> _snapshots = new();
    private readonly List<InterfaceDelta> _deltas = new();
    private readonly HashSet<Guid> _present = new();
    private readonly Dictionary<Guid, AdapterRecord> _adapters = new();
    private readonly HashSet<Guid> _touchedAdapters = new();
    private readonly PendingUsage _pending = new();
    private readonly ProcessResolver _resolver = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _version;

    private Database _db = null!;
    private Dictionary<string, long> _appIds = new();
    private ServiceSettings _settings = new();
    private EtwNetworkSession? _etw;
    private string? _etwError;
    private ConnectivityMonitor? _connectivity;
    private PipeServer? _pipe;
    private Task? _loop;
    private long _startedUtc;
    private long _lastFlushUtc;
    private long _lastMaintenanceUtc;
    private bool _suspended;
    private int _ticksWhileSuspended;

    public Engine(string dataDirectory, bool secureDataDirectory)
    {
        _dataDirectory = dataDirectory;
        _secureDataDirectory = secureDataDirectory;
        string? informational = typeof(Engine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        _version = informational?.Split('+')[0] ?? "0.0.0";
    }

    private static long NowUtc => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private string SnapshotDirectory => Path.Combine(_dataDirectory, "snapshots");

    public void Start()
    {
        PrepareDataDirectory();
        Log.Info($"NetLedger service {_version} starting; data in {_dataDirectory}");

        lock (_sync)
        {
            _db = new Database(Path.Combine(_dataDirectory, "netledger.db"));
            _settings = LoadSettings();
            ReloadCaches();
            _db.CloseDanglingOutages();
            _startedUtc = NowUtc;
            _lastFlushUtc = _startedUtc;
            _lastMaintenanceUtc = 0;
            ApplyAppTracking();
        }

        ApplyConnectivity();
        _pipe = new PipeServer(Handle);
        _pipe.Start();
        _loop = Task.Run(LoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }

        _pipe?.Dispose();
        _connectivity?.Dispose();
        lock (_sync)
        {
            _etw?.Dispose();
            _etw = null;
            DrainAppTraffic(NowUtc);
            FlushLocked(NowUtc);
            _db?.Dispose();
        }

        Log.Info("NetLedger service stopped.");
    }

    public void Suspend()
    {
        lock (_sync)
        {
            DrainAppTraffic(NowUtc);
            FlushLocked(NowUtc);
            _suspended = true;
            _ticksWhileSuspended = 0;
        }

        _connectivity?.Suspend();
        Log.Info("Suspending.");
    }

    public void Resume()
    {
        lock (_sync)
        {
            ResumeLocked();
        }

        _connectivity?.Resume();
        Log.Info("Resumed.");
    }

    private void ResumeLocked()
    {
        _counters.Reset();
        _suspended = false;
    }

    // ---- sampling ----

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        long last = Stopwatch.GetTimestamp();
        do
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Stopwatch.GetElapsedTime(last, now).TotalSeconds;
            last = now;
            try
            {
                lock (_sync)
                {
                    Tick(elapsed);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Sampling failed", ex);
            }
        }
        while (await WaitAsync(timer).ConfigureAwait(false));
    }

    private async Task<bool> WaitAsync(PeriodicTimer timer)
    {
        try
        {
            return await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Tick(double elapsedSeconds)
    {
        if (_suspended)
        {
            // A resume notification can go missing; a minute of normal ticking proves we're awake.
            if (++_ticksWhileSuspended < 60)
            {
                return;
            }

            Log.Warn("No resume notification arrived; resuming anyway.");
            ResumeLocked();
        }

        long now = NowUtc;
        IfTable.Read(_snapshots);
        _present.Clear();
        foreach (InterfaceSnapshot snapshot in _snapshots)
        {
            _present.Add(snapshot.Guid);
        }

        _counters.Update(_snapshots, elapsedSeconds, _deltas);
        foreach (InterfaceDelta delta in _deltas)
        {
            AdapterRecord adapter = EnsureAdapter(delta.Interface, now);
            _pending.AddAdapter(now, adapter.Id, (long)delta.Received, (long)delta.Sent);
        }

        DrainAppTraffic(now);

        if (now - _lastFlushUtc >= FlushIntervalSeconds)
        {
            FlushLocked(now);
        }

        if (now - _lastMaintenanceUtc >= MaintenanceIntervalSeconds)
        {
            Maintenance(now);
        }
    }

    private AdapterRecord EnsureAdapter(InterfaceSnapshot snapshot, long now)
    {
        if (!_adapters.TryGetValue(snapshot.Guid, out AdapterRecord? adapter))
        {
            adapter = _db.AddAdapter(snapshot.Guid, snapshot.Alias, snapshot.Description, snapshot.IfType, snapshot.IsHardware, null, now);
            _adapters[snapshot.Guid] = adapter;
            Log.Info($"New adapter: {adapter.Name} ({adapter.Description}), counts toward total: {adapter.CountsInTotal}");
        }
        else if (adapter.Name != snapshot.Alias || adapter.Description != snapshot.Description || adapter.LastSeenUtc != now)
        {
            adapter = adapter with { Name = snapshot.Alias, Description = snapshot.Description, LastSeenUtc = now };
            _adapters[snapshot.Guid] = adapter;
        }

        _touchedAdapters.Add(snapshot.Guid);
        return adapter;
    }

    private void DrainAppTraffic(long now)
    {
        if (_etw is null)
        {
            return;
        }

        foreach (KeyValuePair<uint, ByteCounts> entry in _etw.Drain())
        {
            AppIdentity app = _resolver.Resolve(entry.Key);
            if (!_appIds.TryGetValue(app.Key, out long appId))
            {
                appId = _db.AddApp(app.Key, app.Name, app.Path);
                _appIds[app.Key] = appId;
            }

            _pending.AddApp(now, appId, entry.Value.Received, entry.Value.Sent);
        }
    }

    private void FlushLocked(long now)
    {
        _lastFlushUtc = now;
        if (_pending.IsEmpty && _touchedAdapters.Count == 0)
        {
            return;
        }

        try
        {
            _db.Flush(_pending, _touchedAdapters.Select(g => _adapters[g]), now);
            _pending.Clear();
            _touchedAdapters.Clear();
        }
        catch (Exception ex)
        {
            // Keep the pending data and try again at the next flush.
            Log.Error("Flush failed", ex);
        }
    }

    private void Maintenance(long now)
    {
        _lastMaintenanceUtc = now;
        try
        {
            int pruned = _db.PruneMinutes(now - (long)_settings.MinuteRetentionDays * 86400);
            if (pruned > 0)
            {
                Log.Info($"Pruned {pruned} minute rows.");
            }

            _resolver.Prune();
            _db.Optimize();
        }
        catch (Exception ex)
        {
            Log.Error("Maintenance failed", ex);
        }
    }

    private void ReloadCaches()
    {
        _adapters.Clear();
        foreach (AdapterRecord adapter in _db.LoadAdapters())
        {
            _adapters[adapter.Guid] = adapter;
        }

        _appIds = _db.LoadAppIds();
    }

    // ---- settings ----

    private ServiceSettings LoadSettings()
    {
        string? json = _db.GetMeta(SettingsKey);
        if (json is null)
        {
            return new ServiceSettings();
        }

        try
        {
            return JsonSerializer.Deserialize(json, LedgerJson.Default.ServiceSettings) ?? new ServiceSettings();
        }
        catch (JsonException ex)
        {
            Log.Warn($"Settings unreadable, using defaults: {ex.Message}");
            return new ServiceSettings();
        }
    }

    private void ApplyAppTracking()
    {
        if (_settings.AppTracking && _etw is null)
        {
            try
            {
                _etw = EtwNetworkSession.Start();
                _etwError = null;
                Log.Info("Per-app tracking started.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _etwError = ex.Message;
                Log.Warn(ex.Message);
            }
            catch (Exception ex)
            {
                _etwError = $"Per-app tracking couldn't start: {ex.Message}.";
                Log.Error("Per-app tracking unavailable", ex);
            }
        }
        else if (!_settings.AppTracking && _etw is not null)
        {
            DrainAppTraffic(NowUtc);
            _etw.Dispose();
            _etw = null;
            Log.Info("Per-app tracking stopped.");
        }
    }

    // Called outside _sync: the monitor's events take _sync themselves.
    private void ApplyConnectivity()
    {
        ServiceSettings settings;
        lock (_sync)
        {
            settings = _settings;
        }

        if (settings.OutageLog && _connectivity is null)
        {
            _connectivity = new ConnectivityMonitor(settings.ProbeUrl, settings.ProbeExpect, settings.ProbeIntervalSeconds);
            _connectivity.OutageStarted += start =>
            {
                lock (_sync)
                {
                    _db.StartOutage(start);
                }

                Log.Info($"Internet connection lost at {DateTimeOffset.FromUnixTimeSeconds(start).ToLocalTime():HH:mm:ss}.");
            };
            _connectivity.OutageEnded += (start, end) =>
            {
                lock (_sync)
                {
                    _db.EndOutage(start, end);
                }

                Log.Info($"Internet connection back after {end - start} s.");
            };
            _connectivity.Start();
        }
        else if (settings.OutageLog && _connectivity is not null)
        {
            _connectivity.Configure(settings.ProbeUrl, settings.ProbeExpect, settings.ProbeIntervalSeconds);
        }
        else if (!settings.OutageLog && _connectivity is not null)
        {
            _connectivity.Dispose();
            _connectivity = null;
            lock (_sync)
            {
                _db.CloseDanglingOutages();
            }
        }
    }

    private static void Validate(ServiceSettings settings)
    {
        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new ArgumentException(message);
            }
        }

        Require(settings.MinuteRetentionDays is >= 1 and <= 366, "Minute retention must be 1–366 days.");
        Require(settings.ProbeIntervalSeconds is >= 10 and <= 3600, "The probe interval must be 10–3600 seconds.");
        Require(Uri.TryCreate(settings.ProbeUrl, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
            "The probe URL must be an http(s) address.");
        Require(settings.ProbeExpect.Length <= 200, "The expected probe text is too long.");
        Require(settings.Quota is not null, "Quota settings are missing.");
        Require(settings.Quota!.LimitBytes > 0, "The quota must be larger than zero.");
        Require(settings.Quota.CycleStartDay is >= 1 and <= 31, "The billing day must be 1–31.");
        Require(settings.Quota.AlertPercents.Count <= 10 && settings.Quota.AlertPercents.All(p => p is >= 1 and <= 1000),
            "Alert thresholds must be 1–1000 %.");
    }

    // ---- requests ----

    private IpcResponse Handle(IpcRequest request)
    {
        if (request.Op == Ops.SetSettings)
        {
            // Changing settings can start or stop the connectivity monitor, which must happen outside _sync.
            ServiceSettings settings = request.Settings ?? throw new ArgumentException("Settings missing.");
            Validate(settings);
            lock (_sync)
            {
                _settings = settings;
                _db.SetMeta(SettingsKey, JsonSerializer.Serialize(settings, LedgerJson.Default.ServiceSettings));
                ApplyAppTracking();
            }

            ApplyConnectivity();
            return new IpcResponse { Ok = true, Settings = settings };
        }

        lock (_sync)
        {
            return request.Op switch
            {
                Ops.Status => new IpcResponse { Ok = true, Status = BuildStatus() },
                Ops.GetSettings => new IpcResponse { Ok = true, Settings = _settings },
                Ops.Adapters => new IpcResponse { Ok = true, Adapters = BuildAdapterList() },
                Ops.SetAdapter => SetAdapter(request.Adapter ?? throw new ArgumentException("Adapter missing.")),
                Ops.Usage => QueryUsage(request.Usage ?? throw new ArgumentException("Query missing.")),
                Ops.Apps => QueryApps(request.Range ?? new RangeQuery()),
                Ops.Outages => QueryOutages(request.Range ?? new RangeQuery()),
                Ops.Quota => new IpcResponse { Ok = true, Quota = BuildQuota() },
                Ops.AckQuota => AckQuota(request.QuotaAck ?? throw new ArgumentException("Acknowledgement missing.")),
                Ops.Export => Export(),
                Ops.ImportPreview => PreviewImport(request.Backup ?? throw new ArgumentException("Backup missing.")),
                Ops.Import => Import(request.Backup ?? throw new ArgumentException("Backup missing.")),
                Ops.UndoImport => UndoImport(),
                _ => IpcResponse.Failure($"Unknown operation '{request.Op}'."),
            };
        }
    }

    private ServiceStatus BuildStatus() => new()
    {
        Version = _version,
        StartedUtc = _startedUtc,
        AppTracking = _etw is not null,
        AppTrackingError = _settings.AppTracking ? _etwError : null,
        EventsLost = _etw?.EventsLost() ?? 0,
        Online = _connectivity?.Online,
        OfflineSinceUtc = _connectivity?.OfflineSinceUtc,
        CanUndoImport = _db.GetMeta(LastImportSnapshotKey) is string name && File.Exists(Path.Combine(SnapshotDirectory, name)),
    };

    private List<AdapterInfo> BuildAdapterList() =>
        _adapters.Values
            .OrderByDescending(a => _present.Contains(a.Guid))
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new AdapterInfo
            {
                Id = a.Id,
                Guid = a.Guid,
                Name = a.Name,
                Description = a.Description,
                IfType = a.IfType,
                IsHardware = a.IsHardware,
                CountsOverride = a.CountsOverride,
                CountsInTotal = a.CountsInTotal,
                Present = _present.Contains(a.Guid),
                LastSeenUtc = a.LastSeenUtc,
            })
            .ToList();

    private IpcResponse SetAdapter(AdapterUpdate update)
    {
        AdapterRecord adapter = _adapters.Values.FirstOrDefault(a => a.Id == update.Id)
            ?? throw new ArgumentException("Unknown adapter.");
        adapter = adapter with { CountsOverride = update.CountsOverride };
        _adapters[adapter.Guid] = adapter;
        _db.UpdateAdapter(adapter);
        return new IpcResponse { Ok = true, Adapters = BuildAdapterList() };
    }

    private List<long> CountedAdapterIds() => _adapters.Values.Where(a => a.CountsInTotal).Select(a => a.Id).ToList();

    private IpcResponse QueryUsage(UsageQuery query)
    {
        DrainAppTraffic(NowUtc);
        FlushLocked(NowUtc);
        TimeZoneInfo.ClearCachedData();
        TimeZoneInfo zone = TimeZoneInfo.Local;

        List<long> ids = query.Adapters switch
        {
            AdapterSelection.All => _adapters.Values.Select(a => a.Id).ToList(),
            AdapterSelection.Single => [query.AdapterId],
            _ => CountedAdapterIds(),
        };

        DateTime from = query.FromLocal;
        if (from == default)
        {
            from = _db.FirstHourUtc() is long first
                ? PeriodMath.BucketStart(PeriodMath.ToLocal(first, zone), query.Kind, query.WeekStart)
                : PeriodMath.BucketStart(PeriodMath.ToLocal(NowUtc, zone), query.Kind, query.WeekStart);
        }

        DateTime to = query.ToLocal;
        if (to <= from)
        {
            throw new ArgumentException("The query range is empty.");
        }

        long step = query.Kind switch
        {
            BucketKind.Minute => 60,
            BucketKind.Hour => 3600,
            BucketKind.Day => 86400,
            BucketKind.Week => 7 * 86400,
            BucketKind.Month => 28 * 86400,
            _ => 365 * 86400,
        };
        if ((to - from).TotalSeconds / step > MaxBuckets)
        {
            throw new ArgumentException("The query range is too large.");
        }

        bool minutes = query.Kind == BucketKind.Minute;
        long fromUtc = PeriodMath.FloorTo(PeriodMath.ToUtcSeconds(from, zone), minutes ? 60 : 3600);
        long toUtc = PeriodMath.ToUtcSeconds(to, zone);
        List<UsageSample> rows = _db.QueryUsage(minutes, fromUtc, toUtc, ids);
        return new IpcResponse
        {
            Ok = true,
            Buckets = UsageAggregator.Aggregate(rows, from, to, query.Kind, query.WeekStart, zone),
        };
    }

    private (long From, long To) ToUtcRange(RangeQuery range)
    {
        TimeZoneInfo.ClearCachedData();
        TimeZoneInfo zone = TimeZoneInfo.Local;
        long from = range.FromLocal == default ? long.MinValue : PeriodMath.ToUtcSeconds(range.FromLocal, zone);
        long to = range.ToLocal == default ? long.MaxValue : PeriodMath.ToUtcSeconds(range.ToLocal, zone);
        return (from, to);
    }

    private IpcResponse QueryApps(RangeQuery range)
    {
        DrainAppTraffic(NowUtc);
        FlushLocked(NowUtc);
        (long from, long to) = ToUtcRange(range);
        return new IpcResponse { Ok = true, Apps = _db.QueryApps(from, to) };
    }

    private IpcResponse QueryOutages(RangeQuery range)
    {
        (long from, long to) = ToUtcRange(range);
        return new IpcResponse { Ok = true, Outages = _db.QueryOutages(from, to, 1000) };
    }

    private QuotaStatus BuildQuota()
    {
        QuotaSettings quota = _settings.Quota;
        TimeZoneInfo.ClearCachedData();
        TimeZoneInfo zone = TimeZoneInfo.Local;
        long now = NowUtc;
        DateTime nowLocal = PeriodMath.ToLocal(now, zone);
        (DateTime start, DateTime end) = PeriodMath.BillingCycle(nowLocal, quota.CycleStartDay);
        var status = new QuotaStatus
        {
            Enabled = quota.Enabled,
            LimitBytes = quota.LimitBytes,
            CycleStartLocal = start,
            CycleEndLocal = end,
            AlertPercents = quota.AlertPercents.Order().ToList(),
        };

        if (!quota.Enabled)
        {
            return status;
        }

        FlushLocked(now);
        long startUtc = PeriodMath.ToUtcSeconds(start, zone);
        long endUtc = PeriodMath.ToUtcSeconds(end, zone);
        foreach (UsageSample row in _db.QueryUsage(false, startUtc, endUtc, CountedAdapterIds()))
        {
            status.UsedBytes += row.Received + (quota.CountUpload ? row.Sent : 0);
        }

        double elapsed = Math.Max(1, now - startUtc);
        double fraction = elapsed / Math.Max(1, endUtc - startUtc);
        status.ProjectedBytes = fraction < 0.03 ? status.UsedBytes : (long)(status.UsedBytes / fraction);

        string? notified = _db.GetMeta(QuotaNotifiedKey);
        string cycleKey = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (notified is not null && notified.StartsWith(cycleKey + "|", StringComparison.Ordinal)
            && int.TryParse(notified.AsSpan(cycleKey.Length + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
        {
            status.NotifiedPercent = percent;
        }

        return status;
    }

    private IpcResponse AckQuota(QuotaAck ack)
    {
        string cycleKey = ack.CycleStartLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _db.SetMeta(QuotaNotifiedKey, $"{cycleKey}|{ack.Percent.ToString(CultureInfo.InvariantCulture)}");
        return IpcResponse.Success();
    }

    // ---- backup ----

    private IpcResponse Export()
    {
        DrainAppTraffic(NowUtc);
        FlushLocked(NowUtc);
        return new IpcResponse { Ok = true, Backup = _db.Export(Environment.MachineName, NowUtc) };
    }

    private IpcResponse PreviewImport(BackupData data) =>
        new() { Ok = true, Preview = _db.PreviewImport(data) };

    private IpcResponse Import(BackupData data)
    {
        DrainAppTraffic(NowUtc);
        FlushLocked(NowUtc);

        Directory.CreateDirectory(SnapshotDirectory);
        string name = $"pre-import-{DateTime.Now:yyyyMMdd-HHmmss}.db";
        string path = Path.Combine(SnapshotDirectory, name);
        _db.SnapshotTo(path);
        PruneSnapshots();

        ImportResult result = _db.Import(data);
        _db.SetMeta(LastImportSnapshotKey, name);
        ReloadCaches();
        Log.Info($"Imported backup from {data.MachineName ?? "unknown machine"}: {result.HoursReplaced} hours, {result.RowsImported} rows. Snapshot {name}.");
        return new IpcResponse { Ok = true, Import = result };
    }

    private IpcResponse UndoImport()
    {
        string? name = _db.GetMeta(LastImportSnapshotKey);
        string? path = name is null ? null : Path.Combine(SnapshotDirectory, Path.GetFileName(name));
        if (path is null || !File.Exists(path))
        {
            throw new InvalidOperationException("There is no import to undo.");
        }

        // Whatever was recorded since the import goes with it; the snapshot predates it.
        _pending.Clear();
        _touchedAdapters.Clear();
        _db.RestoreFrom(path);
        _db.SetMeta(LastImportSnapshotKey, null);
        ReloadCaches();
        Log.Info($"Undid the last import by restoring {name}.");
        return IpcResponse.Success();
    }

    private void PruneSnapshots()
    {
        foreach (FileInfo old in new DirectoryInfo(SnapshotDirectory).GetFiles("pre-import-*.db")
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                     .Skip(SnapshotsKept))
        {
            old.Delete();
        }
    }

    // ---- setup ----

    /// <summary>
    /// The service's data is private to SYSTEM and administrators; the tray app goes through the pipe, so no one
    /// else needs (or gets) direct access, and nobody can plant files for the service to trip over.
    /// </summary>
    private void PrepareDataDirectory()
    {
        var directory = new DirectoryInfo(_dataDirectory);
        directory.Create();
        Log.Init(_dataDirectory, console: !_secureDataDirectory);
        if (!_secureDataDirectory)
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        try
        {
            directory.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn($"Couldn't restrict the data directory: {ex.Message}");
        }
    }
}
