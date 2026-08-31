using SerialWorkbench.Domain;

namespace SerialWorkbench.Ipc;

public static class RpcProtocol
{
    public const int MajorVersion = 1;
    public const int MinorVersion = 1;
}

public sealed record HandshakeRequest(int MajorVersion, int MinorVersion, string ClientName, string Culture);

public sealed record HandshakeResponse(bool Accepted, int MajorVersion, int MinorVersion, string HostVersion, string? Error);

public sealed record HostStatusDto(
    string Version,
    string ApplicationRoot,
    string DataRoot,
    string? WorkspaceRoot,
    int ClientCount,
    IReadOnlyList<ConnectionSnapshot> Connections,
    SessionDescriptor? ActiveSession,
    long LatestEventSequence = 0,
    long PendingSessionEvents = 0);

public sealed record OpenConnectionRequest(SerialConnectionOptions Options);

public sealed record SendRequest(Guid ConnectionId, byte[] Data, string Source = "manual");

public sealed record EventQuery(long AfterSequence = 0, int MaximumCount = 1000, Guid? ConnectionId = null);

public sealed record SessionEventQuery(Guid SessionId, int MaximumCount = 1000, long AfterSequence = 0, Guid? ConnectionId = null);

public sealed record SetWorkspaceRequest(string? Path);

public sealed record RpcResult(bool Success, string? Error = null);

public interface IHostRpc
{
    Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken cancellationToken);

    Task<HostStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SerialPortDescriptor>> ListPortsAsync(CancellationToken cancellationToken);

    Task<ConnectionSnapshot> OpenConnectionAsync(OpenConnectionRequest request, CancellationToken cancellationToken);

    Task<RpcResult> CloseConnectionAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<RpcResult> SendAsync(SendRequest request, CancellationToken cancellationToken);

    Task<RpcResult> SetControlLinesAsync(Guid connectionId, SerialControlLines lines, CancellationToken cancellationToken);

    Task<RpcResult> ClearBuffersAsync(Guid connectionId, bool receive, bool transmit, CancellationToken cancellationToken);

    Task<RpcResult> SendBreakAsync(Guid connectionId, int durationMilliseconds, CancellationToken cancellationToken);

    Task<IReadOnlyList<SerialTrafficEvent>> ReadEventsAsync(EventQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionDescriptor>> ListSessionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SerialTrafficEvent>> ReadSessionEventsAsync(SessionEventQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<SerialTrafficEvent>> ReadAllSessionEventsAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<string> ExportSessionCsvAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<RpcResult> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<LoopbackResult> RunLoopbackAsync(LoopbackRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<LoopbackHistoryEntry>> ReadLoopbackResultsAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<SerialSequenceProgress> RunSequenceAsync(Guid connectionId, SerialSequenceDefinition sequence, CancellationToken cancellationToken);

    Task<XmodemTransferResult> SendXmodemAsync(Guid connectionId, byte[] data, CancellationToken cancellationToken);

    Task<XmodemReceiveResult> ReceiveXmodemAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<ModbusTransactionResult> RunModbusAsync(ModbusTransactionRequest request, CancellationToken cancellationToken);

    Task<HostStatusDto> SetWorkspaceAsync(SetWorkspaceRequest request, CancellationToken cancellationToken);

    Task<RpcResult> StopHostAsync(CancellationToken cancellationToken);
}
