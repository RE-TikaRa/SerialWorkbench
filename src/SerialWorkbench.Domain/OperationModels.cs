namespace SerialWorkbench.Domain;

public enum OperationState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted,
}

public enum ExecutionOutcome
{
    Pending,
    Confirmed,
    Unknown,
    NotExecuted,
}

public sealed record WorkbenchError(
    string Code,
    string Message,
    Guid? ConnectionId = null,
    Guid? OperationId = null,
    string? Owner = null);

public sealed record OperationRequest(
    string Command,
    Guid ConnectionId,
    string ParametersJson,
    string? RequestId = null,
    SerialConnectionOptions? ConnectionOptions = null);

public sealed record XmodemReceiveRequest(string? DestinationPath = null);

public sealed record RepeatSendRequest(byte[] Data, int IntervalMilliseconds = 1000, int Count = 0, string Source = "repeat.send");

public sealed record OperationProgress(
    long Completed,
    long? Total = null,
    string Unit = "steps",
    string? Message = null,
    string? ItemJson = null);

public sealed record OperationSnapshot(
    Guid Id,
    OperationRequest Request,
    OperationState State,
    ExecutionOutcome Outcome,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    long Revision = 1,
    OperationProgress? Progress = null,
    string? ResultJson = null,
    WorkbenchError? Error = null);

public sealed record StartOperationResult(OperationSnapshot? Operation, WorkbenchError? Error = null);

public sealed record OperationQuery(Guid Id, long AfterRevision = 0, int WaitMilliseconds = 0);

public sealed record OperationProgressEvent(long Revision, DateTimeOffset Utc, OperationProgress Progress);

public sealed record OperationProgressQuery(Guid Id, long AfterRevision = 0, int MaximumCount = 1000, int WaitMilliseconds = 0);

public sealed record OperationProgressBatch(OperationSnapshot Operation, IReadOnlyList<OperationProgressEvent> Updates, long NextRevision);

public sealed record ModbusScanRequest(
    Guid ConnectionId,
    byte FirstAddress = 1,
    byte LastAddress = 247,
    ushort RegisterAddress = 0,
    int TimeoutMilliseconds = 200,
    int IntervalMilliseconds = 0);

public sealed record ModbusPollRequest(
    Guid ConnectionId,
    byte SlaveAddress = 1,
    byte FunctionCode = 3,
    ushort Address = 0,
    ushort Quantity = 1,
    int Count = 10,
    int IntervalMilliseconds = 1000,
    int TimeoutMilliseconds = 2000);

public sealed record ModbusSample(int Index, byte SlaveAddress, DateTimeOffset Utc, ModbusTransactionResult Result);

public sealed record ModbusBatchResult(IReadOnlyList<ModbusSample> Samples, int Successful, int Failed);
