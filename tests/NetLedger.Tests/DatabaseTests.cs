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
        var backup = new BackupData { Hours = [new UsageRecord(Hour + 5, 1, 10, 1)] };
        Assert.Throws<InvalidDataException>(() => db.PreviewImport(backup));
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
