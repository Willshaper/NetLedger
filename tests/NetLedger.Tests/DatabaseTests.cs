using NetLedger.Core;
using NetLedger.Core.Ipc;
using NetLedger.Service.Collection;
using NetLedger.Service.Storage;
using Xunit;

namespace NetLedger.Tests;

public sealed class DatabaseTests : IDisposable
{
    private const long Hour = 1_791_626_400; // 2026-10-10 10:00 UTC
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netledger-tests-" + Guid.NewGuid().ToString("N"));

    public DatabaseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Database Open(string name = "a.db") => new(Path.Combine(_directory, name));

    private static AdapterRecord AddEthernet(Database db, Guid? guid = null) =>
        db.AddAdapter(guid ?? Guid.NewGuid(), "Ethernet", "Test", 6, true, null, Hour);

    [Fact]
    public void FlushesAccumulateIntoBuckets()
    {
        using Database db = Open();
        AdapterRecord nic = AddEthernet(db);
        var pending = new PendingUsage();
        pending.AddAdapter(Hour + 10, nic.Id, 100, 10);
        pending.AddAdapter(Hour + 70, nic.Id, 50, 5);
        db.Flush(pending, [nic], Hour + 70);
        pending.Clear();
        pending.AddAdapter(Hour + 3599, nic.Id, 1, 1);
        db.Flush(pending, [], Hour + 3599);

        UsageSample hour = Assert.Single(db.QueryUsage(false, Hour, Hour + 3600, [nic.Id]));
        Assert.Equal(151, hour.Received);
        Assert.Equal(16, hour.Sent);
        Assert.Equal(3, db.QueryUsage(true, Hour, Hour + 3600, [nic.Id]).Count);
    }

    [Fact]
    public void QueriesOnlyIncludeRequestedAdapters()
    {
        using Database db = Open();
        AdapterRecord a = AddEthernet(db);
        AdapterRecord b = db.AddAdapter(Guid.NewGuid(), "VPN", "Tunnel", 53, false, null, Hour);
        var pending = new PendingUsage();
        pending.AddAdapter(Hour, a.Id, 100, 0);
        pending.AddAdapter(Hour, b.Id, 40, 0);
        db.Flush(pending, [], Hour);

        Assert.Equal(100, db.QueryUsage(false, Hour, Hour + 3600, [a.Id]).Sum(r => r.Received));
        Assert.Equal(140, db.QueryUsage(false, Hour, Hour + 3600, [a.Id, b.Id]).Sum(r => r.Received));
        Assert.Empty(db.QueryUsage(false, Hour, Hour + 3600, []));
    }

    [Fact]
    public void ExportImportRoundTripsIntoEmptyDatabase()
    {
        BackupData backup;
        using (Database source = Open("source.db"))
        {
            AdapterRecord nic = AddEthernet(source);
            long app = source.AddApp(@"c:\apps\browser.exe", "Browser", @"C:\Apps\browser.exe");
            var pending = new PendingUsage();
            pending.AddAdapter(Hour, nic.Id, 1000, 100);
            pending.AddAdapter(Hour + 3600, nic.Id, 2000, 200);
            pending.AddApp(Hour, app, 900, 90);
            source.Flush(pending, [], Hour);
            source.StartOutage(Hour + 100);
            source.EndOutage(Hour + 100, Hour + 160);
            backup = source.Export("TEST-PC", Hour + 7200);
        }

        string path = Path.Combine(_directory, "backup" + BackupFile.Extension);
        BackupFile.Write(path, backup);
        BackupData read = BackupFile.Read(path);

        using Database target = Open("target.db");
        ImportPreview preview = target.PreviewImport(read);
        Assert.Equal(2, preview.HourCount);
        Assert.Equal(0, preview.OverlappingHours);
        Assert.Equal(3000, preview.Received);

        target.Import(read);
        AdapterRecord imported = Assert.Single(target.LoadAdapters());
        Assert.Equal(3000, target.QueryUsage(false, Hour, Hour + 7200, [imported.Id]).Sum(r => r.Received));
        AppUsageRow appRow = Assert.Single(target.QueryApps(Hour, Hour + 3600));
        Assert.Equal("Browser", appRow.Name);
        Assert.Equal(900, appRow.Received);
        OutageRow outage = Assert.Single(target.QueryOutages(long.MinValue, long.MaxValue, 10));
        Assert.Equal(Hour + 160, outage.EndUtc);
    }

    [Fact]
    public void ImportReplacesOverlappingHoursInsteadOfAdding()
    {
        using Database db = Open();
        Guid guid = Guid.NewGuid();
        AdapterRecord nic = AddEthernet(db, guid);
        var pending = new PendingUsage();
        pending.AddAdapter(Hour, nic.Id, 500, 50);
        pending.AddAdapter(Hour + 3600, nic.Id, 700, 70);
        db.Flush(pending, [], Hour);

        var backup = new BackupData
        {
            Adapters = [new BackupAdapter { Id = 99, Guid = guid, Name = "Ethernet", Description = "Test", IfType = 6, IsHardware = true }],
            Hours = [new UsageRecord(Hour, 99, 10, 1)],
        };
        Assert.Equal(1, db.PreviewImport(backup).OverlappingHours);
        db.Import(backup);

        List<UsageSample> rows = db.QueryUsage(false, Hour, Hour + 7200, [nic.Id]);
        Assert.Equal([10L, 700L], rows.Select(r => r.Received)); // first hour replaced, second untouched
    }

    [Fact]
    public void InvalidBackupIsRejected()
    {
        using Database db = Open();
        var misaligned = new BackupData
        {
            Adapters = [new BackupAdapter { Id = 1, Guid = Guid.NewGuid(), Name = "Ethernet", IfType = 6, IsHardware = true }],
            Hours = [new UsageRecord(Hour + 5, 1, 10, 1)],
        };
        Assert.Throws<InvalidDataException>(() => db.PreviewImport(misaligned));

        var orphan = new BackupData { Hours = [new UsageRecord(Hour, 42, 10, 1)] };
        Assert.Throws<InvalidDataException>(() => db.Import(orphan));
    }

    [Fact]
    public void FailedImportLeavesDatabaseUsable()
    {
        using Database db = Open();
        AdapterRecord nic = AddEthernet(db);
        Guid duplicate = Guid.NewGuid();
        var broken = new BackupData
        {
            // The same GUID twice violates the unique index halfway through the import.
            Adapters =
            [
                new BackupAdapter { Id = 1, Guid = duplicate, Name = "A", IfType = 6, IsHardware = true },
                new BackupAdapter { Id = 2, Guid = duplicate, Name = "B", IfType = 6, IsHardware = true },
            ],
        };
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => db.Import(broken));

        var pending = new PendingUsage();
        pending.AddAdapter(Hour, nic.Id, 100, 10);
        db.Flush(pending, [nic], Hour);
        Assert.Equal(100, db.QueryUsage(false, Hour, Hour + 3600, [nic.Id]).Single().Received);
        Assert.Single(db.LoadAdapters()); // the half-done import was rolled back
    }

    [Fact]
    public void AdaptersThatOnlyDifferInGuidAreMerged()
    {
        using Database db = Open();
        AdapterRecord ethernet = AddEthernet(db);
        AdapterRecord oldTunnel = db.AddAdapter(Guid.NewGuid(), "wgpia0", "WireGuard Tunnel", 53, false, true, Hour);
        AdapterRecord midTunnel = db.AddAdapter(Guid.NewGuid(), "wgpia0", "WireGuard Tunnel", 53, false, null, Hour + 3600);
        AdapterRecord newTunnel = db.AddAdapter(Guid.NewGuid(), "wgpia0", "WireGuard Tunnel", 53, false, null, Hour + 7200);
        var pending = new PendingUsage();
        pending.AddAdapter(Hour, oldTunnel.Id, 100, 1);
        pending.AddAdapter(Hour + 3600, midTunnel.Id, 200, 2);
        pending.AddAdapter(Hour + 3600, newTunnel.Id, 300, 3); // reconnected within the same hour
        pending.AddAdapter(Hour, ethernet.Id, 5000, 50);
        db.Flush(pending, [], Hour + 7200);

        Assert.Equal(2, db.MergeDuplicateAdapters());

        List<AdapterRecord> adapters = db.LoadAdapters();
        Assert.Equal(2, adapters.Count);
        AdapterRecord tunnel = Assert.Single(adapters, a => a.Name == "wgpia0");
        Assert.Equal(newTunnel.Id, tunnel.Id);       // the most recently seen record survives...
        Assert.True(tunnel.CountsOverride);          // ...with the counting choice made on an older copy
        Assert.Equal([100L, 500L], db.QueryUsage(false, Hour, Hour + 7200, [tunnel.Id]).Select(r => r.Received));
        Assert.Equal(5000, db.QueryUsage(false, Hour, Hour + 3600, [ethernet.Id]).Single().Received);
        Assert.Equal(0, db.MergeDuplicateAdapters());
    }

    [Fact]
    public void AppKeyIgnoresTheVersionFolder()
    {
        Assert.Equal(
            AppKeys.ForExecutable(@"C:\Users\x\AppData\Local\Discord\app-1.0.9261\Discord.exe", "Discord"),
            AppKeys.ForExecutable(@"C:\Users\x\AppData\Local\Discord\app-1.0.9270\Discord.exe", "Discord"));
        Assert.NotEqual(
            AppKeys.ForExecutable(@"C:\Apps\Claude\claude.exe", "Claude"),
            AppKeys.ForExecutable(@"C:\Apps\ClaudeCode\claude.exe", "Claude Code"));
    }

    [Fact]
    public void AppsFromOlderVersionFoldersAreMerged()
    {
        using Database db = Open();
        // Keys as earlier versions stored them: the full lower-case path.
        long v1 = db.AddApp(@"c:\discord\app-1.0.1\discord.exe", "Discord", @"C:\Discord\app-1.0.1\Discord.exe");
        long v2 = db.AddApp(@"c:\discord\app-1.0.2\discord.exe", "Discord", @"C:\Discord\app-1.0.2\Discord.exe");
        long service = db.AddApp("svc:dnscache", "DNS Client", @"C:\Windows\System32\svchost.exe");
        long system = db.AddApp("system", "System (kernel)", null);
        var pending = new PendingUsage();
        pending.AddApp(Hour, v1, 100, 1);
        pending.AddApp(Hour, v2, 50, 5);       // both versions ran in the same hour
        pending.AddApp(Hour + 3600, v2, 70, 7);
        pending.AddApp(Hour, service, 9, 9);
        pending.AddApp(Hour, system, 8, 8);
        db.Flush(pending, [], Hour + 3600);

        Assert.Equal(1, db.MergeDuplicateApps());

        List<AppUsageRow> rows = db.QueryApps(Hour, Hour + 7200);
        Assert.Equal(3, rows.Count);
        AppUsageRow discord = Assert.Single(rows, r => r.Name == "Discord");
        Assert.Equal(220, discord.Received);
        Assert.Equal(@"C:\Discord\app-1.0.2\Discord.exe", discord.Path); // newest location, for the icon
        Assert.Equal(AppKeys.ForExecutable(discord.Path!, "Discord"), discord.Key);
        Assert.Contains(rows, r => r.Key == "svc:dnscache");
        Assert.Contains(rows, r => r.Key == "system");
        Assert.Equal(0, db.MergeDuplicateApps());
    }

    [Fact]
    public void ServicesGetEnglishNames()
    {
        Assert.Equal("Delivery Optimization", ServiceNames.EnglishName("DoSvc"));
        Assert.Equal("Connected Devices Platform User Service", ServiceNames.EnglishName("CDPUserSvc_4f3a2")); // per-user suffix
        Assert.Null(ServiceNames.EnglishName("SomeVendorService"));
        Assert.Equal("Service host: DNS Client, Vendor Service", ServiceNames.Describe([("Dnscache", "DNS-klient"), ("Vendor", "Vendor Service")]));

        using Database db = Open();
        long known = db.AddApp("svc:dosvc", "Leveringsoptimering", @"C:\Windows\System32\svchost.exe");
        long mixed = db.AddApp("svc:dnscache+vendor", "Service host: DNS-klient, Vendor", @"C:\Windows\System32\svchost.exe");
        var pending = new PendingUsage();
        pending.AddApp(Hour, known, 1, 1);
        pending.AddApp(Hour, mixed, 1, 1);
        db.Flush(pending, [], Hour);

        Assert.Equal(1, db.RenameServiceApps());
        List<AppUsageRow> rows = db.QueryApps(Hour, Hour + 3600);
        Assert.Contains(rows, r => r.AppId == known && r.Name == "Delivery Optimization");
        Assert.Contains(rows, r => r.AppId == mixed && r.Name == "Service host: DNS-klient, Vendor"); // unknown service: left alone
        Assert.Equal(0, db.RenameServiceApps());
    }

    [Fact]
    public void PerUserServicesKeepOneKeyAcrossSignIns()
    {
        Assert.Equal("svc:cdpusersvc", AppKeys.ForServices(["CDPUserSvc_4f3a2"]));
        Assert.Equal(AppKeys.ForServices(["CDPUserSvc_4f3a2", "Dnscache"]), AppKeys.ForServices(["dnscache", "CDPUserSvc_9b1c07"]));
        Assert.Equal("Foo_cafe", ServiceNames.BaseName("Foo_cafe")); // no digit: not a sign-in suffix
        Assert.Equal("Foo_ab1", ServiceNames.BaseName("Foo_ab1"));   // too short

        using Database db = Open();
        long first = db.AddApp("svc:cdpusersvc_4f3a2", "Connected Devices Platform User Service_4f3a2", @"C:\Windows\System32\svchost.exe");
        long second = db.AddApp("svc:cdpusersvc_9b1c0", "Connected Devices Platform User Service_9b1c0", @"C:\Windows\System32\svchost.exe");
        var pending = new PendingUsage();
        pending.AddApp(Hour, first, 10, 1);
        pending.AddApp(Hour, second, 20, 2);
        db.Flush(pending, [], Hour);

        Assert.Equal(1, db.MergeDuplicateApps());
        db.RenameServiceApps();
        AppUsageRow row = Assert.Single(db.QueryApps(Hour, Hour + 3600));
        Assert.Equal("svc:cdpusersvc", row.Key);
        Assert.Equal("Connected Devices Platform User Service", row.Name);
        Assert.Equal(30, row.Received);
    }

    [Fact]
    public void SnapshotRestoreUndoesChanges()
    {
        using Database db = Open();
        AdapterRecord nic = AddEthernet(db);
        var pending = new PendingUsage();
        pending.AddAdapter(Hour, nic.Id, 100, 0);
        db.Flush(pending, [], Hour);

        string snapshot = Path.Combine(_directory, "snap.db");
        db.SnapshotTo(snapshot);
        db.Flush(pending, [], Hour); // now 200
        Assert.Equal(200, db.QueryUsage(false, Hour, Hour + 3600, [nic.Id]).Single().Received);

        db.RestoreFrom(snapshot);
        Assert.Equal(100, db.QueryUsage(false, Hour, Hour + 3600, [nic.Id]).Single().Received);
    }
}
