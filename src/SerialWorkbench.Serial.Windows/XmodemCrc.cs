using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.Serial.Windows;

internal static class XmodemCrc
{
    private const byte Soh = 0x01;
    private const byte Eot = 0x04;
    private const byte Ack = 0x06;
    private const byte Nak = 0x15;
    private const byte Can = 0x18;

    public static async Task<XmodemTransferResult> SendAsync(SerialConnection connection, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await using var subscription = connection.Subscribe();
        var stopwatch = Stopwatch.StartNew();
        var retries = 0;
        var blocks = 0;
        try
        {
            await WaitForStartAsync(subscription.Reader, cancellationToken).ConfigureAwait(false);
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
                    var response = await ReadByteAsync(subscription.Reader, cancellationToken).ConfigureAwait(false);
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
            if (await ReadByteAsync(subscription.Reader, cancellationToken).ConfigureAwait(false) != Ack)
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
        var stopwatch = Stopwatch.StartNew();
        var output = new List<byte>();
        var retries = 0;
        var blocks = 0;
        try
        {
            await connection.SendAsync(new byte[] { (byte)'C' }, "xmodem.receive", cancellationToken).ConfigureAwait(false);
            var expected = 1;
            while (true)
            {
                var marker = await ReadByteAsync(subscription.Reader, cancellationToken).ConfigureAwait(false);
                if (marker == Eot)
                {
                    await connection.SendAsync(new byte[] { Ack }, "xmodem.receive", cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (marker != Soh)
                {
                    continue;
                }

                var frame = await ReadBytesAsync(subscription.Reader, 132, cancellationToken).ConfigureAwait(false);
                if (frame[0] != expected || frame[1] != (byte)~expected || Checksums.Crc16XModem(frame.AsSpan(2, 128)) != BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(130)))
                {
                    retries++;
                    await connection.SendAsync(new byte[] { Nak }, "xmodem.receive", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                output.AddRange(frame.AsSpan(2, 128).ToArray());
                blocks++;
                expected = expected == 255 ? 1 : expected + 1;
                await connection.SendAsync(new byte[] { Ack }, "xmodem.receive", cancellationToken).ConfigureAwait(false);
            }

            while (output.Count > 0 && output[^1] == 0x1A)
            {
                output.RemoveAt(output.Count - 1);
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

    private static async Task WaitForStartAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var value = await ReadByteAsync(reader, cancellationToken).ConfigureAwait(false);
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

    private static async Task<byte> ReadByteAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        var block = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return block[0];
    }

    private static async Task<byte[]> ReadBytesAsync(ChannelReader<byte[]> reader, int count, CancellationToken cancellationToken)
    {
        var result = new List<byte>(count);
        while (result.Count < count)
        {
            result.AddRange(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        }

        return result.Take(count).ToArray();
    }
}
