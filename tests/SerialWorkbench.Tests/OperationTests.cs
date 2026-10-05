using SerialWorkbench.Cli;
using SerialWorkbench.Domain;
using SerialWorkbench.Host;
using SerialWorkbench.Ipc;
using SerialWorkbench.Storage;

namespace SerialWorkbench.Tests;

public sealed class OperationTests
{
    [Fact]
    public async Task RestartPreservesRequestIdentityWithoutReplayingDeviceActions()
    {
        var paths = CreatePaths();
        var connectionId = Guid.NewGuid();
        var request = OperationJson.Create("send", connectionId, new SendRequest(connectionId, [0x53, 0x57, 0x42]), "persistent-request");
        var now = DateTimeOffset.UtcNow;
        var original = new OperationSnapshot(Guid.NewGuid(), request, OperationState.Running, ExecutionOutcome.Pending, now, now);
        await using (var store = new OperationStore(paths))
        {
            await store.SaveAsync(original, TestContext.Current.CancellationToken);
        }

        await using var runtime = new HostRuntime(paths);
        var service = new HostRpcService(runtime);
        var saved = Assert.Single(await service.ListOperationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(OperationState.Interrupted, saved.State);
        Assert.Equal(ExecutionOutcome.Unknown, saved.Outcome);
        Assert.Equal("HOST_RESTARTED", saved.Error?.Code);

        var repeated = await service.StartOperationAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(original.Id, repeated.Operation?.Id);
        Assert.Null(repeated.Error);
        Assert.Empty(runtime.Journal.ReadAfter(0, 10));
        Assert.Equal(0, runtime.Operations.ActiveCount);

        var conflict = await service.StartOperationAsync(
            OperationJson.Create("send", connectionId, new SendRequest(connectionId, [0x41]), request.RequestId),
            TestContext.Current.CancellationToken);
        Assert.Equal("REQUEST_ID_CONFLICT", conflict.Error?.Code);
        Assert.Null(conflict.Operation);
    }

    [Fact]
    public async Task InvalidOperationsAreRejectedBeforeAcquiringAConnection()
    {
        await using var runtime = new HostRuntime(CreatePaths());
        var service = new HostRpcService(runtime);
        var request = OperationJson.Create("modbus.poll", Guid.NewGuid(), new ModbusPollRequest(Guid.Empty, Quantity: 126));

        var result = await service.StartOperationAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("INVALID_ARGUMENT", result.Error?.Code);
        Assert.Null(result.Operation);
        Assert.Empty(await service.ListOperationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, runtime.Leases.ActiveCount);
    }

    [Fact]
    public async Task WaitingForInterruptedTasksKeepsTheirUnknownOutcome()
    {
        var paths = CreatePaths();
        var now = DateTimeOffset.UtcNow;
        var original = new OperationSnapshot(Guid.NewGuid(), OperationJson.Create("send", Guid.NewGuid(), new { }),
            OperationState.Running, ExecutionOutcome.Pending, now, now);
        var token = TestContext.Current.CancellationToken;
        await using (var store = new OperationStore(paths))
        {
            await store.SaveAsync(original, token);
        }
        await using var runtime = new HostRuntime(paths);
        var result = await OperationCommands.WaitAsync(new HostRpcService(runtime), original.Id, 1000, token);
        Assert.Equal(OperationState.Interrupted, result.State);
        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
        Assert.Equal(3, OperationCommands.ExitCode(result));
        Assert.Empty(runtime.Journal.ReadAfter(0, 10));
    }

    private static ApplicationPaths CreatePaths()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"operation-store-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        return paths;
    }
}
