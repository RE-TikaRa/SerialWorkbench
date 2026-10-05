using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SerialWorkbench.Cli;
using SerialWorkbench.Cli.Tui;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;
using StreamJsonRpc;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = new UTF8Encoding(false);

var catalog = new CommandCatalog();
catalog.Root.SetAction(async (result, token) =>
{
    if (result.GetValue<bool>("--agent"))
    {
        MachineOutput.Write(result.GetValue<string>("--output") == "jsonl" ? "jsonl" : "json", "help", AgentDiscovery.Capabilities());
        return 0;
    }

    return await TerminalWorkbench.RunAsync(result.GetValue<string>("--app-root") ?? AppContext.BaseDirectory,
        result.GetValue<string>("--culture") ?? CultureInfo.CurrentUICulture.Name, token).ConfigureAwait(false);
});
foreach (var definition in catalog.Commands)
{
    definition.Command.SetAction((result, token) => ExecuteCommandAsync(definition.Bind(result), token));
}

var parsed = catalog.Root.Parse(args);
var isAgent = parsed.GetResult("--agent") is System.CommandLine.Parsing.OptionResult { Implicit: false };
var agentOutput = parsed.GetResult("--output") is { Tokens.Count: > 0 } requestedFormat && requestedFormat.Tokens[0].Value == "jsonl" ? "jsonl" : "json";
if (isAgent && catalog.Root.Directives.Any(directive => parsed.GetResult(directive) is not null))
{
    WriteError(agentOutput, "INVALID_ARGUMENT", "Agent commands use capabilities, schema or help for discovery.");
    return 2;
}
if (isAgent && catalog.Root.Options.OfType<VersionOption>().Any(option => parsed.GetResult(option) is { Implicit: false }))
{
    MachineOutput.Write(agentOutput, "version", new VersionInfo(typeof(CommandCatalog).Assembly.GetName().Version?.ToString() ?? "0.0.0", RpcProtocol.MajorVersion, RpcProtocol.MinorVersion));
    return 0;
}
if (parsed.GetResult("--agent") is System.CommandLine.Parsing.OptionResult { Implicit: false }
    && catalog.Root.Options.OfType<HelpOption>().Any(option => parsed.GetResult(option) is { Implicit: false }))
{
    var selected = catalog.Commands.FirstOrDefault(item => item.Command == parsed.CommandResult.Command);
    MachineOutput.Write(agentOutput, "help", selected is null ? AgentDiscovery.Capabilities() : AgentDiscovery.Schema(selected.Id));
    return 0;
}
var frameworkAction = catalog.Root.Options.Where(static option => option is HelpOption or VersionOption)
    .Any(option => parsed.GetResult(option) is { Implicit: false })
    || catalog.Root.Directives.Any(directive => parsed.GetResult(directive) is not null);
if (parsed.Errors.Count > 0 && !frameworkAction)
{
    var requestedOutput = parsed.GetResult("--output") is { Tokens.Count: > 0 } outputResult ? outputResult.Tokens[0].Value : null;
    var agent = parsed.GetResult("--agent") is System.CommandLine.Parsing.OptionResult { Implicit: false };
    var selected = catalog.Commands.FirstOrDefault(item => item.Command == parsed.CommandResult.Command);
    WriteError(requestedOutput is "json" or "jsonl" ? requestedOutput : agent ? "json" : "text", "INVALID_ARGUMENT", string.Join(Environment.NewLine, parsed.Errors.Select(static error => error.Message)), selected?.Id ?? "cli");
    return 2;
}

return await parsed.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false }).ConfigureAwait(false);

static async Task<int> ExecuteCommandAsync(CommandArguments arguments, CancellationToken cancellationToken)
{
    var output = arguments.Get("--output") ?? "text";
    if (arguments.Has("--agent") && output == "text")
    {
        output = arguments.CommandId is "monitor" or "modbus.poll" ? "jsonl" : "json";
    }
    var culture = arguments.Get("--culture") ?? CultureInfo.CurrentUICulture.Name;
    var applicationRoot = arguments.Get("--app-root") ?? AppContext.BaseDirectory;
    try
    {
        switch (arguments.CommandId)
        {
            case "capabilities":
                WriteResult(output, "capabilities", AgentDiscovery.Capabilities());
                return 0;
            case "version":
                WriteResult(output, "version", new VersionInfo(typeof(CommandCatalog).Assembly.GetName().Version?.ToString() ?? "0.0.0", RpcProtocol.MajorVersion, RpcProtocol.MinorVersion));
                return 0;
            case "schema":
                WriteResult(output, "schema", AgentDiscovery.Schema(arguments.GetArgument("command") ?? throw new ArgumentException("A command is required.")));
                return 0;
            case "help":
                WriteResult(output, "help", arguments.GetArgument("command") is { } helpCommand ? AgentDiscovery.Schema(helpCommand) : AgentDiscovery.Capabilities());
                return 0;
            case "schemas.export":
                WriteResult(output, "schemas.export", await AgentDiscovery.ExportAsync(arguments.Get("--path") ?? throw new ArgumentException("--path is required."), cancellationToken).ConfigureAwait(false));
                return 0;
        }

        if (arguments.CommandId == "protocol.inspect")
        {
            return InspectProtocol(arguments, output);
        }

        await using var client = await HostEndpoint.ConnectAsync(applicationRoot, true, cancellationToken).ConfigureAwait(false);
        var handshake = await client.HandshakeAsync(new HandshakeRequest(RpcProtocol.MajorVersion, RpcProtocol.MinorVersion, "cli", culture), cancellationToken).ConfigureAwait(false);
        if (!handshake.Accepted)
        {
            WriteError(output, "RPC_VERSION_MISMATCH", handshake.Error ?? "IPC version mismatch.", arguments.CommandId);
            return 3;
        }

        return await RunAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        WriteError(output, "CANCELLED", "Task cancelled.", arguments.CommandId);
        return 5;
    }
    catch (TimeoutException ex)
    {
        WriteError(output, "TIMEOUT", ex.Message, arguments.CommandId);
        return 4;
    }
    catch (Exception ex) when (ex is FormatException or OverflowException or JsonException)
    {
        WriteError(output, "INVALID_ARGUMENT", ex.Message, arguments.CommandId);
        return 2;
    }
    catch (HostOperationException ex)
    {
        if (output is "json" or "jsonl")
        {
            MachineOutput.WriteError(output, arguments.CommandId, ex.Error);
        }
        else
        {
            Console.Error.WriteLine(ex.Message);
        }

        return ex.Error.Code == "TIMEOUT" ? 4 : 3;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or KeyNotFoundException or RemoteInvocationException)
    {
        WriteError(output, ex is ArgumentException ? "INVALID_ARGUMENT" : "RUNTIME_ERROR", ex.Message, arguments.CommandId);
        return ex is ArgumentException ? 2 : 3;
    }
}
static async Task<int> RunAsync(IHostRpc client, CommandArguments arguments, string output, CancellationToken cancellationToken)
{
    var command = arguments.CommandId.Replace('.', ' ');
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
        case "connections list":
            WriteResult(output, "connections.list", (await client.GetStatusAsync(cancellationToken).ConfigureAwait(false)).Connections);
            return 0;
        case "connections open":
            WriteResult(output, "connections.open", await client.OpenConnectionAsync(new OpenConnectionRequest(ReadSerialOptions(arguments)), cancellationToken).ConfigureAwait(false));
            return 0;
        case "connections close":
            WriteResult(output, "connections.close", await client.CloseConnectionAsync(ParseGuid(arguments.Get("--id"), "--id"), cancellationToken).ConfigureAwait(false));
            return 0;
        case "connections reconnect":
            WriteResult(output, "connections.reconnect", await client.ReconnectConnectionAsync(ParseGuid(arguments.Get("--id"), "--id"), cancellationToken).ConfigureAwait(false));
            return 0;
        case "operations list":
            WriteResult(output, "operations.list", await client.ListOperationsAsync(cancellationToken).ConfigureAwait(false));
            return 0;
        case "operations show":
        case "operations result":
            WriteResult(output, "operations.show", await client.ReadOperationAsync(new OperationQuery(ParseGuid(arguments.Get("--id"), "--id")), cancellationToken).ConfigureAwait(false));
            return 0;
        case "operations cancel":
            WriteResult(output, "operations.cancel", await client.CancelOperationAsync(ParseGuid(arguments.Get("--id"), "--id"), cancellationToken).ConfigureAwait(false));
            return 0;
        case "operations start":
            var operationCommand = arguments.Get("--kind") ?? throw new ArgumentException("--kind is required.");
            var parameters = arguments.Get("--file") is { } parametersPath
                ? await File.ReadAllTextAsync(parametersPath, cancellationToken).ConfigureAwait(false)
                : arguments.Get("--parameters") ?? "{}";
            var started = await client.StartOperationAsync(new OperationRequest(operationCommand, ParseGuid(arguments.Get("--connection"), "--connection"), parameters, arguments.Get("--request-id")), cancellationToken).ConfigureAwait(false);
            WriteResult(output, "operations.start", started);
            return started.Error is null ? 0 : 3;
        case "workspace show":
            WriteResult(output, "workspace.show", await client.GetStatusAsync(cancellationToken).ConfigureAwait(false));
            return 0;
        case "profiles list":
            WriteResult(output, "profiles.list", (await client.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false)).Profiles);
            return 0;
        case "history list":
            WriteResult(output, "history.list", (await client.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false)).SendHistory);
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
        case "loopback run":
        case "modbus read":
        case "modbus write":
        case "modbus scan":
        case "modbus poll":
        case "xmodem send":
        case "xmodem receive":
        case "sequence run":
            return await RunDeviceCommandAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "monitor":
            return await MonitorAsync(client, arguments, output, cancellationToken).ConfigureAwait(false);
        case "protocol inspect":
            return InspectProtocol(arguments, output);
        default:
            WriteError(output, "INVALID_ARGUMENT", $"Unknown command: {arguments.CommandId}");
            return 2;
    }
}

static async Task<int> RunDeviceCommandAsync(IHostRpc client, CommandArguments arguments, string output, CancellationToken cancellationToken)
{
    var command = arguments.CommandId;
    var connectionId = arguments.Get("--connection") is { } shared ? ParseGuid(shared, "--connection") : Guid.Empty;
    var path = arguments.Get("--file") is { } file ? Path.GetFullPath(file) : null;
    object parameters;
    switch (command)
    {
        case "send":
            var data = arguments.Get("--hex") is { } hex ? HexCodec.Parse(hex)
                : Encoding.GetEncoding(arguments.Get("--encoding") ?? "utf-8").GetBytes(arguments.Get("--text") + ParseLineEnding(arguments.Get("--line-ending")));
            parameters = new SendRequest(connectionId, data, "cli.send");
            break;
        case "loopback.run":
            parameters = new LoopbackRequest(connectionId, arguments.GetInt("--length", 4096), arguments.GetInt("--iterations", 1),
                arguments.GetInt("--timeout", 5000), Enum.Parse<LoopbackPattern>(arguments.Get("--pattern") ?? "Incrementing", true), arguments.GetInt("--seed", 0x534257));
            break;
        case "modbus.read":
        case "modbus.write":
            var slave = GetByte(arguments, "--slave", 1);
            var function = GetByte(arguments, "--function", command == "modbus.read" ? 3 : 6);
            var frame = function switch
            {
                1 or 2 or 3 or 4 => ModbusRtuCodec.BuildReadRequest(slave, function, GetUShort(arguments, "--address"), ValidateReadQuantity(GetUShort(arguments, "--quantity"), function)),
                17 => ModbusRtuCodec.BuildReportServerIdRequest(slave),
                5 => ModbusRtuCodec.BuildWriteSingleCoil(slave, GetUShort(arguments, "--address"), GetCoilValue(arguments)),
                6 => ModbusRtuCodec.BuildWriteSingleRegister(slave, GetUShort(arguments, "--address"), GetUShort(arguments, "--value")),
                15 => ModbusRtuCodec.BuildWriteMultipleCoils(slave, GetUShort(arguments, "--address"), ParseCoilValues(arguments.Get("--values"))),
                16 => ModbusRtuCodec.BuildWriteMultipleRegisters(slave, GetUShort(arguments, "--address"), ParseRegisterValues(arguments.Get("--values"))),
                _ => throw new ArgumentException("Unsupported Modbus function."),
            };
            parameters = new ModbusTransactionRequest(connectionId, frame, slave, function, arguments.GetInt("--timeout", 2000));
            break;
        case "modbus.scan":
            parameters = new ModbusScanRequest(connectionId, GetByte(arguments, "--from", 1), GetByte(arguments, "--to", 247),
                GetUShort(arguments, "--address"), arguments.GetInt("--timeout", 200), arguments.GetInt("--interval", 0));
            break;
        case "modbus.poll":
            parameters = new ModbusPollRequest(connectionId, GetByte(arguments, "--slave", 1), GetByte(arguments, "--function", 3),
                GetUShort(arguments, "--address"), GetUShort(arguments, "--quantity"), arguments.GetInt("--count", 10),
                arguments.GetInt("--interval", 1000), arguments.GetInt("--timeout", 2000));
            break;
        case "xmodem.send":
            parameters = await File.ReadAllBytesAsync(path ?? throw new ArgumentException("--file is required."), cancellationToken).ConfigureAwait(false);
            break;
        case "xmodem.receive":
            parameters = new XmodemReceiveRequest(path);
            break;
        case "sequence.run":
            parameters = SerialSequenceCodec.Deserialize(await File.ReadAllTextAsync(path ?? throw new ArgumentException("--file is required."), cancellationToken).ConfigureAwait(false));
            break;
        default:
            throw new ArgumentException($"Unsupported operation: {command}");
    }

    var persistent = arguments.Has("--background") || arguments.Get("--request-id") is not null;
    var options = connectionId == Guid.Empty && persistent ? ReadSerialOptions(arguments) : null;
    Guid? temporaryConnection = null;
    if (connectionId == Guid.Empty && options is null)
    {
        var connection = await OpenAsync(client, arguments, cancellationToken).ConfigureAwait(false);
        connectionId = connection.Id;
        temporaryConnection = connectionId;
    }

    try
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new OperationRequest(command, connectionId, JsonSerializer.Serialize(parameters, OperationJson.Options), arguments.Get("--request-id"), options);
        var started = await client.StartOperationAsync(request, CancellationToken.None).ConfigureAwait(false);
        var operation = started.Operation ?? throw new HostOperationException(started.Error ?? new WorkbenchError("OPERATION_FAILED", "The Host did not accept the operation."));
        if (arguments.Has("--background"))
        {
            WriteResult(output, command, started);
            return operation.Error is null ? 0 : 3;
        }

        var updates = new ActionProgress<OperationProgress>(progress =>
        {
            if (command == "modbus.poll" && output != "json" && progress.ItemJson is { } json)
            {
                var sample = OperationJson.Read<ModbusSample>(json);
                if (output == "jsonl")
                {
                    MachineOutput.Write(output, command, sample, "progress");
                }
                else
                {
                    ConsoleOutput.Write(command, sample);
                }
            }
        });
        var result = await client.WaitOperationAsync<JsonElement>(operation, cancellationToken, updates: updates).ConfigureAwait(false);
        var resultJson = result.GetRawText();
        switch (command)
        {
            case "send":
                var sent = OperationJson.Read<RpcResult>(resultJson);
                var payload = ((SendRequest)parameters).Data;
                WriteResult(output, command, new SendReceipt(sent.Success, payload.Length, Convert.ToHexString(payload), sent.Error));
                return sent.Success ? 0 : 3;
            case "loopback.run":
                var loopback = OperationJson.Read<LoopbackResult>(resultJson);
                WriteResult(output, command, loopback);
                return loopback.Passed ? 0 : 1;
            case "modbus.read":
            case "modbus.write":
                var modbus = OperationJson.Read<ModbusTransactionResult>(resultJson);
                WriteResult(output, command, new ModbusReceipt(modbus.Success, Convert.ToHexString(((ModbusTransactionRequest)parameters).Frame),
                    Convert.ToHexString(modbus.ResponseFrame), modbus.FunctionCode, modbus.Registers, modbus.Bits, modbus.Address, modbus.Value,
                    modbus.ExceptionCode, modbus.Duration.TotalMilliseconds, modbus.Error, modbus.ErrorCode));
                return ModbusExitCode(modbus);
            case "modbus.scan":
            case "modbus.poll":
                var batch = OperationJson.Read<ModbusBatchResult>(resultJson);
                WriteResult(output, command, batch);
                return command == "modbus.poll" && batch.Failed > 0 ? 1 : 0;
            case "xmodem.send":
                var transfer = OperationJson.Read<XmodemTransferResult>(resultJson);
                WriteTransferResult(output, command, path ?? "", transfer);
                return TransferExitCode(transfer);
            case "xmodem.receive":
                var received = OperationJson.Read<XmodemReceiveResult>(resultJson);
                WriteTransferResult(output, command, path ?? "", received.Result, received.Data.Length);
                return TransferExitCode(received.Result);
            default:
                WriteResult(output, command, OperationJson.Read<SerialSequenceProgress>(resultJson));
                return 0;
        }
    }
    finally
    {
        if (temporaryConnection is { } id)
        {
            await client.CloseConnectionAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

static async Task<int> ListSessionsAsync(IHostRpc client, string output, CancellationToken cancellationToken)
{
    var sessions = await client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
    WriteResult(output, "sessions.list", sessions);
    return 0;
}

static async Task<int> ShowSessionAsync(IHostRpc client, CommandArguments arguments, string output, CancellationToken cancellationToken)
{
    var sessionId = ParseGuid(arguments.Get("--id"), "--id");
    var sessions = await client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
    var session = sessions.SingleOrDefault(item => item.Id == sessionId)
        ?? throw new KeyNotFoundException($"Session {sessionId:D} does not exist.");
    var events = await client.ReadAllSessionEventsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    var loopbacks = await client.ReadLoopbackResultsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    WriteResult(output, "sessions.show", new SessionDetails(session, events, loopbacks));
    return 0;
}

static async Task<int> ExportSessionAsync(IHostRpc client, CommandArguments arguments, string output, CancellationToken cancellationToken)
{
    var sessionId = ParseGuid(arguments.Get("--id"), "--id");
    var path = arguments.Get("--file") ?? throw new ArgumentException("sessions export requires --file.");
    var format = arguments.Get("--format")?.ToLowerInvariant() ?? "csv";
    if (format is not ("csv" or "jsonl" or "binary" or "text" or "hex"))
    {
        throw new ArgumentException("--format must be csv, jsonl, binary, text, or hex.");
    }

    var query = CreateSessionEventQuery(arguments, sessionId);

    if (format == "binary")
    {
        long binaryBytes = 0;
        long afterBinarySequence = 0;
        await using (var stream = File.Create(path))
        {
            while (true)
            {
                var events = await client.ReadSessionEventsAsync(query with { AfterSequence = afterBinarySequence }, cancellationToken).ConfigureAwait(false);
                if (events.Count == 0)
                {
                    break;
                }

                foreach (var item in events)
                {
                    await stream.WriteAsync(item.Data, cancellationToken).ConfigureAwait(false);
                    binaryBytes += item.Data.LongLength;
                    afterBinarySequence = item.Sequence;
                }
            }
        }

        WriteResult(output, "sessions.export", new SessionExportReceipt(sessionId, path, format, binaryBytes));
        return 0;
    }

    if (format is "text" or "hex")
    {
        long textBytes = 0;
        long afterTextSequence = 0;
        await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            while (true)
            {
                var events = await client.ReadSessionEventsAsync(query with { AfterSequence = afterTextSequence }, cancellationToken).ConfigureAwait(false);
                if (events.Count == 0)
                {
                    break;
                }

                foreach (var item in events)
                {
                    var line = format == "hex"
                        ? Convert.ToHexString(item.Data)
                        : $"{item.Utc:O} {(item.Direction == SerialDirection.Receive ? "RX" : "TX")} {item.Source} {Convert.ToHexString(item.Data)}";
                    textBytes += await WriteExportLineAsync(writer, line).ConfigureAwait(false);
                    afterTextSequence = item.Sequence;
                }
            }
        }

        WriteResult(output, "sessions.export", new SessionExportReceipt(sessionId, path, format, textBytes));
        return 0;
    }

    if (format == "jsonl")
    {
        long bytes = 0;
        long afterSequence = 0;
        await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            while (true)
            {
                var events = await client.ReadSessionEventsAsync(query with { AfterSequence = afterSequence }, cancellationToken).ConfigureAwait(false);
                if (events.Count == 0)
                {
                    break;
                }

                foreach (var item in events)
                {
                    var line = JsonSerializer.Serialize(item, MachineOutput.CompactOptions);
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    bytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                    afterSequence = item.Sequence;
                }
            }
        }

        WriteResult(output, "sessions.export", new SessionExportReceipt(sessionId, path, format, bytes));
        return 0;
    }

    long bytesWritten = 0;
    long afterCsvSequence = 0;
    await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
    {
        bytesWritten += await WriteExportLineAsync(writer, "utc,direction,source,hex,byte_count").ConfigureAwait(false);
        while (true)
        {
            var events = await client.ReadSessionEventsAsync(query with { AfterSequence = afterCsvSequence }, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                break;
            }

            foreach (var item in events)
            {
                var line = string.Join(',',
                    CsvField(item.Utc.ToString("O", CultureInfo.InvariantCulture)),
                    item.Direction,
                    CsvField(item.Source),
                    Convert.ToHexString(item.Data),
                    item.Data.LongLength.ToString(CultureInfo.InvariantCulture));
                bytesWritten += await WriteExportLineAsync(writer, line).ConfigureAwait(false);
                afterCsvSequence = item.Sequence;
            }
        }
    }

    WriteResult(output, "sessions.export", new SessionExportReceipt(sessionId, path, format, bytesWritten));
    return 0;
}

static async Task<long> WriteExportLineAsync(StreamWriter writer, string line)
{
    await writer.WriteLineAsync(line).ConfigureAwait(false);
    return Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
}

static string CsvField(string value) =>
    value.IndexOfAny([',', '"', '\r', '\n']) >= 0
        ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        : value;

static SessionEventQuery CreateSessionEventQuery(CommandArguments arguments, Guid sessionId) =>
    new(
        sessionId,
        Direction: ParseDirection(arguments.Get("--direction")),
        SourceContains: arguments.Get("--source"),
        DataContainsHex: ParseHexFilter(arguments.Get("--hex")));

static SerialDirection? ParseDirection(string? value) => value?.ToLowerInvariant() switch
{
    null or "all" => null,
    "rx" => SerialDirection.Receive,
    "tx" => SerialDirection.Transmit,
    _ => throw new ArgumentException("--direction must be all, rx, or tx."),
};

static string? ParseHexFilter(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var data = HexCodec.Parse(value);
    if (data.Length == 0)
    {
        throw new ArgumentException("--hex must contain at least one byte.");
    }

    return Convert.ToHexString(data);
}

static async Task<int> DeleteSessionAsync(IHostRpc client, CommandArguments arguments, string output, CancellationToken cancellationToken)
{
    var sessionId = ParseGuid(arguments.Get("--id"), "--id");
    var result = await client.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    WriteResult(output, "sessions.delete", new SessionDeleteReceipt(sessionId, result.Success, result.Error));
    return result.Success ? 0 : 3;
}



static int InspectProtocol(CommandArguments arguments, string output)
{
    var frame = HexCodec.Parse(arguments.Get("--hex") ?? throw new ArgumentException("protocol inspect requires --hex."));
    var template = arguments.Get("--template") is { } templatePath
        ? ProtocolTemplateParser.Inspect(ProtocolTemplateCodec.Deserialize(File.ReadAllText(templatePath)), frame) : null;
    var modbus = template is null ? ModbusRtuCodec.Inspect(frame) : null;
    var valid = template?.IsValid ?? modbus?.IsValid ?? false;
    WriteResult(output, "protocol.inspect", new ProtocolInspectionResult(valid, Convert.ToHexString(frame), modbus, template));
    return valid ? 0 : 1;
}
static void WriteTransferResult(string output, string command, string path, XmodemTransferResult result, int? receivedBytes = null)
{
    var value = new TransferReceipt(path, result.Success, result.BytesTransferred, receivedBytes, result.Blocks,
        result.Retries, result.Duration.TotalMilliseconds, result.Error, result.ErrorCode);
    WriteResult(output, command, value);
}

static int TransferExitCode(XmodemTransferResult result) => result.Success
    ? 0
    : result.ErrorCode == "TIMEOUT" ? 4 : 3;

static Guid ParseGuid(string? value, string name) => Guid.TryParse(value, out var result)
    ? result
    : throw new ArgumentException($"{name} is required and must be a valid GUID.");


static async Task<int> MonitorAsync(IHostRpc client, CommandArguments arguments, string output, CancellationToken cancellationToken)
{
    if (output == "json")
    {
        throw new ArgumentException("监视的结构化输出使用 --output jsonl。");
    }

    var connection = await OpenAsync(client, arguments, cancellationToken).ConfigureAwait(false);
    var seconds = arguments.GetInt("--seconds", 10);
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    var sequence = status.LatestEventSequence;
    var streamId = status.EventStreamId;
    var direction = ParseDirection(arguments.Get("--direction"));
    var source = arguments.Get("--source");
    long eventCount = 0;
    long bytes = 0;
    try
    {
        while (DateTime.UtcNow < deadline)
        {
            var remaining = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            var batch = await client.ReadEventBatchAsync(new EventQuery(sequence, 1000, connection.Id, direction, source, streamId,
                Math.Min(remaining, 1000)), cancellationToken).ConfigureAwait(false);
            if (batch.Gap is not null || batch.ResetRequired)
            {
                if (output == "jsonl")
                {
                    MachineOutput.Write(output, "monitor", new MonitorGap(batch.StreamId, batch.Gap, batch.ResetRequired), "event");
                }
                else
                {
                    ConsoleOutput.Write("monitor.gap", new MonitorGap(batch.StreamId, batch.Gap, batch.ResetRequired));
                }
            }

            foreach (var item in batch.Events)
            {
                sequence = Math.Max(sequence, item.Sequence);
                eventCount++;
                bytes += item.Data.Length;
                if (output == "jsonl")
                {
                    MachineOutput.Write(output, "monitor", item, "event");
                }
                else
                {
                    Console.WriteLine($"{item.Utc:HH:mm:ss.fff} {(item.Direction == SerialDirection.Receive ? "RX" : "TX")} {Convert.ToHexString(item.Data)}");
                }
            }

            sequence = batch.NextSequence;
            streamId = batch.StreamId;
        }

        WriteResult(output, "monitor", new MonitorReceipt(connection.Id, eventCount, bytes, sequence, streamId));
        return 0;
    }
    finally
    {
        await CloseTemporaryConnectionAsync(client, arguments, connection.Id).ConfigureAwait(false);
    }
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

    if (result.ErrorCode == "TIMEOUT")
    {
        return 4;
    }

    return 3;
}

static byte GetByte(CommandArguments arguments, string name, int defaultValue)
{
    var value = arguments.GetInt(name, defaultValue);
    return value is >= byte.MinValue and <= byte.MaxValue
        ? (byte)value
        : throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {byte.MinValue} and {byte.MaxValue}.");
}

static ushort GetUShort(CommandArguments arguments, string name)
{
    var value = arguments.GetInt(name, -1);
    return value is >= ushort.MinValue and <= ushort.MaxValue
        ? (ushort)value
        : throw new ArgumentException($"{name} is required and must be between {ushort.MinValue} and {ushort.MaxValue}.");
}

static bool GetCoilValue(CommandArguments arguments)
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

static async Task<ConnectionSnapshot> OpenAsync(IHostRpc client, CommandArguments arguments, CancellationToken cancellationToken)
{
    if (arguments.Get("--connection") is { } id)
    {
        var connectionId = ParseGuid(id, "--connection");
        var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status.Connections.FirstOrDefault(item => item.Id == connectionId)
            ?? throw new KeyNotFoundException($"Connection {connectionId} was not found.");
    }

    return await client.OpenConnectionAsync(new OpenConnectionRequest(ReadSerialOptions(arguments), false), cancellationToken).ConfigureAwait(false);
}

static Task CloseTemporaryConnectionAsync(IHostRpc client, CommandArguments arguments, Guid connectionId) =>
    arguments.Has("--connection") ? Task.CompletedTask : client.CloseConnectionAsync(connectionId, CancellationToken.None);

static SerialConnectionOptions ReadSerialOptions(CommandArguments arguments)
{
    var port = arguments.Get("--port") ?? throw new ArgumentException("--port is required.");
    return new SerialConnectionOptions(
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
        arguments.GetInt("--rts-after", 0),
        !arguments.Has("--no-reconnect"));
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
        MachineOutput.Write(output, command, value);
        return;
    }

    ConsoleOutput.Write(command, value);
}

static void WriteError(string output, string code, string message, string command = "cli")
{
    if (output is "json" or "jsonl")
    {
        MachineOutput.WriteError(output, command, new WorkbenchError(code, message));
    }
    else
    {
        Console.Error.WriteLine($"{code}: {message}");
    }
}

file sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
