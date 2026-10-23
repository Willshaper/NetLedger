using System.Globalization;

namespace NetLedger.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(initiallyOwned: true, @"Local\NetLedger.Tray", out bool first);
        if (!first)
        {
            // Already running: ask that instance to bring up its statistics window instead. This process got the
            // user's click, so it may pass on the right to come to the front.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            Native.PostMessage(Native.HWND_BROADCAST, Native.RegisterWindowMessage(MessageWindow.ShowStatisticsMessageName), 0, 0);
            return;
        }

        // English throughout, whatever the Windows regional format: day-month dates, 24-hour clock, decimal points.
        CultureInfo english = CultureInfo.GetCultureInfo("en-GB");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new TrayApp());
    }
}
