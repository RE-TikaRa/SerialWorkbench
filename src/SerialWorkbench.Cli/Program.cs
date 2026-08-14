using System.Globalization;
using System.Text;
using System.Text.Json;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Protocols;
using StreamJsonRpc;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var parsed = Arguments.Parse(args);
if (parsed.Positionals.Count == 0 || parsed.Has("--help") || parsed.Has("-h"))
{
    PrintHelp();
    return parsed.Positionals.Count == 0 ? 2 : 0;
}

var output = parsed.Get("--output") ?? "text";
var culture = parsed.Get("--culture") ?? CultureInfo.CurrentUICulture.Name;
var applicationRoot = parsed.Get("--app-root") ?? AppContext.BaseDirectory;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    await using var client = await HostEndpoint.ConnectAsync(applicationRoot, true, cancellation.Token).ConfigureAwait(false);
    var handshake = await client.HandshakeAsync(new HandshakeRequest(RpcProtocol.MajorVersion, RpcProtocol.MinorVersion, "cli", culture), cancellation.Token).ConfigureAwait(false);
    if (!handshake.Accepted)
    {
        WriteError(output, "SWB-IPC-VERSION", handshake.Error ?? "IPC version mismatch.");
        return 3;
    }

    return await RunAsync(client, parsed, output, cancellation.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    WriteError(output, "SWB-CANCELLED", "Task cancelled.");
    return 5;
}
catch (TimeoutException ex)
{
    WriteError(output, "SWB-TIMEOUT", ex.Message);
    return 4;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or KeyNotFoundException or RemoteInvocationException)
{
    WriteError(output, "SWB-RUNTIME", ex.Message);
    return 3;
}

static async Task<int> RunAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var command = string.Join(' ', arguments.Positionals.Take(2)).ToLowerInvariant();
    switch (command)
    {
        case "host status":
            WriteResult(output, "host.status", await client.GetStatusAsync(cancellationToken).ConfigureAwait(false));
            return 0;
        case "host stop":
            WriteResult(output, "host.stop", await client.StopHostAsync(cancellationToken).ConfigureAwait(false));
            return 0;
        case "ports list":
            WriteResult(output, "ports.list", await client.ListPortsAsync(cancellationToken).ConfigureAwait(false));
            return 0;
        case "workspace show":
            WriteResult(output, "workspace.show", await client.GetStatusAsync(cancellationToken).ConfigureAwait(false));
            return 0;
        case "workspace set":
            WriteResult(output, "workspace.set", await client.SetWorkspaceAsync(new SetWorkspaceRequest(arguments.Get("--path") ?? throw new ArgumentException("workspace set requires --path.")), cancellationToken).ConfigureAwait(false));
            return 0;
        case "workspace clear":
            WriteResult(output, "workspace.clear", await client.SetWorkspaceAsync(new SetWorkspaceRequest(null), cancellationToken).ConfigureAwait(false));
            return 0;
        case "send":
        case "send ":
            return await SendAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "monitor":
        case "monitor ":
            return await MonitorAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "loopback run":
            return await LoopbackAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        default:
            WriteError(output, "SWB-ARGUMENT", $"Unknown command: {string.Join(' ', arguments.Positionals)}");
            return 2;
    }
}

static async Task<int> SendAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var connection = await OpenAsync(client, arguments, cancellationToken).ConfigureAwait(false);
    try
    {
        byte[] data;
        var hex = arguments.Get("--hex");
        if (hex is not null)
        {
            data = HexCodec.Parse(hex);
        }
        else
        {
            var text = arguments.Get("--text") ?? throw new ArgumentException("send requires --text or --hex.");
            var encoding = Encoding.GetEncoding(arguments.Get("--encoding") ?? "utf-8");
            data = encoding.GetBytes(text + ParseLineEnding(arguments.Get("--line-ending")));
        }

        var result = await client.SendAsync(new SendRequest(connection.Id, data, "cli.send"), cancellationToken).ConfigureAwait(false);
        WriteResult(output, "send", new { result.Success, bytes = data.Length, hex = Convert.ToHexString(data), result.Error });
        return result.Success ? 0 : 3;
    }
    finally
    {
        await client.CloseConnectionAsync(connection.Id, CancellationToken.None).ConfigureAwait(false);
    }
}

static async Task<int> MonitorAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var connection = await OpenAsync(client, arguments, cancellationToken).ConfigureAwait(false);
    var seconds = arguments.GetInt("--seconds", 10);
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    long sequence = 0;
    try
    {
        while (DateTime.UtcNow < deadline)
        {
            var events = await client.ReadEventsAsync(new EventQuery(sequence, 1000, connection.Id), cancellationToken).ConfigureAwait(false);
            foreach (var item in events)
            {
                sequence = Math.Max(sequence, item.Sequence);
                if (output == "jsonl")
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, command = "monitor", item }));
                }
                else
                {
                    Console.WriteLine($"{item.Utc:HH:mm:ss.fff} {(item.Direction == SerialDirection.Receive ? "RX" : "TX")} {Convert.ToHexString(item.Data)}");
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }
    finally
    {
        await client.CloseConnectionAsync(connection.Id, CancellationToken.None).ConfigureAwait(false);
    }
}

static async Task<int> LoopbackAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var connection = await OpenAsync(client, arguments, cancellationToken).ConfigureAwait(false);
    try
    {
        var pattern = Enum.Parse<LoopbackPattern>(arguments.Get("--pattern") ?? "Incrementing", true);
        var request = new LoopbackRequest(
            connection.Id,
            arguments.GetInt("--length", 4096),
            arguments.GetInt("--iterations", 1),
            arguments.GetInt("--timeout", 5000),
            pattern,
            arguments.GetInt("--seed", 0x534257));
        var result = await client.RunLoopbackAsync(request, cancellationToken).ConfigureAwait(false);
        WriteResult(output, "loopback.run", result);
        return result.Passed ? 0 : 1;
    }
    finally
    {
        await client.CloseConnectionAsync(connection.Id, CancellationToken.None).ConfigureAwait(false);
    }
}

static Task<ConnectionSnapshot> OpenAsync(IHostRpc client, Arguments arguments, CancellationToken cancellationToken)
{
    var port = arguments.Get("--port") ?? throw new ArgumentException("--port is required.");
    var options = new SerialConnectionOptions(
        port,
        arguments.GetInt("--baud", 115200),
        arguments.GetInt("--data-bits", 8),
        Enum.Parse<SerialParity>(arguments.Get("--parity") ?? "None", true),
        Enum.Parse<SerialStopBits>(arguments.Get("--stop-bits") ?? "One", true),
        Enum.Parse<SerialHandshake>(arguments.Get("--handshake") ?? "None", true),
        arguments.Has("--dtr"),
        arguments.Has("--rts"),
        arguments.Get("--encoding") ?? "utf-8");
    return client.OpenConnectionAsync(new OpenConnectionRequest(options), cancellationToken);
}

static string ParseLineEnding(string? value) => value?.ToLowerInvariant() switch
{
    null or "none" => "",
    "cr" => "\r",
    "lf" => "\n",
    "crlf" => "\r\n",
    _ => throw new ArgumentException("--line-ending must be none, cr, lf, or crlf."),
};

static void WriteResult(string output, string command, object value)
{
    if (output is "json" or "jsonl")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, command, time = DateTimeOffset.UtcNow, result = value }, CliJson.Options));
        return;
    }

    Console.WriteLine(JsonSerializer.Serialize(value, CliJson.Options));
}

static void WriteError(string output, string code, string message)
{
    var value = new { schemaVersion = 1, code, message, time = DateTimeOffset.UtcNow };
    if (output is "json" or "jsonl")
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(value, CliJson.Options));
    }
    else
    {
        Console.Error.WriteLine($"{code}: {message}");
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
        SerialWorkbench CLI

        serial-workbench ports list [--output text|json]
        serial-workbench host status|stop
        serial-workbench workspace show|clear
        serial-workbench workspace set --path PATH
        serial-workbench send --port <port> (--text TEXT | --hex HEX) [--baud 115200]
        serial-workbench monitor --port <port> [--seconds 10] [--output text|jsonl]
        serial-workbench loopback run --port <port> [--baud 115200] [--length 4096] [--iterations 1]
        """);
}

file static class CliJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}

file sealed class Arguments
{
    private readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = [];

    public static Arguments Parse(string[] values)
    {
        var result = new Arguments();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (!value.StartsWith('-'))
            {
                result.Positionals.Add(value);
                continue;
            }

            if (index + 1 < values.Length && !values[index + 1].StartsWith('-'))
            {
                result.options[value] = values[++index];
            }
            else
            {
                result.options[value] = null;
            }
        }

        return result;
    }

    public bool Has(string name) => options.ContainsKey(name);

    public string? Get(string name) => options.GetValueOrDefault(name);

    public int GetInt(string name, int defaultValue) => Get(name) is { } text
        ? int.Parse(text, CultureInfo.InvariantCulture)
        : defaultValue;
}
