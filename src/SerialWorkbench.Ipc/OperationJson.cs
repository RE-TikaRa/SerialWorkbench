using System.Text.Json;
using System.Text.Json.Serialization;
using SerialWorkbench.Domain;

namespace SerialWorkbench.Ipc;

public static class OperationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static OperationRequest Create<T>(string command, Guid connectionId, T parameters, string? requestId = null) =>
        new(command, connectionId, JsonSerializer.Serialize(parameters, Options), requestId);

    public static T Read<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Operation parameters are empty.");
}
