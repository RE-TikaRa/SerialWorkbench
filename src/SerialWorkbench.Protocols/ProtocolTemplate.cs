using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace SerialWorkbench.Protocols;

public enum ProtocolByteOrder
{
    LittleEndian,
    BigEndian,
}

public enum ProtocolFieldType
{
    U8,
    I8,
    U16,
    I16,
    U32,
    I32,
    F32,
    Hex,
}

public enum ProtocolChecksumKind
{
    None,
    Xor,
    Sum8,
    Crc16Modbus,
    Crc16XModem,
    Crc32,
}

public sealed record ProtocolFieldDefinition(
    string Name,
    int Offset,
    ProtocolFieldType Type,
    int Length = 0,
    ProtocolByteOrder ByteOrder = ProtocolByteOrder.BigEndian);

public sealed record ProtocolLengthFieldDefinition(
    int Offset,
    int Size,
    int Adjustment = 0,
    ProtocolByteOrder ByteOrder = ProtocolByteOrder.BigEndian);

public sealed record ProtocolChecksumDefinition(
    ProtocolChecksumKind Kind,
    int Offset,
    int DataOffset,
    int DataLength,
    ProtocolByteOrder ByteOrder = ProtocolByteOrder.LittleEndian);

public sealed record ProtocolTemplateDefinition(
    string Name,
    string? HeaderHex,
    int? FixedLength,
    ProtocolLengthFieldDefinition? LengthField,
    IReadOnlyList<ProtocolFieldDefinition> Fields,
    ProtocolChecksumDefinition? Checksum = null);

public sealed record ProtocolFieldValue(string Name, string Type, string Value, string Hex);

public sealed record ProtocolTemplateInspection(
    bool IsValid,
    string Template,
    int FrameLength,
    int? ExpectedLength,
    bool? ChecksumValid,
    IReadOnlyList<ProtocolFieldValue> Fields,
    string? Error);

public static class ProtocolTemplateCodec
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static ProtocolTemplateDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<ProtocolTemplateDefinition>(json, jsonOptions)
        ?? throw new InvalidDataException("Protocol template is empty.");

    public static string Serialize(ProtocolTemplateDefinition template) => JsonSerializer.Serialize(template, jsonOptions);
}

public static class ProtocolTemplateParser
{
    public static ProtocolTemplateInspection Inspect(ProtocolTemplateDefinition template, ReadOnlySpan<byte> frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Name);
        var error = ValidateFrame(template, frame, out var expectedLength, out var checksumValid);
        var values = new List<ProtocolFieldValue>();
        foreach (var field in template.Fields)
        {
            try
            {
                values.Add(ReadField(field, frame));
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException)
            {
                error ??= ex.Message;
            }
        }

        return new ProtocolTemplateInspection(error is null, template.Name, frame.Length, expectedLength, checksumValid, values, error);
    }

    private static string? ValidateFrame(ProtocolTemplateDefinition template, ReadOnlySpan<byte> frame, out int? expectedLength, out bool? checksumValid)
    {
        expectedLength = template.FixedLength;
        checksumValid = null;
        if (template.HeaderHex is { Length: > 0 } headerText)
        {
            var header = HexCodec.Parse(headerText);
            if (!frame.StartsWith(header))
            {
                return "Frame header does not match the template.";
            }
        }

        if (template.LengthField is { } lengthField)
        {
            if (lengthField.Size is < 1 or > 4 || lengthField.Offset < 0 || lengthField.Offset + lengthField.Size > frame.Length)
            {
                return "Length field is outside the frame.";
            }

            expectedLength = checked((int)ReadUnsigned(frame.Slice(lengthField.Offset, lengthField.Size), lengthField.ByteOrder) + lengthField.Adjustment);
        }

        if (expectedLength is { } length && frame.Length != length)
        {
            return $"Expected {length} bytes, received {frame.Length}.";
        }

        if (template.Checksum is { Kind: not ProtocolChecksumKind.None } checksum)
        {
            try
            {
                checksumValid = VerifyChecksum(checksum, frame);
            }
            catch (ArgumentOutOfRangeException)
            {
                return "Checksum range is outside the frame.";
            }

            if (checksumValid == false)
            {
                return "Frame checksum is invalid.";
            }
        }

        return null;
    }

    private static ProtocolFieldValue ReadField(ProtocolFieldDefinition field, ReadOnlySpan<byte> frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field.Name);
        var length = field.Type switch
        {
            ProtocolFieldType.U8 or ProtocolFieldType.I8 => 1,
            ProtocolFieldType.U16 or ProtocolFieldType.I16 => 2,
            ProtocolFieldType.U32 or ProtocolFieldType.I32 or ProtocolFieldType.F32 => 4,
            ProtocolFieldType.Hex when field.Length > 0 => field.Length,
            ProtocolFieldType.Hex => throw new InvalidDataException($"Field {field.Name} requires a positive length."),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        if (field.Offset < 0 || field.Offset + length > frame.Length)
        {
            throw new InvalidDataException($"Field {field.Name} is outside the frame.");
        }

        var data = frame.Slice(field.Offset, length);
        var value = field.Type switch
        {
            ProtocolFieldType.U8 => data[0].ToString(CultureInfo.InvariantCulture),
            ProtocolFieldType.I8 => ((sbyte)data[0]).ToString(CultureInfo.InvariantCulture),
            ProtocolFieldType.U16 => ReadUnsigned(data, field.ByteOrder).ToString(CultureInfo.InvariantCulture),
            ProtocolFieldType.I16 => ReadSigned(data, field.ByteOrder).ToString(CultureInfo.InvariantCulture),
            ProtocolFieldType.U32 => ReadUnsigned(data, field.ByteOrder).ToString(CultureInfo.InvariantCulture),
            ProtocolFieldType.I32 => ReadSigned(data, field.ByteOrder).ToString(CultureInfo.InvariantCulture),
            ProtocolFieldType.F32 => ReadSingle(data, field.ByteOrder).ToString("R", CultureInfo.InvariantCulture),
            ProtocolFieldType.Hex => HexCodec.Format(data),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        return new ProtocolFieldValue(field.Name, field.Type.ToString(), value, HexCodec.Format(data));
    }

    private static bool VerifyChecksum(ProtocolChecksumDefinition definition, ReadOnlySpan<byte> frame)
    {
        var size = definition.Kind switch
        {
            ProtocolChecksumKind.Xor or ProtocolChecksumKind.Sum8 => 1,
            ProtocolChecksumKind.Crc16Modbus or ProtocolChecksumKind.Crc16XModem => 2,
            ProtocolChecksumKind.Crc32 => 4,
            _ => 0,
        };
        var data = frame.Slice(definition.DataOffset, definition.DataLength);
        var actual = ReadUnsigned(frame.Slice(definition.Offset, size), definition.ByteOrder);
        var calculated = definition.Kind switch
        {
            ProtocolChecksumKind.Xor => Checksums.Xor(data),
            ProtocolChecksumKind.Sum8 => Checksums.Sum8(data),
            ProtocolChecksumKind.Crc16Modbus => Checksums.Crc16Modbus(data),
            ProtocolChecksumKind.Crc16XModem => Checksums.Crc16XModem(data),
            ProtocolChecksumKind.Crc32 => Checksums.Crc32(data),
            _ => throw new ArgumentOutOfRangeException(nameof(definition)),
        };
        return calculated == actual;
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> data, ProtocolByteOrder byteOrder)
    {
        ulong value = 0;
        if (byteOrder == ProtocolByteOrder.BigEndian)
        {
            foreach (var item in data)
            {
                value = (value << 8) | item;
            }
        }
        else
        {
            for (var index = data.Length - 1; index >= 0; index--)
            {
                value = (value << 8) | data[index];
            }
        }

        return value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> data, ProtocolByteOrder byteOrder)
    {
        var value = ReadUnsigned(data, byteOrder);
        var bits = data.Length * 8;
        return (value & (1UL << (bits - 1))) == 0 ? (long)value : unchecked((long)(value | (ulong.MaxValue << bits)));
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, ProtocolByteOrder byteOrder)
    {
        var bits = checked((uint)ReadUnsigned(data, byteOrder));
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }
}
