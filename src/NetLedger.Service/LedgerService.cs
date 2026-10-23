using System.ServiceProcess;
using NetLedger.Core.Ipc;

namespace NetLedger.Service;

internal sealed class LedgerService : ServiceBase
{
    private readonly string _dataDirectory;
    private Engine? _engine;

    public LedgerService(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        ServiceName = PipeProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _engine = new Engine(_dataDirectory, secureDataDirectory: true);
        try
        {
            _engine.Start();
        }
        catch (Exception ex)
        {
            // Without this the Service Control Manager only reports "failed to start".
            Log.Error("Startup failed", ex);
            throw;
        }
    }

    protected override void OnStop() => StopEngine();

    protected override void OnShutdown() => StopEngine();

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                _engine?.Suspend();
                break;
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeSuspend:
                _engine?.Resume();
                break;
        }

        return true;
    }

    private void StopEngine()
    {
        _engine?.Dispose();
        _engine = null;
    }
}
