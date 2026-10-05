using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Serial.Windows;

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
    public void EventJournalFiltersDirectionAndSource()
    {
        var journal = new EventJournal();
        var connectionId = Guid.NewGuid();
        journal.Append(connectionId, SerialDirection.Receive, [1], "serial");
        journal.Append(connectionId, SerialDirection.Transmit, [2], "cli.send");

        var events = journal.ReadAfter(0, 10, connectionId, SerialDirection.Transmit, "cli");

        var item = Assert.Single(events);
        Assert.Equal(SerialDirection.Transmit, item.Direction);
        Assert.Equal("cli.send", item.Source);
    }

    [Fact]
    public async Task EventCursorReportsRetainedRangeAndAdvancesPastFilteredEvents()
    {
        var journal = new EventJournal(2);
        var connectionId = Guid.NewGuid();
        journal.Append(connectionId, SerialDirection.Receive, [1], "serial");
        journal.Append(connectionId, SerialDirection.Transmit, [2], "send");
        journal.Append(connectionId, SerialDirection.Receive, [3], "serial");

        var batch = await journal.ReadBatchAsync(0, 10, connectionId, SerialDirection.Transmit, null, journal.StreamId, 0, TestContext.Current.CancellationToken);

        Assert.Equal(new EventGap(1, 1), batch.Gap);
        Assert.Equal(2, batch.EarliestSequence);
        Assert.Equal(3, batch.NextSequence);
        Assert.Equal(2, Assert.Single(batch.Events).Sequence);
        var reset = await journal.ReadBatchAsync(3, 10, null, null, null, Guid.NewGuid(), 0, TestContext.Current.CancellationToken);
        Assert.True(reset.ResetRequired);
        Assert.Equal([2, 3], reset.Events.Select(static item => item.Sequence));
    }

    [Fact]
    public async Task ConcurrentEventsKeepSequenceOrderAndWakeWaitingReaders()
    {
        var journal = new EventJournal();
        var waiting = journal.ReadBatchAsync(0, 1000, null, null, null, journal.StreamId, 30_000, TestContext.Current.CancellationToken);
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(index => Task.Run(() =>
            journal.Append(Guid.Empty, SerialDirection.Receive, [(byte)index], "test"), TestContext.Current.CancellationToken)));

        Assert.NotEmpty((await waiting).Events);
        Assert.Equal(Enumerable.Range(1, 1000).Select(static value => (long)value), journal.ReadAfter(0, 1000).Select(static item => item.Sequence));
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
        var snapshot = new ConnectionSnapshot(Guid.NewGuid(), new SerialConnectionOptions("COM1"), ConnectionState.Open, 0, 0, 0, 0, 0, null, null, status, 7, 12.5, 8.25);

        Assert.True(snapshot.ControlLines?.DtrEnable);
        Assert.True(snapshot.ControlLines?.CtsHolding);
        Assert.True(snapshot.ControlLines?.CarrierDetect);
        Assert.False(snapshot.ControlLines?.DsrHolding);
        Assert.Null(snapshot.ControlLines?.RingIndicator);
        Assert.Equal(7, snapshot.ObserverDroppedBlocks);
        Assert.Equal(12.5, snapshot.ReceivedBytesPerSecond);
        Assert.Equal(8.25, snapshot.TransmittedBytesPerSecond);
    }

    [Fact]
    public void SerialOptionsCarryRs485DirectionTiming()
    {
        var options = new SerialConnectionOptions("COM1", Rs485Mode: true, RtsBeforeSendMilliseconds: 5, RtsAfterSendMilliseconds: 8);

        Assert.True(options.Rs485Mode);
        Assert.Equal(5, options.RtsBeforeSendMilliseconds);
        Assert.Equal(8, options.RtsAfterSendMilliseconds);
    }

    [Theory]
    [InlineData(5, SerialStopBits.Two)]
    [InlineData(8, SerialStopBits.OnePointFive)]
    public async Task SerialConnectionManagerRejectsInvalidStopBits(int dataBits, SerialStopBits stopBits)
    {
        await using var manager = new SerialConnectionManager(new EventJournal(), new WriteLeaseManager(), (_, _) => ValueTask.CompletedTask);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.OpenAsync(new SerialConnectionOptions("COM1", DataBits: dataBits, StopBits: stopBits), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void HostStatusCarriesEventPersistenceMetrics()
    {
        var status = new HostStatusDto("1.0", "app", "data", null, 1, [], null, 42, 3, 18.5);

        Assert.Equal(42, status.LatestEventSequence);
        Assert.Equal(3, status.PendingSessionEvents);
        Assert.Equal(18.5, status.SessionEventPersistenceEventsPerSecond);
    }

    [Fact]
    public async Task HostColdStartAcceptsConcurrentClients()
    {
        var clients = Enumerable.Range(0, 16)
            .Select(_ => HostEndpoint.ConnectAsync(AppContext.BaseDirectory, true, TestContext.Current.CancellationToken))
            .ToArray();

        try
        {
            var connectedClients = await Task.WhenAll(clients);
            Assert.All(clients, static client => Assert.True(client.IsCompletedSuccessfully));
            var handshake = await connectedClients[0].HandshakeAsync(
                new HandshakeRequest(RpcProtocol.MajorVersion, RpcProtocol.MinorVersion, "test", "zh-CN"), TestContext.Current.CancellationToken);
            Assert.True(handshake.Accepted);
            var result = await EntryPointTests.InvokeAsync("SW_CLI", "--agent", "host", "status", "--app-root", AppContext.BaseDirectory);
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Error);
            using var document = System.Text.Json.JsonDocument.Parse(result.Output);
            Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("result").GetProperty("connections").GetArrayLength());
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

    [Fact]
    public async Task KillStopsTheHostNormallyAndDoesNotRestartIt()
    {
        var started = await EntryPointTests.InvokeAsync("SW_CLI", "--agent", "host", "status", "--app-root", AppContext.BaseDirectory);
        Assert.Equal(0, started.ExitCode);
        var result = await EntryPointTests.InvokeAsync("SW_CLI", "--agent", "kill", "--app-root", AppContext.BaseDirectory);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var document = System.Text.Json.JsonDocument.Parse(result.Output);
        var receipt = document.RootElement.GetProperty("result");
        Assert.True(receipt.GetProperty("hostStoppedGracefully").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, receipt.GetProperty("gracefulShutdownError").ValueKind);
        var host = Assert.Single(receipt.GetProperty("processes").EnumerateArray());
        Assert.Equal("SW_HOST", host.GetProperty("name").GetString());
        Assert.True(host.GetProperty("stopped").GetBoolean());
        Assert.False(host.GetProperty("forced").GetBoolean());
        var repeated = await EntryPointTests.InvokeAsync("SW_CLI", "--agent", "kill", "--app-root", AppContext.BaseDirectory);
        Assert.Equal(0, repeated.ExitCode);
        using var repeatedDocument = System.Text.Json.JsonDocument.Parse(repeated.Output);
        Assert.Equal(0, repeatedDocument.RootElement.GetProperty("result").GetProperty("processes").GetArrayLength());
    }
}
