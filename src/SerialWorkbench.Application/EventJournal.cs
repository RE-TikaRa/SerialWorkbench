using SerialWorkbench.Domain;

namespace SerialWorkbench.Application;

public sealed class EventJournal(int capacity = 100_000)
{
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly LinkedList<SerialTrafficEvent> events = [];
    private readonly object gate = new();
    private long nextSequence;
    private TaskCompletionSource changed = NewSignal();

    public Guid StreamId { get; } = Guid.NewGuid();

    public SerialTrafficEvent Append(Guid connectionId, SerialDirection direction, ReadOnlySpan<byte> data, string source, string? message = null)
    {
        lock (gate)
        {
            var item = new SerialTrafficEvent(
                ++nextSequence,
                DateTimeOffset.UtcNow,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                connectionId,
                direction,
                data.ToArray(),
                source,
                message);

            events.AddLast(item);
            while (events.Count > capacity)
            {
                events.RemoveFirst();
            }
            var previous = changed;
            changed = NewSignal();
            previous.TrySetResult();
            return item;
        }
    }

    public IReadOnlyList<SerialTrafficEvent> ReadAfter(long sequence, int maximumCount, Guid? connectionId = null, SerialDirection? direction = null, string? sourceContains = null)
    {
        if (maximumCount is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        lock (gate)
        {
            return events
                .Where(item => item.Sequence > sequence
                    && (connectionId is null || item.ConnectionId == connectionId)
                    && (direction is null || item.Direction == direction)
                    && (sourceContains is null || item.Source.Contains(sourceContains, StringComparison.OrdinalIgnoreCase)))
                .Take(maximumCount)
                .ToArray();
        }
    }

    public long LatestSequence => Volatile.Read(ref nextSequence);

    public long EarliestSequence
    {
        get
        {
            lock (gate)
            {
                return events.First?.Value.Sequence ?? nextSequence + 1;
            }
        }
    }

    public async Task<EventBatch> ReadBatchAsync(long sequence, int maximumCount, Guid? connectionId, SerialDirection? direction,
        string? sourceContains, Guid? streamId, int waitMilliseconds, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 10_000);
        ArgumentOutOfRangeException.ThrowIfNegative(waitMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(waitMilliseconds, 30_000);
        Task notification;
        lock (gate)
        {
            if (sequence != nextSequence || streamId is { } expected && expected != StreamId || waitMilliseconds == 0)
            {
                return CreateBatch(sequence, maximumCount, connectionId, direction, sourceContains, streamId);
            }

            notification = changed.Task;
        }

        try
        {
            await notification.WaitAsync(TimeSpan.FromMilliseconds(waitMilliseconds), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        lock (gate)
        {
            return CreateBatch(sequence, maximumCount, connectionId, direction, sourceContains, streamId);
        }
    }

    private EventBatch CreateBatch(long sequence, int maximumCount, Guid? connectionId, SerialDirection? direction, string? sourceContains, Guid? streamId)
    {
        var reset = streamId is { } expected && expected != StreamId || sequence > nextSequence;
        if (reset)
        {
            sequence = 0;
        }

        var earliest = events.First?.Value.Sequence ?? nextSequence + 1;
        var gap = sequence + 1 < earliest ? new EventGap(sequence + 1, earliest - 1) : null;
        var items = events.Where(item => item.Sequence > sequence
                && (connectionId is null || item.ConnectionId == connectionId)
                && (direction is null || item.Direction == direction)
                && (sourceContains is null || item.Source.Contains(sourceContains, StringComparison.OrdinalIgnoreCase)))
            .Take(maximumCount).ToArray();
        var next = items.Length == maximumCount ? items[^1].Sequence : nextSequence;
        return new EventBatch(StreamId, items, next, earliest, nextSequence, gap, reset);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
