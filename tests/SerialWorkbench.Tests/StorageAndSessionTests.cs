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
        await store.DeleteAsync(session.Id, cancellationToken);
        Assert.Empty(await store.ListAsync(cancellationToken));
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
