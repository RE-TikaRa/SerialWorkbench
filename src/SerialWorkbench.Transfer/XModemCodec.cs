using SerialWorkbench.Protocols;

namespace SerialWorkbench.Transfer;

public static class XModemCodec
{
    public const byte Soh = 0x01;
    public const byte Stx = 0x02;
    public const byte Eot = 0x04;
    public const byte Ack = 0x06;
    public const byte Nak = 0x15;
    public const byte Can = 0x18;

    public static byte[] CreateBlock(byte blockNumber, ReadOnlySpan<byte> data, bool oneKilobyte = false, byte padding = 0x1A)
    {
        var payloadLength = oneKilobyte ? 1024 : 128;
        if (data.Length > payloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        var block = new byte[payloadLength + 5];
        block[0] = oneKilobyte ? Stx : Soh;
        block[1] = blockNumber;
        block[2] = (byte)~blockNumber;
        block.AsSpan(3, payloadLength).Fill(padding);
        data.CopyTo(block.AsSpan(3));
        var crc = Checksums.Crc16XModem(block.AsSpan(3, payloadLength));
        block[^2] = (byte)(crc >> 8);
        block[^1] = (byte)crc;
        return block;
    }

    public static ReadOnlyMemory<byte> ValidateBlock(ReadOnlyMemory<byte> block, byte expectedBlockNumber)
    {
        var span = block.Span;
        if (span.IsEmpty)
        {
            throw new InvalidDataException("XMODEM block is empty.");
        }

        var payloadLength = span[0] switch
        {
            Soh => 128,
            Stx => 1024,
            _ => throw new InvalidDataException("XMODEM block does not start with SOH or STX."),
        };

        if (span.Length != payloadLength + 5 || span[1] != expectedBlockNumber || span[2] != (byte)~expectedBlockNumber)
        {
            throw new InvalidDataException("XMODEM block header is invalid.");
        }

        var expectedCrc = Checksums.Crc16XModem(span.Slice(3, payloadLength));
        var actualCrc = (ushort)((span[^2] << 8) | span[^1]);
        if (expectedCrc != actualCrc)
        {
            throw new InvalidDataException("XMODEM block CRC is invalid.");
        }

        return block.Slice(3, payloadLength);
    }
}
