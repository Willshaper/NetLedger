namespace NetLedger.Tray;

/// <summary>
/// Message dialogs with English button text. <see cref="MessageBox"/> labels its buttons in the Windows display
/// language, which made the app a mix of languages.
/// </summary>
internal static class Dialogs
{
    private const string Caption = "NetLedger";

    public static void Info(IWin32Window? owner, string text) => Show(owner, text, TaskDialogIcon.Information, "OK");

    public static void Warn(IWin32Window? owner, string text) => Show(owner, text, TaskDialogIcon.Warning, "OK");

    /// <summary>Asks a yes/no question; closing the dialog counts as no.</summary>
    public static bool Confirm(IWin32Window? owner, string text) =>
        Show(owner, text, TaskDialogIcon.None, "Yes", "No") == "Yes";

    private static string? Show(IWin32Window? owner, string text, TaskDialogIcon icon, params string[] buttons)
    {
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Text = text,
            Icon = icon,
            AllowCancel = true,
        };
        foreach (string label in buttons)
        {
            page.Buttons.Add(new TaskDialogButton(label));
        }

        page.DefaultButton = page.Buttons[0];
        TaskDialogButton clicked = owner is null
            ? TaskDialog.ShowDialog(page)
            : TaskDialog.ShowDialog(owner, page);
        return page.Buttons.Contains(clicked) ? clicked.Text : null;
    }
}
