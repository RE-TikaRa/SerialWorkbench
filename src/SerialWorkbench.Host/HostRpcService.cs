using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Serial.Windows;

namespace SerialWorkbench.Host;

public sealed class HostRpcService(HostRuntime runtime) : IHostRpc
{
    public Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = request.MajorVersion == RpcProtocol.MajorVersion;
        return Task.FromResult(new HandshakeResponse(
            accepted,
            RpcProtocol.MajorVersion,
            RpcProtocol.MinorVersion,
            typeof(HostRpcService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            accepted ? null : $"IPC protocol {request.MajorVersion}.{request.MinorVersion} is incompatible with host {RpcProtocol.MajorVersion}.{RpcProtocol.MinorVersion}."));
    }

    public Task<HostStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateStatus());
    }

    public async Task<HostStatusDto> SetWorkspaceAsync(SetWorkspaceRequest request, CancellationToken cancellationToken)
    {
        await runtime.SetWorkspaceAsync(request.Path, cancellationToken).ConfigureAwait(false);
        return CreateStatus();
    }

    private HostStatusDto CreateStatus() =>
        new(
            typeof(HostRpcService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            runtime.Paths.ApplicationRoot,
            runtime.Paths.DataRoot,
            runtime.Paths.WorkspaceRoot,
            runtime.ClientCount,
            runtime.Connections.GetSnapshots(),
            runtime.Sessions.ActiveSession);

    public Task<IReadOnlyList<SerialPortDescriptor>> ListPortsAsync(CancellationToken cancellationToken) =>
        SerialPortCatalog.GetPortsAsync(cancellationToken);

    public Task<ConnectionSnapshot> OpenConnectionAsync(OpenConnectionRequest request, CancellationToken cancellationToken) =>
        runtime.Connections.OpenAsync(request.Options, cancellationToken);

    public async Task<RpcResult> CloseConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await runtime.Connections.CloseAsync(connectionId).ConfigureAwait(false);
        return new RpcResult(true);
    }

    public async Task<RpcResult> SendAsync(SendRequest request, CancellationToken cancellationToken)
    {
        await runtime.Connections.SendAsync(request.ConnectionId, request.Data, request.Source, cancellationToken).ConfigureAwait(false);
        return new RpcResult(true);
    }

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadEventsAsync(EventQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SerialTrafficEvent> events = runtime.Journal.ReadAfter(query.AfterSequence, query.MaximumCount);
        if (query.ConnectionId is not null)
        {
            events = events.Where(item => item.ConnectionId == query.ConnectionId).ToArray();
        }

        return Task.FromResult(events);
    }

    public Task<IReadOnlyList<SessionDescriptor>> ListSessionsAsync(CancellationToken cancellationToken) =>
        runtime.Sessions.ListAsync(cancellationToken);

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadSessionEventsAsync(SessionEventQuery query, CancellationToken cancellationToken) =>
        runtime.Sessions.ReadEventsAsync(query.SessionId, query.MaximumCount, cancellationToken);

    public async Task<RpcResult> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await runtime.Sessions.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new RpcResult(true);
    }

    public Task<LoopbackResult> RunLoopbackAsync(LoopbackRequest request, CancellationToken cancellationToken) =>
        runtime.Connections.RunLoopbackAsync(request, cancellationToken);

    public Task<ModbusTransactionResult> RunModbusAsync(ModbusTransactionRequest request, CancellationToken cancellationToken) =>
        runtime.Connections.RunModbusAsync(request, cancellationToken);

    public Task<RpcResult> StopHostAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtime.RequestStop();
        return Task.FromResult(new RpcResult(true));
    }
}
