namespace NetLedger.Tray;

/// <summary>
/// A flat toggle button used as a tab. Buttons in the same container act as one group; the selected one is filled
/// and outlined in the accent colour so it is obvious which page is showing.
/// </summary>
internal sealed class NavButton : RadioButton
{
    public NavButton(string text)
    {
        Text = text;
        Appearance = Appearance.Button;
        AutoSize = true;
        FlatStyle = FlatStyle.Flat;
        TextAlign = ContentAlignment.MiddleCenter;
        Cursor = Cursors.Hand;
        ApplyPalette();
    }

    public void ApplyPalette()
    {
        Palette palette = Theme.Current;
        BackColor = palette.Window;
        ForeColor = palette.Text;
        FlatAppearance.CheckedBackColor = palette.Selection;
        FlatAppearance.MouseOverBackColor = Checked ? palette.Selection : palette.Hover;
        FlatAppearance.BorderColor = Checked ? palette.Accent : palette.Border;
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        ApplyPalette();
    }
}
