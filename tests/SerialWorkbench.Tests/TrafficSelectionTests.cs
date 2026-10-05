using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.WinUI;

namespace SerialWorkbench.Tests;

public sealed class TrafficSelectionTests
{
    private static readonly Guid ConnectionId = Guid.Parse("51247409-5c0b-488a-908b-5cdbf84bca9b");

    [Fact]
    public void AppendedRowsKeepExistingItemsWithoutResettingTheList()
    {
        var buffer = CreateRawBuffer();
        buffer.Append([CreateEvent(1, "A"), CreateEvent(2, "B")]);
        var visible = new ObservableCollection<TrafficRow>(buffer.Rows);
        var selected = visible[1];
        var changes = new List<NotifyCollectionChangedAction>();
        visible.CollectionChanged += (_, args) => changes.Add(args.Action);
        buffer.Append([CreateEvent(3, "C")]);
        TrafficSelection.UpdateRows(visible, buffer.Rows);

        Assert.Same(selected, visible[1]);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        Assert.Same(selected, Assert.Single(TrafficSelection.GetSelectedRows(visible, new HashSet<TrafficRowIdentity> { selected.Identity })));
    }

    [Fact]
    public void FilteringPreservesTheOrderOfRemainingSelectedRows()
    {
        var buffer = CreateRawBuffer();
        buffer.Append([CreateEvent(1, "A"), CreateEvent(2, "B"), CreateEvent(3, "C")]);
        var visible = new ObservableCollection<TrafficRow>(buffer.Rows);
        var selected = new HashSet<TrafficRowIdentity> { visible[2].Identity, visible[0].Identity };
        TrafficSelection.UpdateRows(visible, [buffer.Rows[0], buffer.Rows[2]]);

        Assert.Equal(["41", "43"], TrafficSelection.GetSelectedRows(visible, selected).Select(static row => row.Hex));
        TrafficSelection.UpdateRows(visible, [buffer.Rows[2]]);
        Assert.Equal("43", Assert.Single(TrafficSelection.GetSelectedRows(visible, selected)).Hex);
    }

    [Fact]
    public void ReorderingRowsPreservesItemsAndUsesTheDisplayedCopyOrder()
    {
        var buffer = CreateRawBuffer();
        buffer.Append([CreateEvent(1, "A"), CreateEvent(2, "B"), CreateEvent(3, "C")]);
        var visible = new ObservableCollection<TrafficRow>(buffer.Rows);
        var selected = new HashSet<TrafficRowIdentity> { visible[0].Identity, visible[2].Identity };
        TrafficSelection.UpdateRows(visible, [buffer.Rows[2], buffer.Rows[0], buffer.Rows[1]]);

        Assert.Equal(3, visible.Count);
        Assert.Equal(["43", "41", "42"], visible.Select(static row => row.Hex));
        Assert.Equal(["43", "41"], TrafficSelection.GetSelectedRows(visible, selected).Select(static row => row.Hex));
    }

    [Fact]
    public void SelectionIdentitiesSurviveHistoryRebuildsAndSnapshots()
    {
        var buffer = CreateRawBuffer(2);
        buffer.Append([CreateEvent(1, "A"), CreateEvent(2, "B")]);
        var original = buffer.Rows[1];
        var selected = new HashSet<TrafficRowIdentity> { original.Identity };
        buffer.Append([CreateEvent(3, "C")]);
        var restored = Assert.Single(TrafficSelection.GetSelectedRows(buffer.Rows, selected));

        Assert.NotSame(original, restored);
        Assert.Equal(original.Identity, restored.Identity);
        Assert.Equal("42", restored.Hex);
        var snapshot = restored.Snapshot();
        Assert.Same(snapshot, Assert.Single(TrafficSelection.GetSelectedRows([snapshot], selected)));
    }

    [Fact]
    public void TextLinesFromOneEventHaveDistinctStableIdentities()
    {
        var buffer = new TrafficBuffer(Encoding.UTF8);
        buffer.SetPresentation(Encoding.UTF8, true, true);
        buffer.Append([CreateEvent(1, "温\r\n湿\r\n")]);
        var selected = new HashSet<TrafficRowIdentity> { buffer.Rows[1].Identity };
        buffer.SetPresentation(Encoding.UTF8, true, false);

        Assert.Equal("湿", Assert.Single(TrafficSelection.GetSelectedRows(buffer.Rows, selected)).Display);
        Assert.Equal(2, buffer.Rows.Select(static row => row.Identity).Distinct().Count());
        Assert.Equal(5, buffer.Rows[1].Identity.ByteOffset);
    }

    private static SerialTrafficEvent CreateEvent(long sequence, string text) =>
        new(sequence, DateTimeOffset.UnixEpoch.AddMilliseconds(sequence), sequence, ConnectionId, SerialDirection.Receive, Encoding.UTF8.GetBytes(text), "serial");

    private static TrafficBuffer CreateRawBuffer(int capacity = 20_000)
    {
        var buffer = new TrafficBuffer(Encoding.UTF8, capacity);
        buffer.SetPresentation(Encoding.UTF8, false, true, 0);
        return buffer;
    }
}
