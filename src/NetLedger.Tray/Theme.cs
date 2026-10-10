using Microsoft.Win32;

namespace NetLedger.Tray;

/// <summary>Colours for NetLedger's own windows, following the Windows light/dark settings.</summary>
internal sealed class Palette
{
    public required bool Dark { get; init; }
    public required Color Window { get; init; }
    public required Color Surface { get; init; }
    public required Color Text { get; init; }
    public required Color SubtleText { get; init; }
    public required Color Border { get; init; }
    public required Color Grid { get; init; }
    public required Color Accent { get; init; }
    public required Color Warning { get; init; }
    public required Color Danger { get; init; }
}

internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Palette DarkPalette = new()
    {
        Dark = true,
        Window = Color.FromArgb(32, 32, 32),
        Surface = Color.FromArgb(43, 43, 43),
        Text = Color.FromArgb(242, 242, 242),
        SubtleText = Color.FromArgb(170, 170, 170),
        Border = Color.FromArgb(64, 64, 64),
        Grid = Color.FromArgb(58, 58, 58),
        Accent = Color.FromArgb(96, 165, 250),
        Warning = Color.FromArgb(245, 180, 70),
        Danger = Color.FromArgb(240, 100, 90),
    };

    private static readonly Palette LightPalette = new()
    {
        Dark = false,
        Window = Color.FromArgb(249, 249, 249),
        Surface = Color.White,
        Text = Color.FromArgb(28, 28, 28),
        SubtleText = Color.FromArgb(96, 96, 96),
        Border = Color.FromArgb(222, 222, 222),
        Grid = Color.FromArgb(232, 232, 232),
        Accent = Color.FromArgb(0, 103, 192),
        Warning = Color.FromArgb(196, 120, 0),
        Danger = Color.FromArgb(196, 43, 28),
    };

    /// <summary>The taskbar has its own light/dark setting, separate from apps.</summary>
    public static bool TaskbarIsLight => ReadFlag("SystemUsesLightTheme", fallback: false);

    public static bool AppsAreLight => ReadFlag("AppsUseLightTheme", fallback: true);

    public static Palette Current => AppsAreLight ? LightPalette : DarkPalette;

    private static bool ReadFlag(string name, bool fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int value ? value != 0 : fallback;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return fallback;
        }
    }

    public static Color Parse(string? hex, Color fallback)
    {
        if (hex is { Length: 7 } && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        return fallback;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Applies the palette to a form and its controls, and darkens the title bar to match.</summary>
    public static void Apply(Form form)
    {
        Palette palette = Current;
        form.BackColor = palette.Window;
        form.ForeColor = palette.Text;
        if (form.IsHandleCreated)
        {
            Native.SetDarkTitleBar(form.Handle, palette.Dark);
        }
        else
        {
            form.HandleCreated += (_, _) => Native.SetDarkTitleBar(form.Handle, Current.Dark);
        }
    }
}
