using System.Text.Json.Serialization;

namespace NetLedger.Core.Ipc;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(BackupData))]
[JsonSerializable(typeof(ServiceSettings))]
public sealed partial class LedgerJson : JsonSerializerContext
{
}
