using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using NetLedger.Core;
using NetLedger.Core.Ipc;

namespace NetLedger.Tray;

/// <summary>
/// The speed readout on the taskbar. Windows 11 has no API for taskbar toolbars, so this is a per-pixel-alpha
/// layered window created as a child of the taskbar (Shell_TrayWnd), just left of the notification area.
/// If that fails it falls back to an always-on-top window laid over the same spot.
/// </summary>
/// <remarks>
/// A cross-process child window shares input with Explorer: never block the UI thread, or the taskbar hangs too.
/// </remarks>
internal sealed unsafe class TaskbarWidget : NativeWindow, IDisposable
{
    private const int LayoutCheckEvery = 2; // seconds

    private readonly SpeedMonitor _speed;
    private UiSettings _settings;
    private QuotaStatus? _quota;
    private nint _tray;
    private nint _notify;
    private Native.RECT _trayRect;
    private Native.RECT _notifyRect;
    private uint _dpi;
    private Size _size;
    private Point _screenPosition;
    private bool _embedded;
    private bool _hiddenForFullScreen;
    private bool _tracking;
    private int _ticks;
    private bool _disposed;

    // Drawing surface: a 32-bit premultiplied DIB that GDI+ draws into and UpdateLayeredWindow composites.
    private nint _memDc;
    private nint _dib;
    private nint _oldBitmap;
    private Bitmap? _surface;
    private Size _surfaceSize;

    private Font? _font;
    private float _scale = 1;
    private int _textWidth;
    private int _lineHeight;
    private double[] _rxHistory = [];
    private double[] _txHistory = [];

    public TaskbarWidget(SpeedMonitor speed, UiSettings settings)
    {
        _speed = speed;
        _settings = settings;
    }

    public event Action? Clicked;

    public event Action<Point>? MenuRequested;

    public event Action<Rectangle>? HoverStarted;

    public event Action? HoverEnded;

    public Rectangle ScreenBounds => new(_screenPosition, _size);

    public bool IsEmbedded => _embedded && Handle != 0;

    public void ApplySettings(UiSettings settings)
    {
        _settings = settings;
        Attach();
    }

    public void SetQuota(QuotaStatus? quota)
    {
        _quota = quota;
        Render();
    }

    /// <summary>(Re)creates the window against the current taskbar. Safe to call at any time.</summary>
    public void Attach()
    {
        if (_disposed)
        {
            return;
        }

        if (Handle != 0)
        {
            DestroyHandle();
        }

        _tray = Native.FindWindow("Shell_TrayWnd", null);
        if (_tray == 0)
        {
            return; // Explorer isn't up yet; Tick retries.
        }

        _notify = Native.FindWindowEx(_tray, 0, "TrayNotifyWnd", null);
        Layout();
        if (_settings.Placement == WidgetPlacement.Embedded && TryCreate(embedded: true))
        {
            return;
        }

        TryCreate(embedded: false);
    }

    /// <summary>Called once a second with fresh speed data.</summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        if (++_ticks % LayoutCheckEvery == 0)
        {
            if (Handle == 0 || !Native.IsWindow(_tray))
            {
                Attach();
                return;
            }

            if (LayoutChanged())
            {
                Layout();
                Position();
            }

            if (!_embedded)
            {
                KeepFloatingWindowSensible();
            }
        }

        Render();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Handle != 0)
        {
            DestroyHandle();
        }

        FreeSurface();
        _font?.Dispose();
    }

    // ---- window management ----

    private bool TryCreate(bool embedded)
    {
        Point location = embedded ? ToTrayClient(_screenPosition) : _screenPosition;
        var cp = new CreateParams
        {
            Caption = "NetLedger",
            X = location.X,
            Y = location.Y,
            Width = _size.Width,
            Height = _size.Height,
            Style = (embedded ? Native.WS_CHILD | Native.WS_CLIPSIBLINGS : Native.WS_POPUP) | Native.WS_VISIBLE,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                      | (embedded ? 0 : Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE),
            Parent = embedded ? _tray : 0,
        };

        try
        {
            CreateHandle(cp);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }

        _embedded = embedded;
        if (!Render())
        {
            DestroyHandle();
            return false;
        }

        Position();
        return true;
    }

    private void Position()
    {
        if (Handle == 0)
        {
            return;
        }

        if (_embedded)
        {
            Point p = ToTrayClient(_screenPosition);
            Native.SetWindowPos(Handle, Native.HWND_TOP, p.X, p.Y, _size.Width, _size.Height, Native.SWP_NOACTIVATE);
        }
        else
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _screenPosition.X, _screenPosition.Y,
                _size.Width, _size.Height, Native.SWP_NOACTIVATE);
        }
    }

    private Point ToTrayClient(Point screen)
    {
        var p = new Native.POINT { X = screen.X, Y = screen.Y };
        Native.ScreenToClient(_tray, ref p);
        return new Point(p.X, p.Y);
    }

    private bool LayoutChanged()
    {
        Native.GetWindowRect(_tray, out Native.RECT tray);
        Native.RECT notify = default;
        if (_notify != 0)
        {
            Native.GetWindowRect(_notify, out notify);
        }

        return !tray.Equals(_trayRect) || !notify.Equals(_notifyRect) || Native.GetDpiForWindow(_tray) != _dpi;
    }

    /// <summary>A floating window has to dodge full-screen apps and an auto-hidden taskbar by itself.</summary>
    private void KeepFloatingWindowSensible()
    {
        Screen screen = Screen.FromRectangle(_trayRect.ToRectangle());
        Rectangle visible = Rectangle.Intersect(screen.Bounds, _trayRect.ToRectangle());
        bool taskbarShown = Native.IsWindowVisible(_tray) && visible.Height >= _trayRect.Height / 2;
        bool hide = Native.IsFullScreenAppActive() || !taskbarShown;
        if (hide != _hiddenForFullScreen)
        {
            _hiddenForFullScreen = hide;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE
                | (hide ? Native.SWP_HIDEWINDOW : Native.SWP_SHOWWINDOW));
        }
        else if (!hide)
        {
            // Clicking the taskbar raises it above other topmost windows; climb back on top.
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    // ---- layout ----

    private void Layout()
    {
        Native.GetWindowRect(_tray, out _trayRect);
        _notifyRect = default;
        if (_notify == 0 || !Native.GetWindowRect(_notify, out _notifyRect))
        {
            _notifyRect = new Native.RECT { Left = _trayRect.Right, Right = _trayRect.Right, Top = _trayRect.Top, Bottom = _trayRect.Bottom };
        }

        _dpi = Native.GetDpiForWindow(_tray);
        _scale = (_dpi == 0 ? 96 : _dpi) / 96f;

        _font?.Dispose();
        float pixels = _settings.FontSize * _scale * 96f / 72f;
        _font = new Font(_settings.FontFamily, pixels, FontStyle.Regular, GraphicsUnit.Pixel);

        using (var measure = new Bitmap(1, 1))
        using (Graphics g = Graphics.FromImage(measure))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            SizeF text = g.MeasureString(ByteFormat.WidestRate(_settings.RateUnit), _font, PointF.Empty, StringFormat.GenericTypographic);
            _textWidth = (int)Math.Ceiling(text.Width * 1.06f + ArrowWidth + S(5));
            _lineHeight = (int)Math.Ceiling(_font.GetHeight(g));
        }

        int graphWidth = _settings.ShowGraph ? S(_settings.GraphWidth) : 0;
        int width = S(4) + graphWidth + (_settings.ShowGraph ? S(6) : 0) + _textWidth + S(4);
        bool horizontal = _trayRect.Width >= _trayRect.Height;
        int height = horizontal ? _trayRect.Height : Math.Min(_trayRect.Width, S(48));
        _size = new Size(width, Math.Max(height, _lineHeight * 2));
        _screenPosition = horizontal
            ? new Point(_notifyRect.Left - S(_settings.GapFromTray) - width, _trayRect.Top)
            : new Point(_trayRect.Left, _notifyRect.Top - S(_settings.GapFromTray) - _size.Height);

        if (_rxHistory.Length != _settings.GraphSeconds)
        {
            _rxHistory = new double[_settings.GraphSeconds];
            _txHistory = new double[_settings.GraphSeconds];
        }
    }

    private int S(float logical) => (int)Math.Round(logical * _scale);

    private float ArrowWidth => _lineHeight * 0.5f;

    // ---- rendering ----

    private bool Render()
    {
        if (Handle == 0 || _font is null || _size.Width <= 0 || _size.Height <= 0)
        {
            return false;
        }

        EnsureSurface(_size);
        using (Graphics g = Graphics.FromImage(_surface!))
        {
            // Alpha 1 keeps every pixel hit-testable (fully transparent pixels click through) yet invisible.
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            bool lightTaskbar = Theme.TaskbarIsLight;
            Color up = Readable(_settings.Upload, lightTaskbar);
            Color down = Readable(_settings.Download, lightTaskbar);
            Color frame = lightTaskbar ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(46, 255, 255, 255);

            int x = S(4);
            if (_settings.ShowGraph)
            {
                var graph = new Rectangle(x, S(8), S(_settings.GraphWidth), _size.Height - S(16));
                DrawGraph(g, graph, up, down, frame);
                x = graph.Right + S(6);
            }

            (double rx, double tx) = _speed.CurrentRate();
            int top = (_size.Height - 2 * _lineHeight) / 2;
            DrawLine(g, new Rectangle(x, top, _textWidth, _lineHeight), pointsUp: true, ByteFormat.Rate(tx, _settings.RateUnit), up);
            DrawLine(g, new Rectangle(x, top + _lineHeight, _textWidth, _lineHeight), pointsUp: false, ByteFormat.Rate(rx, _settings.RateUnit), down);

            if (_settings.ShowQuotaBar && _quota is { Enabled: true })
            {
                DrawQuotaBar(g, new Rectangle(x + (int)ArrowWidth + S(5), top + 2 * _lineHeight + S(1), _textWidth - (int)ArrowWidth - S(5), Math.Max(2, S(2))), frame);
            }
        }

        var size = new Native.SIZE { Cx = _size.Width, Cy = _size.Height };
        var source = new Native.POINT();
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        nint screenDc = Native.GetDC(0);
        bool ok = Native.UpdateLayeredWindow(Handle, screenDc, null, &size, _memDc, &source, 0, &blend, Native.ULW_ALPHA);
        Native.ReleaseDC(0, screenDc);
        return ok;
    }

    private static Color Readable(Color color, bool lightBackground) =>
        lightBackground ? Color.FromArgb(255, color.R * 7 / 10, color.G * 7 / 10, color.B * 7 / 10) : color;

    private void DrawLine(Graphics g, Rectangle bounds, bool pointsUp, string text, Color color)
    {
        float w = ArrowWidth;
        float h = w * 0.8f;
        float cx = bounds.Left + w / 2;
        float cy = bounds.Top + bounds.Height / 2f;
        PointF[] triangle = pointsUp
            ? [new(cx - w / 2, cy + h / 2), new(cx + w / 2, cy + h / 2), new(cx, cy - h / 2)]
            : [new(cx - w / 2, cy - h / 2), new(cx + w / 2, cy - h / 2), new(cx, cy + h / 2)];
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, triangle);

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, _font!, brush, new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height), format);
    }

    private void DrawGraph(Graphics g, Rectangle bounds, Color up, Color down, Color frame)
    {
        int n = _rxHistory.Length;
        _speed.CopyHistory(n, _rxHistory, _txHistory);
        double max = 64 * 1024; // ~0.5 Mbit: keeps idle background chatter from filling the graph
        for (int i = 0; i < n; i++)
        {
            max = Math.Max(max, Math.Max(_rxHistory[i], _txHistory[i]));
        }

        max *= 1.1;
        using (var pen = new Pen(frame, 1))
        {
            g.DrawLine(pen, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
        }

        PointF[] Points(double[] values)
        {
            var points = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                float px = bounds.Left + (float)i * bounds.Width / Math.Max(1, n - 1);
                float py = bounds.Bottom - (float)(values[i] / max * bounds.Height);
                points[i] = new PointF(px, py);
            }

            return points;
        }

        PointF[] rx = Points(_rxHistory);
        var area = new PointF[n + 2];
        rx.CopyTo(area, 0);
        area[n] = new PointF(bounds.Right, bounds.Bottom);
        area[n + 1] = new PointF(bounds.Left, bounds.Bottom);
        using (var fill = new SolidBrush(Color.FromArgb(90, down)))
        {
            g.FillPolygon(fill, area);
        }

        using (var downPen = new Pen(down, Math.Max(1f, _scale)))
        {
            g.DrawLines(downPen, rx);
        }

        using (var upPen = new Pen(up, Math.Max(1f, _scale)))
        {
            g.DrawLines(upPen, Points(_txHistory));
        }
    }

    private void DrawQuotaBar(Graphics g, Rectangle bounds, Color track)
    {
        QuotaStatus quota = _quota!;
        double fraction = Math.Clamp(quota.Percent / 100.0, 0, 1);
        Color color = quota.Percent >= 100 ? Color.FromArgb(232, 72, 60)
            : quota.Percent >= 80 ? Color.FromArgb(240, 170, 40)
            : Color.FromArgb(90, 170, 240);
        using (var trackBrush = new SolidBrush(track))
        {
            g.FillRectangle(trackBrush, bounds);
        }

        using var fill = new SolidBrush(color);
        g.FillRectangle(fill, bounds.Left, bounds.Top, (int)Math.Round(bounds.Width * fraction), bounds.Height);
    }

    private void EnsureSurface(Size size)
    {
        if (_surface is not null && _surfaceSize == size)
        {
            return;
        }

        FreeSurface();
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Native.BITMAPINFOHEADER),
            biWidth = size.Width,
            biHeight = -size.Height, // top-down, matching GDI+'s row order
            biPlanes = 1,
            biBitCount = 32,
        };
        nint screenDc = Native.GetDC(0);
        _memDc = Native.CreateCompatibleDC(screenDc);
        Native.ReleaseDC(0, screenDc);
        _dib = Native.CreateDIBSection(_memDc, &header, 0, out nint bits, 0, 0);
        _oldBitmap = Native.SelectObject(_memDc, _dib);
        _surface = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
        _surfaceSize = size;
    }

    private void FreeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_memDc != 0)
        {
            Native.SelectObject(_memDc, _oldBitmap);
            Native.DeleteDC(_memDc);
            _memDc = 0;
        }

        if (_dib != 0)
        {
            Native.DeleteObject(_dib);
            _dib = 0;
        }
    }

    // ---- input ----

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                m.Result = Native.MA_NOACTIVATE;
                return;
            case Native.WM_MOUSEMOVE when !_tracking:
                var tme = new Native.TRACKMOUSEEVENT
                {
                    cbSize = (uint)sizeof(Native.TRACKMOUSEEVENT),
                    dwFlags = Native.TME_HOVER | Native.TME_LEAVE,
                    hwndTrack = Handle,
                    dwHoverTime = 350,
                };
                _tracking = Native.TrackMouseEvent(ref tme);
                break;
            case Native.WM_MOUSEHOVER:
                HoverStarted?.Invoke(ScreenBounds);
                break;
            case Native.WM_MOUSELEAVE:
                _tracking = false;
                HoverEnded?.Invoke();
                break;
            case Native.WM_LBUTTONUP:
                HoverEnded?.Invoke();
                Clicked?.Invoke();
                break;
            case Native.WM_RBUTTONUP:
                HoverEnded?.Invoke();
                MenuRequested?.Invoke(Cursor.Position);
                break;
            case Native.WM_DPICHANGED:
            case Native.WM_DPICHANGED_AFTERPARENT:
                _dpi = 0; // forces a relayout on the next layout check
                break;
        }

        base.WndProc(ref m);
    }
}
