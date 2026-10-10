using System.Drawing.Drawing2D;
using NetLedger.Core;

namespace NetLedger.Tray;

/// <summary>Stacked download/upload bars with a byte axis and a hover readout.</summary>
internal sealed class BarChart : Control
{
    private List<ChartBar> _bars = [];
    private int _hover = -1;
    private Rectangle _plot;
    private float _slot;

    public BarChart()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public Color DownloadColor { get; set; } = Color.FromArgb(242, 163, 58);

    public Color UploadColor { get; set; } = Color.FromArgb(124, 195, 74);

    public string EmptyText { get; set; } = "No data";

    /// <summary>Raised with the bar index when a bar is clicked.</summary>
    public event Action<int>? BarClicked;

    public void SetBars(List<ChartBar> bars)
    {
        _bars = bars;
        _hover = -1;
        Invalidate();
    }

    private int Px(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);

    protected override void OnPaint(PaintEventArgs e)
    {
        Palette palette = Theme.Current;
        Graphics g = e.Graphics;
        g.Clear(palette.Window);
        g.SmoothingMode = SmoothingMode.None;

        if (_bars.Count == 0 || _bars.All(b => b.Received + b.Sent == 0))
        {
            TextRenderer.DrawText(g, EmptyText, Font, ClientRectangle, palette.SubtleText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (_bars.Count == 0)
            {
                return;
            }
        }

        long max = Math.Max(1, _bars.Max(b => b.Received + b.Sent));
        long top = NiceCeiling(max);
        int labelWidth = Enumerable.Range(0, 5)
            .Max(i => TextRenderer.MeasureText(ByteFormat.Size(top * i / 4), Font).Width);
        int textHeight = TextRenderer.MeasureText("Ag", Font).Height;
        _plot = Rectangle.FromLTRB(labelWidth + Px(10), Px(10), Width - Px(10), Height - textHeight - Px(8));
        if (_plot.Width < 10 || _plot.Height < 10)
        {
            return;
        }

        using var gridPen = new Pen(palette.Grid);
        for (int i = 0; i <= 4; i++)
        {
            int y = _plot.Bottom - (int)((long)_plot.Height * i / 4);
            g.DrawLine(gridPen, _plot.Left, y, _plot.Right, y);
            string label = ByteFormat.Size(top * i / 4);
            Size size = TextRenderer.MeasureText(label, Font);
            TextRenderer.DrawText(g, label, Font, new Point(_plot.Left - Px(6) - size.Width, y - size.Height / 2), palette.SubtleText);
        }

        _slot = (float)_plot.Width / _bars.Count;
        float barWidth = Math.Max(1f, _slot * 0.72f);
        for (int i = 0; i < _bars.Count; i++)
        {
            ChartBar bar = _bars[i];
            float x = _plot.Left + i * _slot + (_slot - barWidth) / 2;
            float rxHeight = (float)((double)bar.Received / top * _plot.Height);
            float txHeight = (float)((double)bar.Sent / top * _plot.Height);
            int alpha = bar.Faded ? 110 : 255;
            if (i == _hover)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(palette.Dark ? 30 : 18, palette.Text));
                g.FillRectangle(hoverBrush, _plot.Left + i * _slot, _plot.Top, _slot, _plot.Height);
            }

            using (var down = new SolidBrush(Color.FromArgb(alpha, DownloadColor)))
            {
                g.FillRectangle(down, x, _plot.Bottom - rxHeight, barWidth, rxHeight);
            }

            using (var up = new SolidBrush(Color.FromArgb(alpha, UploadColor)))
            {
                g.FillRectangle(up, x, _plot.Bottom - rxHeight - txHeight, barWidth, txHeight);
            }

            if (bar.Highlight)
            {
                using var accent = new Pen(palette.Accent, Px(2));
                g.DrawLine(accent, x, _plot.Bottom + Px(2), x + barWidth, _plot.Bottom + Px(2));
            }
        }

        int widest = _bars.Max(b => TextRenderer.MeasureText(b.Label, Font).Width) + Px(8);
        int every = Math.Max(1, (int)Math.Ceiling(widest / Math.Max(1f, _slot)));
        for (int i = 0; i < _bars.Count; i += every)
        {
            Size size = TextRenderer.MeasureText(_bars[i].Label, Font);
            int cx = (int)(_plot.Left + i * _slot + _slot / 2);
            TextRenderer.DrawText(g, _bars[i].Label, Font, new Point(cx - size.Width / 2, _plot.Bottom + Px(4)), palette.SubtleText);
        }

        if (_hover >= 0 && _hover < _bars.Count)
        {
            DrawReadout(g, palette, _bars[_hover]);
        }
    }

    private void DrawReadout(Graphics g, Palette palette, ChartBar bar)
    {
        string text = $"{bar.Detail}\n↓ {ByteFormat.Size(bar.Received)}   ↑ {ByteFormat.Size(bar.Sent)}   Σ {ByteFormat.Size(bar.Received + bar.Sent)}";
        Size size = TextRenderer.MeasureText(text, Font);
        size += new Size(Px(16), Px(10));
        float cx = _plot.Left + _hover * _slot + _slot / 2;
        int x = (int)Math.Clamp(cx - size.Width / 2f, _plot.Left, Math.Max(_plot.Left, _plot.Right - size.Width));
        var box = new Rectangle(x, _plot.Top, size.Width, size.Height);
        using (var fill = new SolidBrush(palette.Surface))
        {
            g.FillRectangle(fill, box);
        }

        using (var border = new Pen(palette.Border))
        {
            g.DrawRectangle(border, box);
        }

        TextRenderer.DrawText(g, text, Font, Rectangle.Inflate(box, -Px(8), -Px(5)), palette.Text, TextFormatFlags.Left);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = _slot > 0 && _plot.Contains(e.Location) ? (int)((e.X - _plot.Left) / _slot) : -1;
        if (index >= _bars.Count)
        {
            index = -1;
        }

        if (index != _hover)
        {
            _hover = index;
            Cursor = index >= 0 && BarClicked is not null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_hover >= 0 && e.Button == MouseButtons.Left)
        {
            BarClicked?.Invoke(_hover);
        }
    }

    /// <summary>Rounds up to 1, 2, 2.5 or 5 × 10ⁿ of the most readable byte unit.</summary>
    internal static long NiceCeiling(long value)
    {
        double step = ByteFormat.SizeBase == SizeBase.Binary ? 1024 : 1000;
        double unit = 1;
        while (value / unit >= step)
        {
            unit *= step;
        }

        double scaled = value / unit;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(scaled)));
        double fraction = scaled / magnitude;
        double nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10;
        return (long)Math.Ceiling(nice * magnitude * unit);
    }
}

internal sealed record ChartBar(string Label, string Detail, long Received, long Sent, bool Faded, bool Highlight);
