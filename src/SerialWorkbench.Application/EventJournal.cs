using SerialWorkbench.Domain;

namespace SerialWorkbench.Application;

public sealed class EventJournal(int capacity = 100_000)
{
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly LinkedList<SerialTrafficEvent> events = [];
    private readonly object gate = new();
    private long nextSequence;

    public SerialTrafficEvent Append(Guid connectionId, SerialDirection direction, ReadOnlySpan<byte> data, string source, string? message = null)
    {
        var item = new SerialTrafficEvent(
            Interlocked.Increment(ref nextSequence),
            DateTimeOffset.UtcNow,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            connectionId,
            direction,
            data.ToArray(),
            source,
            message);

        lock (gate)
        {
            events.AddLast(item);
            while (events.Count > capacity)
            {
                events.RemoveFirst();
            }
        }

        return item;
    }

    public IReadOnlyList<SerialTrafficEvent> ReadAfter(long sequence, int maximumCount)
    {
        if (maximumCount is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        lock (gate)
        {
            return events.Where(item => item.Sequence > sequence).Take(maximumCount).ToArray();
        }
    }

    public long LatestSequence => Volatile.Read(ref nextSequence);
}
