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

    public async Task<ConnectionSnapshot> OpenConnectionAsync(OpenConnectionRequest request, CancellationToken cancellationToken)
    {
        var connection = await runtime.Connections.OpenAsync(request.Options, cancellationToken).ConfigureAwait(false);
        try
        {
            await runtime.Sessions.EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await runtime.Connections.CloseAsync(connection.Id).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RpcResult> CloseConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await runtime.Connections.CloseAsync(connectionId).ConfigureAwait(false);
        if (runtime.Connections.GetSnapshots().Count == 0)
        {
            await runtime.Sessions.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RpcResult(true);
    }

    public async Task<RpcResult> SendAsync(SendRequest request, CancellationToken cancellationToken)
    {
        await runtime.Connections.SendAsync(request.ConnectionId, request.Data, request.Source, cancellationToken).ConfigureAwait(false);
        return new RpcResult(true);
    }

    public async Task<RpcResult> SetControlLinesAsync(Guid connectionId, SerialControlLines lines, CancellationToken cancellationToken)
    {
        await runtime.Connections.SetControlLinesAsync(connectionId, lines, cancellationToken).ConfigureAwait(false);
        return new RpcResult(true);
    }

    public async Task<RpcResult> ClearBuffersAsync(Guid connectionId, bool receive, bool transmit, CancellationToken cancellationToken)
    {
        await runtime.Connections.ClearBuffersAsync(connectionId, receive, transmit, cancellationToken).ConfigureAwait(false);
        return new RpcResult(true);
    }

    public async Task<RpcResult> SendBreakAsync(Guid connectionId, int durationMilliseconds, CancellationToken cancellationToken)
    {
        await runtime.Connections.SendBreakAsync(connectionId, durationMilliseconds, cancellationToken).ConfigureAwait(false);
        return new RpcResult(true);
    }

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadEventsAsync(EventQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(runtime.Journal.ReadAfter(query.AfterSequence, query.MaximumCount, query.ConnectionId));
    }

    public Task<IReadOnlyList<SessionDescriptor>> ListSessionsAsync(CancellationToken cancellationToken) =>
        runtime.Sessions.ListAsync(cancellationToken);

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadSessionEventsAsync(SessionEventQuery query, CancellationToken cancellationToken) =>
        runtime.Sessions.ReadEventsAsync(query.SessionId, query.MaximumCount, cancellationToken);

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadAllSessionEventsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        runtime.Sessions.ReadAllEventsAsync(sessionId, cancellationToken);

    public Task<string> ExportSessionCsvAsync(Guid sessionId, CancellationToken cancellationToken) =>
        runtime.Sessions.ExportCsvAsync(sessionId, cancellationToken);

    public async Task<RpcResult> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await runtime.Sessions.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new RpcResult(true);
    }

    public async Task<LoopbackResult> RunLoopbackAsync(LoopbackRequest request, CancellationToken cancellationToken)
    {
        var result = await runtime.Connections.RunLoopbackAsync(request, cancellationToken).ConfigureAwait(false);
        await runtime.Sessions.AppendLoopbackResultAsync(request, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<IReadOnlyList<LoopbackHistoryEntry>> ReadLoopbackResultsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        runtime.Sessions.ReadLoopbackResultsAsync(sessionId, cancellationToken);

    public Task<SerialSequenceProgress> RunSequenceAsync(Guid connectionId, SerialSequenceDefinition sequence, CancellationToken cancellationToken) =>
        runtime.Connections.RunSequenceAsync(connectionId, sequence, cancellationToken);

    public Task<XmodemTransferResult> SendXmodemAsync(Guid connectionId, byte[] data, CancellationToken cancellationToken) =>
        runtime.Connections.SendXmodemAsync(connectionId, data, cancellationToken);

    public Task<XmodemReceiveResult> ReceiveXmodemAsync(Guid connectionId, CancellationToken cancellationToken) =>
        runtime.Connections.ReceiveXmodemAsync(connectionId, cancellationToken);

    public Task<ModbusTransactionResult> RunModbusAsync(ModbusTransactionRequest request, CancellationToken cancellationToken) =>
        runtime.Connections.RunModbusAsync(request, cancellationToken);

    public Task<RpcResult> StopHostAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtime.RequestStop();
        return Task.FromResult(new RpcResult(true));
    }
}
