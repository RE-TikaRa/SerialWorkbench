using System.Text;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;

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
        Assert.Contains("Illegal data address", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, 7)]
    [InlineData(4, 9)]
    [InlineData(6, 8)]
    public void ModbusResponseLengthIsDerivedFromTheFunction(byte function, int length)
    {
        var prefix = function == 6 ? new byte[] { 1, function } : new byte[] { 1, function, (byte)(length - 5) };

        Assert.Equal(length, ModbusRtuCodec.GetResponseLength(prefix, function)!.Value);
    }

    [Fact]
    public void ModbusWriteSingleRegisterResponseParsesTheEcho()
    {
        var frame = WithModbusCrc([0x01, 0x06, 0x00, 0x10, 0xAB, 0xCD]);

        var result = ModbusRtuCodec.ParseWriteSingleRegisterResponse(frame, 1);

        Assert.Equal((ushort)0x0010, result.Address);
        Assert.Equal((ushort)0xABCD, result.Value);
    }

    [Fact]
    public void ModbusWriteSingleRegisterResponseAcceptsFullValueRange()
    {
        var frame = WithModbusCrc([0x01, 0x06, 0xFF, 0xFF, 0xFF, 0xFF]);

        var result = ModbusRtuCodec.ParseWriteSingleRegisterResponse(frame, 1);

        Assert.Equal(ushort.MaxValue, result.Address);
        Assert.Equal(ushort.MaxValue, result.Value);
    }

    [Fact]
    public void ModbusFrameInspectionReportsFieldsAndCrc()
    {
        var frame = WithModbusCrc([0x01, 0x03, 0x02, 0x00, 0x0A]);

        var inspection = ModbusRtuCodec.Inspect(frame);

        Assert.True(inspection.IsValid);
        Assert.Equal("Read response", inspection.Kind);
        Assert.Equal((byte)1, inspection.Address);
        Assert.Equal((byte)3, inspection.FunctionCode);
        Assert.Equal((byte)2, inspection.ByteCount);
        Assert.Equal(7, inspection.ExpectedLength);
        Assert.Equal(inspection.CalculatedCrc, inspection.ActualCrc);
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
