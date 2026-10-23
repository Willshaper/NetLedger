using Microsoft.Data.Sqlite;
using NetLedger.Core;
using NetLedger.Core.Ipc;
using NetLedger.Core.Native;
using NetLedger.Service.Collection;

namespace NetLedger.Service.Storage;

internal sealed record AdapterRecord(
    long Id,
    Guid Guid,
    string Name,
    string Description,
    uint IfType,
    bool IsHardware,
    bool? CountsOverride,
    long LastSeenUtc)
{
    public bool CountsInTotal => CountsOverride ?? IfTable.CountsByDefault(IsHardware, IfType);

    /// <summary>
    /// Whether an interface is this adapter apart from its GUID. VPN tunnels (WireGuard, OpenVPN) and Hyper-V/WSL
    /// switches are created afresh, with a new GUID, every time they connect or start.
    /// </summary>
    public bool SameAdapterAs(InterfaceSnapshot snapshot) =>
        Name == snapshot.Alias && Description == snapshot.Description && IfType == snapshot.IfType && IsHardware == snapshot.IsHardware;
}

/// <summary>
/// SQLite storage. All times are UTC Unix seconds; minute/hour columns hold the bucket start.
/// Not thread-safe: the engine serialises every call.
/// </summary>
internal sealed class Database : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly string _path;
    private SqliteConnection _connection;
    private SqliteTransaction? _transaction;

    public Database(string path)
    {
        _path = path;
        _connection = Open(path);
    }

    public void Dispose() => _connection.Dispose();

    private static SqliteConnection Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        Migrate(connection);
        return connection;
    }

    private static void Migrate(SqliteConnection connection)
    {
        long version = Scalar<long>(connection, "PRAGMA user_version");
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"Database schema {version} is newer than this NetLedger ({SchemaVersion}).");
        }

        if (version < 1)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS meta(
                    name TEXT PRIMARY KEY,
                    value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS adapters(
                    id INTEGER PRIMARY KEY,
                    guid TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    description TEXT NOT NULL,
                    if_type INTEGER NOT NULL,
                    is_hardware INTEGER NOT NULL,
                    counts_override INTEGER,
                    last_seen INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS usage_minute(
                    minute INTEGER NOT NULL,
                    adapter_id INTEGER NOT NULL,
                    rx INTEGER NOT NULL,
                    tx INTEGER NOT NULL,
                    PRIMARY KEY(minute, adapter_id)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS usage_hour(
                    hour INTEGER NOT NULL,
                    adapter_id INTEGER NOT NULL,
                    rx INTEGER NOT NULL,
                    tx INTEGER NOT NULL,
                    PRIMARY KEY(hour, adapter_id)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS apps(
                    id INTEGER PRIMARY KEY,
                    app_key TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    path TEXT);
                CREATE TABLE IF NOT EXISTS app_usage_hour(
                    hour INTEGER NOT NULL,
                    app_id INTEGER NOT NULL,
                    rx INTEGER NOT NULL,
                    tx INTEGER NOT NULL,
                    PRIMARY KEY(hour, app_id)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS outages(
                    start_utc INTEGER PRIMARY KEY,
                    end_utc INTEGER);
                PRAGMA user_version = 1;
                """);
        }
    }

    // ---- meta ----

    public string? GetMeta(string name)
    {
        using SqliteCommand cmd = Command("SELECT value FROM meta WHERE name = $n", ("$n", name));
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string name, string? value)
    {
        using SqliteCommand cmd = value is null
            ? Command("DELETE FROM meta WHERE name = $n", ("$n", name))
            : Command("INSERT INTO meta(name, value) VALUES($n, $v) ON CONFLICT(name) DO UPDATE SET value = excluded.value",
                ("$n", name), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    // ---- adapters and apps ----

    public List<AdapterRecord> LoadAdapters()
    {
        var result = new List<AdapterRecord>();
        using SqliteCommand cmd = Command(
            "SELECT id, guid, name, description, if_type, is_hardware, counts_override, last_seen FROM adapters");
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AdapterRecord(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                (uint)reader.GetInt64(4),
                reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetInt64(6) != 0,
                reader.GetInt64(7)));
        }

        return result;
    }

    public AdapterRecord AddAdapter(Guid guid, string name, string description, uint ifType, bool isHardware, bool? countsOverride, long seenUtc)
    {
        using SqliteCommand cmd = Command(
            """
            INSERT INTO adapters(guid, name, description, if_type, is_hardware, counts_override, last_seen)
            VALUES($g, $n, $d, $t, $h, $c, $s) RETURNING id
            """,
            ("$g", guid.ToString()), ("$n", name), ("$d", description), ("$t", (long)ifType),
            ("$h", isHardware ? 1L : 0L), ("$c", countsOverride is null ? DBNull.Value : countsOverride.Value ? 1L : 0L),
            ("$s", seenUtc));
        long id = (long)cmd.ExecuteScalar()!;
        return new AdapterRecord(id, guid, name, description, ifType, isHardware, countsOverride, seenUtc);
    }

    public void UpdateAdapter(AdapterRecord adapter)
    {
        using SqliteCommand cmd = Command(
            """
            UPDATE adapters SET guid = $g, name = $n, description = $d, if_type = $t, is_hardware = $h,
                counts_override = $c, last_seen = $s WHERE id = $id
            """,
            ("$id", adapter.Id), ("$g", adapter.Guid.ToString()), ("$n", adapter.Name), ("$d", adapter.Description), ("$t", (long)adapter.IfType),
            ("$h", adapter.IsHardware ? 1L : 0L),
            ("$c", adapter.CountsOverride is null ? DBNull.Value : adapter.CountsOverride.Value ? 1L : 0L),
            ("$s", adapter.LastSeenUtc));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Folds adapters that differ only in GUID into one record each, adding their usage together. Earlier versions
    /// added a new adapter every time a VPN tunnel or virtual switch came back with a new GUID.
    /// The most recently seen record survives. Returns how many records were folded away.
    /// </summary>
    public int MergeDuplicateAdapters()
    {
        List<IGrouping<(string, string, uint, bool), AdapterRecord>> groups = LoadAdapters()
            .GroupBy(a => (a.Name, a.Description, a.IfType, a.IsHardware))
            .Where(g => g.Count() > 1)
            .ToList();
        if (groups.Count == 0)
        {
            return 0;
        }

        int merged = 0;
        InTransaction(tx =>
        {
            foreach (IGrouping<(string, string, uint, bool), AdapterRecord> group in groups)
            {
                AdapterRecord keep = group.MaxBy(a => (a.LastSeenUtc, a.Id))!;
                foreach (AdapterRecord duplicate in group.Where(a => a.Id != keep.Id))
                {
                    MoveUsage("usage_hour", "hour", "adapter_id", duplicate.Id, keep.Id);
                    MoveUsage("usage_minute", "minute", "adapter_id", duplicate.Id, keep.Id);
                    using SqliteCommand delete = Command("DELETE FROM adapters WHERE id = $id", ("$id", duplicate.Id));
                    delete.ExecuteNonQuery();
                    merged++;
                }

                // Keep the user's most recent counting choice for the adapter, wherever it was made.
                bool? choice = group.OrderByDescending(a => (a.LastSeenUtc, a.Id)).Select(a => a.CountsOverride).FirstOrDefault(c => c is not null);
                if (choice != keep.CountsOverride)
                {
                    UpdateAdapter(keep with { CountsOverride = choice });
                }
            }
        });
        return merged;
    }

    private void MoveUsage(string table, string timeColumn, string idColumn, long fromId, long toId)
    {
        using (SqliteCommand move = Command(
                   $"INSERT INTO {table}({timeColumn}, {idColumn}, rx, tx) SELECT {timeColumn}, $to, rx, tx FROM {table} WHERE {idColumn} = $from " +
                   $"ON CONFLICT({timeColumn}, {idColumn}) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx",
                   ("$to", toId), ("$from", fromId)))
        {
            move.ExecuteNonQuery();
        }

        using SqliteCommand delete = Command($"DELETE FROM {table} WHERE {idColumn} = $from", ("$from", fromId));
        delete.ExecuteNonQuery();
    }

    /// <summary>
    /// Brings every app's key up to <see cref="AppKeys.Current"/> and folds together apps that end up with the same
    /// key, adding their usage. Earlier versions keyed programs by full path, so every update installed into a new
    /// version folder became a new app, and kept the sign-in-specific suffix of per-user services. The newest record
    /// (and its path, for the icon) survives. Returns how many records were folded away.
    /// </summary>
    public int MergeDuplicateApps()
    {
        var apps = new List<(long Id, string Key, string Name, string? Path)>();
        using (SqliteCommand cmd = Command("SELECT id, app_key, name, path FROM apps"))
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                apps.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var groups = apps
            .GroupBy(a => AppKeys.Current(a.Key, a.Name, a.Path))
            .Where(g => g.Count() > 1 || g.First().Key != g.Key)
            .ToList();
        if (groups.Count == 0)
        {
            return 0;
        }

        int merged = 0;
        InTransaction(_ =>
        {
            foreach (var group in groups)
            {
                var keep = group.MaxBy(a => a.Id);
                foreach (var duplicate in group.Where(a => a.Id != keep.Id))
                {
                    MoveUsage("app_usage_hour", "hour", "app_id", duplicate.Id, keep.Id);
                    using SqliteCommand delete = Command("DELETE FROM apps WHERE id = $id", ("$id", duplicate.Id));
                    delete.ExecuteNonQuery();
                    merged++;
                }

                if (keep.Key != group.Key)
                {
                    using SqliteCommand rekey = Command("UPDATE apps SET app_key = $k WHERE id = $id", ("$k", group.Key), ("$id", keep.Id));
                    rekey.ExecuteNonQuery();
                }
            }
        });
        return merged;
    }

    public Dictionary<string, long> LoadAppIds()
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using SqliteCommand cmd = Command("SELECT app_key, id FROM apps");
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }

        return result;
    }

    /// <summary>Gives stored Windows-service apps their English names where known. Returns how many were renamed.</summary>
    public int RenameServiceApps()
    {
        var renames = new List<(long Id, string Name)>();
        using (SqliteCommand cmd = Command("SELECT id, app_key, name FROM apps WHERE app_key LIKE 'svc:%'"))
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (ServiceNames.EnglishNameForKey(reader.GetString(1)) is { } english && english != reader.GetString(2))
                {
                    renames.Add((reader.GetInt64(0), english));
                }
            }
        }

        foreach ((long id, string name) in renames)
        {
            using SqliteCommand update = Command("UPDATE apps SET name = $n WHERE id = $id", ("$id", id), ("$n", name));
            update.ExecuteNonQuery();
        }

        return renames.Count;
    }

    public void UpdateApp(long id, string name, string? path)
    {
        using SqliteCommand cmd = Command(
            "UPDATE apps SET name = $n, path = $p WHERE id = $id",
            ("$id", id), ("$n", name), ("$p", (object?)path ?? DBNull.Value));
        cmd.ExecuteNonQuery();
    }

    public long AddApp(string key, string name, string? path)
    {
        using SqliteCommand cmd = Command(
            "INSERT INTO apps(app_key, name, path) VALUES($k, $n, $p) RETURNING id",
            ("$k", key), ("$n", name), ("$p", (object?)path ?? DBNull.Value));
        return (long)cmd.ExecuteScalar()!;
    }

    // ---- recording ----

    public void Flush(PendingUsage pending, IEnumerable<AdapterRecord> touchedAdapters, long nowUtc)
    {
        InTransaction(tx =>
        {
            Upsert(tx, "usage_minute", "minute", "adapter_id", pending.Minutes);
            Upsert(tx, "usage_hour", "hour", "adapter_id", pending.Hours);
            Upsert(tx, "app_usage_hour", "hour", "app_id", pending.AppHours);
            foreach (AdapterRecord adapter in touchedAdapters)
            {
                UpdateAdapter(adapter);
            }

            SetMeta("lastAlive", nowUtc.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
    }

    private void Upsert(SqliteTransaction tx, string table, string timeColumn, string idColumn, Dictionary<(long Time, long Id), ByteCounts> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT INTO {table}({timeColumn}, {idColumn}, rx, tx) VALUES($t, $i, $r, $s) " +
            $"ON CONFLICT({timeColumn}, {idColumn}) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx";
        SqliteParameter t = cmd.Parameters.Add("$t", SqliteType.Integer);
        SqliteParameter i = cmd.Parameters.Add("$i", SqliteType.Integer);
        SqliteParameter r = cmd.Parameters.Add("$r", SqliteType.Integer);
        SqliteParameter s = cmd.Parameters.Add("$s", SqliteType.Integer);
        cmd.Prepare();
        foreach (KeyValuePair<(long Time, long Id), ByteCounts> row in rows)
        {
            t.Value = row.Key.Time;
            i.Value = row.Key.Id;
            r.Value = row.Value.Received;
            s.Value = row.Value.Sent;
            cmd.ExecuteNonQuery();
        }
    }

    public int PruneMinutes(long beforeUtc)
    {
        using SqliteCommand cmd = Command("DELETE FROM usage_minute WHERE minute < $t", ("$t", beforeUtc));
        return cmd.ExecuteNonQuery();
    }

    // ---- queries ----

    public List<UsageSample> QueryUsage(bool minutes, long fromUtc, long toUtc, IReadOnlyCollection<long> adapterIds)
    {
        var result = new List<UsageSample>();
        if (adapterIds.Count == 0)
        {
            return result;
        }

        string table = minutes ? "usage_minute" : "usage_hour";
        string column = minutes ? "minute" : "hour";
        // Ids are integers from our own tables, so inlining them is safe.
        string ids = string.Join(",", adapterIds);
        using SqliteCommand cmd = Command(
            $"SELECT {column}, SUM(rx), SUM(tx) FROM {table} WHERE {column} >= $f AND {column} < $t " +
            $"AND adapter_id IN ({ids}) GROUP BY {column} ORDER BY {column}",
            ("$f", fromUtc), ("$t", toUtc));
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new UsageSample(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return result;
    }

    public long? FirstHourUtc()
    {
        using SqliteCommand cmd = Command("SELECT MIN(hour) FROM usage_hour");
        return cmd.ExecuteScalar() is long value ? value : null;
    }

    public List<AppUsageRow> QueryApps(long fromUtc, long toUtc)
    {
        var result = new List<AppUsageRow>();
        using SqliteCommand cmd = Command(
            """
            SELECT a.id, a.app_key, a.name, a.path, SUM(u.rx), SUM(u.tx)
            FROM app_usage_hour u JOIN apps a ON a.id = u.app_id
            WHERE u.hour >= $f AND u.hour < $t
            GROUP BY a.id
            ORDER BY SUM(u.rx) + SUM(u.tx) DESC
            """,
            ("$f", fromUtc), ("$t", toUtc));
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AppUsageRow
            {
                AppId = reader.GetInt64(0),
                Key = reader.GetString(1),
                Name = reader.GetString(2),
                Path = reader.IsDBNull(3) ? null : reader.GetString(3),
                Received = reader.GetInt64(4),
                Sent = reader.GetInt64(5),
            });
        }

        return result;
    }

    // ---- outages ----

    public void StartOutage(long startUtc)
    {
        using SqliteCommand cmd = Command("INSERT OR IGNORE INTO outages(start_utc, end_utc) VALUES($s, NULL)", ("$s", startUtc));
        cmd.ExecuteNonQuery();
    }

    public void EndOutage(long startUtc, long endUtc)
    {
        using SqliteCommand cmd = Command("UPDATE outages SET end_utc = $e WHERE start_utc = $s", ("$s", startUtc), ("$e", endUtc));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Closes outages left open by a crash or power loss at the last moment the service was alive.</summary>
    public void CloseDanglingOutages()
    {
        long lastAlive = long.TryParse(GetMeta("lastAlive"), out long value) ? value : 0;
        using SqliteCommand cmd = Command(
            "UPDATE outages SET end_utc = MAX(start_utc, $a) WHERE end_utc IS NULL", ("$a", lastAlive));
        cmd.ExecuteNonQuery();
    }

    public List<OutageRow> QueryOutages(long fromUtc, long toUtc, int limit)
    {
        var result = new List<OutageRow>();
        using SqliteCommand cmd = Command(
            "SELECT start_utc, end_utc FROM outages WHERE start_utc >= $f AND start_utc < $t ORDER BY start_utc DESC LIMIT $l",
            ("$f", fromUtc), ("$t", toUtc), ("$l", (long)limit));
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new OutageRow { StartUtc = reader.GetInt64(0), EndUtc = reader.IsDBNull(1) ? null : reader.GetInt64(1) });
        }

        return result;
    }

    // ---- backup ----

    public BackupData Export(string? machineName, long nowUtc)
    {
        var data = new BackupData { MachineName = machineName, ExportedUtc = nowUtc };
        foreach (AdapterRecord a in LoadAdapters())
        {
            data.Adapters.Add(new BackupAdapter
            {
                Id = a.Id, Guid = a.Guid, Name = a.Name, Description = a.Description,
                IfType = a.IfType, IsHardware = a.IsHardware, CountsOverride = a.CountsOverride,
            });
        }

        using (SqliteCommand cmd = Command("SELECT id, app_key, name, path FROM apps"))
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                data.Apps.Add(new BackupApp
                {
                    Id = reader.GetInt64(0), Key = reader.GetString(1), Name = reader.GetString(2),
                    Path = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }
        }

        ReadRows("SELECT hour, adapter_id, rx, tx FROM usage_hour ORDER BY hour", data.Hours);
        ReadRows("SELECT minute, adapter_id, rx, tx FROM usage_minute ORDER BY minute", data.Minutes);
        ReadRows("SELECT hour, app_id, rx, tx FROM app_usage_hour ORDER BY hour", data.AppHours);
        data.Outages.AddRange(QueryOutages(long.MinValue, long.MaxValue, int.MaxValue));
        data.Outages.Reverse();
        return data;
    }

    private void ReadRows(string sql, List<UsageRecord> into)
    {
        using SqliteCommand cmd = Command(sql);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            into.Add(new UsageRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        }
    }

    public ImportPreview PreviewImport(BackupData data)
    {
        Validate(data);
        var hours = new HashSet<long>(data.Hours.Select(r => r.Time));
        var preview = new ImportPreview
        {
            MachineName = data.MachineName,
            ExportedUtc = data.ExportedUtc,
            HourCount = hours.Count,
            AppRows = data.AppHours.Count,
            Outages = data.Outages.Count,
            FirstUtc = hours.Count > 0 ? hours.Min() : null,
            LastUtc = hours.Count > 0 ? hours.Max() : null,
        };

        // Totals use the backup's own idea of which adapters count toward the internet total.
        var counted = data.Adapters
            .Where(a => a.CountsOverride ?? IfTable.CountsByDefault(a.IsHardware, a.IfType))
            .Select(a => a.Id)
            .ToHashSet();
        foreach (UsageRecord row in data.Hours.Where(r => counted.Contains(r.Id)))
        {
            preview.Received += row.Received;
            preview.Sent += row.Sent;
        }

        using SqliteCommand cmd = Command("SELECT 1 FROM usage_hour WHERE hour = $h LIMIT 1");
        SqliteParameter h = cmd.Parameters.Add("$h", SqliteType.Integer);
        cmd.Prepare();
        foreach (long hour in hours)
        {
            h.Value = hour;
            if (cmd.ExecuteScalar() is not null)
            {
                preview.OverlappingHours++;
            }
        }

        return preview;
    }

    /// <summary>
    /// Imports a backup. Any hour (or minute) present in the backup replaces what this database has for it;
    /// adapters and apps are matched by GUID and app key.
    /// </summary>
    public ImportResult Import(BackupData data)
    {
        Validate(data);
        var result = new ImportResult();
        InTransaction(tx => ImportCore(tx, data, result));
        return result;
    }

    private void ImportCore(SqliteTransaction tx, BackupData data, ImportResult result)
    {
        var adapterMap = new Dictionary<long, long>();
        Dictionary<Guid, AdapterRecord> existingAdapters = LoadAdapters().ToDictionary(a => a.Guid);
        foreach (BackupAdapter a in data.Adapters)
        {
            adapterMap[a.Id] = existingAdapters.TryGetValue(a.Guid, out AdapterRecord? existing)
                ? existing.Id
                : AddAdapter(a.Guid, a.Name, a.Description, a.IfType, a.IsHardware, a.CountsOverride, 0).Id;
        }

        var appMap = new Dictionary<long, long>();
        Dictionary<string, long> existingApps = LoadAppIds();
        foreach (BackupApp app in data.Apps)
        {
            appMap[app.Id] = existingApps.TryGetValue(app.Key, out long id) ? id : AddApp(app.Key, app.Name, app.Path);
        }

        result.HoursReplaced = Replace(tx, "usage_hour", "hour", "adapter_id", data.Hours, adapterMap, result);
        Replace(tx, "usage_minute", "minute", "adapter_id", data.Minutes, adapterMap, result);
        Replace(tx, "app_usage_hour", "hour", "app_id", data.AppHours, appMap, result);

        using (SqliteCommand cmd = Command("INSERT OR IGNORE INTO outages(start_utc, end_utc) VALUES($s, $e)"))
        {
            SqliteParameter s = cmd.Parameters.Add("$s", SqliteType.Integer);
            SqliteParameter e = cmd.Parameters.Add("$e", SqliteType.Integer);
            foreach (OutageRow outage in data.Outages)
            {
                s.Value = outage.StartUtc;
                e.Value = outage.EndUtc ?? outage.StartUtc;
                cmd.ExecuteNonQuery();
            }
        }
    }

    private int Replace(
        SqliteTransaction tx,
        string table,
        string timeColumn,
        string idColumn,
        List<UsageRecord> rows,
        Dictionary<long, long> idMap,
        ImportResult result)
    {
        var times = new HashSet<long>(rows.Select(r => r.Time));
        using (SqliteCommand delete = _connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = $"DELETE FROM {table} WHERE {timeColumn} = $t";
            SqliteParameter t = delete.Parameters.Add("$t", SqliteType.Integer);
            delete.Prepare();
            foreach (long time in times)
            {
                t.Value = time;
                delete.ExecuteNonQuery();
            }
        }

        using (SqliteCommand insert = _connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                $"INSERT INTO {table}({timeColumn}, {idColumn}, rx, tx) VALUES($t, $i, $r, $s) " +
                $"ON CONFLICT({timeColumn}, {idColumn}) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx";
            SqliteParameter t = insert.Parameters.Add("$t", SqliteType.Integer);
            SqliteParameter i = insert.Parameters.Add("$i", SqliteType.Integer);
            SqliteParameter r = insert.Parameters.Add("$r", SqliteType.Integer);
            SqliteParameter s = insert.Parameters.Add("$s", SqliteType.Integer);
            insert.Prepare();
            foreach (UsageRecord row in rows)
            {
                t.Value = row.Time;
                i.Value = idMap[row.Id];
                r.Value = row.Received;
                s.Value = row.Sent;
                insert.ExecuteNonQuery();
                result.RowsImported++;
            }
        }

        return times.Count;
    }

    private static void Validate(BackupData data)
    {
        if (data.FormatVersion is < 1 or > BackupData.CurrentFormatVersion)
        {
            throw new InvalidDataException($"Unsupported backup format version {data.FormatVersion}.");
        }

        static void Check(List<UsageRecord> rows, long step, string what)
        {
            foreach (UsageRecord row in rows)
            {
                if (row.Received < 0 || row.Sent < 0 || row.Time % step != 0)
                {
                    throw new InvalidDataException($"The backup contains an invalid {what} row.");
                }
            }
        }

        Check(data.Hours, 3600, "hourly");
        Check(data.Minutes, 60, "minute");
        Check(data.AppHours, 3600, "per-app");
        if (data.Apps.Any(a => string.IsNullOrEmpty(a.Key)))
        {
            throw new InvalidDataException("The backup contains an app without a key.");
        }

        // Every row must belong to an adapter or app in the backup, or importing it would wipe an hour for nothing.
        var adapterIds = data.Adapters.Select(a => a.Id).ToHashSet();
        var appIds = data.Apps.Select(a => a.Id).ToHashSet();
        if (data.Hours.Any(r => !adapterIds.Contains(r.Id)) || data.Minutes.Any(r => !adapterIds.Contains(r.Id))
            || data.AppHours.Any(r => !appIds.Contains(r.Id)))
        {
            throw new InvalidDataException("The backup refers to adapters or apps it doesn't contain.");
        }
    }

    // ---- snapshots ----

    public void SnapshotTo(string path)
    {
        using SqliteCommand cmd = Command("VACUUM INTO $p", ("$p", path));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Swaps the live database for a snapshot taken by <see cref="SnapshotTo"/>.</summary>
    public void RestoreFrom(string snapshotPath)
    {
        _connection.Dispose();
        try
        {
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                File.Delete(_path + suffix);
            }

            File.Copy(snapshotPath, _path, overwrite: true);
        }
        finally
        {
            // Whether or not the copy worked, the service needs a working connection afterwards.
            _connection = Open(_path);
        }
    }

    public void Optimize() => Execute(_connection, "PRAGMA optimize;");

    // ---- helpers ----

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction that every <see cref="Command"/> joins. On failure it rolls back,
    /// and the field is always cleared so later commands don't try to join a dead transaction.
    /// </summary>
    private void InTransaction(Action<SqliteTransaction> work)
    {
        using SqliteTransaction tx = _connection.BeginTransaction();
        _transaction = tx;
        try
        {
            work(tx);
            tx.Commit();
        }
        finally
        {
            _transaction = null;
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
