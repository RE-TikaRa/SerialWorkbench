using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SerialWorkbench.Domain;
using SerialWorkbench.Storage;

namespace SerialWorkbench.Sessions;

public sealed class SessionStore(ApplicationPaths paths) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SqliteConnection? connection;
    private SessionDescriptor? descriptor;
    private long eventCount;
    private long rawByteCount;

    public SessionDescriptor? ActiveSession => descriptor is null
        ? null
        : descriptor with { EventCount = eventCount, RawByteCount = rawByteCount };

    public async Task<IReadOnlyList<SessionDescriptor>> ListAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SerialTrafficEvent>> ReadEventsAsync(Guid sessionId, int maximumCount, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = (await ReadDescriptorsAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == sessionId)
                ?? throw new KeyNotFoundException($"Session {sessionId:D} does not exist.");
            await using var sessionConnection = CreateReadOnlyConnection(session.Path);
            await sessionConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = sessionConnection.CreateCommand();
            command.CommandText = """
                SELECT sequence, utc, monotonic_ticks, connection_id, direction, data, source, message
                FROM (
                    SELECT sequence, utc, monotonic_ticks, connection_id, direction, data, source, message
                    FROM events
                    ORDER BY sequence DESC
                    LIMIT $maximumCount
                )
                ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$maximumCount", maximumCount);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var events = new List<SerialTrafficEvent>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(new SerialTrafficEvent(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetInt64(2),
                    Guid.Parse(reader.GetString(3)),
                    Enum.Parse<SerialDirection>(reader.GetString(4)),
                    (byte[])reader[5],
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return events;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SerialTrafficEvent>> ReadAllEventsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = (await ReadDescriptorsAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == sessionId)
                ?? throw new KeyNotFoundException($"Session {sessionId:D} does not exist.");
            await using var sessionConnection = CreateReadOnlyConnection(session.Path);
            await sessionConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = sessionConnection.CreateCommand();
            command.CommandText = """
                SELECT sequence, utc, monotonic_ticks, connection_id, direction, data, source, message
                FROM events
                ORDER BY sequence;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var events = new List<SerialTrafficEvent>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(new SerialTrafficEvent(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetInt64(2),
                    Guid.Parse(reader.GetString(3)),
                    Enum.Parse<SerialDirection>(reader.GetString(4)),
                    (byte[])reader[5],
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return events;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> ExportCsvAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = (await ReadDescriptorsAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == sessionId)
                ?? throw new KeyNotFoundException($"Session {sessionId:D} does not exist.");
            await using var sessionConnection = CreateReadOnlyConnection(session.Path);
            await sessionConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = sessionConnection.CreateCommand();
            command.CommandText = """
                SELECT utc, direction, source, hex(data), length(data)
                FROM events
                ORDER BY sequence;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var csv = new StringBuilder();
            csv.AppendLine("utc,direction,source,hex,byte_count");
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                csv.Append(CsvField(reader.GetString(0))).Append(',')
                    .Append(CsvField(reader.GetString(1))).Append(',')
                    .Append(CsvField(reader.GetString(2))).Append(',')
                    .Append(reader.GetString(3)).Append(',')
                    .Append(reader.GetInt64(4))
                    .AppendLine();
            }

            return csv.ToString();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection is not null && descriptor?.Id == sessionId)
            {
                throw new InvalidOperationException("The active session cannot be deleted.");
            }

            var session = (await ReadDescriptorsAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == sessionId)
                ?? throw new KeyNotFoundException($"Session {sessionId:D} does not exist.");
            File.Delete(session.Path);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask AppendAsync(SerialTrafficEvent item, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection!.CreateCommand();
            command.CommandText = """
                INSERT INTO events(sequence, utc, monotonic_ticks, connection_id, direction, source, message, data)
                VALUES ($sequence, $utc, $monotonicTicks, $connectionId, $direction, $source, $message, $data);
                """;
            command.Parameters.AddWithValue("$sequence", item.Sequence);
            command.Parameters.AddWithValue("$utc", item.Utc.ToString("O"));
            command.Parameters.AddWithValue("$monotonicTicks", item.MonotonicTicks);
            command.Parameters.AddWithValue("$connectionId", item.ConnectionId.ToString("D"));
            command.Parameters.AddWithValue("$direction", item.Direction.ToString());
            command.Parameters.AddWithValue("$source", item.Source);
            command.Parameters.AddWithValue("$message", (object?)item.Message ?? DBNull.Value);
            command.Parameters.Add("$data", SqliteType.Blob).Value = item.Data;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            eventCount++;
            rawByteCount += item.Data.LongLength;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection is null || descriptor is null)
            {
                return;
            }

            var endedUtc = DateTimeOffset.UtcNow;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE session SET ended_utc = $endedUtc, event_count = $eventCount, raw_byte_count = $rawByteCount WHERE id = $id;";
                command.Parameters.AddWithValue("$endedUtc", endedUtc.ToString("O"));
                command.Parameters.AddWithValue("$eventCount", eventCount);
                command.Parameters.AddWithValue("$rawByteCount", rawByteCount);
                command.Parameters.AddWithValue("$id", descriptor.Id.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await connection.CloseAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            descriptor = descriptor with { EndedUtc = endedUtc, EventCount = eventCount, RawByteCount = rawByteCount };
            connection = null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        gate.Dispose();
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection is not null)
        {
            return;
        }

        Directory.CreateDirectory(paths.SessionsRoot);
        var id = Guid.NewGuid();
        var startedUtc = DateTimeOffset.UtcNow;
        var fileName = $"{startedUtc:yyyyMMdd-HHmmss}-{id:N}.swbsession";
        var sessionPath = Path.Combine(paths.SessionsRoot, fileName);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = sessionPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };

        connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA foreign_keys=ON;
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
                CREATE INDEX events_connection_sequence ON events(connection_id, sequence);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO session(id, started_utc, schema_version) VALUES ($id, $startedUtc, 1);";
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$startedUtc", startedUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        descriptor = new SessionDescriptor(id, sessionPath, startedUtc, null, 0, 0);
    }

    private async Task<IReadOnlyList<SessionDescriptor>> ReadDescriptorsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.SessionsRoot))
        {
            return [];
        }

        var sessions = new List<SessionDescriptor>();
        foreach (var path in Directory.EnumerateFiles(paths.SessionsRoot, "*.swbsession"))
        {
            await using var sessionConnection = CreateReadOnlyConnection(path);
            await sessionConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = sessionConnection.CreateCommand();
            command.CommandText = "SELECT id, started_utc, ended_utc, event_count, raw_byte_count FROM session LIMIT 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException($"Session metadata is missing from {path}.");
            }

            var id = Guid.Parse(reader.GetString(0));
            var session = new SessionDescriptor(
                id,
                path,
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt64(3),
                reader.GetInt64(4));
            if (connection is not null && descriptor?.Id == id)
            {
                session = ActiveSession!;
            }

            sessions.Add(session);
        }

        return sessions.OrderByDescending(item => item.StartedUtc).ToArray();
    }

    private static SqliteConnection CreateReadOnlyConnection(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ConnectionString);

    private static string CsvField(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
