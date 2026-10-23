using System.IO.Compression;
using System.Text.Json;

namespace NetLedger.Core.Ipc;

/// <summary>A <c>.netledger</c> backup: a zip holding one JSON document.</summary>
public static class BackupFile
{
    public const string Extension = ".netledger";
    private const string EntryName = "netledger-backup.json";

    public static void Write(string path, BackupData data)
    {
        string temp = path + ".tmp";
        try
        {
            using (FileStream file = File.Create(temp))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry(EntryName, CompressionLevel.SmallestSize);
                using Stream stream = entry.Open();
                JsonSerializer.Serialize(stream, data, LedgerJson.Default.BackupData);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    public static BackupData Read(string path)
    {
        using FileStream file = File.OpenRead(path);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(file, ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("This isn't a NetLedger backup file.");
        }

        using (zip)
        {
            ZipArchiveEntry entry = zip.GetEntry(EntryName)
                ?? throw new InvalidDataException("This isn't a NetLedger backup file.");
            using Stream stream = entry.Open();
            BackupData data = JsonSerializer.Deserialize(stream, LedgerJson.Default.BackupData)
                ?? throw new InvalidDataException("The backup is empty.");
            if (data.FormatVersion > BackupData.CurrentFormatVersion)
            {
                throw new InvalidDataException("This backup was made by a newer version of NetLedger.");
            }

            return data;
        }
    }
}
