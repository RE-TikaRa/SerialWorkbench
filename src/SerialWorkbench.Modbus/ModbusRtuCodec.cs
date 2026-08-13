using System.Buffers.Binary;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.Modbus;

public static class ModbusRtuCodec
{
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
}

public sealed class ModbusException(byte exceptionCode) : Exception($"Modbus exception 0x{exceptionCode:X2}.")
{
    public byte ExceptionCode { get; } = exceptionCode;
}
