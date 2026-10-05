using SerialWorkbench.Domain;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.Cli;

public sealed record SessionDetails(SessionDescriptor Session, IReadOnlyList<SerialTrafficEvent> Events, IReadOnlyList<LoopbackHistoryEntry> Loopbacks);

public sealed record SessionExportReceipt(Guid SessionId, string Path, string Format, long Bytes);

public sealed record SessionDeleteReceipt(Guid SessionId, bool Success, string? Error);

public sealed record SendReceipt(bool Success, int Bytes, string Hex, string? Error);

public sealed record TransferReceipt(string Path, bool Success, long BytesTransferred, int? ReceivedBytes, int Blocks, int Retries, double DurationMilliseconds, string? Error, string? ErrorCode);

public sealed record ModbusReceipt(bool Success, string RequestFrame, string ResponseFrame, byte FunctionCode, ushort[] Registers,
    bool[]? Bits, ushort? Address, ushort? RegisterValue, byte? ExceptionCode, double DurationMilliseconds, string? Error, string? ErrorCode);

public sealed record ProtocolInspectionResult(bool Valid, string Hex, ModbusFrameInspection? Modbus, ProtocolTemplateInspection? Template);

public sealed record MonitorReceipt(Guid ConnectionId, long Events, long Bytes, long NextSequence, Guid? StreamId);

public sealed record MonitorGap(Guid StreamId, EventGap? Gap, bool ResetRequired);

public sealed record VersionInfo(string Version, int RpcMajorVersion, int RpcMinorVersion);
