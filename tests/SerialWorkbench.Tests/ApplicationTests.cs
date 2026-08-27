using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;

namespace SerialWorkbench.Tests;

public sealed class ApplicationTests
{
    [Fact]
    public void WriteLeaseIsExclusiveAndReleasesExactlyOnce()
    {
        var manager = new WriteLeaseManager();
        var connectionId = Guid.NewGuid();
        var lease = manager.Acquire(connectionId, "test");

        Assert.Equal("test", manager.GetOwner(connectionId));
        Assert.Throws<InvalidOperationException>(() => manager.Acquire(connectionId, "other"));

        lease.Dispose();
        lease.Dispose();

        Assert.Null(manager.GetOwner(connectionId));
        using var next = manager.Acquire(connectionId, "next");
        Assert.Equal("next", next.Owner);
    }

    [Fact]
    public void EventJournalRetainsCapacityAndCopiesInput()
    {
        var journal = new EventJournal(2);
        var connectionId = Guid.NewGuid();
        var source = new byte[] { 1, 2 };
        journal.Append(connectionId, SerialDirection.Receive, source, "serial");
        source[0] = 9;
        journal.Append(connectionId, SerialDirection.Transmit, [3], "test");
        journal.Append(connectionId, SerialDirection.Receive, [4], "serial");

        var events = journal.ReadAfter(0, 10);

        Assert.Equal(2, events.Count);
        Assert.Equal(2, events[0].Sequence);
        Assert.Equal(3, events[1].Sequence);
        Assert.Equal([3], events[0].Data);
    }

    [Fact]
    public void EventJournalFiltersBeforeApplyingMaximumCount()
    {
        var journal = new EventJournal();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        journal.Append(first, SerialDirection.Receive, [1], "first");
        journal.Append(second, SerialDirection.Receive, [2], "second");
        journal.Append(second, SerialDirection.Receive, [3], "second");
        journal.Append(first, SerialDirection.Receive, [4], "first");

        var events = journal.ReadAfter(0, 1, first);

        var item = Assert.Single(events);
        Assert.Equal(1, item.Sequence);
        Assert.Equal(first, item.ConnectionId);
    }

    [Fact]
    public void PipeNameIsStableForEquivalentApplicationPaths()
    {
        var first = HostEndpoint.GetPipeName(@"C:\Apps\SerialWorkbench");
        var second = HostEndpoint.GetPipeName(@"c:\apps\serialworkbench\");

        Assert.Equal(first, second);
        Assert.StartsWith("SerialWorkbench-", first, StringComparison.Ordinal);
    }
}
