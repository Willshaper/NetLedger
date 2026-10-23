using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using NetLedger.Core.Ipc;

namespace NetLedger.Service.Ipc;

/// <summary>
/// Serves requests from the tray app. Local signed-in users may connect; network logons are denied outright.
/// The handler never receives file paths, so a caller can't make this SYSTEM process touch arbitrary files.
/// </summary>
internal sealed class PipeServer : IDisposable
{
    private const int Listeners = 2;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<IpcRequest, IpcResponse> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = new();

    public PipeServer(Func<IpcRequest, IpcResponse> handler) => _handler = handler;

    public void Start()
    {
        for (int i = 0; i < Listeners; i++)
        {
            _tasks.Add(Task.Run(ListenAsync));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            Task.WaitAll(_tasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }

    private async Task ListenAsync()
    {
        CancellationToken token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    CreateSecurity());
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(RequestTimeout);
                IpcRequest request = await PipeProtocol.ReadAsync(pipe, LedgerJson.Default.IpcRequest, timeout.Token).ConfigureAwait(false);
                IpcResponse response;
                try
                {
                    response = _handler(request);
                }
                catch (Exception ex)
                {
                    Log.Error($"Request '{request.Op}' failed", ex);
                    response = IpcResponse.Failure(ex.Message);
                }

                await PipeProtocol.WriteAsync(pipe, response, LedgerJson.Default.IpcResponse, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Pipe connection failed: {ex.Message}");
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // In --console mode the server runs as a normal user, which still needs to create further instances.
        if (WindowsIdentity.GetCurrent().User is SecurityIdentifier self)
        {
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }
}
