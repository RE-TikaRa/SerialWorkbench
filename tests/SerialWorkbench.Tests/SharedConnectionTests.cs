using SerialWorkbench.Domain;
using SerialWorkbench.Host;
using SerialWorkbench.Ipc;
using SerialWorkbench.Storage;

namespace SerialWorkbench.Tests;

public sealed class SharedConnectionTests
{
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
}
