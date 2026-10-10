using System.ServiceProcess;
using NetLedger.Service;

// Usage:
//   NetLedger.Service.exe                    run under the Service Control Manager
//   NetLedger.Service.exe --console [--data <dir>]
//                                            run in a console for development (per-app tracking needs admin)
bool console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
int dataIndex = Array.FindIndex(args, a => a.Equals("--data", StringComparison.OrdinalIgnoreCase));
string dataDirectory = dataIndex >= 0 && dataIndex + 1 < args.Length
    ? Path.GetFullPath(args[dataIndex + 1])
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetLedger");

if (!console)
{
    ServiceBase.Run(new LedgerService(dataDirectory));
    return 0;
}

using var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Set();
};

var engine = new Engine(dataDirectory, secureDataDirectory: false);
engine.Start();
Console.WriteLine("NetLedger service running in console mode. Press Ctrl+C to stop.");
stop.Wait();
engine.Dispose();
return 0;
