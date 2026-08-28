using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using SerialWorkbench.Domain;
using StreamJsonRpc;

namespace SerialWorkbench.Ipc;

public static class HostEndpoint
{
    public static string GetPipeName(string applicationRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRoot)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..20];
        return $"SerialWorkbench-{hash}";
    }

    public static async Task<HostRpcClient> ConnectAsync(string applicationRoot, bool startHost, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRoot));
        var pipeName = GetPipeName(root);

        if (startHost)
        {
            await EnsureHostStartedAsync(root, pipeName, cancellationToken).ConfigureAwait(false);
        }

        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await stream.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
        return new HostRpcClient(stream);
    }

    private static async Task EnsureHostStartedAsync(string applicationRoot, string pipeName, CancellationToken cancellationToken)
    {
        var hostPath = Path.Combine(applicationRoot, "SerialWorkbench.Host.exe");
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException("SerialWorkbench.Host.exe was not found beside the client executable.", hostPath);
        }

        using var startupSemaphore = new Semaphore(1, 1, $"Local\\{pipeName}-startup");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = false;
            try
            {
                acquired = startupSemaphore.WaitOne(0);

                if (!acquired)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await probe.ConnectAsync(100, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException)
                {
                }

                using (HostProcessLauncher.Start(hostPath, applicationRoot))
                {
                    while (DateTime.UtcNow < deadline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await using var retry = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        try
                        {
                            await retry.ConnectAsync(200, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }
                    }
                }
            }
            finally
            {
                if (acquired)
                {
                    startupSemaphore.Release();
                }
            }
        }

        throw new TimeoutException("SerialWorkbench.Host did not create its IPC endpoint within 10 seconds.");
    }
}

public sealed class HostRpcClient : IHostRpc, IAsyncDisposable
{
    private readonly NamedPipeClientStream stream;
    private readonly JsonRpc rpc;

    internal HostRpcClient(NamedPipeClientStream stream)
    {
        this.stream = stream;
        rpc = new JsonRpc(stream);
        rpc.StartListening();
    }

    public Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<HandshakeResponse>(nameof(HandshakeAsync), [request], cancellationToken);

    public Task<HostStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<HostStatusDto>(nameof(GetStatusAsync), [], cancellationToken);

    public Task<IReadOnlyList<SerialPortDescriptor>> ListPortsAsync(CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<IReadOnlyList<SerialPortDescriptor>>(nameof(ListPortsAsync), [], cancellationToken);

    public Task<ConnectionSnapshot> OpenConnectionAsync(OpenConnectionRequest request, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<ConnectionSnapshot>(nameof(OpenConnectionAsync), [request], cancellationToken);

    public Task<RpcResult> CloseConnectionAsync(Guid connectionId, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<RpcResult>(nameof(CloseConnectionAsync), [connectionId], cancellationToken);

    public Task<RpcResult> SendAsync(SendRequest request, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<RpcResult>(nameof(SendAsync), [request], cancellationToken);

    public Task<RpcResult> SetControlLinesAsync(Guid connectionId, SerialControlLines lines, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<RpcResult>(nameof(SetControlLinesAsync), [connectionId, lines], cancellationToken);

    public Task<RpcResult> ClearBuffersAsync(Guid connectionId, bool receive, bool transmit, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<RpcResult>(nameof(ClearBuffersAsync), [connectionId, receive, transmit], cancellationToken);

    public Task<RpcResult> SendBreakAsync(Guid connectionId, int durationMilliseconds, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<RpcResult>(nameof(SendBreakAsync), [connectionId, durationMilliseconds], cancellationToken);

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadEventsAsync(EventQuery query, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<IReadOnlyList<SerialTrafficEvent>>(nameof(ReadEventsAsync), [query], cancellationToken);

    public Task<IReadOnlyList<SessionDescriptor>> ListSessionsAsync(CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<IReadOnlyList<SessionDescriptor>>(nameof(ListSessionsAsync), [], cancellationToken);

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadSessionEventsAsync(SessionEventQuery query, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<IReadOnlyList<SerialTrafficEvent>>(nameof(ReadSessionEventsAsync), [query], cancellationToken);

    public Task<IReadOnlyList<SerialTrafficEvent>> ReadAllSessionEventsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<IReadOnlyList<SerialTrafficEvent>>(nameof(ReadAllSessionEventsAsync), [sessionId], cancellationToken);

    public Task<string> ExportSessionCsvAsync(Guid sessionId, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<string>(nameof(ExportSessionCsvAsync), [sessionId], cancellationToken);

    public Task<RpcResult> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<RpcResult>(nameof(DeleteSessionAsync), [sessionId], cancellationToken);

    public Task<LoopbackResult> RunLoopbackAsync(LoopbackRequest request, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<LoopbackResult>(nameof(RunLoopbackAsync), [request], cancellationToken);

    public Task<IReadOnlyList<LoopbackHistoryEntry>> ReadLoopbackResultsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<IReadOnlyList<LoopbackHistoryEntry>>(nameof(ReadLoopbackResultsAsync), [sessionId], cancellationToken);

    public Task<SerialSequenceProgress> RunSequenceAsync(Guid connectionId, SerialSequenceDefinition sequence, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<SerialSequenceProgress>(nameof(RunSequenceAsync), [connectionId, sequence], cancellationToken);

    public Task<XmodemTransferResult> SendXmodemAsync(Guid connectionId, byte[] data, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<XmodemTransferResult>(nameof(SendXmodemAsync), [connectionId, data], cancellationToken);

    public Task<XmodemReceiveResult> ReceiveXmodemAsync(Guid connectionId, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<XmodemReceiveResult>(nameof(ReceiveXmodemAsync), [connectionId], cancellationToken);

    public Task<ModbusTransactionResult> RunModbusAsync(ModbusTransactionRequest request, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<ModbusTransactionResult>(nameof(RunModbusAsync), [request], cancellationToken);

    public Task<HostStatusDto> SetWorkspaceAsync(SetWorkspaceRequest request, CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<HostStatusDto>(nameof(SetWorkspaceAsync), [request], cancellationToken);

    public Task<RpcResult> StopHostAsync(CancellationToken cancellationToken) =>
        rpc.InvokeWithCancellationAsync<RpcResult>(nameof(StopHostAsync), [], cancellationToken);

    public async ValueTask DisposeAsync()
    {
        rpc.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}
