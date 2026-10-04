using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Serial.Windows;

namespace SerialWorkbench.Host;

public sealed class HostRpcService(HostRuntime runtime) : IHostRpc
{
    public Task<StartOperationResult> StartOperationAsync(OperationRequest request, CancellationToken cancellationToken) =>
        runtime.Operations.StartAsync(request, cancellationToken);

    public Task<IReadOnlyList<OperationSnapshot>> ListOperationsAsync(CancellationToken cancellationToken) =>
        runtime.Operations.ListAsync(cancellationToken);

    public Task<OperationSnapshot> ReadOperationAsync(OperationQuery query, CancellationToken cancellationToken) =>
        runtime.Operations.ReadAsync(query, cancellationToken);

    public Task<OperationProgressBatch> ReadOperationProgressAsync(OperationProgressQuery query, CancellationToken cancellationToken) =>
        runtime.Operations.ReadProgressAsync(query, cancellationToken);

    public Task<OperationSnapshot> CancelOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
        runtime.Operations.CancelAsync(operationId, cancellationToken);

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
            runtime.Sessions.ActiveSession,
            runtime.Journal.LatestSequence,
            runtime.PendingSessionEvents,
            runtime.SessionEventPersistenceEventsPerSecond,
            runtime.Operations.ActiveCount,
            runtime.Journal.EarliestSequence,
            runtime.Journal.StreamId);

    public Task<IReadOnlyList<SerialPortDescriptor>> ListPortsAsync(CancellationToken cancellationToken) =>
        SerialPortCatalog.GetPortsAsync(cancellationToken);

    public Task<ConnectionSnapshot> OpenConnectionAsync(OpenConnectionRequest request, CancellationToken cancellationToken) =>
        runtime.OpenConnectionAsync(request, cancellationToken);

    public async Task<RpcResult> CloseConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await runtime.CloseConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);

        return new RpcResult(true);
    }

    public async Task<RpcResult> SendAsync(SendRequest request, CancellationToken cancellationToken)
    {
        await runtime.Operations.RunAsync<RpcResult>(OperationJson.Create("send", request.ConnectionId, request), cancellationToken).ConfigureAwait(false);
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
        return Task.FromResult(runtime.Journal.ReadAfter(query.AfterSequence, query.MaximumCount, query.ConnectionId, query.Direction, query.SourceContains));
    }

    public Task<EventBatch> ReadEventBatchAsync(EventQuery query, CancellationToken cancellationToken) =>
        runtime.Journal.ReadBatchAsync(query.AfterSequence, query.MaximumCount, query.ConnectionId, query.Direction,
            query.SourceContains, query.StreamId, query.WaitMilliseconds, cancellationToken);

    public Task<IReadOnlyList<SessionDescriptor>> ListSessionsAsync(CancellationToken cancellationToken) =>
        runtime.Sessions.ListAsync(cancellationToken);

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadSessionEventsAsync(SessionEventQuery query, CancellationToken cancellationToken) =>
        runtime.Sessions.ReadEventsAsync(query.SessionId, query.MaximumCount, cancellationToken, query.AfterSequence, query.ConnectionId, query.Direction, query.SourceContains, query.DataContainsHex);

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadAllSessionEventsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        runtime.Sessions.ReadAllEventsAsync(sessionId, cancellationToken);

    public Task<string> ExportSessionCsvAsync(SessionEventQuery query, CancellationToken cancellationToken) =>
        runtime.Sessions.ExportCsvAsync(query.SessionId, query.ConnectionId, query.Direction, query.SourceContains, query.DataContainsHex, cancellationToken);

    public async Task<RpcResult> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await runtime.Sessions.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new RpcResult(true);
    }

    public Task<LoopbackResult> RunLoopbackAsync(LoopbackRequest request, CancellationToken cancellationToken) =>
        runtime.Operations.RunAsync<LoopbackResult>(OperationJson.Create("loopback.run", request.ConnectionId, request), cancellationToken);

    public Task<IReadOnlyList<LoopbackHistoryEntry>> ReadLoopbackResultsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        runtime.Sessions.ReadLoopbackResultsAsync(sessionId, cancellationToken);

    public Task<SerialSequenceProgress> RunSequenceAsync(Guid connectionId, SerialSequenceDefinition sequence, CancellationToken cancellationToken) =>
        runtime.Operations.RunAsync<SerialSequenceProgress>(OperationJson.Create("sequence.run", connectionId, sequence), cancellationToken);

    public Task<XmodemTransferResult> SendXmodemAsync(Guid connectionId, byte[] data, CancellationToken cancellationToken) =>
        runtime.Operations.RunAsync<XmodemTransferResult>(OperationJson.Create("xmodem.send", connectionId, data), cancellationToken);

    public Task<XmodemReceiveResult> ReceiveXmodemAsync(Guid connectionId, CancellationToken cancellationToken) =>
        runtime.Operations.RunAsync<XmodemReceiveResult>(OperationJson.Create("xmodem.receive", connectionId, new { }), cancellationToken);

    public Task<ModbusTransactionResult> RunModbusAsync(ModbusTransactionRequest request, CancellationToken cancellationToken) =>
        runtime.Operations.RunAsync<ModbusTransactionResult>(OperationJson.Create(request.FunctionCode is 5 or 6 or 15 or 16 ? "modbus.write" : "modbus.read", request.ConnectionId, request), cancellationToken);

    public Task<RpcResult> StopHostAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtime.RequestStop();
        return Task.FromResult(new RpcResult(true));
    }
}
