using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.Modbus;

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

    public async Task<ModbusTransactionResult> RunModbusAsync(ModbusTransactionRequest request, CancellationToken cancellationToken)
    {
        if (request.Frame.Length < 2)
        {
            throw new ArgumentException("Modbus request frame must contain an address and function code.", nameof(request));
        }

        if (request.TimeoutMilliseconds is < 1 or > 600_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.TimeoutMilliseconds, "Timeout must be between 1 and 600000 milliseconds.");
        }

        if (request.Frame[0] != request.SlaveAddress || request.Frame[1] != request.FunctionCode)
        {
            throw new InvalidDataException("Modbus request metadata does not match the frame.");
        }

        if (request.Frame.Length != 8 || !ModbusRtuCodec.HasValidCrc(request.Frame))
        {
            throw new InvalidDataException("Modbus request frame length or CRC is invalid.");
        }

        var connection = Get(request.ConnectionId);
        await using var lease = leases.Acquire(request.ConnectionId, "modbus");
        await using var subscription = connection.Subscribe();
        var stopwatch = Stopwatch.StartNew();
        await connection.SendAsync(request.Frame, "modbus", cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.TimeoutMilliseconds);
        var buffer = new List<byte>(256);
        string? lastError = null;
        try
        {
            while (true)
            {
                var block = await subscription.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                buffer.AddRange(block);
                while (buffer.Count >= 2)
                {
                    var start = buffer.FindIndex(value => value == request.SlaveAddress);
                    if (start < 0)
                    {
                        buffer.Clear();
                        break;
                    }

                    if (start > 0)
                    {
                        buffer.RemoveRange(0, start);
                    }

                    var function = buffer[1];
                    if (function != request.FunctionCode && function != (request.FunctionCode | 0x80))
                    {
                        buffer.RemoveAt(0);
                        continue;
                    }

                    var length = ModbusRtuCodec.GetResponseLength(CollectionsMarshal.AsSpan(buffer), request.FunctionCode);
                    if (length is null || buffer.Count < length.Value)
                    {
                        break;
                    }

                    var frame = buffer.GetRange(0, length.Value).ToArray();
                    try
                    {
                        if ((frame[1] & 0x80) != 0)
                        {
                            ModbusRtuCodec.ParseRegisterResponse(frame, request.SlaveAddress, request.FunctionCode);
                        }

                        if (request.FunctionCode == 6)
                        {
                            var result = ModbusRtuCodec.ParseWriteSingleRegisterResponse(frame, request.SlaveAddress);
                            stopwatch.Stop();
                            return new ModbusTransactionResult(true, frame, request.FunctionCode, [], result.Address, result.Value, null, stopwatch.Elapsed, null);
                        }

                        var registers = ModbusRtuCodec.ParseRegisterResponse(frame, request.SlaveAddress, request.FunctionCode);
                        stopwatch.Stop();
                        return new ModbusTransactionResult(true, frame, request.FunctionCode, registers, null, null, null, stopwatch.Elapsed, null);
                    }
                    catch (ModbusException exception)
                    {
                        stopwatch.Stop();
                        return new ModbusTransactionResult(false, frame, request.FunctionCode, [], null, null, exception.ExceptionCode, stopwatch.Elapsed, exception.Message);
                    }
                    catch (InvalidDataException exception)
                    {
                        lastError = exception.Message;
                        buffer.RemoveAt(0);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ModbusTransactionResult(false, buffer.ToArray(), request.FunctionCode, [], null, null, null, stopwatch.Elapsed, lastError ?? "Timed out waiting for Modbus response.");
        }
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
