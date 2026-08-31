using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading.Channels;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;

namespace SerialWorkbench.Serial.Windows;

public sealed class SerialConnection : IAsyncDisposable
{
    private readonly SerialPort port;
    private readonly EventJournal journal;
    private readonly Func<SerialTrafficEvent, CancellationToken, ValueTask> persist;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> subscriptions = [];
    private Task? readerTask;
    private long receivedBytes;
    private long transmittedBytes;
    private long receiveBlocks;
    private long transmitOperations;
    private long errorCount;
    private long lastActivityUnixMilliseconds;
    private string? error;
    private bool dtrEnable;
    private bool rtsEnable;

    public SerialConnection(
        Guid id,
        SerialConnectionOptions options,
        EventJournal journal,
        Func<SerialTrafficEvent, CancellationToken, ValueTask> persist)
    {
        Id = id;
        Options = options;
        this.journal = journal;
        this.persist = persist;
        dtrEnable = options.DtrEnable;
        rtsEnable = options.Rs485Mode ? false : options.RtsEnable;
        port = new SerialPort(options.PortName, options.BaudRate, Convert(options.Parity), options.DataBits, Convert(options.StopBits))
        {
            Handshake = Convert(options.Handshake),
            DtrEnable = options.DtrEnable,
            RtsEnable = options.Rs485Mode ? false : options.RtsEnable,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 64 * 1024,
            ReadTimeout = 500,
            WriteTimeout = 5000,
        };
    }

    public Guid Id { get; }

    public SerialConnectionOptions Options { get; }

    public ConnectionState State { get; private set; } = ConnectionState.Closed;

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = ConnectionState.Opening;
        try
        {
            port.Open();
            State = ConnectionState.Open;
            readerTask = Task.Run(() => ReadLoopAsync(lifetime.Token), CancellationToken.None);
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            error = ex.Message;
            State = ConnectionState.Faulted;
            throw;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, string source, CancellationToken cancellationToken)
    {
        if (State != ConnectionState.Open)
        {
            throw new InvalidOperationException($"Connection {Id} is {State}: {error ?? "no device error was reported"}.");
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var rs485DirectionEnabled = false;
        try
        {
            if (Options.Rs485Mode)
            {
                port.RtsEnable = true;
                rtsEnable = true;
                rs485DirectionEnabled = true;
                await Task.Delay(Options.RtsBeforeSendMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (Options.Rs485Mode)
            {
                await Task.Delay(Options.RtsAfterSendMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            Interlocked.Add(ref transmittedBytes, data.Length);
            Interlocked.Increment(ref transmitOperations);
            MarkActivity();
            var item = journal.Append(Id, SerialDirection.Transmit, data.Span, source);
            await persist(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Interlocked.Increment(ref errorCount);
            error = ex.Message;
            throw;
        }
        finally
        {
            if (rs485DirectionEnabled)
            {
                port.RtsEnable = false;
                rtsEnable = false;
            }

            writeGate.Release();
        }
    }

    public void SetControlLines(SerialControlLines lines)
    {
        if (State != ConnectionState.Open)
        {
            throw new InvalidOperationException($"Connection {Id} is {State}: {error ?? "no device error was reported"}.");
        }

        port.DtrEnable = lines.DtrEnable;
        port.RtsEnable = Options.Rs485Mode ? false : lines.RtsEnable;
        dtrEnable = lines.DtrEnable;
        rtsEnable = Options.Rs485Mode ? false : lines.RtsEnable;
    }

    public void ClearBuffers(bool receive, bool transmit)
    {
        if (State != ConnectionState.Open)
        {
            throw new InvalidOperationException($"Connection {Id} is {State}: {error ?? "no device error was reported"}.");
        }

        if (receive)
        {
            port.DiscardInBuffer();
        }

        if (transmit)
        {
            port.DiscardOutBuffer();
        }
    }

    public async Task SendBreakAsync(int durationMilliseconds, CancellationToken cancellationToken)
    {
        if (durationMilliseconds is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        if (State != ConnectionState.Open)
        {
            throw new InvalidOperationException($"Connection {Id} is {State}: {error ?? "no device error was reported"}.");
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            port.BreakState = true;
            await Task.Delay(durationMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            port.BreakState = false;
            writeGate.Release();
        }
    }

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        subscriptions.AddOrUpdate(id, channel, static (_, _) => throw new InvalidOperationException());
        return new Subscription(id, channel.Reader, this);
    }

    public ConnectionSnapshot GetSnapshot()
    {
        var milliseconds = Interlocked.Read(ref lastActivityUnixMilliseconds);
        var controlLines = new SerialControlLineStatus(dtrEnable, rtsEnable, false, false, false, null);
        if (port.IsOpen)
        {
            try
            {
                controlLines = new SerialControlLineStatus(dtrEnable, rtsEnable, port.CtsHolding, port.DsrHolding, port.CDHolding, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }
        }

        return new ConnectionSnapshot(
            Id,
            Options with { DtrEnable = dtrEnable, RtsEnable = rtsEnable },
            State,
            Interlocked.Read(ref receivedBytes),
            Interlocked.Read(ref transmittedBytes),
            Interlocked.Read(ref receiveBlocks),
            Interlocked.Read(ref transmitOperations),
            Interlocked.Read(ref errorCount),
            milliseconds == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
            error,
            controlLines);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (port.IsOpen)
        {
            port.Close();
        }

        if (readerTask is not null)
        {
            try
            {
                await readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var channel in subscriptions.Values)
        {
            channel.Writer.TryComplete();
        }

        subscriptions.Clear();
        writeGate.Dispose();
        lifetime.Dispose();
        port.Dispose();
        State = ConnectionState.Closed;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is (IOException or InvalidOperationException or UnauthorizedAccessException))
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref errorCount);
                    error = ex.Message;
                    State = ConnectionState.Faulted;
                    break;
                }

                if (count == 0)
                {
                    continue;
                }

                var data = buffer.AsSpan(0, count).ToArray();
                Interlocked.Add(ref receivedBytes, count);
                Interlocked.Increment(ref receiveBlocks);
                MarkActivity();
                var item = journal.Append(Id, SerialDirection.Receive, data, "serial");
                await persist(item, cancellationToken).ConfigureAwait(false);

                foreach (var channel in subscriptions.Values)
                {
                    channel.Writer.TryWrite(data);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void MarkActivity() => Interlocked.Exchange(ref lastActivityUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private void Unsubscribe(Guid id)
    {
        if (subscriptions.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    private static Parity Convert(SerialParity value) => value switch
    {
        SerialParity.None => Parity.None,
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static StopBits Convert(SerialStopBits value) => value switch
    {
        SerialStopBits.One => StopBits.One,
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static Handshake Convert(SerialHandshake value) => value switch
    {
        SerialHandshake.None => Handshake.None,
        SerialHandshake.XOnXOff => Handshake.XOnXOff,
        SerialHandshake.RequestToSend => Handshake.RequestToSend,
        SerialHandshake.RequestToSendXOnXOff => Handshake.RequestToSendXOnXOff,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public sealed class Subscription(Guid id, ChannelReader<byte[]> reader, SerialConnection owner) : IAsyncDisposable
    {
        public ChannelReader<byte[]> Reader { get; } = reader;

        public ValueTask DisposeAsync()
        {
            owner.Unsubscribe(id);
            return ValueTask.CompletedTask;
        }
    }
}
