using System.Globalization;
using System.Text;
using System.Text.Json;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;

namespace SerialWorkbench.Cli;

public static class SessionExporter
{
    public static async Task<SessionExportReceipt> ExportAsync(IHostRpc client, SessionEventQuery query, string path, string format, CancellationToken cancellationToken)
    {
        if (format is not ("csv" or "jsonl" or "binary" or "text" or "hex"))
        {
            throw new ArgumentException("Unsupported session export format.", nameof(format));
        }
        await using var stream = File.Create(path);
        await using var writer = format == "binary" ? null : new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        long bytes = 0;
        if (writer is not null && format == "csv")
        {
            const string header = "utc,direction,source,hex,byte_count";
            await writer.WriteLineAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
            bytes += Encoding.UTF8.GetByteCount(header) + Environment.NewLine.Length;
        }
        while (true)
        {
            var events = await client.ReadSessionEventsAsync(query, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                break;
            }
            foreach (var item in events)
            {
                if (writer is null)
                {
                    await stream.WriteAsync(item.Data, cancellationToken).ConfigureAwait(false);
                    bytes += item.Data.Length;
                }
                else
                {
                    var line = format switch
                    {
                        "jsonl" => JsonSerializer.Serialize(item, MachineOutput.CompactOptions),
                        "hex" => Convert.ToHexString(item.Data),
                        "text" => $"{item.Utc:O} {(item.Direction == SerialDirection.Receive ? "RX" : "TX")} {item.Source} {Convert.ToHexString(item.Data)}",
                        _ => string.Join(',', item.Utc.ToString("O", CultureInfo.InvariantCulture), item.Direction, CsvField(item.Source),
                            Convert.ToHexString(item.Data), item.Data.Length.ToString(CultureInfo.InvariantCulture)),
                    };
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    bytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                }
                query = query with { AfterSequence = item.Sequence };
            }
        }
        return new SessionExportReceipt(query.SessionId, path, format, bytes);
    }

    private static string CsvField(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
        ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
