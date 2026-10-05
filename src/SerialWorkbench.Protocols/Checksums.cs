namespace SerialWorkbench.Protocols;

public enum ChecksumKind
{
    None,
    Xor,
    Sum8,
    Crc16Modbus,
    Crc16Xmodem,
    Crc32,
}

public static class Checksums
{
    public static byte[] Append(byte[] data, ChecksumKind kind)
    {
        switch (kind)
        {
            case ChecksumKind.None:
                return data;
            case ChecksumKind.Xor:
                return [.. data, Xor(data)];
            case ChecksumKind.Sum8:
                return [.. data, Sum8(data)];
            case ChecksumKind.Crc16Modbus:
                var modbus = Crc16Modbus(data);
                return [.. data, (byte)modbus, (byte)(modbus >> 8)];
            case ChecksumKind.Crc16Xmodem:
                var xmodem = Crc16XModem(data);
                return [.. data, (byte)(xmodem >> 8), (byte)xmodem];
            case ChecksumKind.Crc32:
                var crc = Crc32(data);
                return [.. data, (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24)];
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public static byte Xor(ReadOnlySpan<byte> data)
    {
        byte value = 0;
        foreach (var item in data)
        {
            value ^= item;
        }

        return value;
    }

    public static byte Sum8(ReadOnlySpan<byte> data)
    {
        var value = 0;
        foreach (var item in data)
        {
            value += item;
        }

        return (byte)value;
    }

    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var item in data)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    public static ushort Crc16XModem(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var item in data)
        {
            crc ^= (ushort)(item << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var item in data)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
