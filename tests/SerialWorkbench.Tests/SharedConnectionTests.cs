using SerialWorkbench.Domain;
using SerialWorkbench.Host;
using SerialWorkbench.Ipc;
using SerialWorkbench.Storage;

namespace SerialWorkbench.Tests;

public sealed class SharedConnectionTests
{
    [Fact]
    public async Task RepeatedSendingKeepsTheLeaseUntilItsConfiguredCountCompletes()
    {
        var port = Environment.GetEnvironmentVariable("SERIALWORKBENCH_TEST_PORT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(port), "Set SERIALWORKBENCH_TEST_PORT to run serial hardware tests.");
        await using var runtime = CreateRuntime();
        var client = new HostRpcService(runtime);
        var token = TestContext.Current.CancellationToken;
        var connection = await client.OpenConnectionAsync(new OpenConnectionRequest(new SerialConnectionOptions(port)), token);
        var request = OperationJson.Create("send.repeat", connection.Id, new RepeatSendRequest([0x53, 0x57, 0x42], 10, 3));
        var result = await client.RunOperationAsync<System.Text.Json.JsonElement>(request, token);
        Assert.Equal(3, result.GetProperty("sends").GetInt64());
        Assert.Equal(9, runtime.Connections.GetSnapshots().Single().TransmittedBytes);
        var events = runtime.Journal.ReadAfter(0, 100, connection.Id, SerialDirection.Transmit);
        Assert.Equal(3, events.Count);
        Assert.All(events, item => Assert.Equal([0x53, 0x57, 0x42], item.Data));
        Assert.Equal(0, runtime.Leases.ActiveCount);
        await client.CloseConnectionAsync(connection.Id, token);
    }

    [Fact]
    public async Task HostRemainsRunningWhileAWritingTaskExists()
    {
        await using var runtime = CreateRuntime();
        runtime.ClientConnected();
        using (runtime.Leases.Acquire(Guid.NewGuid(), "test.operation"))
        {
            runtime.ClientDisconnected();
            Assert.False(runtime.ShouldStopAfterIdle(TimeSpan.Zero));
        }

        Assert.True(runtime.ShouldStopAfterIdle(TimeSpan.Zero));
    }

    [Fact]
    public async Task ClientsShareTheSameSerialConnectionAndSession()
    {
        var port = Environment.GetEnvironmentVariable("SERIALWORKBENCH_TEST_PORT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(port), "Set SERIALWORKBENCH_TEST_PORT to run serial hardware tests.");
        await using var runtime = CreateRuntime();
        var firstClient = new HostRpcService(runtime);
        var secondClient = new HostRpcService(runtime);
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new OpenConnectionRequest(new SerialConnectionOptions(port));
        runtime.ClientConnected();
        runtime.ClientConnected();

        var connections = await Task.WhenAll(
            firstClient.OpenConnectionAsync(request, cancellationToken),
            secondClient.OpenConnectionAsync(request, cancellationToken));
        Assert.Equal(connections[0].Id, connections[1].Id);
        var status = await secondClient.GetStatusAsync(cancellationToken);
        Assert.Single(status.Connections);
        Assert.NotNull(status.ActiveSession);
        var sessionId = status.ActiveSession.Id;

        runtime.ClientDisconnected();
        runtime.ClientDisconnected();
        Assert.False(runtime.ShouldStopAfterIdle(TimeSpan.Zero));
        Assert.Equal(ConnectionState.Open, Assert.Single((await secondClient.GetStatusAsync(cancellationToken)).Connections).State);

        await secondClient.CloseConnectionAsync(connections[0].Id, cancellationToken);
        Assert.Empty((await firstClient.GetStatusAsync(cancellationToken)).Connections);
        Assert.Null(runtime.Sessions.ActiveSession);
        Assert.Equal(sessionId, Assert.Single(await secondClient.ListSessionsAsync(cancellationToken)).Id);
        Assert.True(runtime.ShouldStopAfterIdle(TimeSpan.Zero));
    }

    private static HostRuntime CreateRuntime()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "artifacts", $"shared-host-{Guid.NewGuid():N}");
        var paths = new ApplicationPaths(root);
        paths.EnsureWritable();
        return new HostRuntime(paths);
    }

    [Fact]
    public async Task ReconnectKeepsTheSharedIdentityAndRecordsANewSegment()
    {
        var port = Environment.GetEnvironmentVariable("SERIALWORKBENCH_TEST_PORT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(port), "Set SERIALWORKBENCH_TEST_PORT to run serial hardware tests.");
        await using var runtime = CreateRuntime();
        var service = new HostRpcService(runtime);
        var token = TestContext.Current.CancellationToken;
        var original = await service.OpenConnectionAsync(new OpenConnectionRequest(new SerialConnectionOptions(port)), token);
        Assert.NotNull(original.Options.DeviceInstanceId);
        var sessionId = runtime.Sessions.ActiveSession!.Id;

        var restored = await service.ReconnectConnectionAsync(original.Id, token);

        Assert.Equal(original.Id, restored.Id);
        Assert.NotEqual(original.SegmentId, restored.SegmentId);
        Assert.Equal(original.SegmentNumber + 1, restored.SegmentNumber);
        Assert.Equal(ConnectionState.Open, restored.State);
        Assert.Equal(original.Options.DeviceInstanceId, restored.Options.DeviceInstanceId);
        Assert.Single((await service.GetStatusAsync(token)).Connections);
        await service.CloseConnectionAsync(restored.Id, token);
        var recorded = await runtime.Sessions.ReadAllEventsAsync(sessionId, token);
        Assert.Contains(recorded, item => item.SegmentId == original.SegmentId && item.Message == "opened");
        Assert.Contains(recorded, item => item.SegmentId == restored.SegmentId && item.Message == "opened");
    }

    [Fact]
    public async Task BusyOperationsIdentifyTheirOwnerAndSurviveClientDisconnect()
    {
        var port = Environment.GetEnvironmentVariable("SERIALWORKBENCH_TEST_PORT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(port), "Set SERIALWORKBENCH_TEST_PORT to run serial hardware tests.");
        await using var runtime = CreateRuntime();
        var service = new HostRpcService(runtime);
        var token = TestContext.Current.CancellationToken;
        runtime.ClientConnected();
        var connection = await service.OpenConnectionAsync(new OpenConnectionRequest(new SerialConnectionOptions(port)), token);
        var sequence = new SerialSequenceDefinition("hardware-task", [new SerialSequenceStep([0x53, 0x57, 0x42], "hex", 5000, 1, 0)]);
        var request = OperationJson.Create("sequence.run", connection.Id, sequence, "sequence-request");
        var started = await service.StartOperationAsync(request, token);
        Assert.NotNull(started.Operation);
        var id = started.Operation.Id;

        var busy = await service.StartOperationAsync(OperationJson.Create("send", connection.Id, new SendRequest(connection.Id, [0x41])), token);
        Assert.Equal("CONNECTION_BUSY", busy.Error?.Code);
        Assert.Equal(id, busy.Error?.OperationId);

        runtime.ClientDisconnected();
        Assert.Equal(1, runtime.Operations.ActiveCount);
        Assert.False(runtime.ShouldStopAfterIdle(TimeSpan.Zero));

        var cancelled = await service.CancelOperationAsync(id, token);
        Assert.Equal(OperationState.Cancelled, cancelled.State);
        Assert.Equal(ExecutionOutcome.Unknown, cancelled.Outcome);
        Assert.Equal(0, runtime.Leases.ActiveCount);
        Assert.Equal(0, runtime.Operations.ActiveCount);

        var repeated = await service.StartOperationAsync(request, token);
        Assert.Equal(id, repeated.Operation?.Id);
        Assert.Equal(OperationState.Cancelled, repeated.Operation?.State);
        Assert.Equal(0, runtime.Operations.ActiveCount);
        await service.CloseConnectionAsync(connection.Id, token);
    }

    [Fact]
    public async Task RepeatingASendRequestReturnsItsSavedResultWithoutAnotherTransmission()
    {
        var port = Environment.GetEnvironmentVariable("SERIALWORKBENCH_TEST_PORT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(port), "Set SERIALWORKBENCH_TEST_PORT to run serial hardware tests.");
        await using var runtime = CreateRuntime();
        var service = new HostRpcService(runtime);
        var token = TestContext.Current.CancellationToken;
        var connection = await service.OpenConnectionAsync(new OpenConnectionRequest(new SerialConnectionOptions(port)), token);
        var request = OperationJson.Create("send", connection.Id, new SendRequest(connection.Id, "SWB\r\n"u8.ToArray()), "send-request");

        var result = await service.RunOperationAsync<RpcResult>(request, token);
        Assert.True(result.Success);
        var saved = Assert.Single(await service.ListOperationsAsync(token));
        var repeated = await service.StartOperationAsync(request, token);

        Assert.Equal(saved.Id, repeated.Operation?.Id);
        Assert.Equal(OperationState.Succeeded, repeated.Operation?.State);
        Assert.Equal(5, Assert.Single((await service.GetStatusAsync(token)).Connections).TransmittedBytes);
        Assert.Single(runtime.Journal.ReadAfter(0, 100, connection.Id, SerialDirection.Transmit));
        var progress = await service.ReadOperationProgressAsync(new OperationProgressQuery(saved.Id), token);
        Assert.Equal(5, Assert.Single(progress.Updates).Progress.Completed);
        await service.CloseConnectionAsync(connection.Id, token);
    }

    [Fact]
    public async Task PortRequestsReuseTheirIdentityAfterClientsExitAndTheHostRestarts()
    {
        var port = Environment.GetEnvironmentVariable("SERIALWORKBENCH_TEST_PORT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(port), "Set SERIALWORKBENCH_TEST_PORT to run serial hardware tests.");
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"port-request-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        var token = TestContext.Current.CancellationToken;
        var request = OperationJson.Create("send", Guid.Empty, new SendRequest(Guid.Empty, "SWB\r\n"u8.ToArray()), "port-send")
            with
        { ConnectionOptions = new SerialConnectionOptions(port) };
        Guid operationId;
        await using (var runtime = new HostRuntime(paths))
        {
            var service = new HostRpcService(runtime);
            var started = await service.StartOperationAsync(request, token);
            var operation = Assert.IsType<OperationSnapshot>(started.Operation);
            operationId = operation.Id;
            await service.WaitOperationAsync<RpcResult>(operation, token);
            Assert.Equal(operationId, (await service.StartOperationAsync(request, token)).Operation?.Id);
            Assert.Equal(5, Assert.Single(runtime.Connections.GetSnapshots()).TransmittedBytes);
            await service.CloseConnectionAsync(operation.Request.ConnectionId, token);
        }

        await using var restarted = new HostRuntime(paths);
        var repeated = await new HostRpcService(restarted).StartOperationAsync(request, token);
        Assert.Equal(operationId, repeated.Operation?.Id);
        Assert.Equal(OperationState.Succeeded, repeated.Operation?.State);
        Assert.Empty(restarted.Connections.GetSnapshots());
        Assert.Empty(restarted.Journal.ReadAfter(0, 100));
    }

    [Fact]
    public async Task ForegroundCancellationStopsAnAcceptedTaskBeforeProgressIsRead()
    {
        var port = Environment.GetEnvironmentVariable("SERIALWORKBENCH_TEST_PORT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(port), "Set SERIALWORKBENCH_TEST_PORT to run serial hardware tests.");
        await using var runtime = CreateRuntime();
        var service = new HostRpcService(runtime);
        var token = TestContext.Current.CancellationToken;
        var connection = await service.OpenConnectionAsync(new OpenConnectionRequest(new SerialConnectionOptions(port)), token);
        var definition = new SerialSequenceDefinition("foreground", [new SerialSequenceStep([0x53], "hex", 5000, 1, 0)]);
        var started = await service.StartOperationAsync(OperationJson.Create("sequence.run", connection.Id, definition), token);
        var operation = Assert.IsType<OperationSnapshot>(started.Operation);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WaitOperationAsync<SerialSequenceProgress>(operation, cancelled.Token));

        Assert.Equal(OperationState.Cancelled, (await service.ReadOperationAsync(new OperationQuery(operation.Id), token)).State);
        Assert.Equal(0, runtime.Operations.ActiveCount);
        Assert.Equal(0, runtime.Leases.ActiveCount);
        await service.CloseConnectionAsync(connection.Id, token);
    }
}
