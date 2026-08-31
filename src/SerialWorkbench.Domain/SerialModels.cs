namespace SerialWorkbench.Domain;

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space,
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two,
}

public enum SerialHandshake
{
    None,
    XOnXOff,
    RequestToSend,
    RequestToSendXOnXOff,
}

public enum SerialConnectionRole
{
    Dut,
    Debug,
    Controller,
    Loopback,
}

public enum SerialDirection
{
    Receive,
    Transmit,
}

public enum ConnectionState
{
    Closed,
    Opening,
    Open,
    Reconnecting,
    Faulted,
}

public sealed record SerialConnectionOptions(
    string PortName,
    int BaudRate = 115200,
    int DataBits = 8,
    SerialParity Parity = SerialParity.None,
    SerialStopBits StopBits = SerialStopBits.One,
    SerialHandshake Handshake = SerialHandshake.None,
    bool DtrEnable = false,
    bool RtsEnable = false,
    string EncodingName = "utf-8",
    SerialConnectionRole Role = SerialConnectionRole.Dut,
    string? DeviceInstanceId = null,
    bool Rs485Mode = false,
    int RtsBeforeSendMilliseconds = 0,
    int RtsAfterSendMilliseconds = 0);

public sealed record SerialPortDescriptor(
    string PortName,
    string DisplayName,
    string? DeviceInstanceId = null,
    string? Vid = null,
    string? Pid = null,
    bool IsPresent = true);

public sealed record ConnectionSnapshot(
    Guid Id,
    SerialConnectionOptions Options,
    ConnectionState State,
    long ReceivedBytes,
    long TransmittedBytes,
    long ReceiveBlocks,
    long TransmitOperations,
    long ErrorCount,
    DateTimeOffset? LastActivityUtc,
    string? Error,
    SerialControlLineStatus? ControlLines = null);

public sealed record SerialControlLines(bool DtrEnable, bool RtsEnable);

public sealed record SerialControlLineStatus(
    bool DtrEnable,
    bool RtsEnable,
    bool CtsHolding,
    bool DsrHolding,
    bool CarrierDetect,
    bool? RingIndicator);

public sealed record SerialTrafficEvent(
    long Sequence,
    DateTimeOffset Utc,
    long MonotonicTicks,
    Guid ConnectionId,
    SerialDirection Direction,
    byte[] Data,
    string Source,
    string? Message = null);

public sealed record LoopbackRequest(
    Guid ConnectionId,
    int PayloadLength = 4096,
    int Iterations = 1,
    int TimeoutMilliseconds = 5000,
    LoopbackPattern Pattern = LoopbackPattern.Incrementing,
    int Seed = 0x534257);

public enum LoopbackPattern
{
    Fixed,
    Incrementing,
    Random,
}

public sealed record LoopbackResult(
    bool Passed,
    int Iterations,
    long SentBytes,
    long ReceivedBytes,
    TimeSpan Duration,
    double BytesPerSecond,
    int? FirstDifferenceIndex,
    byte? ExpectedByte,
    byte? ActualByte,
    string? Error);

public sealed record LoopbackHistoryEntry(
    DateTimeOffset Utc,
    Guid ConnectionId,
    LoopbackRequest Request,
    LoopbackResult Result);

public sealed record SerialSequenceStep(
    byte[] Data,
    string Format,
    int DelayMilliseconds,
    int RepeatCount,
    int WaitMilliseconds,
    string? ResponseHex = null,
    int ResponseTimeoutMilliseconds = 0,
    int RetryCount = 0);

public sealed record SerialSequenceDefinition(
    string Name,
    IReadOnlyList<SerialSequenceStep> Steps);

public sealed record SerialSequenceProgress(
    string Name,
    int StepIndex,
    int StepCount,
    int RepeatIndex,
    int RepeatCount,
    bool Completed,
    bool Cancelled,
    string? Error);

public sealed record XmodemTransferResult(
    bool Success,
    long BytesTransferred,
    int Blocks,
    int Retries,
    TimeSpan Duration,
    string? Error);

public sealed record XmodemReceiveResult(XmodemTransferResult Result, byte[] Data);

public sealed record ModbusTransactionRequest(
    Guid ConnectionId,
    byte[] Frame,
    byte SlaveAddress,
    byte FunctionCode,
    int TimeoutMilliseconds = 2000);

public sealed record ModbusTransactionResult(
    bool Success,
    byte[] ResponseFrame,
    byte FunctionCode,
    ushort[] Registers,
    ushort? Address,
    ushort? Value,
    byte? ExceptionCode,
    TimeSpan Duration,
    string? Error);

public sealed record SessionDescriptor(
    Guid Id,
    string Path,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    long EventCount,
    long RawByteCount);
