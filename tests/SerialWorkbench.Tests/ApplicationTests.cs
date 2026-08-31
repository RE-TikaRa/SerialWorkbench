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

    [Fact]
    public void ConnectionRoleIsPartOfSerialOptions()
    {
        var defaultOptions = new SerialConnectionOptions("COM1");
        var controllerOptions = defaultOptions with
        {
            Role = SerialConnectionRole.Controller,
            DeviceInstanceId = "USB\\VID_1A86&PID_7523\\1",
        };

        Assert.Equal(SerialConnectionRole.Dut, defaultOptions.Role);
        Assert.Equal(SerialConnectionRole.Controller, controllerOptions.Role);
        Assert.Equal("USB\\VID_1A86&PID_7523\\1", controllerOptions.DeviceInstanceId);
    }

    [Fact]
    public void ConnectionSnapshotCarriesControlLineStatus()
    {
        var status = new SerialControlLineStatus(true, false, true, false, true, null);
        var snapshot = new ConnectionSnapshot(Guid.NewGuid(), new SerialConnectionOptions("COM1"), ConnectionState.Open, 0, 0, 0, 0, 0, null, null, status);

        Assert.True(snapshot.ControlLines?.DtrEnable);
        Assert.True(snapshot.ControlLines?.CtsHolding);
        Assert.True(snapshot.ControlLines?.CarrierDetect);
        Assert.False(snapshot.ControlLines?.DsrHolding);
        Assert.Null(snapshot.ControlLines?.RingIndicator);
    }

    [Fact]
    public void SerialOptionsCarryRs485DirectionTiming()
    {
        var options = new SerialConnectionOptions("COM1", Rs485Mode: true, RtsBeforeSendMilliseconds: 5, RtsAfterSendMilliseconds: 8);

        Assert.True(options.Rs485Mode);
        Assert.Equal(5, options.RtsBeforeSendMilliseconds);
        Assert.Equal(8, options.RtsAfterSendMilliseconds);
    }

    [Fact]
    public void HostStatusCarriesEventPersistenceMetrics()
    {
        var status = new HostStatusDto("1.0", "app", "data", null, 1, [], null, 42, 3);

        Assert.Equal(42, status.LatestEventSequence);
        Assert.Equal(3, status.PendingSessionEvents);
    }

    [Fact]
    public async Task HostColdStartAcceptsConcurrentClients()
    {
        var clients = Enumerable.Range(0, 16)
            .Select(_ => HostEndpoint.ConnectAsync(AppContext.BaseDirectory, true, TestContext.Current.CancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(clients);
            Assert.All(clients, static client => Assert.True(client.IsCompletedSuccessfully));
        }
        finally
        {
            var connected = clients
                .Where(static client => client.IsCompletedSuccessfully)
                .Select(static client => client.Result)
                .ToArray();
            if (connected.Length > 0)
            {
                await connected[0].StopHostAsync(CancellationToken.None);
            }

            foreach (var client in connected)
            {
                await client.DisposeAsync();
            }
        }
    }
}
