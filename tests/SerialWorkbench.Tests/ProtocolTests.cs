using System.Text;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;
using SerialWorkbench.Transfer;

namespace SerialWorkbench.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void HexCodecParsesCommonSeparatorsAndPrefixes()
    {
        var bytes = HexCodec.Parse("0x01, 02-0A:ff");

        Assert.Equal([0x01, 0x02, 0x0A, 0xFF], bytes);
        Assert.Equal("01 02 0A FF", HexCodec.Format(bytes));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01xz")]
    public void HexCodecRejectsInvalidInput(string input)
    {
        Assert.Throws<FormatException>(() => HexCodec.Parse(input));
    }

    [Fact]
    public void ChecksumsMatchPublishedCheckValues()
    {
        var bytes = Encoding.ASCII.GetBytes("123456789");

        Assert.Equal((ushort)0x4B37, Checksums.Crc16Modbus(bytes));
        Assert.Equal((ushort)0x31C3, Checksums.Crc16XModem(bytes));
        Assert.Equal(0xCBF43926U, Checksums.Crc32(bytes));
    }

    [Fact]
    public void FixedLengthFramerPreservesPartialInput()
    {
        var framer = new FixedLengthFramer(3);

        Assert.Empty(framer.Feed([1]));
        var first = framer.Feed([2, 3, 4, 5]);
        var second = framer.Feed([6]);

        Assert.Single(first);
        Assert.Equal([1, 2, 3], first[0]);
        Assert.Single(second);
        Assert.Equal([4, 5, 6], second[0]);
    }

    [Fact]
    public void DelimiterFramerAcceptsSeveralBoundedFramesInOneRead()
    {
        var framer = new DelimiterFramer([(byte)'\n'], maximumFrameLength: 4);

        var frames = framer.Feed("A\nB\nC\n"u8);

        Assert.Equal(3, frames.Count);
        Assert.Equal("A\n", Encoding.ASCII.GetString(frames[0]));
        Assert.Equal("B\n", Encoding.ASCII.GetString(frames[1]));
        Assert.Equal("C\n", Encoding.ASCII.GetString(frames[2]));
    }

    [Fact]
    public void DelimiterFramerRejectsAnOversizedFrame()
    {
        var framer = new DelimiterFramer([(byte)'\n'], maximumFrameLength: 4);

        Assert.Throws<InvalidDataException>(() => framer.Feed("ABCDE"u8));
    }

    [Fact]
    public void LengthFieldFramerHandlesBackToBackFrames()
    {
        var framer = new LengthFieldFramer(2, 1, 1, littleEndian: false);

        var frames = framer.Feed([0xA0, 0x02, 0x10, 0x11, 0xB0, 0x01, 0x22]);

        Assert.Equal(2, frames.Count);
        Assert.Equal([0xA0, 0x02, 0x10, 0x11], frames[0]);
        Assert.Equal([0xB0, 0x01, 0x22], frames[1]);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(2, -1, 1)]
    [InlineData(2, 0, 0)]
    [InlineData(2, 1, 2)]
    public void LengthFieldFramerRejectsInvalidLayouts(int headerLength, int lengthOffset, int lengthSize)
    {
        Assert.ThrowsAny<ArgumentException>(() => new LengthFieldFramer(headerLength, lengthOffset, lengthSize, littleEndian: false));
    }

    [Fact]
    public void ModbusReadRequestMatchesKnownVector()
    {
        var request = ModbusRtuCodec.BuildReadRequest(1, 3, 0x006B, 3);

        Assert.Equal([0x01, 0x03, 0x00, 0x6B, 0x00, 0x03, 0x74, 0x17], request);
    }

    [Fact]
    public void ModbusRegisterResponseParsesBigEndianValues()
    {
        var frame = WithModbusCrc([0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD]);

        var registers = ModbusRtuCodec.ParseRegisterResponse(frame, 1, 3);

        Assert.Equal([(ushort)0x1234, (ushort)0xABCD], registers);
    }

    [Fact]
    public void ModbusExceptionResponseKeepsTheExceptionCode()
    {
        var frame = WithModbusCrc([0x01, 0x83, 0x02]);

        var exception = Assert.Throws<ModbusException>(() => ModbusRtuCodec.ParseRegisterResponse(frame, 1, 3));

        Assert.Equal((byte)0x02, exception.ExceptionCode);
    }

    [Fact]
    public void XModemBlockRoundTripsAndDetectsDamage()
    {
        var data = Encoding.ASCII.GetBytes("SerialWorkbench");
        var block = XModemCodec.CreateBlock(7, data);

        var payload = XModemCodec.ValidateBlock(block, 7);
        Assert.Equal(data, payload.Span[..data.Length].ToArray());

        block[12] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => XModemCodec.ValidateBlock(block, 7));
        Assert.Throws<InvalidDataException>(() => XModemCodec.ValidateBlock(ReadOnlyMemory<byte>.Empty, 7));
    }

    private static byte[] WithModbusCrc(ReadOnlySpan<byte> data)
    {
        var frame = new byte[data.Length + 2];
        data.CopyTo(frame);
        var crc = Checksums.Crc16Modbus(data);
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }
}
