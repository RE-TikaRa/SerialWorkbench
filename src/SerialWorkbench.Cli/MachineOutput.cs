using System.Text.Json;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;

namespace SerialWorkbench.Cli;

public sealed record MachineRecord<T>(
    int SchemaVersion,
    string Command,
    string Type,
    string Status,
    DateTimeOffset Time,
    T? Result,
    WorkbenchError? Error,
    Guid? OperationId = null);

public static class MachineOutput
{
    private static readonly string[] successProperties = ["success", "passed", "valid"];
    public static JsonSerializerOptions CompactOptions { get; } = new(OperationJson.Options);

    public static JsonSerializerOptions DocumentOptions { get; } = new(CompactOptions) { WriteIndented = true };

    public static void Write(string output, string command, object value, string type = "result", TextWriter? writer = null)
    {
        var payload = JsonSerializer.SerializeToElement(value, CompactOptions);
        WorkbenchError? error = null;
        Guid? operationId = null;
        var status = "success";
        if (value is StartOperationResult start)
        {
            error = start.Error ?? start.Operation?.Error;
            operationId = start.Operation?.Id;
            status = start.Operation?.State == OperationState.Running ? "running" : start.Operation?.State == OperationState.Cancelled ? "cancelled" : "success";
        }
        else if (value is OperationSnapshot operation)
        {
            error = operation.Error;
            operationId = operation.Id;
            status = operation.State == OperationState.Running ? "running" : operation.State == OperationState.Cancelled ? "cancelled" : "success";
        }
        else if (payload.ValueKind == JsonValueKind.Object
            && successProperties.Any(name => payload.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.False))
        {
            var message = payload.TryGetProperty("error", out var messageField) && messageField.ValueKind == JsonValueKind.String
                ? messageField.GetString() : "The operation did not pass.";
            var code = payload.TryGetProperty("errorCode", out var codeField) && codeField.ValueKind == JsonValueKind.String
                ? codeField.GetString() : "PROTOCOL_ERROR";
            error = new WorkbenchError(code ?? "PROTOCOL_ERROR", message ?? "The operation did not pass.");
        }

        if (error is not null && status != "cancelled")
        {
            status = "error";
        }

        var record = new MachineRecord<JsonElement>(2, command, type, status, DateTimeOffset.UtcNow, payload, error, operationId);
        (writer ?? Console.Out).WriteLine(JsonSerializer.Serialize(record, output == "jsonl" ? CompactOptions : DocumentOptions));
    }

    public static void WriteError(string output, string command, WorkbenchError error, TextWriter? writer = null)
    {
        var record = new MachineRecord<object>(2, command, "result", error.Code == "CANCELLED" ? "cancelled" : "error", DateTimeOffset.UtcNow, null, error, error.OperationId);
        (writer ?? Console.Out).WriteLine(JsonSerializer.Serialize(record, output == "jsonl" ? CompactOptions : DocumentOptions));
    }
}
