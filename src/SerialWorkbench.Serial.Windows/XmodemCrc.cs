using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.Serial.Windows;

internal static class XmodemCrc
{
    private const byte MetadataBlock = 0;
    private const uint MetadataMagic = 0x53425758;
    private const byte Soh = 0x01;
    private const byte Eot = 0x04;
    private const byte Ack = 0x06;
    private const byte Nak = 0x15;
    private const byte Can = 0x18;

    public static async Task<XmodemTransferResult> SendAsync(SerialConnection connection, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await using var subscription = connection.Subscribe();
        var reader = new ByteReader(subscription.Reader);
        var stopwatch = Stopwatch.StartNew();
        var retries = 0;
        var blocks = 0;
        try
        {
            await WaitForStartAsync(reader, cancellationToken).ConfigureAwait(false);
            if (!await TrySendMetadataAsync(connection, reader, data.Length, cancellationToken).ConfigureAwait(false))
            {
                // The receiver is a standard XMODEM peer and does not support the length preamble.
            }

            var blockNumber = 1;
            for (var offset = 0; offset < data.Length || offset == 0; offset += 128)
            {
                var payload = new byte[128];
                payload.AsSpan().Fill(0x1A);
                var count = Math.Min(128, Math.Max(0, data.Length - offset));
                data.Span.Slice(offset, count).CopyTo(payload);
                var frame = new byte[133];
                frame[0] = Soh;
                frame[1] = (byte)blockNumber;
                frame[2] = (byte)~blockNumber;
                payload.CopyTo(frame, 3);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(131), Checksums.Crc16XModem(payload));
                var sent = false;
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    await connection.SendAsync(frame, "xmodem.send", cancellationToken).ConfigureAwait(false);
                    var response = await ReadByteWithTimeoutAsync(reader, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    if (response == Ack)
                    {
                        sent = true;
                        break;
                    }

                    retries++;
                    if (response == Can)
                    {
                        throw new IOException("Receiver cancelled XMODEM transfer.");
                    }
                }

                if (!sent)
                {
                    throw new TimeoutException($"XMODEM block {blockNumber} was not acknowledged.");
                }

                blocks++;
                blockNumber = blockNumber == 255 ? 1 : blockNumber + 1;
            }

            await connection.SendAsync(new byte[] { Eot }, "xmodem.send", cancellationToken).ConfigureAwait(false);
            if (await ReadByteWithTimeoutAsync(reader, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false) != Ack)
            {
                throw new TimeoutException("XMODEM EOT was not acknowledged.");
            }

            stopwatch.Stop();
            return new XmodemTransferResult(true, data.Length, blocks, retries, stopwatch.Elapsed, null);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            stopwatch.Stop();
            return new XmodemTransferResult(false, Math.Min(data.Length, blocks * 128L), blocks, retries, stopwatch.Elapsed, ex.Message);
        }
    }

    public static async Task<(XmodemTransferResult Result, byte[] Data)> ReceiveAsync(SerialConnection connection, CancellationToken cancellationToken)
    {
        await using var subscription = connection.Subscribe();
        var reader = new ByteReader(subscription.Reader);
        var stopwatch = Stopwatch.StartNew();
        var output = new List<byte>();
        var retries = 0;
        var blocks = 0;
        int? expectedLength = null;
        try
        {
            var marker = await WaitForFirstBlockAsync(connection, reader, cancellationToken).ConfigureAwait(false);
            var expected = 1;
            while (true)
            {
                if (marker == Eot)
                {
                    await connection.SendAsync(new byte[] { Ack }, "xmodem.receive", cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (marker != Soh)
                {
                    marker = await ReadByteWithTimeoutAsync(reader, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var frame = await ReadBytesWithTimeoutAsync(reader, 132, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (frame[0] == MetadataBlock && frame[1] == 0xFF && IsValidFrame(frame) && TryReadMetadata(frame.AsSpan(2, 128), out var length))
                {
                    expectedLength = length;
                    await connection.SendAsync(new byte[] { Ack }, "xmodem.receive", cancellationToken).ConfigureAwait(false);
                    marker = await ReadByteWithTimeoutAsync(reader, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (frame[0] != expected || frame[1] != (byte)~expected || Checksums.Crc16XModem(frame.AsSpan(2, 128)) != BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(130)))
                {
                    retries++;
                    await connection.SendAsync(new byte[] { Nak }, "xmodem.receive", cancellationToken).ConfigureAwait(false);
                    marker = await ReadByteWithTimeoutAsync(reader, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                output.AddRange(frame.AsSpan(2, 128).ToArray());
                blocks++;
                expected = expected == 255 ? 1 : expected + 1;
                await connection.SendAsync(new byte[] { Ack }, "xmodem.receive", cancellationToken).ConfigureAwait(false);
                marker = await ReadByteWithTimeoutAsync(reader, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }

            if (expectedLength is { } exactLength)
            {
                if (exactLength > output.Count)
                {
                    throw new InvalidDataException("XMODEM length metadata exceeds the received data.");
                }

                output.RemoveRange(exactLength, output.Count - exactLength);
            }
            else
            {
                while (output.Count > 0 && output[^1] == 0x1A)
                {
                    output.RemoveAt(output.Count - 1);
                }
            }

            stopwatch.Stop();
            return (new XmodemTransferResult(true, output.Count, blocks, retries, stopwatch.Elapsed, null), [.. output]);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            stopwatch.Stop();
            return (new XmodemTransferResult(false, output.Count, blocks, retries, stopwatch.Elapsed, ex.Message), [.. output]);
        }
    }

    private static async Task<bool> TrySendMetadataAsync(SerialConnection connection, ByteReader reader, int length, CancellationToken cancellationToken)
    {
        var payload = new byte[128];
        BinaryPrimitives.WriteUInt32BigEndian(payload, MetadataMagic);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), checked((uint)length));
        var frame = BuildFrame(MetadataBlock, payload);
        await connection.SendAsync(frame, "xmodem.send", cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            return await reader.ReadByteAsync(timeout.Token).ConfigureAwait(false) == Ack;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task WaitForStartAsync(ByteReader reader, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var value = await reader.ReadByteAsync(timeout.Token).ConfigureAwait(false);
            if (value is (byte)'C' or Nak)
            {
                return;
            }

            if (value == Can)
            {
                throw new IOException("Receiver cancelled XMODEM transfer.");
            }
        }
    }

    private static async Task<byte> WaitForFirstBlockAsync(SerialConnection connection, ByteReader reader, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await connection.SendAsync(new byte[] { (byte)'C' }, "xmodem.receive", cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                return await reader.ReadByteAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        throw new TimeoutException("XMODEM sender did not start the transfer.");
    }

    private static byte[] BuildFrame(byte blockNumber, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[133];
        frame[0] = Soh;
        frame[1] = blockNumber;
        frame[2] = (byte)~blockNumber;
        payload.CopyTo(frame.AsSpan(3, 128));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(131), Checksums.Crc16XModem(payload));
        return frame;
    }

    private static bool TryReadMetadata(ReadOnlySpan<byte> payload, out int length)
    {
        length = 0;
        if (payload.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(payload) != MetadataMagic)
        {
            return false;
        }

        var value = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
        if (value > int.MaxValue)
        {
            throw new InvalidDataException("XMODEM length metadata is out of range.");
        }

        length = (int)value;
        return true;
    }

    private static async Task<byte> ReadByteWithTimeoutAsync(ByteReader reader, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            return await reader.ReadByteAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("XMODEM response timed out.");
        }
    }

    private static async Task<byte[]> ReadBytesWithTimeoutAsync(ByteReader reader, int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            return await reader.ReadBytesAsync(count, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("XMODEM data block timed out.");
        }
    }

    private static bool IsValidFrame(ReadOnlySpan<byte> frame) =>
        frame.Length == 132 && Checksums.Crc16XModem(frame[2..130]) == BinaryPrimitives.ReadUInt16BigEndian(frame[130..]);

    private sealed class ByteReader(ChannelReader<byte[]> reader)
    {
        private readonly List<byte> buffer = [];

        public async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (buffer.Count == 0)
            {
                buffer.AddRange(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            }

            var value = buffer[0];
            buffer.RemoveAt(0);
            return value;
        }

        public async Task<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
        {
            while (buffer.Count < count)
            {
                buffer.AddRange(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            }

            var result = buffer.Take(count).ToArray();
            buffer.RemoveRange(0, count);
            return result;
        }
    }
}
