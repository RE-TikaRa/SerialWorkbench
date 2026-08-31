using System.Buffers.Binary;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.Modbus;

public static class ModbusRtuCodec
{
    public static ModbusFrameInspection Inspect(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return new ModbusFrameInspection(false, "Modbus RTU", null, null, null, frame.Length, null, null, null, null, null, null, "Modbus frame is too short.");
        }

        var address = frame[0];
        var function = frame[1];
        byte? exception = (function & 0x80) != 0 && frame.Length >= 3 ? frame[2] : null;
        var baseFunction = (byte)(function & 0x7F);
        var kind = exception is not null ? "Exception response" : "Modbus RTU";
        int? expectedLength = null;
        byte? byteCount = null;
        ushort? dataAddress = null;
        ushort? value = null;
        string? error = null;

        if (exception is not null)
        {
            expectedLength = 5;
        }
        else if (baseFunction is 1 or 2 or 3 or 4)
        {
            if (frame.Length == 8)
            {
                kind = "Request";
                dataAddress = BinaryPrimitives.ReadUInt16BigEndian(frame[2..4]);
                value = BinaryPrimitives.ReadUInt16BigEndian(frame[4..6]);
                expectedLength = 8;
            }
            else if (frame.Length >= 3)
            {
                kind = "Read response";
                byteCount = frame[2];
                expectedLength = byteCount + 5;
            }
        }
        else if (baseFunction is 5 or 6)
        {
            kind = "Write single response";
            expectedLength = 8;
            if (frame.Length >= 6)
            {
                dataAddress = BinaryPrimitives.ReadUInt16BigEndian(frame[2..4]);
                value = BinaryPrimitives.ReadUInt16BigEndian(frame[4..6]);
            }
        }

        if (expectedLength is { } length && frame.Length != length)
        {
            error = $"Expected {length} bytes, received {frame.Length}.";
        }

        ushort? calculatedCrc = null;
        ushort? actualCrc = null;
        if (frame.Length >= 4)
        {
            calculatedCrc = Checksums.Crc16Modbus(frame[..^2]);
            actualCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
            if (calculatedCrc != actualCrc)
            {
                error ??= "Modbus CRC is invalid.";
            }
        }
        else
        {
            error ??= "Modbus frame does not contain a CRC.";
        }

        return new ModbusFrameInspection(
            error is null,
            kind,
            address,
            function,
            exception,
            frame.Length,
            expectedLength,
            byteCount,
            dataAddress,
            value,
            calculatedCrc,
            actualCrc,
            error);
    }

    public static int? GetResponseLength(ReadOnlySpan<byte> framePrefix, byte expectedFunction)
    {
        if (framePrefix.Length < 2)
        {
            return null;
        }

        var functionCode = framePrefix[1];

        if (functionCode != expectedFunction && functionCode != (expectedFunction | 0x80))
        {
            throw new InvalidDataException("Modbus response function does not match the request.");
        }

        if ((functionCode & 0x80) != 0)
        {
            return 5;
        }

        return expectedFunction is 5 or 6
            ? 8
            : framePrefix.Length >= 3
                ? framePrefix[2] + 5
                : null;
    }

    public static byte[] BuildReadRequest(byte slaveAddress, byte functionCode, ushort startAddress, ushort quantity)
    {
        if (functionCode is not (1 or 2 or 3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(functionCode));
        }

        if (quantity == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        Span<byte> frame = stackalloc byte[8];
        frame[0] = slaveAddress;
        frame[1] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(frame[2..4], startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(frame[4..6], quantity);
        var crc = Checksums.Crc16Modbus(frame[..6]);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[6..8], crc);
        return frame.ToArray();
    }

    public static byte[] BuildWriteSingleRegister(byte slaveAddress, ushort address, ushort value)
    {
        Span<byte> frame = stackalloc byte[8];
        frame[0] = slaveAddress;
        frame[1] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(frame[2..4], address);
        BinaryPrimitives.WriteUInt16BigEndian(frame[4..6], value);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[6..8], Checksums.Crc16Modbus(frame[..6]));
        return frame.ToArray();
    }

    public static byte[] BuildWriteSingleCoil(byte slaveAddress, ushort address, bool value)
    {
        Span<byte> frame = stackalloc byte[8];
        frame[0] = slaveAddress;
        frame[1] = 5;
        BinaryPrimitives.WriteUInt16BigEndian(frame[2..4], address);
        BinaryPrimitives.WriteUInt16BigEndian(frame[4..6], value ? (ushort)0xFF00 : (ushort)0x0000);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[6..8], Checksums.Crc16Modbus(frame[..6]));
        return frame.ToArray();
    }

    public static bool HasValidCrc(ReadOnlySpan<byte> frame) => frame.Length >= 4 && Checksums.Crc16Modbus(frame[..^2]) == BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);

    public static ushort[] ParseRegisterResponse(ReadOnlySpan<byte> frame, byte expectedSlave, byte expectedFunction)
    {
        if (frame.Length < 5)
        {
            throw new InvalidDataException("Modbus response is too short.");
        }

        if (!HasValidCrc(frame))
        {
            throw new InvalidDataException("Modbus CRC is invalid.");
        }

        if (frame[0] != expectedSlave || (frame[1] & 0x7F) != expectedFunction)
        {
            throw new InvalidDataException("Modbus response address or function does not match the request.");
        }

        if ((frame[1] & 0x80) != 0)
        {
            throw new ModbusException(frame[2]);
        }

        var byteCount = frame[2];
        if ((byteCount & 1) != 0 || frame.Length != byteCount + 5)
        {
            throw new InvalidDataException("Modbus register response length is invalid.");
        }

        var registers = new ushort[byteCount / 2];
        for (var index = 0; index < registers.Length; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3 + (index * 2), 2));
        }

        return registers;
    }

    public static bool[] ParseBitResponse(ReadOnlySpan<byte> frame, byte expectedSlave, byte expectedFunction, ushort quantity)
    {
        if (expectedFunction is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedFunction));
        }

        if (quantity is < 1 or > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        if (frame.Length < 5)
        {
            throw new InvalidDataException("Modbus response is too short.");
        }

        if (!HasValidCrc(frame))
        {
            throw new InvalidDataException("Modbus CRC is invalid.");
        }

        if (frame[0] != expectedSlave || (frame[1] & 0x7F) != expectedFunction)
        {
            throw new InvalidDataException("Modbus response address or function does not match the request.");
        }

        if ((frame[1] & 0x80) != 0)
        {
            throw new ModbusException(frame[2]);
        }

        var byteCount = frame[2];
        if (frame.Length != byteCount + 5 || byteCount < (quantity + 7) / 8)
        {
            throw new InvalidDataException("Modbus bit response length is invalid.");
        }

        var bits = new bool[quantity];
        for (var index = 0; index < bits.Length; index++)
        {
            bits[index] = (frame[3 + (index / 8)] & (1 << (index % 8))) != 0;
        }

        return bits;
    }

    public static (ushort Address, ushort Value) ParseWriteSingleRegisterResponse(ReadOnlySpan<byte> frame, byte expectedSlave)
    {
        if (frame.Length != 8)
        {
            throw new InvalidDataException("Modbus write response length is invalid.");
        }

        if (!HasValidCrc(frame))
        {
            throw new InvalidDataException("Modbus CRC is invalid.");
        }

        if (frame[0] != expectedSlave || frame[1] != 6)
        {
            throw new InvalidDataException("Modbus response address or function does not match the request.");
        }

        return (
            BinaryPrimitives.ReadUInt16BigEndian(frame[2..4]),
            BinaryPrimitives.ReadUInt16BigEndian(frame[4..6]));
    }

    public static (ushort Address, bool Value) ParseWriteSingleCoilResponse(ReadOnlySpan<byte> frame, byte expectedSlave)
    {
        if (frame.Length != 8)
        {
            throw new InvalidDataException("Modbus write response length is invalid.");
        }

        if (!HasValidCrc(frame))
        {
            throw new InvalidDataException("Modbus CRC is invalid.");
        }

        if (frame[0] != expectedSlave || frame[1] != 5)
        {
            throw new InvalidDataException("Modbus response address or function does not match the request.");
        }

        var rawValue = BinaryPrimitives.ReadUInt16BigEndian(frame[4..6]);
        if (rawValue is not (0x0000 or 0xFF00))
        {
            throw new InvalidDataException("Modbus coil response value is invalid.");
        }

        return (BinaryPrimitives.ReadUInt16BigEndian(frame[2..4]), rawValue == 0xFF00);
    }
}

public sealed record ModbusFrameInspection(
    bool IsValid,
    string Kind,
    byte? Address,
    byte? FunctionCode,
    byte? ExceptionCode,
    int FrameLength,
    int? ExpectedLength,
    byte? ByteCount,
    ushort? DataAddress,
    ushort? Value,
    ushort? CalculatedCrc,
    ushort? ActualCrc,
    string? Error);

public sealed class ModbusException(byte exceptionCode) : Exception($"Modbus exception 0x{exceptionCode:X2}: {GetDescription(exceptionCode)}.")
{
    public byte ExceptionCode { get; } = exceptionCode;

    private static string GetDescription(byte code) => code switch
    {
        0x01 => "Illegal function",
        0x02 => "Illegal data address",
        0x03 => "Illegal data value",
        0x04 => "Server device failure",
        0x05 => "Acknowledge",
        0x06 => "Server device busy",
        0x08 => "Memory parity error",
        0x0A => "Gateway path unavailable",
        0x0B => "Gateway target device failed to respond",
        _ => "Unknown exception",
    };
}
