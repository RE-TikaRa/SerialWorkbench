using Microsoft.Data.Sqlite;
using SerialWorkbench.Domain;
using SerialWorkbench.Host;
using SerialWorkbench.Sessions;
using SerialWorkbench.Storage;

namespace SerialWorkbench.Tests;

public sealed class StorageAndSessionTests
{
    [Fact]
    public void WorkspaceMovesSessionsWithoutMovingGlobalData()
    {
        var applicationRoot = CreateArtifactDirectory("paths-app");
        var workspaceRoot = CreateArtifactDirectory("paths-workspace");
        var paths = new ApplicationPaths(applicationRoot, workspaceRoot);

        paths.EnsureWritable();

        Assert.Equal(Path.Combine(applicationRoot, "data"), paths.DataRoot);
        Assert.Equal(Path.Combine(workspaceRoot, "sessions"), paths.SessionsRoot);
        Assert.True(Directory.Exists(paths.DataRoot));
        Assert.True(Directory.Exists(paths.SessionsRoot));
    }

    [Fact]
    public async Task SessionFilePreservesEventOrderAndRawBytes()
    {
        var applicationRoot = CreateArtifactDirectory("session-app");
        var paths = new ApplicationPaths(applicationRoot);
        paths.EnsureWritable();
        var connectionId = Guid.NewGuid();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var store = new SessionStore(paths);

        await store.AppendAsync(CreateEvent(1, connectionId, SerialDirection.Transmit, [0x10, 0x20]), cancellationToken);
        await store.AppendAsync(CreateEvent(2, connectionId, SerialDirection.Receive, [0x30, 0x40, 0x50]), cancellationToken);

        Assert.Equal(2, store.ActiveSession?.EventCount);
        Assert.Equal(5, store.ActiveSession?.RawByteCount);
        var sessionPath = Assert.IsType<string>(store.ActiveSession?.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(store.ActiveSession!.Id, cancellationToken));
        await store.CompleteAsync(cancellationToken);

        {
            await using var connection = new SqliteConnection($"Data Source={sessionPath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sequence, direction, data FROM events ORDER BY sequence;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("Transmit", reader.GetString(1));
            Assert.Equal([0x10, 0x20], (byte[])reader[2]);
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal("Receive", reader.GetString(1));
            Assert.Equal([0x30, 0x40, 0x50], (byte[])reader[2]);
            Assert.False(await reader.ReadAsync(cancellationToken));
        }

        var sessions = await store.ListAsync(cancellationToken);
        var session = Assert.Single(sessions);
        Assert.Equal(2, session.EventCount);
        Assert.Equal(5, session.RawByteCount);
        var events = await store.ReadEventsAsync(session.Id, 1000, cancellationToken);
        Assert.Equal([1, 2], events.Select(item => item.Sequence));
        var allEvents = await store.ReadAllEventsAsync(session.Id, cancellationToken);
        Assert.Equal([1, 2], allEvents.Select(item => item.Sequence));
        await store.DeleteAsync(session.Id, cancellationToken);
        Assert.Empty(await store.ListAsync(cancellationToken));
    }

    [Fact]
    public async Task SessionCsvExportPreservesUtcSourceAndRawBytes()
    {
        var applicationRoot = CreateArtifactDirectory("session-export-app");
        var paths = new ApplicationPaths(applicationRoot);
        paths.EnsureWritable();
        var connectionId = Guid.NewGuid();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var store = new SessionStore(paths);

        await store.AppendAsync(
            new SerialTrafficEvent(
                1,
                new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero),
                10,
                connectionId,
                SerialDirection.Transmit,
                [0x10, 0x20],
                "cli.send"),
            cancellationToken);
        await store.AppendAsync(
            new SerialTrafficEvent(
                2,
                new DateTimeOffset(2026, 8, 26, 1, 2, 4, TimeSpan.Zero),
                20,
                connectionId,
                SerialDirection.Receive,
                [0x00, 0xFF],
                "设备,通道\"A\""),
            cancellationToken);

        var sessionId = Assert.IsType<Guid>(store.ActiveSession?.Id);
        var csv = await store.ExportCsvAsync(sessionId, cancellationToken);

        Assert.StartsWith("utc,direction,source,hex,byte_count\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("2026-08-26T01:02:03.0000000+00:00,Transmit,cli.send,1020,2\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("2026-08-26T01:02:04.0000000+00:00,Receive,\"设备,通道\"\"A\"\"\",00FF,2\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySessionCsvExportContainsHeaderOnly()
    {
        var applicationRoot = CreateArtifactDirectory("empty-session-export-app");
        var paths = new ApplicationPaths(applicationRoot);
        paths.EnsureWritable();
        var sessionId = Guid.NewGuid();
        var sessionPath = Path.Combine(paths.SessionsRoot, "empty.swbsession");
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var connection = new SqliteConnection($"Data Source={sessionPath};Mode=ReadWriteCreate;Pooling=False"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE session(
                    id TEXT PRIMARY KEY,
                    started_utc TEXT NOT NULL,
                    ended_utc TEXT NULL,
                    event_count INTEGER NOT NULL DEFAULT 0,
                    raw_byte_count INTEGER NOT NULL DEFAULT 0,
                    schema_version INTEGER NOT NULL
                );
                CREATE TABLE events(
                    sequence INTEGER PRIMARY KEY,
                    utc TEXT NOT NULL,
                    monotonic_ticks INTEGER NOT NULL,
                    connection_id TEXT NOT NULL,
                    direction TEXT NOT NULL,
                    source TEXT NOT NULL,
                    message TEXT NULL,
                    data BLOB NOT NULL
                );
                INSERT INTO session(id, started_utc, schema_version)
                VALUES ($id, $startedUtc, 1);
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$startedUtc", "2026-08-26T01:02:03.0000000+00:00");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var store = new SessionStore(paths);
        var csv = await store.ExportCsvAsync(sessionId, cancellationToken);

        Assert.Equal("utc,direction,source,hex,byte_count\r\n", csv);
    }

    [Fact]
    public async Task HostSwitchesSessionStorageToTheSelectedWorkspace()
    {
        var applicationRoot = CreateArtifactDirectory("host-app");
        var workspaceRoot = CreateArtifactDirectory("host-workspace");
        var paths = new ApplicationPaths(applicationRoot);
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);

        await runtime.SetWorkspaceAsync(workspaceRoot, TestContext.Current.CancellationToken);

        Assert.Equal(workspaceRoot, runtime.Paths.WorkspaceRoot);
        Assert.Equal(Path.Combine(workspaceRoot, "sessions"), runtime.Paths.SessionsRoot);
    }

    private static SerialTrafficEvent CreateEvent(long sequence, Guid connectionId, SerialDirection direction, byte[] data) =>
        new(sequence, DateTimeOffset.UtcNow, sequence * 10, connectionId, direction, data, "test");

    private static string CreateArtifactDirectory(string name)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "artifacts", $"{name}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(path);
        return path;
    }
}
