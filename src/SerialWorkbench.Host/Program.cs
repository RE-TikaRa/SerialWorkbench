using System.IO.Pipes;
using SerialWorkbench.Host;
using SerialWorkbench.Ipc;
using SerialWorkbench.Storage;
using StreamJsonRpc;

var applicationRoot = ReadOption(args, "--app-root") ?? AppContext.BaseDirectory;
var paths = new ApplicationPaths(applicationRoot);
paths.EnsureWritable();

var pipeName = HostEndpoint.GetPipeName(paths.ApplicationRoot);
using var mutex = new Mutex(true, $"Local\\{pipeName}", out var ownsMutex);
if (!ownsMutex)
{
    return 0;
}

await using var runtime = new HostRuntime(paths);
var clients = new HashSet<Task>();
var clientsGate = new object();

var idleMonitor = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await timer.WaitForNextTickAsync(runtime.Stopping).ConfigureAwait(false))
    {
        if (runtime.ShouldStopAfterIdle(TimeSpan.FromSeconds(30)))
        {
            runtime.RequestStop();
            break;
        }
    }
}, runtime.Stopping);

try
{
    while (!runtime.Stopping.IsCancellationRequested)
    {
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.WaitForConnectionAsync(runtime.Stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            break;
        }

        runtime.ClientConnected();
        var client = HandleClientAsync(pipe, runtime);
        lock (clientsGate)
        {
            clients.Add(client);
        }

        _ = client.ContinueWith(
            completed =>
            {
                lock (clientsGate)
                {
                    clients.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
finally
{
    runtime.RequestStop();
    Task[] remaining;
    lock (clientsGate)
    {
        remaining = [.. clients];
    }

    await Task.WhenAll(remaining).ConfigureAwait(false);
    try
    {
        await idleMonitor.ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }
}

return 0;

static async Task HandleClientAsync(NamedPipeServerStream pipe, HostRuntime runtime)
{
    try
    {
        using var rpc = JsonRpc.Attach(pipe, new HostRpcService(runtime));
        await rpc.Completion.ConfigureAwait(false);
    }
    finally
    {
        runtime.ClientDisconnected();
        await pipe.DisposeAsync().ConfigureAwait(false);
    }
}

static string? ReadOption(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}
