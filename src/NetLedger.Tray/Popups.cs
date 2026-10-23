namespace NetLedger.Tray;

/// <summary>A borderless, non-activating, always-on-top card with rounded corners.</summary>
internal class PopupForm : Form
{
    protected PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    protected Palette Palette { get; private set; } = Theme.Current;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetRoundedCorners(Handle);
    }

    protected void RefreshPalette()
    {
        Palette = Theme.Current;
        BackColor = Palette.Surface;
    }

    protected int Px(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Surface);
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>Puts the card above <paramref name="anchor"/>, inside the working area of its screen.</summary>
    protected void PlaceAbove(Rectangle anchor)
    {
        Rectangle work = Screen.FromRectangle(anchor).WorkingArea;
        int x = anchor.Left + anchor.Width / 2 - Width / 2;
        x = Math.Clamp(x, work.Left + Px(8), Math.Max(work.Left + Px(8), work.Right - Width - Px(8)));
        int y = Math.Min(anchor.Top, work.Bottom) - Height - Px(8);
        Location = new Point(x, Math.Max(work.Top, y));
    }
}

/// <summary>The summary that appears when hovering over the taskbar widget.</summary>
internal sealed class HoverCard : PopupForm
{
    // Shown and repainted every second while hovered, so the font is made once.
    private readonly Font _font = new(SystemFonts.MessageBoxFont!.FontFamily, 9f);
    private readonly List<(string Label, string Value)> _rows = new();
    private string? _note;
    private Color _noteColor;

    public void ShowAbove(Rectangle anchor, IEnumerable<(string Label, string Value)> rows, string? note, bool noteIsWarning)
    {
        RefreshPalette();
        _rows.Clear();
        _rows.AddRange(rows);
        _note = note;
        _noteColor = noteIsWarning ? Palette.Warning : Palette.SubtleText;

        Font font = _font;
        int labelWidth = 0, valueWidth = 0, lineHeight = 0;
        foreach ((string label, string value) in _rows)
        {
            Size l = TextRenderer.MeasureText(label, font);
            Size v = TextRenderer.MeasureText(value, font);
            labelWidth = Math.Max(labelWidth, l.Width);
            valueWidth = Math.Max(valueWidth, v.Width);
            lineHeight = Math.Max(lineHeight, Math.Max(l.Height, v.Height));
        }

        int width = labelWidth + Px(16) + valueWidth;
        int noteHeight = 0;
        if (_note is not null)
        {
            Size n = TextRenderer.MeasureText(_note, font, new Size(Math.Max(width, Px(220)), 0), TextFormatFlags.WordBreak);
            width = Math.Max(width, Math.Min(n.Width, Px(320)));
            noteHeight = TextRenderer.MeasureText(_note, font, new Size(width, 0), TextFormatFlags.WordBreak).Height + Px(6);
        }

        Size = new Size(width + Px(24), _rows.Count * (lineHeight + Px(3)) + noteHeight + Px(20));
        PlaceAbove(anchor);
        Invalidate();
        if (!Visible)
        {
            Show();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Font font = _font;
        int y = Px(10);
        int lineHeight = TextRenderer.MeasureText("Ag", font).Height;
        int valueRight = Width - Px(12);
        foreach ((string label, string value) in _rows)
        {
            TextRenderer.DrawText(e.Graphics, label, font, new Point(Px(12), y), Palette.SubtleText);
            Size v = TextRenderer.MeasureText(value, font);
            TextRenderer.DrawText(e.Graphics, value, font, new Point(valueRight - v.Width, y), Palette.Text);
            y += lineHeight + Px(3);
        }

        if (_note is not null)
        {
            var bounds = new Rectangle(Px(12), y + Px(4), Width - Px(24), Height - y);
            TextRenderer.DrawText(e.Graphics, _note, font, bounds, _noteColor, TextFormatFlags.WordBreak);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A small notification card in the bottom-right corner that closes by itself.</summary>
internal sealed class Toast : PopupForm
{
    private readonly string _title;
    private readonly string _body;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 12000 };

    public Toast(string title, string body)
    {
        _title = title;
        _body = body;
        RefreshPalette();
        Cursor = Cursors.Hand;
        _timer.Tick += (_, _) => Close();
    }

    public void ShowNow()
    {
        using Font bodyFont = new(SystemFonts.MessageBoxFont!.FontFamily, 9f);
        using Font titleFont = new(bodyFont, FontStyle.Bold);
        int width = Px(340);
        Size title = TextRenderer.MeasureText(_title, titleFont, new Size(width - Px(32), 0), TextFormatFlags.WordBreak);
        Size body = TextRenderer.MeasureText(_body, bodyFont, new Size(width - Px(32), 0), TextFormatFlags.WordBreak);
        Size = new Size(width, title.Height + body.Height + Px(36));
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(work.Right - Width - Px(12), work.Bottom - Height - Px(12));
        Show();
        _timer.Start();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using Font bodyFont = new(SystemFonts.MessageBoxFont!.FontFamily, 9f);
        using Font titleFont = new(bodyFont, FontStyle.Bold);
        int inner = Width - Px(32);
        Size title = TextRenderer.MeasureText(_title, titleFont, new Size(inner, 0), TextFormatFlags.WordBreak);
        using (var accent = new SolidBrush(Palette.Accent))
        {
            e.Graphics.FillRectangle(accent, 0, 0, Px(4), Height);
        }

        TextRenderer.DrawText(e.Graphics, _title, titleFont, new Rectangle(Px(18), Px(14), inner, title.Height), Palette.Text, TextFormatFlags.WordBreak);
        TextRenderer.DrawText(e.Graphics, _body, bodyFont, new Rectangle(Px(18), Px(18) + title.Height, inner, Height), Palette.SubtleText, TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
