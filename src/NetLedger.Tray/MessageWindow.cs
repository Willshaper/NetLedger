namespace NetLedger.Tray;

/// <summary>
/// A hidden top-level window for broadcasts that child and message-only windows never see: Explorer restarting,
/// theme changes, display changes, and a second NetLedger.exe asking this one to show its statistics.
/// </summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    public const string ShowStatisticsMessageName = "NetLedger.ShowStatistics";

    private readonly int _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private readonly int _showStatistics = Native.RegisterWindowMessage(ShowStatisticsMessageName);

    public MessageWindow()
    {
        CreateHandle(new CreateParams { Caption = "NetLedger messages" });
    }

    public event Action? TaskbarCreated;

    public event Action? ShowStatisticsRequested;

    /// <summary>Theme, display or DPI configuration changed.</summary>
    public event Action? EnvironmentChanged;

    public void Dispose() => DestroyHandle();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == _taskbarCreated)
        {
            TaskbarCreated?.Invoke();
        }
        else if (m.Msg == _showStatistics)
        {
            ShowStatisticsRequested?.Invoke();
        }
        else if (m.Msg is Native.WM_SETTINGCHANGE or Native.WM_DISPLAYCHANGE or Native.WM_DPICHANGED)
        {
            EnvironmentChanged?.Invoke();
        }

        base.WndProc(ref m);
    }
}
