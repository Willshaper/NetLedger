using System.Globalization;
using System.Text.Json;
using NetLedger.Core;
using NetLedger.Core.Ipc;

namespace NetLedger.Tray;

/// <summary>
/// Back up and restore. The file is read and written here, in the user's process: the service only ever sees
/// the data, never a path, so it can't be steered into reading or writing files as SYSTEM.
/// </summary>
internal sealed class BackupFlow(LedgerClient client)
{
    private static readonly TimeSpan LongCall = TimeSpan.FromMinutes(5);
    private const string Filter = "NetLedger backup (*.netledger)|*.netledger|All files (*.*)|*.*";

    /// <summary>Raised after a restore or undo changed the history.</summary>
    public event Action? HistoryChanged;

    public async Task ExportAsync(IWin32Window? owner)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Back up NetLedger data",
            Filter = Filter,
            DefaultExt = BackupFile.Extension,
            FileName = $"NetLedger-backup-{DateTime.Now:yyyy-MM-dd}{BackupFile.Extension}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            return;
        }

        try
        {
            BackupData data;
            using (new BusyCursor())
            {
                IpcResponse response = await client.CallAsync(new IpcRequest { Op = Ops.Export }, LongCall);
                data = response.Backup ?? throw new ServiceRequestException("The service returned no data.");
                await Task.Run(() => BackupFile.Write(dialog.FileName, data));
            }

            Dialogs.Info(owner,
                $"Saved {data.Hours.Select(h => h.Time).Distinct().Count():N0} hours of history, {data.AppHours.Count:N0} per-app records and {data.Outages.Count:N0} outages to\n{dialog.FileName}");
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceRequestException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Warn(owner, $"The backup failed: {ex.Message}");
        }
    }

    public async Task ImportAsync(IWin32Window? owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Restore NetLedger data",
            Filter = Filter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            return;
        }

        try
        {
            BackupData data;
            ImportPreview preview;
            using (new BusyCursor())
            {
                data = await Task.Run(() => BackupFile.Read(dialog.FileName));
                IpcResponse response = await client.CallAsync(new IpcRequest { Op = Ops.ImportPreview, Backup = data }, LongCall);
                preview = response.Preview!;
            }

            string range = preview.FirstUtc is long first && preview.LastUtc is long last
                ? $"{Local(first).ToString("d MMM yyyy", CultureInfo.CurrentCulture)} – {Local(last).ToString("d MMM yyyy", CultureInfo.CurrentCulture)}"
                : "no hourly history";
            string overlap = preview.OverlappingHours == 0
                ? "None of those hours overlap what this PC has recorded."
                : $"{preview.OverlappingHours:N0} of those hours already have data here; the backup's version will replace them.";
            string message =
                $"Backup from {preview.MachineName ?? "an unknown PC"}, made {Local(preview.ExportedUtc).ToString("g", CultureInfo.CurrentCulture)}.\n\n"
                + $"It covers {range}: {preview.HourCount:N0} hours, ↓ {ByteFormat.Size(preview.Received)} ↑ {ByteFormat.Size(preview.Sent)}, "
                + $"{preview.AppRows:N0} per-app records and {preview.Outages:N0} outages.\n\n{overlap}\n\n"
                + "A snapshot of the current history is taken first, so you can undo this. Restore now?";
            if (!Dialogs.Confirm(owner, message))
            {
                return;
            }

            ImportResult result;
            using (new BusyCursor())
            {
                IpcResponse response = await client.CallAsync(new IpcRequest { Op = Ops.Import, Backup = data }, LongCall);
                result = response.Import!;
            }

            HistoryChanged?.Invoke();
            Dialogs.Info(owner,
                $"Restored {result.RowsImported:N0} records ({result.HoursReplaced:N0} hours). Use \"Undo last restore\" in Settings → Data to go back.");
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceRequestException or IOException
                                       or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Dialogs.Warn(owner, $"The restore failed: {ex.Message}");
        }
    }

    public async Task UndoAsync(IWin32Window? owner)
    {
        if (!Dialogs.Confirm(owner,
                "Put the history back the way it was before the last restore? Anything recorded since that restore is lost."))
        {
            return;
        }

        try
        {
            using (new BusyCursor())
            {
                await client.CallAsync(new IpcRequest { Op = Ops.UndoImport }, LongCall);
            }

            HistoryChanged?.Invoke();
            Dialogs.Info(owner, "The last restore was undone.");
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceRequestException)
        {
            Dialogs.Warn(owner, ex.Message);
        }
    }

    private static DateTime Local(long utcSeconds) => DateTimeOffset.FromUnixTimeSeconds(utcSeconds).LocalDateTime;

    private sealed class BusyCursor : IDisposable
    {
        private readonly Cursor _previous = Cursor.Current ?? Cursors.Default;

        public BusyCursor()
        {
            Cursor.Current = Cursors.WaitCursor;
        }

        public void Dispose() => Cursor.Current = _previous;
    }
}
