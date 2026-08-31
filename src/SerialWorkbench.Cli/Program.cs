using System.Globalization;
using System.Text;
using System.Text.Json;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Modbus;
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
if (output is not ("text" or "json" or "jsonl"))
{
    WriteError(output, "SWB-ARGUMENT", "--output must be text, json, or jsonl.");
    return 2;
}
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
catch (Exception ex) when (ex is FormatException or OverflowException)
{
    WriteError(output, "SWB-ARGUMENT", ex.Message);
    return 2;
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
        case "sessions list":
            return await ListSessionsAsync(client, output, cancellationToken).ConfigureAwait(false);
        case "sessions show":
            return await ShowSessionAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "sessions export":
            return await ExportSessionAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "sessions delete":
            return await DeleteSessionAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "send":
        case "send ":
            return await SendAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "monitor":
        case "monitor ":
            return await MonitorAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "loopback run":
            return await LoopbackAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "modbus read":
            return await ModbusReadAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "modbus write":
            return await ModbusWriteAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "xmodem send":
            return await XmodemSendAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "xmodem receive":
            return await XmodemReceiveAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "protocol inspect":
            return InspectProtocol(arguments, output);
        default:
            WriteError(output, "SWB-ARGUMENT", $"Unknown command: {string.Join(' ', arguments.Positionals)}");
            return 2;
    }
}

static async Task<int> ListSessionsAsync(IHostRpc client, string output, CancellationToken cancellationToken)
{
    var sessions = await client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
    WriteResult(output, "sessions.list", sessions);
    return 0;
}

static async Task<int> ShowSessionAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var sessionId = ParseGuid(arguments.Get("--id"), "--id");
    var sessions = await client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
    var session = sessions.SingleOrDefault(item => item.Id == sessionId)
        ?? throw new KeyNotFoundException($"Session {sessionId:D} does not exist.");
    var events = await client.ReadAllSessionEventsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    var loopbacks = await client.ReadLoopbackResultsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    WriteResult(output, "sessions.show", new { session, events, loopbacks });
    return 0;
}

static async Task<int> ExportSessionAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var sessionId = ParseGuid(arguments.Get("--id"), "--id");
    var path = arguments.Get("--file") ?? throw new ArgumentException("sessions export requires --file.");
    var csv = await client.ExportSessionCsvAsync(sessionId, cancellationToken).ConfigureAwait(false);
    await File.WriteAllTextAsync(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    WriteResult(output, "sessions.export", new { sessionId, path, bytes = Encoding.UTF8.GetByteCount(csv) });
    return 0;
}

static async Task<int> DeleteSessionAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var sessionId = ParseGuid(arguments.Get("--id"), "--id");
    var result = await client.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    WriteResult(output, "sessions.delete", new { sessionId, result.Success, result.Error });
    return result.Success ? 0 : 3;
}

static async Task<int> XmodemSendAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var path = arguments.Get("--file") ?? throw new ArgumentException("xmodem send requires --file.");
    var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    var connection = await OpenAsync(client, arguments, cancellationToken).ConfigureAwait(false);
    try
    {
        var result = await client.SendXmodemAsync(connection.Id, data, cancellationToken).ConfigureAwait(false);
        WriteTransferResult(output, "xmodem.send", path, result);
        return TransferExitCode(result);
    }
    finally
    {
        await client.CloseConnectionAsync(connection.Id, CancellationToken.None).ConfigureAwait(false);
    }
}

static async Task<int> XmodemReceiveAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var path = arguments.Get("--file") ?? throw new ArgumentException("xmodem receive requires --file.");
    var connection = await OpenAsync(client, arguments, cancellationToken).ConfigureAwait(false);
    try
    {
        var result = await client.ReceiveXmodemAsync(connection.Id, cancellationToken).ConfigureAwait(false);
        if (result.Result.Success)
        {
            await File.WriteAllBytesAsync(path, result.Data, cancellationToken).ConfigureAwait(false);
        }

        WriteTransferResult(output, "xmodem.receive", path, result.Result, result.Data.Length);
        return TransferExitCode(result.Result);
    }
    finally
    {
        await client.CloseConnectionAsync(connection.Id, CancellationToken.None).ConfigureAwait(false);
    }
}

static int InspectProtocol(Arguments arguments, string output)
{
    var frame = HexCodec.Parse(arguments.Get("--hex") ?? throw new ArgumentException("protocol inspect requires --hex."));
    if (arguments.Get("--template") is { } templatePath)
    {
        var template = ProtocolTemplateCodec.Deserialize(File.ReadAllText(templatePath));
        var templateInspection = ProtocolTemplateParser.Inspect(template, frame);
        var templateValue = new
        {
            valid = templateInspection.IsValid,
            template = templateInspection.Template,
            frameLength = templateInspection.FrameLength,
            expectedLength = templateInspection.ExpectedLength,
            checksumValid = templateInspection.ChecksumValid,
            fields = templateInspection.Fields,
            error = templateInspection.Error,
        };

        if (output is "json" or "jsonl")
        {
            WriteResult(output, "protocol.inspect", templateValue);
        }
        else
        {
            Console.WriteLine($"Template: {templateValue.template}");
            Console.WriteLine($"Valid: {templateValue.valid}");
            Console.WriteLine($"Length: {templateValue.frameLength} / {templateValue.expectedLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            if (templateValue.checksumValid is { } checksumValid)
            {
                Console.WriteLine($"Checksum: {checksumValid}");
            }

            foreach (var field in templateValue.fields)
            {
                Console.WriteLine($"{field.Name}: {field.Value} ({field.Hex})");
            }

            if (templateValue.error is { } error)
            {
                Console.WriteLine($"Error: {error}");
            }
        }

        return templateValue.valid ? 0 : 1;
    }

    var inspection = ModbusRtuCodec.Inspect(frame);
    var value = new
    {
        valid = inspection.IsValid,
        kind = inspection.Kind,
        address = inspection.Address,
        functionCode = inspection.FunctionCode,
        exceptionCode = inspection.ExceptionCode,
        frameLength = inspection.FrameLength,
        expectedLength = inspection.ExpectedLength,
        byteCount = inspection.ByteCount,
        dataAddress = inspection.DataAddress,
        registerValue = inspection.Value,
        calculatedCrc = inspection.CalculatedCrc,
        actualCrc = inspection.ActualCrc,
        error = inspection.Error,
    };

    if (output is "json" or "jsonl")
    {
        WriteResult(output, "protocol.inspect", value);
    }
    else
    {
        Console.WriteLine($"Kind: {value.kind}");
        Console.WriteLine($"Valid: {value.valid}");
        Console.WriteLine($"Address: {FormatByte(value.address)}");
        Console.WriteLine($"Function: {FormatByte(value.functionCode)}");
        Console.WriteLine($"Length: {value.frameLength} / {value.expectedLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        Console.WriteLine($"CRC: {FormatUShort(value.calculatedCrc)} / {FormatUShort(value.actualCrc)}");
        if (value.exceptionCode is { } exceptionCode)
        {
            Console.WriteLine($"Exception: 0x{exceptionCode:X2}");
        }

        if (value.error is { } error)
        {
            Console.WriteLine($"Error: {error}");
        }
    }

    return value.valid ? 0 : 1;
}

static string FormatByte(byte? value) => value is { } item ? $"0x{item:X2}" : "(none)";

static string FormatUShort(ushort? value) => value is { } item ? $"0x{item:X4}" : "(none)";

static void WriteTransferResult(string output, string command, string path, XmodemTransferResult result, int? receivedBytes = null)
{
    var value = new
    {
        path,
        success = result.Success,
        bytesTransferred = result.BytesTransferred,
        receivedBytes,
        blocks = result.Blocks,
        retries = result.Retries,
        durationMilliseconds = result.Duration.TotalMilliseconds,
        error = result.Error,
    };

    if (output is "json" or "jsonl")
    {
        WriteResult(output, command, value);
        return;
    }

    Console.WriteLine($"File: {path}");
    Console.WriteLine($"Result: {(result.Success ? "success" : "failed")}");
    Console.WriteLine($"Bytes: {result.BytesTransferred:N0}");
    Console.WriteLine($"Blocks: {result.Blocks:N0}");
    Console.WriteLine($"Retries: {result.Retries:N0}");
    Console.WriteLine($"Duration: {result.Duration.TotalMilliseconds:N0} ms");
    if (result.Error is { } error)
    {
        Console.WriteLine($"Error: {error}");
    }
}

static int TransferExitCode(XmodemTransferResult result) => result.Success
    ? 0
    : result.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true ? 4 : 3;

static Guid ParseGuid(string? value, string name) => Guid.TryParse(value, out var result)
    ? result
    : throw new ArgumentException($"{name} is required and must be a valid GUID.");

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

static async Task<int> ModbusReadAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var connection = await OpenAsync(client, arguments, cancellationToken).ConfigureAwait(false);
    try
    {
        var slave = GetByte(arguments, "--slave", 1);
        var function = GetByte(arguments, "--function", 3);
        if (function is not (1 or 2 or 3 or 4 or 17))
        {
            throw new ArgumentException("--function must be 1, 2, 3, 4, or 17.");
        }

        var frame = function == 17
            ? ModbusRtuCodec.BuildReportServerIdRequest(slave)
            : ModbusRtuCodec.BuildReadRequest(slave, function, GetUShort(arguments, "--address"), ValidateReadQuantity(GetUShort(arguments, "--quantity"), function));
        return await RunModbusAsync(client, connection, arguments, output, cancellationToken, "modbus.read", frame, slave, function).ConfigureAwait(false);
    }
    finally
    {
        await client.CloseConnectionAsync(connection.Id, CancellationToken.None).ConfigureAwait(false);
    }
}

static async Task<int> ModbusWriteAsync(IHostRpc client, Arguments arguments, string output, CancellationToken cancellationToken)
{
    var connection = await OpenAsync(client, arguments, cancellationToken).ConfigureAwait(false);
    try
    {
        var slave = GetByte(arguments, "--slave", 1);
        var function = GetByte(arguments, "--function", 6);
        if (function is not (5 or 6 or 15 or 16))
        {
            throw new ArgumentException("--function must be 5, 6, 15, or 16.");
        }

        var address = GetUShort(arguments, "--address");
        var frame = function switch
        {
            5 => ModbusRtuCodec.BuildWriteSingleCoil(slave, address, GetCoilValue(arguments)),
            6 => ModbusRtuCodec.BuildWriteSingleRegister(slave, address, GetUShort(arguments, "--value")),
            15 => ModbusRtuCodec.BuildWriteMultipleCoils(slave, address, ParseCoilValues(arguments.Get("--values"))),
            16 => ModbusRtuCodec.BuildWriteMultipleRegisters(slave, address, ParseRegisterValues(arguments.Get("--values"))),
            _ => throw new InvalidOperationException("Unsupported Modbus write function."),
        };
        return await RunModbusAsync(client, connection, arguments, output, cancellationToken, "modbus.write", frame, slave, function).ConfigureAwait(false);
    }
    finally
    {
        await client.CloseConnectionAsync(connection.Id, CancellationToken.None).ConfigureAwait(false);
    }
}

static async Task<int> RunModbusAsync(
    IHostRpc client,
    ConnectionSnapshot connection,
    Arguments arguments,
    string output,
    CancellationToken cancellationToken,
    string command,
    byte[] requestFrame,
    byte slave,
    byte function)
{
    var request = new ModbusTransactionRequest(
        connection.Id,
        requestFrame,
        slave,
        function,
        arguments.GetInt("--timeout", 2000));
    var result = await client.RunModbusAsync(request, cancellationToken).ConfigureAwait(false);
    var value = new
    {
        success = result.Success,
        requestFrame = Convert.ToHexString(requestFrame),
        responseFrame = Convert.ToHexString(result.ResponseFrame),
        functionCode = result.FunctionCode,
        registers = result.Registers,
        bits = result.Bits,
        address = result.Address,
        registerValue = result.Value,
        exceptionCode = result.ExceptionCode,
        durationMilliseconds = result.Duration.TotalMilliseconds,
        error = result.Error,
    };

    if (output is "json" or "jsonl")
    {
        WriteResult(output, command, value);
    }
    else
    {
        Console.WriteLine($"Request: {value.requestFrame}");
        Console.WriteLine($"Response: {(string.IsNullOrEmpty(value.responseFrame) ? "(none)" : value.responseFrame)}");
        Console.WriteLine($"Result: {(value.success ? "success" : "failed")}");
        Console.WriteLine($"Duration: {value.durationMilliseconds:N0} ms");
        if (value.registers.Length > 0)
        {
            Console.WriteLine($"Registers: {string.Join(' ', value.registers.Select(static item => $"0x{item:X4}"))}");
        }

        if (value.bits is { Length: > 0 } bits)
        {
            Console.WriteLine($"Bits: {string.Join(' ', bits.Select(static item => item ? '1' : '0'))}");
        }

        if (value.address is { } address && value.registerValue is { } registerValue)
        {
            Console.WriteLine($"Address: 0x{address:X4}");
            Console.WriteLine($"Value: 0x{registerValue:X4}");
        }

        if (value.exceptionCode is { } exceptionCode)
        {
            Console.WriteLine($"Exception: 0x{exceptionCode:X2}");
        }

        if (value.error is { } error)
        {
            Console.WriteLine($"Error: {error}");
        }
    }

    return ModbusExitCode(result);
}

static int ModbusExitCode(ModbusTransactionResult result)
{
    if (result.Success)
    {
        return 0;
    }

    if (result.ExceptionCode is not null)
    {
        return 1;
    }

    if (result.Error?.Contains("Timed out", StringComparison.OrdinalIgnoreCase) == true)
    {
        return 4;
    }

    return 3;
}

static byte GetByte(Arguments arguments, string name, int defaultValue)
{
    var value = arguments.GetInt(name, defaultValue);
    return value is >= byte.MinValue and <= byte.MaxValue
        ? (byte)value
        : throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {byte.MinValue} and {byte.MaxValue}.");
}

static ushort GetUShort(Arguments arguments, string name)
{
    var value = arguments.GetInt(name, -1);
    return value is >= ushort.MinValue and <= ushort.MaxValue
        ? (ushort)value
        : throw new ArgumentException($"{name} is required and must be between {ushort.MinValue} and {ushort.MaxValue}.");
}

static bool GetCoilValue(Arguments arguments)
{
    var value = GetUShort(arguments, "--value");
    return value switch
    {
        0 => false,
        1 => true,
        _ => throw new ArgumentException("--value must be 0 or 1 for function 5."),
    };
}

static bool[] ParseCoilValues(string? text)
{
    var values = SplitValues(text, "--values is required for function 15.")
        .Select(static value => value switch
        {
            "0" => false,
            "1" => true,
            _ => throw new ArgumentException("Coil values must be 0 or 1."),
        })
        .ToArray();
    return values.Length is >= 1 and <= 1968
        ? values
        : throw new ArgumentException("Function 15 requires between 1 and 1968 coil values.");
}

static ushort[] ParseRegisterValues(string? text)
{
    var values = SplitValues(text, "--values is required for function 16.")
        .Select(static value => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture))
        .ToArray();
    return values.Length is >= 1 and <= 123
        ? values
        : throw new ArgumentException("Function 16 requires between 1 and 123 register values.");
}

static string[] SplitValues(string? text, string missingMessage) =>
    string.IsNullOrWhiteSpace(text)
        ? throw new ArgumentException(missingMessage)
        : text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static ushort ValidateReadQuantity(ushort quantity, byte function)
{
    var maximum = function is 1 or 2 ? 2000 : 125;
    return quantity is >= 1 && quantity <= maximum
        ? quantity
        : throw new ArgumentOutOfRangeException(nameof(quantity), quantity, $"Read quantity must be between 1 and {maximum}.");
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
        arguments.Get("--encoding") ?? "utf-8",
        Enum.Parse<SerialConnectionRole>(arguments.Get("--role") ?? "Dut", true),
        arguments.Get("--device-id"),
        arguments.Has("--rs485"),
        arguments.GetInt("--rts-before", 0),
        arguments.GetInt("--rts-after", 0));
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
        serial-workbench sessions list|show|export|delete
        serial-workbench sessions show --id SESSION_ID [--output json]
        serial-workbench sessions export --id SESSION_ID --file PATH
        serial-workbench sessions delete --id SESSION_ID
        serial-workbench send --port <port> (--text TEXT | --hex HEX) [--baud 115200]
        serial-workbench monitor --port <port> [--seconds 10] [--output text|jsonl]
        serial-workbench loopback run --port <port> [--baud 115200] [--length 4096] [--iterations 1]
        serial-workbench modbus read --port <port> --slave 1 --address 0 --quantity 1 [--function 1|2|3|4|17]
        serial-workbench modbus write --port <port> --slave 1 --address 0 (--value VALUE | --values VALUES) [--function 5|6|15|16]
        serial-workbench xmodem send --port <port> --file PATH [--baud 115200]
        serial-workbench xmodem receive --port <port> --file PATH [--baud 115200]
        serial-workbench protocol inspect --hex HEX [--template PATH] [--output text|json]
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
