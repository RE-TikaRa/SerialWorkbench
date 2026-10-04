using System.Text;
using SerialWorkbench.Domain;
using SerialWorkbench.WinUI;

namespace SerialWorkbench.Tests;

public sealed class TrafficCopyTests
{
    private static readonly Guid ConnectionId = Guid.Parse("9015f3f8-b128-4075-bb07-55aa53a53d1f");

    static TrafficCopyTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void CurrentDisplayCopiesOnlySelectedRowsInListOrder()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        buffer.SetPresentation(Encoding.UTF8, true, true);
        buffer.Append([CreateEvent(1, "温度\r\n湿度\r\n状态\r\n"u8.ToArray())]);
        var selected = new HashSet<TrafficRowIdentity> { buffer.Rows[2].Identity, buffer.Rows[0].Identity };
        var rows = TrafficSelection.GetSelectedRows(buffer.Rows, selected);

        Assert.Equal($"温度{Environment.NewLine}状态", TrafficCopyFormatter.Format(rows, TrafficCopyFormat.CurrentDisplay));
    }

    [Theory]
    [InlineData("utf-8", "温度🙂\r\n")]
    [InlineData("gbk", "温度\r\n")]
    [InlineData("utf-16", "温度🙂\r\n")]
    public void TextCopyDecodesAcrossSelectedRawReadBlocks(string encodingName, string text)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var buffer = new TrafficBuffer(encoding);
        buffer.SetPresentation(encoding, false, true, 0);
        var bytes = encoding.GetBytes(text);
        buffer.Append(bytes.Select((value, index) => CreateEvent(index + 1, [value])).ToArray());

        Assert.Equal(text, TrafficCopyFormatter.Format(buffer.Rows, TrafficCopyFormat.Text));
        Assert.Equal(bytes, buffer.Rows.SelectMany(static row => row.Data.ToArray()));
    }

    [Fact]
    public void HexFormatsUseExactSelectedBytesAndPreserveLineEndings()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        buffer.SetPresentation(Encoding.UTF8, true, true);
        buffer.Append([CreateEvent(1, "A\r\nB\n"u8.ToArray())]);

        Assert.Equal($"41 0D 0A{Environment.NewLine}42 0A", TrafficCopyFormatter.Format(buffer.Rows, TrafficCopyFormat.Hex));
        Assert.Equal("410D0A420A", TrafficCopyFormatter.Format(buffer.Rows, TrafficCopyFormat.CompactHex));
        Assert.Equal("A\r\nB\n", TrafficCopyFormatter.Format(buffer.Rows, TrafficCopyFormat.Text));
        Assert.Equal("A\r\n", TrafficCopyFormatter.Format([buffer.Rows[0]], TrafficCopyFormat.Text));
    }

    [Fact]
    public void TextCopySeparatesConnectionDirectionAndSourceChanges()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        var otherConnection = Guid.Parse("98f21314-5661-4d7d-9a4a-49c4c699d907");
        buffer.Append([
            CreateEvent(1, "A"u8.ToArray()),
            CreateEvent(2, "B"u8.ToArray(), SerialDirection.Transmit),
            CreateEvent(3, "C"u8.ToArray(), source: "other"),
            CreateEvent(4, "D"u8.ToArray()) with { ConnectionId = otherConnection },
        ]);

        Assert.Equal(string.Join(Environment.NewLine, "A", "B", "C", "D"), TrafficCopyFormatter.Format(buffer.Rows, TrafficCopyFormat.Text));
    }

    [Fact]
    public void TextCopyPreservesExistingLineBreaksBetweenDirections()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        buffer.Append([
            CreateEvent(1, "A\r\n"u8.ToArray()),
            CreateEvent(2, "B"u8.ToArray(), SerialDirection.Transmit),
            CreateEvent(3, "\nC"u8.ToArray()),
        ]);

        Assert.Equal("A\r\nB\nC", TrafficCopyFormatter.Format(buffer.Rows, TrafficCopyFormat.Text));
    }

    [Fact]
    public void LogCopyIncludesMetadataEvenWhenTimestampsAreHidden()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        buffer.SetPresentation(Encoding.UTF8, true, false);
        buffer.Append([
            CreateEvent(1, "ready\r\n"u8.ToArray()),
            CreateEvent(2, "read\r\n"u8.ToArray(), SerialDirection.Transmit, "winui.send"),
        ]);

        Assert.Equal($"{buffer.Rows[0].Time} RX [serial] ready{Environment.NewLine}{buffer.Rows[1].Time} TX [winui.send] read",
            TrafficCopyFormatter.Format(buffer.Rows, TrafficCopyFormat.Log));
        Assert.All(buffer.Rows, static row => Assert.False(row.ShowTimestamp));
    }

    [Fact]
    public void CopyingFrozenRowsKeepsTheirCapturedContentAndEncoding()
    {
        var encoding = Encoding.GetEncoding("gbk");
        var buffer = new TrafficBuffer(encoding);
        buffer.SetPresentation(encoding, true, true);
        buffer.Append([CreateEvent(1, encoding.GetBytes("温度="))]);
        var snapshot = buffer.Rows[0].Snapshot();
        buffer.Append([CreateEvent(2, encoding.GetBytes("25°C"))]);

        Assert.Equal("温度=", TrafficCopyFormatter.Format([snapshot], TrafficCopyFormat.CurrentDisplay));
        Assert.Equal("温度=", TrafficCopyFormatter.Format([snapshot], TrafficCopyFormat.Text));
        Assert.Equal(Convert.ToHexString(encoding.GetBytes("温度=")), TrafficCopyFormatter.Format([snapshot], TrafficCopyFormat.CompactHex));
    }

    [Theory]
    [InlineData(TrafficCopyFormat.CurrentDisplay)]
    [InlineData(TrafficCopyFormat.Text)]
    [InlineData(TrafficCopyFormat.Hex)]
    [InlineData(TrafficCopyFormat.CompactHex)]
    [InlineData(TrafficCopyFormat.Log)]
    public void EmptySelectionsHaveNoCopyPayload(TrafficCopyFormat format) =>
        Assert.Equal("", TrafficCopyFormatter.Format([], format));

    private static SerialTrafficEvent CreateEvent(long sequence, byte[] data, SerialDirection direction = SerialDirection.Receive, string source = "serial") =>
        new(sequence, DateTimeOffset.UnixEpoch.AddMilliseconds(sequence), sequence, ConnectionId, direction, data, source);
}
