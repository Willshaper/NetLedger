using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using NetLedger.Core;

namespace NetLedger.Tray;

internal enum WidgetPlacement
{
    /// <summary>Inside the taskbar, next to the notification area.</summary>
    Embedded,

    /// <summary>An always-on-top window laid over the taskbar.</summary>
    Floating,
}

/// <summary>Per-user display preferences, stored in %APPDATA%\NetLedger\settings.json.</summary>
internal sealed class UiSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NetLedger";

    public RateUnit RateUnit { get; set; } = RateUnit.AutoBits;
    public int SmoothingSeconds { get; set; } = 2;
    public SizeBase SizeBase { get; set; } = SizeBase.Binary;
    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;
    public WidgetPlacement Placement { get; set; } = WidgetPlacement.Embedded;
    public bool ShowGraph { get; set; } = true;
    public int GraphWidth { get; set; } = 44;
    public int GraphSeconds { get; set; } = 60;
    public bool ShowQuotaBar { get; set; } = true;
    public string UploadColor { get; set; } = "#7CC34A";
    public string DownloadColor { get; set; } = "#F2A33A";
    public string FontFamily { get; set; } = "Segoe UI";
    public float FontSize { get; set; } = 9f;
    public int GapFromTray { get; set; } = 6;
    public int StatsWidth { get; set; }
    public int StatsHeight { get; set; }

    [JsonIgnore]
    public Color Upload => Theme.Parse(UploadColor, Color.FromArgb(124, 195, 74));

    [JsonIgnore]
    public Color Download => Theme.Parse(DownloadColor, Color.FromArgb(242, 163, 58));

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetLedger", "settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                UiSettings? loaded = JsonSerializer.Deserialize(File.ReadAllText(FilePath), UiJson.Default.UiSettings);
                if (loaded is not null)
                {
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to defaults; the file is rewritten on the next save.
        }

        return new UiSettings();
    }

    public void Save()
    {
        Clamp();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, UiJson.Default.UiSettings));
        File.Move(temp, FilePath, overwrite: true);
    }

    public UiSettings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, UiJson.Default.UiSettings), UiJson.Default.UiSettings)!;

    private void Clamp()
    {
        SmoothingSeconds = Math.Clamp(SmoothingSeconds, 1, 10);
        GraphWidth = Math.Clamp(GraphWidth, 20, 200);
        GraphSeconds = Math.Clamp(GraphSeconds, 20, 600);
        FontSize = Math.Clamp(FontSize, 6f, 16f);
        GapFromTray = Math.Clamp(GapFromTray, 0, 2000);
        if (string.IsNullOrWhiteSpace(FontFamily))
        {
            FontFamily = "Segoe UI";
        }
    }

    public static bool StartsWithWindows
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
            {
                key.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(UiSettings))]
internal sealed partial class UiJson : JsonSerializerContext
{
}
