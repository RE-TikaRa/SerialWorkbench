using System.Runtime.InteropServices;

namespace SerialWorkbench.Protocols;

public interface IStreamFramer
{
    IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> data);

    void Reset();
}

public sealed class FixedLengthFramer(int frameLength) : IStreamFramer
{
    private readonly int frameLength = frameLength > 0 ? frameLength : throw new ArgumentOutOfRangeException(nameof(frameLength));
    private readonly List<byte> buffer = [];

    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        buffer.AddRange(data.ToArray());
        var frames = new List<byte[]>();
        while (buffer.Count >= frameLength)
        {
            frames.Add(buffer.GetRange(0, frameLength).ToArray());
            buffer.RemoveRange(0, frameLength);
        }

        return frames;
    }

    public void Reset() => buffer.Clear();
}

public sealed class DelimiterFramer(ReadOnlySpan<byte> delimiter, bool includeDelimiter = true, int maximumFrameLength = 1024 * 1024) : IStreamFramer
{
    private readonly byte[] delimiter = delimiter.Length > 0 ? delimiter.ToArray() : throw new ArgumentException("Delimiter cannot be empty.", nameof(delimiter));
    private readonly int maximumFrameLength = maximumFrameLength > 0 ? maximumFrameLength : throw new ArgumentOutOfRangeException(nameof(maximumFrameLength));
    private readonly List<byte> buffer = [];

    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        buffer.AddRange(data.ToArray());
        var frames = new List<byte[]>();
        while (FindDelimiter() is var index && index >= 0)
        {
            var consumedLength = index + delimiter.Length;
            if (consumedLength > maximumFrameLength)
            {
                buffer.Clear();
                throw new InvalidDataException($"Frame exceeded {maximumFrameLength} bytes.");
            }

            var frameLength = includeDelimiter ? consumedLength : index;
            frames.Add(buffer.GetRange(0, frameLength).ToArray());
            buffer.RemoveRange(0, consumedLength);
        }

        if (buffer.Count > maximumFrameLength)
        {
            buffer.Clear();
            throw new InvalidDataException($"Frame exceeded {maximumFrameLength} bytes.");
        }

        return frames;
    }

    public void Reset() => buffer.Clear();

    private int FindDelimiter()
    {
        for (var index = 0; index <= buffer.Count - delimiter.Length; index++)
        {
            var matches = true;
            for (var offset = 0; offset < delimiter.Length; offset++)
            {
                if (buffer[index + offset] != delimiter[offset])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return index;
            }
        }

        return -1;
    }
}

public sealed class LengthFieldFramer : IStreamFramer
{
    private readonly List<byte> buffer = [];
    private readonly int headerLength;
    private readonly int lengthAdjustment;
    private readonly int lengthOffset;
    private readonly int lengthSize;
    private readonly bool littleEndian;
    private readonly int maximumFrameLength;

    public LengthFieldFramer(int headerLength, int lengthOffset, int lengthSize, bool littleEndian, int lengthAdjustment = 0, int maximumFrameLength = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(headerLength);
        ArgumentOutOfRangeException.ThrowIfNegative(lengthOffset);
        if (lengthSize is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthSize));
        }

        if (lengthOffset + lengthSize > headerLength)
        {
            throw new ArgumentException("The length field must fit inside the header.", nameof(lengthOffset));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFrameLength, headerLength);

        this.headerLength = headerLength;
        this.lengthOffset = lengthOffset;
        this.lengthSize = lengthSize;
        this.littleEndian = littleEndian;
        this.lengthAdjustment = lengthAdjustment;
        this.maximumFrameLength = maximumFrameLength;
    }

    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        buffer.AddRange(data.ToArray());
        var frames = new List<byte[]>();

        while (buffer.Count >= headerLength)
        {
            var length = ReadLength(CollectionsMarshal.AsSpan(buffer).Slice(lengthOffset, lengthSize));
            var frameLength = checked(headerLength + length + lengthAdjustment);
            if (frameLength < headerLength || frameLength > maximumFrameLength)
            {
                buffer.RemoveAt(0);
                continue;
            }

            if (buffer.Count < frameLength)
            {
                break;
            }

            frames.Add(buffer.GetRange(0, frameLength).ToArray());
            buffer.RemoveRange(0, frameLength);
        }

        return frames;
    }

    public void Reset() => buffer.Clear();

    private int ReadLength(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        var value = 0;
        for (var index = 0; index < data.Length; index++)
        {
            var sourceIndex = littleEndian ? data.Length - 1 - index : index;
            value = (value << 8) | data[sourceIndex];
        }

        return value;
    }
}
