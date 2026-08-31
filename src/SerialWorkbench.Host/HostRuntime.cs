using System.Diagnostics;
using System.Threading.Channels;
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
    private readonly Channel<SerialTrafficEvent> sessionEvents = Channel.CreateUnbounded<SerialTrafficEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task sessionWriter;
    private readonly object sessionQueueGate = new();
    private TaskCompletionSource<bool> sessionFlushed = CompletedSignal();
    private long pendingSessionEvents;
    private Exception? sessionWriterError;
    private long persistedSessionEvents;
    private long persistenceStartedTimestamp;
    private int clientCount;
    private long idleSinceUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public HostRuntime(ApplicationPaths paths)
    {
        Paths = paths;
        Journal = new EventJournal();
        Leases = new WriteLeaseManager();
        Sessions = new SessionStore(paths);
        Connections = new SerialConnectionManager(Journal, Leases, PersistAsync);
        sessionWriter = Task.Run(WriteSessionEventsAsync);
    }

    public ApplicationPaths Paths { get; private set; }

    public EventJournal Journal { get; }

    public WriteLeaseManager Leases { get; }

    public SessionStore Sessions { get; private set; }

    public SerialConnectionManager Connections { get; }

    public CancellationToken Stopping => stopping.Token;

    public int ClientCount => Volatile.Read(ref clientCount);

    public long PendingSessionEvents => Interlocked.Read(ref pendingSessionEvents);

    public double SessionEventPersistenceEventsPerSecond
    {
        get
        {
            var started = Volatile.Read(ref persistenceStartedTimestamp);
            var elapsed = started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalSeconds;
            return elapsed > 0 ? Interlocked.Read(ref persistedSessionEvents) / elapsed : 0;
        }
    }

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
        await FlushSessionEventsAsync(cancellationToken).ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
        Paths = nextPaths;
        Sessions = new SessionStore(nextPaths);
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        await Connections.DisposeAsync().ConfigureAwait(false);
        await FlushSessionEventsAsync(CancellationToken.None).ConfigureAwait(false);
        sessionEvents.Writer.TryComplete();
        await sessionWriter.ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
        stopping.Dispose();
    }

    public async Task FlushSessionEventsAsync(CancellationToken cancellationToken)
    {
        Task flushTask;
        lock (sessionQueueGate)
        {
            flushTask = sessionFlushed.Task;
        }

        await flushTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        Exception? persistenceError;
        lock (sessionQueueGate)
        {
            persistenceError = sessionWriterError;
        }

        if (persistenceError is { } error)
        {
            throw new InvalidOperationException("Session event persistence failed.", error);
        }
    }

    private ValueTask PersistAsync(SerialTrafficEvent item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sessionQueueGate)
        {
            if (sessionWriterError is { } error)
            {
                throw new InvalidOperationException("Session event persistence failed.", error);
            }

            if (pendingSessionEvents++ == 0)
            {
                sessionFlushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            Interlocked.CompareExchange(ref persistenceStartedTimestamp, Stopwatch.GetTimestamp(), 0);
        }

        if (!sessionEvents.Writer.TryWrite(item))
        {
            CompletePersistedEvent();
            throw new InvalidOperationException("The session event writer is closed.");
        }

        return ValueTask.CompletedTask;
    }

    private async Task WriteSessionEventsAsync()
    {
        var batch = new List<SerialTrafficEvent>(256);
        await foreach (var item in sessionEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            batch.Add(item);
            while (batch.Count < 256 && sessionEvents.Reader.TryRead(out var next))
            {
                batch.Add(next);
            }

            try
            {
                await Sessions.AppendManyAsync(batch, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Add(ref persistedSessionEvents, batch.Count);
            }
            catch (Exception ex)
            {
                lock (sessionQueueGate)
                {
                    sessionWriterError ??= ex;
                }
            }
            finally
            {
                for (var index = 0; index < batch.Count; index++)
                {
                    CompletePersistedEvent();
                }

                batch.Clear();
            }
        }
    }

    private void CompletePersistedEvent()
    {
        lock (sessionQueueGate)
        {
            pendingSessionEvents--;
            if (pendingSessionEvents == 0)
            {
                sessionFlushed.TrySetResult(true);
            }
        }
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }
}
