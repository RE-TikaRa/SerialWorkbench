using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Serial.Windows;
using SerialWorkbench.Sessions;
using SerialWorkbench.Storage;

namespace SerialWorkbench.Host;

public sealed class HostRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private int clientCount;
    private long idleSinceUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public HostRuntime(ApplicationPaths paths)
    {
        Paths = paths;
        Journal = new EventJournal();
        Leases = new WriteLeaseManager();
        Sessions = new SessionStore(paths);
        Connections = new SerialConnectionManager(Journal, Leases, PersistAsync);
    }

    public ApplicationPaths Paths { get; private set; }

    public EventJournal Journal { get; }

    public WriteLeaseManager Leases { get; }

    public SessionStore Sessions { get; private set; }

    public SerialConnectionManager Connections { get; }

    public CancellationToken Stopping => stopping.Token;

    public int ClientCount => Volatile.Read(ref clientCount);

    public void ClientConnected()
    {
        Interlocked.Increment(ref clientCount);
        Interlocked.Exchange(ref idleSinceUnixMilliseconds, 0);
    }

    public void ClientDisconnected()
    {
        if (Interlocked.Decrement(ref clientCount) == 0)
        {
            Interlocked.Exchange(ref idleSinceUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    public bool ShouldStopAfterIdle(TimeSpan idleTimeout)
    {
        if (ClientCount != 0)
        {
            return false;
        }

        var idleSince = Interlocked.Read(ref idleSinceUnixMilliseconds);
        return idleSince != 0 && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(idleSince) >= idleTimeout;
    }

    public void RequestStop() => stopping.Cancel();

    public async Task SetWorkspaceAsync(string? workspaceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Connections.GetSnapshots().Count != 0)
        {
            throw new InvalidOperationException("Close all serial connections before changing the workspace.");
        }

        var nextPaths = Paths.WithWorkspace(workspaceRoot);
        nextPaths.EnsureWritable();
        await Sessions.DisposeAsync().ConfigureAwait(false);
        Paths = nextPaths;
        Sessions = new SessionStore(nextPaths);
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        await Connections.DisposeAsync().ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
        stopping.Dispose();
    }

    private ValueTask PersistAsync(SerialTrafficEvent item, CancellationToken cancellationToken) => Sessions.AppendAsync(item, cancellationToken);
}
