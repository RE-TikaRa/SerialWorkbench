namespace SerialWorkbench.Protocols;

public static class BytePatternMatcher
{
    public static bool Contains(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
    {
        if (pattern.Length == 0)
        {
            return true;
        }

        if (pattern.Length > data.Length)
        {
            return false;
        }

        for (var offset = 0; offset <= data.Length - pattern.Length; offset++)
        {
            if (data.Slice(offset, pattern.Length).SequenceEqual(pattern))
            {
                return true;
            }
        }

        return false;
    }
}
