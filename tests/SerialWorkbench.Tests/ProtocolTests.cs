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
    [InlineData(1, 7)]
    [InlineData(3, 7)]
    [InlineData(4, 9)]
    [InlineData(5, 8)]
    [InlineData(6, 8)]
    [InlineData(15, 8)]
    [InlineData(16, 8)]
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
    public void ModbusWriteSingleCoilMatchesKnownVector()
    {
        var frame = ModbusRtuCodec.BuildWriteSingleCoil(1, 0x0013, true);

        Assert.Equal([0x01, 0x05, 0x00, 0x13, 0xFF, 0x00, 0x7D, 0xFF], frame);
        var response = ModbusRtuCodec.ParseWriteSingleCoilResponse(frame, 1);
        Assert.Equal((ushort)0x0013, response.Address);
        Assert.True(response.Value);
    }

    [Fact]
    public void ModbusWriteMultipleCoilsMatchesKnownVector()
    {
        var values = new[] { true, false, true, true, false, false, true, true, true, false };

        var frame = ModbusRtuCodec.BuildWriteMultipleCoils(0x11, 0x0013, values);

        Assert.Equal([0x11, 0x0F, 0x00, 0x13, 0x00, 0x0A, 0x02, 0xCD, 0x01, 0xBF, 0x0B], frame);
    }

    [Fact]
    public void ModbusWriteMultipleRegistersBuildsDataAndParsesResponse()
    {
        var request = ModbusRtuCodec.BuildWriteMultipleRegisters(1, 1, [0x000A, 0x0102]);

        Assert.Equal([0x01, 0x10, 0x00, 0x01, 0x00, 0x02, 0x04, 0x00, 0x0A, 0x01, 0x02], request[..^2]);
        Assert.True(ModbusRtuCodec.HasValidCrc(request));
        var response = ModbusRtuCodec.ParseWriteMultipleResponse(WithModbusCrc([0x01, 0x10, 0x00, 0x01, 0x00, 0x02]), 1, 16);
        Assert.Equal((ushort)1, response.Address);
        Assert.Equal((ushort)2, response.Quantity);
    }

    [Fact]
    public void ModbusBitResponseParsesLeastSignificantBitFirst()
    {
        var frame = WithModbusCrc([0x01, 0x01, 0x02, 0xCD, 0x01]);

        var bits = ModbusRtuCodec.ParseBitResponse(frame, 1, 1, 10);

        Assert.Equal([true, false, true, true, false, false, true, true, true, false], bits);
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

    [Fact]
    public void ProtocolTemplateParsesFieldsAndChecksum()
    {
        var template = new ProtocolTemplateDefinition(
            "sensor",
            "AA",
            9,
            null,
            [
                new ProtocolFieldDefinition("address", 1, ProtocolFieldType.U8),
                new ProtocolFieldDefinition("value", 2, ProtocolFieldType.F32, ByteOrder: ProtocolByteOrder.LittleEndian),
            ],
            new ProtocolChecksumDefinition(ProtocolChecksumKind.Xor, 8, 0, 8));
        var frame = new byte[] { 0xAA, 0x01, 0x00, 0x00, 0x80, 0x3F, 0x10, 0x20, 0x24 };

        var result = ProtocolTemplateParser.Inspect(template, frame);

        Assert.True(result.IsValid);
        Assert.Equal(9, result.ExpectedLength);
        Assert.True(result.ChecksumValid);
        Assert.Equal("1", result.Fields[0].Value);
        Assert.Equal("1", result.Fields[1].Value);
    }

    [Fact]
    public void ProtocolTemplateLengthFieldDeterminesExpectedFrameSize()
    {
        var template = new ProtocolTemplateDefinition(
            "length",
            null,
            null,
            new ProtocolLengthFieldDefinition(1, 1, 3),
            [new ProtocolFieldDefinition("payload", 2, ProtocolFieldType.Hex, 3)]);

        var result = ProtocolTemplateParser.Inspect(template, [0x01, 0x03, 0x10, 0x20, 0x30, 0x40]);

        Assert.True(result.IsValid);
        Assert.Equal(6, result.ExpectedLength);
    }

    [Fact]
    public void BytePatternMatcherFindsPatternsAcrossAByteBuffer()
    {
        Assert.True(BytePatternMatcher.Contains([0x10, 0x20, 0x30, 0x40], [0x20, 0x30]));
        Assert.False(BytePatternMatcher.Contains([0x10, 0x20], [0x20, 0x30]));
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
