using System.IO.Pipes;

namespace NetLedger.Core.Ipc;

/// <summary>The service isn't installed, isn't running, or didn't answer in time.</summary>
public sealed class ServiceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The service answered, but refused or failed the request.</summary>
public sealed class ServiceRequestException(string message) : Exception(message);

/// <summary>Talks to the NetLedger service. One short-lived connection per request.</summary>
public sealed class LedgerClient
{
    private const int ConnectTimeoutMs = 1500;

    public async Task<IpcResponse> CallAsync(IpcRequest request, TimeSpan? timeout = null, CancellationToken token = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        IpcResponse response;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(ConnectTimeoutMs, cts.Token).ConfigureAwait(false);
            await PipeProtocol.WriteAsync(pipe, request, LedgerJson.Default.IpcRequest, cts.Token).ConfigureAwait(false);
            response = await PipeProtocol.ReadAsync(pipe, LedgerJson.Default.IpcResponse, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException
                                       or InvalidDataException or System.Text.Json.JsonException
                                   || (ex is OperationCanceledException && !token.IsCancellationRequested))
        {
            throw new ServiceUnavailableException("The NetLedger service isn't responding.", ex);
        }

        if (!response.Ok)
        {
            throw new ServiceRequestException(response.Error ?? "The service reported an unknown error.");
        }

        return response;
    }

    public Task<IpcResponse> CallAsync(string op, CancellationToken token = default) =>
        CallAsync(new IpcRequest { Op = op }, null, token);
}
