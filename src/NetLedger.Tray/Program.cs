namespace NetLedger.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(initiallyOwned: true, @"Local\NetLedger.Tray", out bool first);
        if (!first)
        {
            // Already running: ask that instance to bring up its statistics window instead.
            Native.PostMessage(Native.HWND_BROADCAST, Native.RegisterWindowMessage(MessageWindow.ShowStatisticsMessageName), 0, 0);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new TrayApp());
    }
}
