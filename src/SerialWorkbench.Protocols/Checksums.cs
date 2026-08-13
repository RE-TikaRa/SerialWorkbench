namespace SerialWorkbench.Protocols;

public static class Checksums
{
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
