using System.Globalization;
using System.Text;
using SerialWorkbench.Domain;
using SerialWorkbench.WinUI;

namespace SerialWorkbench.Tests;

public sealed class TrafficBufferTests
{
    private static readonly Guid ConnectionId = Guid.Parse("f67b19ae-7c43-4647-99e3-05cd4476c862");

    static TrafficBufferTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Theory]
    [InlineData("utf-8")]
    [InlineData("us-ascii")]
    [InlineData("gbk")]
    [InlineData("utf-16")]
    public void TextLinesDoNotDependOnReadBoundaries(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var original = encodingName == "us-ascii"
            ? "temperature=25.6C\r\nhumidity=40%\n\rtail"
            : "温度=25.6°C\r\n湿度=40%\n\r尾行";
        var bytes = encoding.GetBytes(original);
        var expected = original.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        foreach (var size in new[] { 1, 2, 5, bytes.Length })
        {
            var buffer = CreateBuffer(encoding);
            for (var offset = 0; offset < bytes.Length; offset += size)
            {
                buffer.Append([CreateEvent(offset + 1, bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset)).ToArray())]);
            }

            Assert.Equal(expected, buffer.Rows.Select(static row => row.Display));
            Assert.Equal(Convert.ToHexString(bytes), string.Concat(buffer.Rows.Select(static row => row.Hex)));
        }
    }

    [Fact]
    public void IncompleteTextUpdatesTheSameRowImmediately()
    {
        var buffer = CreateBuffer(Encoding.UTF8);
        var first = CreateEvent(1, "温度="u8.ToArray());
        buffer.Append([first]);
        var row = Assert.Single(buffer.Rows);
        var snapshot = row.Snapshot();

        Assert.Equal("温度=", row.Display);
        buffer.Append([CreateEvent(2, "25.6°C"u8.ToArray())]);

        Assert.Same(row, Assert.Single(buffer.Rows));
        Assert.Equal("温度=25.6°C", row.Display);
        Assert.Equal("温度=", snapshot.Display);
        Assert.Equal(Convert.ToHexString(first.Data), snapshot.Hex);
        Assert.Equal(first.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), row.Time);
    }

    [Fact]
    public void PartialUtf8CharactersDoNotCreateEmptyRows()
    {
        var buffer = CreateBuffer(Encoding.UTF8);
        var bytes = "温\r\n"u8.ToArray();
        var first = CreateEvent(1, bytes[..2]);
        buffer.Append([first]);

        Assert.Empty(buffer.Rows);
        buffer.Append([CreateEvent(2, bytes[2..])]);

        var row = Assert.Single(buffer.Rows);
        Assert.Equal("温", row.Display);
        Assert.Equal(Convert.ToHexString(bytes), row.Hex);
        Assert.Equal(first.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), row.Time);
    }

    [Fact]
    public void Utf8SurrogatePairsSurviveIndividualByteReads()
    {
        var buffer = CreateBuffer(Encoding.UTF8);
        var bytes = "状态🙂\r\n"u8.ToArray();
        for (var index = 0; index < bytes.Length; index++)
        {
            buffer.Append([CreateEvent(index + 1, [bytes[index]])]);
        }

        Assert.Equal("状态🙂", Assert.Single(buffer.Rows).Display);
        Assert.Equal(Convert.ToHexString(bytes), buffer.Rows[0].Hex);
    }

    [Fact]
    public void BlankLinesArePreservedWithoutDuplicatingCrLf()
    {
        var buffer = CreateBuffer(Encoding.UTF8);
        var bytes = "\r\n\r\r\n\n"u8.ToArray();
        for (var index = 0; index < bytes.Length; index++)
        {
            buffer.Append([CreateEvent(index + 1, [bytes[index]])]);
        }

        Assert.Equal(["", "", "", ""], buffer.Rows.Select(static row => row.Display));
        Assert.Equal(Convert.ToHexString(bytes), string.Concat(buffer.Rows.Select(static row => row.Hex)));
    }

    [Fact]
    public void DirectionAndSourceChangesStartSeparateRows()
    {
        var buffer = CreateBuffer(Encoding.UTF8);
        buffer.Append([
            CreateEvent(1, "A"u8.ToArray()),
            CreateEvent(2, "Q\r\n"u8.ToArray(), SerialDirection.Transmit, "winui.send"),
            CreateEvent(3, "B"u8.ToArray()),
            CreateEvent(4, "C"u8.ToArray(), source: "other"),
            CreateEvent(5, "D"u8.ToArray()),
        ]);

        Assert.Equal(["A", "Q", "B", "C", "D"], buffer.Rows.Select(static row => row.Display));
        Assert.True(buffer.Rows[1].IsTransmit);
        Assert.Equal("other", buffer.Rows[3].Source);
    }

    [Fact]
    public void DecoderStateIsIndependentForEachDirection()
    {
        var buffer = CreateBuffer(Encoding.UTF8);
        var receive = "温"u8.ToArray();
        var transmit = "度"u8.ToArray();
        buffer.Append([
            CreateEvent(1, receive[..1]),
            CreateEvent(2, transmit[..1], SerialDirection.Transmit),
            CreateEvent(3, receive[1..]),
            CreateEvent(4, transmit[1..], SerialDirection.Transmit),
        ]);

        Assert.Equal(["温", "度"], buffer.Rows.Select(static row => row.Display));
        Assert.True(buffer.Rows[0].IsReceive);
        Assert.True(buffer.Rows[1].IsTransmit);
        Assert.Equal(Convert.ToHexString(receive), buffer.Rows[0].Hex);
        Assert.Equal(Convert.ToHexString(transmit), buffer.Rows[1].Hex);
    }

    [Fact]
    public void ConnectionChangesPreserveIndependentDecoderState()
    {
        var buffer = CreateBuffer(Encoding.UTF8);
        var otherConnection = Guid.Parse("9d28b731-de93-482c-aa47-8c0b7cd6ba80");
        var first = "温"u8.ToArray();
        var second = "湿"u8.ToArray();
        buffer.Append([
            CreateEvent(1, first[..1]),
            CreateEvent(2, second[..1]) with { ConnectionId = otherConnection },
            CreateEvent(3, first[1..]),
            CreateEvent(4, second[1..]) with { ConnectionId = otherConnection },
        ]);

        Assert.Equal(["温", "湿"], buffer.Rows.Select(static row => row.Display));
        Assert.Equal(Convert.ToHexString(first), buffer.Rows[0].Hex);
        Assert.Equal(Convert.ToHexString(second), buffer.Rows[1].Hex);
    }

    [Fact]
    public void EventByEventReplayMatchesBatchReception()
    {
        var events = new[]
        {
            CreateEvent(1, [0xE6]),
            CreateEvent(2, "温度"u8[1..].ToArray()),
            CreateEvent(3, "=25.6\r"u8.ToArray()),
            CreateEvent(4, "\n湿度=40%"u8.ToArray()),
            CreateEvent(5, "read\r\n"u8.ToArray(), SerialDirection.Transmit),
            CreateEvent(6, "完成"u8.ToArray()),
        };
        var live = CreateBuffer(Encoding.UTF8);
        var replay = CreateBuffer(Encoding.UTF8);
        live.Append(events);
        foreach (var item in events)
        {
            replay.Append([item]);
        }

        Assert.Equal(live.Rows.Select(static row => row.Display), replay.Rows.Select(static row => row.Display));
        Assert.Equal(live.Rows.Select(static row => row.Hex), replay.Rows.Select(static row => row.Hex));
        Assert.Equal(live.Rows.Select(static row => row.Time), replay.Rows.Select(static row => row.Time));
        Assert.Equal(live.Rows.Select(static row => row.IsReceive), replay.Rows.Select(static row => row.IsReceive));
    }

    [Fact]
    public void SwitchingTextAndHexRebuildsFromUnmodifiedEvents()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        buffer.SetPresentation(Encoding.UTF8, false, true, 0);
        var bytes = "温度\r\n湿度"u8.ToArray();
        var events = new[] { CreateEvent(1, bytes[..1]), CreateEvent(2, bytes[1..5]), CreateEvent(3, bytes[5..]) };
        buffer.Append(events);

        Assert.Equal(events.Select(static item => Convert.ToHexString(item.Data)), buffer.Rows.Select(static row => row.Hex));
        buffer.SetPresentation(Encoding.UTF8, true, true);
        Assert.Equal(["温度", "湿度"], buffer.Rows.Select(static row => row.Display));
        buffer.SetPresentation(Encoding.UTF8, false, false, 0);

        Assert.Equal(events.Select(static item => Convert.ToHexString(item.Data)), buffer.Rows.Select(static row => row.Hex));
        Assert.All(buffer.Rows, static row => Assert.False(row.ShowTimestamp));
        Assert.Equal(bytes, events.SelectMany(static item => item.Data));
        buffer.SetPresentation(Encoding.UTF8, true, true);
        buffer.Append([CreateEvent(4, "=40%"u8.ToArray())]);
        Assert.Equal(["温度", "湿度=40%"], buffer.Rows.Select(static row => row.Display));
    }

    [Fact]
    public void TimestampChangesPreserveTheIncompleteLine()
    {
        var buffer = CreateBuffer(Encoding.UTF8);
        buffer.Append([CreateEvent(1, "A"u8.ToArray())]);
        var row = Assert.Single(buffer.Rows);
        buffer.SetPresentation(Encoding.UTF8, true, false);
        buffer.Append([CreateEvent(2, "B"u8.ToArray())]);

        Assert.Same(row, Assert.Single(buffer.Rows));
        Assert.Equal("AB", row.Display);
        Assert.False(row.ShowTimestamp);
    }

    [Fact]
    public void ClearResetsBothLineAndDecoderState()
    {
        var buffer = CreateBuffer(Encoding.UTF8);
        buffer.Append([CreateEvent(1, "old"u8.ToArray()), CreateEvent(2, [0xE6, 0xB8])]);
        buffer.Clear();
        buffer.Append([CreateEvent(3, "new\r\n"u8.ToArray())]);

        Assert.Equal("new", Assert.Single(buffer.Rows).Display);
        Assert.Equal(Convert.ToHexString("new\r\n"u8), buffer.Rows[0].Hex);
    }

    [Fact]
    public void TextHistoryRespectsTheRawEventCapacity()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8, 3);
        buffer.SetPresentation(Encoding.UTF8, true, true);
        for (var index = 0; index < 4; index++)
        {
            buffer.Append([CreateEvent(index + 1, [(byte)('A' + index)])]);
        }

        Assert.Equal("BCD", Assert.Single(buffer.Rows).Display);
        buffer.SetPresentation(Encoding.UTF8, false, true, 0);
        Assert.Equal(["42", "43", "44"], buffer.Rows.Select(static row => row.Hex));
    }

    private static TrafficBuffer CreateBuffer(Encoding encoding)
    {
        var buffer = new TrafficBuffer(encoding);
        buffer.SetPresentation(encoding, true, true);
        return buffer;
    }

    private static SerialTrafficEvent CreateEvent(long sequence, byte[] data, SerialDirection direction = SerialDirection.Receive, string source = "serial") =>
        new(sequence, DateTimeOffset.UnixEpoch.AddMilliseconds(sequence), sequence, ConnectionId, direction, data, source);
}
