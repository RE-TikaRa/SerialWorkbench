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

    public SessionDescriptor? ActiveSession => connection is null || descriptor is null
        ? null
        : descriptor with { EventCount = eventCount, RawByteCount = rawByteCount };

    public async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

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

    public async Task AppendLoopbackResultAsync(LoopbackRequest request, LoopbackResult result, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection!.CreateCommand();
            command.CommandText = """
                INSERT INTO loopback_results(
                    utc, connection_id, payload_length, iterations, timeout_milliseconds, pattern, seed,
                    passed, sent_bytes, received_bytes, duration_milliseconds, bytes_per_second,
                    first_difference_index, expected_byte, actual_byte, error)
                VALUES(
                    $utc, $connectionId, $payloadLength, $iterations, $timeoutMilliseconds, $pattern, $seed,
                    $passed, $sentBytes, $receivedBytes, $durationMilliseconds, $bytesPerSecond,
                    $firstDifferenceIndex, $expectedByte, $actualByte, $error);
                """;
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$connectionId", request.ConnectionId.ToString("D"));
            command.Parameters.AddWithValue("$payloadLength", request.PayloadLength);
            command.Parameters.AddWithValue("$iterations", request.Iterations);
            command.Parameters.AddWithValue("$timeoutMilliseconds", request.TimeoutMilliseconds);
            command.Parameters.AddWithValue("$pattern", request.Pattern.ToString());
            command.Parameters.AddWithValue("$seed", request.Seed);
            command.Parameters.AddWithValue("$passed", result.Passed ? 1 : 0);
            command.Parameters.AddWithValue("$sentBytes", result.SentBytes);
            command.Parameters.AddWithValue("$receivedBytes", result.ReceivedBytes);
            command.Parameters.AddWithValue("$durationMilliseconds", result.Duration.TotalMilliseconds);
            command.Parameters.AddWithValue("$bytesPerSecond", result.BytesPerSecond);
            command.Parameters.AddWithValue("$firstDifferenceIndex", (object?)result.FirstDifferenceIndex ?? DBNull.Value);
            command.Parameters.AddWithValue("$expectedByte", (object?)result.ExpectedByte ?? DBNull.Value);
            command.Parameters.AddWithValue("$actualByte", (object?)result.ActualByte ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<LoopbackHistoryEntry>> ReadLoopbackResultsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = (await ReadDescriptorsAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == sessionId)
                ?? throw new KeyNotFoundException($"Session {sessionId:D} does not exist.");
            await using var sessionConnection = CreateReadOnlyConnection(session.Path);
            await sessionConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var table = sessionConnection.CreateCommand())
            {
                table.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'loopback_results' LIMIT 1;";
                if (await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    return [];
                }
            }

            await using var command = sessionConnection.CreateCommand();
            command.CommandText = """
                SELECT utc, connection_id, payload_length, iterations, timeout_milliseconds, pattern, seed,
                       passed, sent_bytes, received_bytes, duration_milliseconds, bytes_per_second,
                       first_difference_index, expected_byte, actual_byte, error
                FROM loopback_results
                ORDER BY id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<LoopbackHistoryEntry>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var request = new LoopbackRequest(
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    Enum.Parse<LoopbackPattern>(reader.GetString(5)),
                    reader.GetInt32(6));
                var result = new LoopbackResult(
                    reader.GetInt32(7) != 0,
                    request.Iterations,
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    TimeSpan.FromMilliseconds(reader.GetDouble(10)),
                    reader.GetDouble(11),
                    reader.IsDBNull(12) ? null : reader.GetInt32(12),
                    reader.IsDBNull(13) ? null : (byte)reader.GetInt32(13),
                    reader.IsDBNull(14) ? null : (byte)reader.GetInt32(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15));
                results.Add(new LoopbackHistoryEntry(
                    DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    request.ConnectionId,
                    request,
                    result));
            }

            return results;
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
                CREATE TABLE loopback_results(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    utc TEXT NOT NULL,
                    connection_id TEXT NOT NULL,
                    payload_length INTEGER NOT NULL,
                    iterations INTEGER NOT NULL,
                    timeout_milliseconds INTEGER NOT NULL,
                    pattern TEXT NOT NULL,
                    seed INTEGER NOT NULL,
                    passed INTEGER NOT NULL,
                    sent_bytes INTEGER NOT NULL,
                    received_bytes INTEGER NOT NULL,
                    duration_milliseconds REAL NOT NULL,
                    bytes_per_second REAL NOT NULL,
                    first_difference_index INTEGER NULL,
                    expected_byte INTEGER NULL,
                    actual_byte INTEGER NULL,
                    error TEXT NULL
                );
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
