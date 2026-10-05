using System.Text;
using System.Text.Json;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.WinUI;

namespace SerialWorkbench.Tests;

public sealed class HexAggregationTests
{
    private static readonly Guid ConnectionId = Guid.Parse("3bd4a880-a4d6-4976-9b91-3e81f79f8857");
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 5, 9, 50, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitEchoesFromDeviceLogsFormOneReceiveRow(bool replay)
    {
        var payload = "测试"u8.ToArray();
        var events = new[]
        {
            CreateEvent(1, 498, payload, SerialDirection.Transmit, "winui.send"),
            CreateEvent(2, 498, payload[..1]),
            CreateEvent(3, 499, payload[1..]),
            CreateEvent(4, 4598, payload, SerialDirection.Transmit, "winui.send"),
            CreateEvent(5, 4600, payload[..1]),
            CreateEvent(6, 4600, payload[1..]),
            CreateEvent(7, 5977, payload, SerialDirection.Transmit, "winui.send"),
            CreateEvent(8, 5978, payload[..1]),
            CreateEvent(9, 5978, payload[1..]),
        };
        var buffer = new TrafficBuffer(Encoding.UTF8);
        if (replay)
        {
            foreach (var item in events)
            {
                buffer.Append([item]);
            }
        }
        else
        {
            buffer.Append(events);
        }

        Assert.Equal(6, buffer.Rows.Count);
        Assert.All(buffer.Rows, static row => Assert.Equal("E6 B5 8B E8 AF 95", row.Display));
        Assert.Equal([false, true, false, true, false, true], buffer.Rows.Select(static row => row.IsReceive));
        Assert.Equal([1, 2, 4, 5, 7, 8], buffer.Rows.Select(static row => row.Identity.Sequence));
        Assert.Equal("测试", TrafficCopyFormatter.Format([buffer.Rows[1]], TrafficCopyFormat.Text));
        Assert.Equal(events.SelectMany(static item => item.Data), buffer.Rows.SelectMany(static row => row.GetData().ToArray()));
    }

    [Fact]
    public void ReceiveGapUsesEventTimesAndCanRebuildTheSameHistory()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        buffer.Append([CreateEvent(1, 0, [0x41]), CreateEvent(2, 10, [0x42]), CreateEvent(3, 21, [0x43])]);

        Assert.Equal(["4142", "43"], buffer.Rows.Select(static row => row.Hex));
        buffer.SetPresentation(Encoding.UTF8, false, true, 20);
        Assert.Equal("414243", Assert.Single(buffer.Rows).Hex);
        buffer.SetPresentation(Encoding.UTF8, false, true, 0);
        Assert.Equal(["41", "42", "43"], buffer.Rows.Select(static row => row.Hex));
        buffer.SetPresentation(Encoding.UTF8, false, true);
        Assert.Equal(["4142", "43"], buffer.Rows.Select(static row => row.Hex));
    }

    [Fact]
    public void TransmitOperationsKeepTheirOwnRowsAndSeparateReceiveGroups()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        buffer.Append([
            CreateEvent(1, 0, [0x41]),
            CreateEvent(2, 1, [0x42], SerialDirection.Transmit),
            CreateEvent(3, 2, [0x43], SerialDirection.Transmit),
            CreateEvent(4, 3, [0x44]),
            CreateEvent(5, 4, [0x45]),
        ]);

        Assert.Equal(["41", "42", "43", "4445"], buffer.Rows.Select(static row => row.Hex));
    }

    [Fact]
    public void ReceiveGroupsKeepConnectionsAndSourcesSeparate()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        var otherConnection = Guid.Parse("83574d97-301a-48b2-9269-1c1e4d76354f");
        buffer.Append([
            CreateEvent(1, 0, [0x41]),
            CreateEvent(2, 1, [0x42], source: "other"),
            CreateEvent(3, 2, [0x43]) with { ConnectionId = otherConnection },
            CreateEvent(4, 3, [0x44]),
        ]);

        Assert.Equal(["41", "42", "43", "44"], buffer.Rows.Select(static row => row.Hex));
    }

    [Fact]
    public void AppendingToReceiveRowsPreservesSelectionAndFrozenCopies()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        buffer.Append([CreateEvent(1, 0, [0x41])]);
        var row = Assert.Single(buffer.Rows);
        var selected = new HashSet<TrafficRowIdentity> { row.Identity };
        var snapshot = row.Snapshot();
        buffer.Append([CreateEvent(2, 1, [0x42])]);

        Assert.Same(row, Assert.Single(TrafficSelection.GetSelectedRows(buffer.Rows, selected)));
        Assert.Equal("4142", row.Hex);
        Assert.Equal("41", snapshot.Hex);
        buffer.Clear();
        buffer.Append([CreateEvent(3, 2, [0x43])]);
        Assert.Equal("43", Assert.Single(buffer.Rows).Hex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public void ReceiveGapRejectsValuesOutsideTheSupportedRange(int gap)
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.SetPresentation(Encoding.UTF8, false, true, gap));
    }

    [Fact]
    public void ReceiveGapPreferencesUseTheDefaultAndPreserveConfiguredValues()
    {
        const string original = """{"portName":null,"baudRate":115200,"dataBits":8,"parity":0,"stopBits":0,"handshake":0,"encodingName":"utf-8","dtrEnable":false,"rtsEnable":false,"monitorFormatIndex":0,"sendFormatIndex":0,"sendLineEndingIndex":0,"sendChecksumIndex":0,"loopIntervalMilliseconds":1000,"plotModeIndex":0,"plotFrameLength":8,"plotSampleTypeIndex":0}""";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var preference = Assert.IsType<SerialPreference>(JsonSerializer.Deserialize<SerialPreference>(original, options));
        Assert.Equal(10, preference.HexReceiveGapMilliseconds);
        var configured = preference with { HexReceiveGapMilliseconds = 25 };
        var restored = JsonSerializer.Deserialize<SerialPreference>(JsonSerializer.Serialize(configured, options), options);
        Assert.Equal(configured, restored);
    }

    private static SerialTrafficEvent CreateEvent(long sequence, int milliseconds, byte[] data, SerialDirection direction = SerialDirection.Receive, string source = "serial") =>
        new(sequence, Start.AddMilliseconds(milliseconds), sequence, ConnectionId, direction, data, source);
}
