namespace NetLedger.Tray;

/// <summary>
/// A window laid out in code with <see cref="Px"/>. It can be built at one DPI and open on a monitor with another
/// (two monitors with different scaling), so the layout is corrected once the real DPI is known, and
/// <see cref="Px"/> follows the window's DPI from then on.
/// </summary>
internal class ScaledForm : Form
{
    private int _layoutDpi;

    protected ScaledForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        _layoutDpi = DeviceDpi;
    }

    protected int Px(int logical) => (int)Math.Round(logical * _layoutDpi / 96.0);

    protected override void OnLoad(EventArgs e)
    {
        int actual = (int)Native.GetDpiForWindow(Handle);
        if (actual > 0 && actual != _layoutDpi)
        {
            float factor = actual / (float)_layoutDpi;
            Scale(new SizeF(factor, factor));
            _layoutDpi = actual;
        }

        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // WinForms rescales the controls itself here; later Px() calls must match.
        base.OnDpiChanged(e);
        _layoutDpi = e.DeviceDpiNew;
    }
}
