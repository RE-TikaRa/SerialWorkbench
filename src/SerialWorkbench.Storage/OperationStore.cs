using System.Text.Json;
using Microsoft.Data.Sqlite;
using SerialWorkbench.Domain;

namespace SerialWorkbench.Storage;

public sealed class OperationStore(ApplicationPaths paths) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SqliteConnection? connection;

    public async Task<IReadOnlyList<OperationSnapshot>> ReadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = database.CreateCommand();
            command.CommandText = "SELECT snapshot FROM operations ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<OperationSnapshot>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(JsonSerializer.Deserialize<OperationSnapshot>(reader.GetString(0))
                    ?? throw new InvalidDataException("Operation snapshot is empty."));
            }

            return results;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(OperationSnapshot snapshot, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = database.CreateCommand();
            command.CommandText = """
                INSERT INTO operations(id, request_id, snapshot) VALUES ($id, $requestId, $snapshot)
                ON CONFLICT(id) DO UPDATE SET snapshot = excluded.snapshot;
                """;
            command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
            command.Parameters.AddWithValue("$requestId", (object?)snapshot.Request.RequestId ?? DBNull.Value);
            command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        gate.Dispose();
    }

    private async Task<SqliteConnection> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection is not null)
        {
            return connection;
        }

        var next = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(paths.DataRoot, "operations.sqlite3"),
        }.ToString());
        try
        {
            await next.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = next.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS operations(
                    id TEXT PRIMARY KEY,
                    request_id TEXT UNIQUE,
                    snapshot TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            connection = next;
            return next;
        }
        catch
        {
            await next.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
