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
    SessionDescriptor? ActiveSession);

public sealed record OpenConnectionRequest(SerialConnectionOptions Options);

public sealed record SendRequest(Guid ConnectionId, byte[] Data, string Source = "manual");

public sealed record EventQuery(long AfterSequence = 0, int MaximumCount = 1000, Guid? ConnectionId = null);

public sealed record SessionEventQuery(Guid SessionId, int MaximumCount = 1000);

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

    Task<IReadOnlyList<SerialTrafficEvent>> ReadEventsAsync(EventQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionDescriptor>> ListSessionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SerialTrafficEvent>> ReadSessionEventsAsync(SessionEventQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<SerialTrafficEvent>> ReadAllSessionEventsAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<string> ExportSessionCsvAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<RpcResult> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<LoopbackResult> RunLoopbackAsync(LoopbackRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<LoopbackHistoryEntry>> ReadLoopbackResultsAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<SerialSequenceProgress> RunSequenceAsync(Guid connectionId, SerialSequenceDefinition sequence, CancellationToken cancellationToken);

    Task<ModbusTransactionResult> RunModbusAsync(ModbusTransactionRequest request, CancellationToken cancellationToken);

    Task<HostStatusDto> SetWorkspaceAsync(SetWorkspaceRequest request, CancellationToken cancellationToken);

    Task<RpcResult> StopHostAsync(CancellationToken cancellationToken);
}
