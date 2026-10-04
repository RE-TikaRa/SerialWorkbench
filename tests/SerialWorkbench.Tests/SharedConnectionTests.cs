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
}
