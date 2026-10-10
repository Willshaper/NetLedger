using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NetLedger.Core.Ipc;

/// <summary>Length-prefixed JSON messages over the service's named pipe.</summary>
public static class PipeProtocol
{
    public const string PipeName = "NetLedger";
    public const string ServiceName = "NetLedger";

    // Backups of many years with per-app history can reach tens of MB; anything far beyond that is bogus.
    public const int MaxMessageBytes = 512 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> typeInfo, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> typeInfo, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Invalid message length {length}.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize(payload, typeInfo)
            ?? throw new InvalidDataException("Empty message.");
    }
}
