using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SerialWorkbench.WinUI;

public enum WaveformMode
{
    CsvText,
    BinaryFrame,
}

public enum WaveformSampleType
{
    Int16LittleEndian,
    Int16BigEndian,
    UInt16LittleEndian,
    Float32LittleEndian,
}

public sealed class WaveformParser
{
    private static readonly char[] Separators = [',', ' ', '\t', ';'];
    private readonly StringBuilder textBuffer = new();
    private readonly List<byte> byteBuffer = [];

    public WaveformMode Mode { get; set; } = WaveformMode.CsvText;

    public int FrameLength { get; set; } = 8;

    public WaveformSampleType SampleType { get; set; } = WaveformSampleType.Int16LittleEndian;

    public void Reset()
    {
        textBuffer.Clear();
        byteBuffer.Clear();
    }

    public IReadOnlyList<double[]> Feed(ReadOnlySpan<byte> data) =>
        Mode == WaveformMode.CsvText ? FeedCsv(data) : FeedBinary(data);

    private List<double[]> FeedCsv(ReadOnlySpan<byte> data)
    {
        textBuffer.Append(Encoding.UTF8.GetString(data));
        var samples = new List<double[]>();
        var text = textBuffer.ToString();
        var lineStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\n' or '\r'))
            {
                continue;
            }

            var line = text[lineStart..index];
            lineStart = index + 1;
            var channels = ParseCsvLine(line);
            if (channels.Length > 0)
            {
                samples.Add(channels);
            }
        }

        textBuffer.Clear();
        textBuffer.Append(text[lineStart..]);
        return samples;
    }

    private static double[] ParseCsvLine(string line)
    {
        var values = new List<double>();
        foreach (var token in line.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return [.. values];
    }

    private List<double[]> FeedBinary(ReadOnlySpan<byte> data)
    {
        var sampleSize = SampleType == WaveformSampleType.Float32LittleEndian ? 4 : 2;
        var channelCount = FrameLength / sampleSize;
        var samples = new List<double[]>();
        if (channelCount == 0)
        {
            return samples;
        }

        byteBuffer.AddRange(data);
        var frameSize = channelCount * sampleSize;
        var offset = 0;
        while (byteBuffer.Count - offset >= frameSize)
        {
            var channels = new double[channelCount];
            for (var channel = 0; channel < channelCount; channel++)
            {
                var slice = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(byteBuffer).Slice(offset + (channel * sampleSize), sampleSize);
                channels[channel] = ReadSample(slice);
            }

            samples.Add(channels);
            offset += frameSize;
        }

        byteBuffer.RemoveRange(0, offset);
        return samples;
    }

    private double ReadSample(ReadOnlySpan<byte> slice) => SampleType switch
    {
        WaveformSampleType.Int16LittleEndian => BinaryPrimitives.ReadInt16LittleEndian(slice),
        WaveformSampleType.Int16BigEndian => BinaryPrimitives.ReadInt16BigEndian(slice),
        WaveformSampleType.UInt16LittleEndian => BinaryPrimitives.ReadUInt16LittleEndian(slice),
        WaveformSampleType.Float32LittleEndian => BinaryPrimitives.ReadSingleLittleEndian(slice),
        _ => 0,
    };
}
