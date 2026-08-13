using System.Collections.Concurrent;
using System.Diagnostics;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;

namespace SerialWorkbench.Serial.Windows;

public sealed class SerialConnectionManager(
    EventJournal journal,
    WriteLeaseManager leases,
    Func<SerialTrafficEvent, CancellationToken, ValueTask> persist) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SerialConnection> connections = [];

    public IReadOnlyList<ConnectionSnapshot> GetSnapshots() => connections.Values.Select(static item => item.GetSnapshot()).OrderBy(static item => item.Options.PortName).ToArray();

    public async Task<ConnectionSnapshot> OpenAsync(SerialConnectionOptions options, CancellationToken cancellationToken)
    {
        if (connections.Values.Any(item => string.Equals(item.Options.PortName, options.PortName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{options.PortName} is already open.");
        }

        var connection = new SerialConnection(Guid.NewGuid(), options, journal, persist);
        if (!connections.TryAdd(connection.Id, connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Unable to register serial connection.");
        }

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection.GetSnapshot();
        }
        catch
        {
            connections.TryRemove(connection.Id, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task CloseAsync(Guid id)
    {
        if (connections.TryRemove(id, out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task SendAsync(Guid id, ReadOnlyMemory<byte> data, string source, CancellationToken cancellationToken)
    {
        var connection = Get(id);
        await using var lease = leases.Acquire(id, source);
        await connection.SendAsync(data, source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoopbackResult> RunLoopbackAsync(LoopbackRequest request, CancellationToken cancellationToken)
    {
        if (request.PayloadLength is < 1 or > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.PayloadLength, "Payload length must be between 1 and 16777216 bytes.");
        }

        if (request.Iterations is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Iterations, "Iterations must be between 1 and 10000.");
        }

        if (request.TimeoutMilliseconds is < 1 or > 600_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.TimeoutMilliseconds, "Timeout must be between 1 and 600000 milliseconds.");
        }

        var connection = Get(request.ConnectionId);
        await using var lease = leases.Acquire(request.ConnectionId, "loopback");
        await using var subscription = connection.Subscribe();
        var stopwatch = Stopwatch.StartNew();
        long sent = 0;
        long received = 0;

        for (var iteration = 0; iteration < request.Iterations; iteration++)
        {
            var expected = CreatePayload(request, iteration);
            await connection.SendAsync(expected, "loopback", cancellationToken).ConfigureAwait(false);
            sent += expected.Length;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.TimeoutMilliseconds);
            var actual = new byte[expected.Length];
            var offset = 0;
            try
            {
                while (offset < actual.Length)
                {
                    var block = await subscription.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                    var count = Math.Min(block.Length, actual.Length - offset);
                    block.AsSpan(0, count).CopyTo(actual.AsSpan(offset));
                    offset += count;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return new LoopbackResult(false, iteration, sent, received + offset, stopwatch.Elapsed, Rate(received + offset, stopwatch.Elapsed), null, null, null, "Timed out waiting for loopback data.");
            }

            received += actual.Length;
            var difference = FirstDifference(expected, actual);
            if (difference >= 0)
            {
                stopwatch.Stop();
                return new LoopbackResult(false, iteration + 1, sent, received, stopwatch.Elapsed, Rate(received, stopwatch.Elapsed), difference, expected[difference], actual[difference], "Loopback data differs.");
            }
        }

        stopwatch.Stop();
        return new LoopbackResult(true, request.Iterations, sent, received, stopwatch.Elapsed, Rate(received, stopwatch.Elapsed), null, null, null, null);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in connections.Keys)
        {
            await CloseAsync(id).ConfigureAwait(false);
        }
    }

    private SerialConnection Get(Guid id) => connections.TryGetValue(id, out var connection)
        ? connection
        : throw new KeyNotFoundException($"Connection {id} was not found.");

    private static byte[] CreatePayload(LoopbackRequest request, int iteration)
    {
        var data = new byte[request.PayloadLength];
        switch (request.Pattern)
        {
            case LoopbackPattern.Fixed:
                data.AsSpan().Fill((byte)(0xA5 ^ iteration));
                break;
            case LoopbackPattern.Incrementing:
                for (var index = 0; index < data.Length; index++)
                {
                    data[index] = (byte)(index + iteration);
                }

                break;
            case LoopbackPattern.Random:
                new Random(request.Seed + iteration).NextBytes(data);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Pattern, "Unsupported loopback pattern.");
        }

        return data;
    }

    private static int FirstDifference(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        for (var index = 0; index < Math.Min(expected.Length, actual.Length); index++)
        {
            if (expected[index] != actual[index])
            {
                return index;
            }
        }

        return expected.Length == actual.Length ? -1 : Math.Min(expected.Length, actual.Length);
    }

    private static double Rate(long bytes, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 ? 0 : bytes / elapsed.TotalSeconds;
}
