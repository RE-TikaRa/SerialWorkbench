using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;

namespace SerialWorkbench.Cli;

public static class AgentDiscovery
{
    private static readonly JsonSerializerOptions schemaOptions = new(MachineOutput.CompactOptions)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static JsonObject Capabilities()
    {
        var commands = new JsonArray();
        foreach (var definition in new CommandCatalog().Commands)
        {
            var options = new JsonArray();
            foreach (var option in definition.Options.Values)
            {
                options.Add(new JsonObject
                {
                    ["name"] = option.Name,
                    ["description"] = option.Description,
                    ["required"] = option.Required,
                    ["schema"] = ParameterSchema(definition, option.Name),
                });
            }

            commands.Add(new JsonObject
            {
                ["command"] = definition.Id,
                ["description"] = definition.Command.Description,
                ["requiresHost"] = definition.Id is not ("capabilities" or "version" or "schema" or "help" or "schemas.export" or "protocol.inspect" or "kill"),
                ["streaming"] = definition.Id is "monitor" or "modbus.poll",
                ["options"] = options,
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["platform"] = "windows",
            ["rpcVersion"] = $"{RpcProtocol.MajorVersion}.{RpcProtocol.MinorVersion}",
            ["formats"] = new JsonArray("json", "jsonl"),
            ["commands"] = commands,
        };
    }

    public static JsonObject Schema(string command)
    {
        var definition = new CommandCatalog().Commands.SingleOrDefault(item => item.Id == command)
            ?? throw new ArgumentException($"Unknown command: {command}", nameof(command));
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var option in definition.Options.Values)
        {
            properties.Add(option.Name, ParameterSchema(definition, option.Name));
            if (option.Required)
            {
                required.Add(option.Name);
            }
        }

        var input = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required };
        if (command == "connections.open")
        {
            input["oneOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("--port") },
                new JsonObject { ["required"] = new JsonArray("--profile") });
        }
        foreach (var argument in definition.Command.Arguments)
        {
            properties.Add(argument.Name, schemaOptions.GetJsonSchemaAsNode(argument.ValueType));
            if (argument.Arity.MinimumNumberOfValues > 0)
            {
                required.Add(argument.Name);
            }
        }

        var payloadType = ResultType(command);
        var output = EnvelopeSchema(payloadType, command);
        if (definition.Options.ContainsKey("--background"))
        {
            var result = output["properties"]?.AsObject() ?? throw new InvalidDataException("The envelope schema has no properties.");
            result["result"] = new JsonObject { ["anyOf"] = new JsonArray(result["result"]?.DeepClone(), schemaOptions.GetJsonSchemaAsNode(typeof(StartOperationResult))) };
        }
        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["command"] = command,
            ["input"] = input,
            ["output"] = output,
            ["events"] = command switch
            {
                "monitor" => new JsonObject { ["oneOf"] = new JsonArray(EnvelopeSchema(typeof(SerialTrafficEvent), command, "event"), EnvelopeSchema(typeof(MonitorGap), command, "event")) },
                "modbus.poll" => EnvelopeSchema(typeof(ModbusSample), command, "progress"),
                _ => null,
            },
        };
    }

    public static async Task<string> ExportAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(path);
        var definitions = new CommandCatalog().Commands;
        foreach (var definition in definitions)
        {
            var schema = Schema(definition.Id);
            await File.WriteAllTextAsync(Path.Combine(path, $"{definition.Id}.schema.json"), schema.ToJsonString(MachineOutput.DocumentOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }

        var resultSchemas = new JsonArray();
        foreach (var definition in definitions)
        {
            resultSchemas.Add(Schema(definition.Id)["output"]?.DeepClone());
        }

        await File.WriteAllTextAsync(Path.Combine(path, "cli-result.schema.json"), new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["oneOf"] = resultSchemas,
        }.ToJsonString(MachineOutput.DocumentOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        var eventSchema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["oneOf"] = new JsonArray(EnvelopeSchema(typeof(SerialTrafficEvent), "monitor", "event"), EnvelopeSchema(typeof(MonitorGap), "monitor", "event")),
        };
        await File.WriteAllTextAsync(Path.Combine(path, "cli-event.schema.json"), eventSchema.ToJsonString(MachineOutput.DocumentOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        var error = EnvelopeSchema(typeof(object), null);
        var errorProperties = error["properties"]?.AsObject() ?? throw new InvalidDataException("The error schema has no properties.");
        errorProperties["result"] = new JsonObject { ["type"] = "null" };
        errorProperties["status"] = new JsonObject { ["enum"] = new JsonArray("error", "cancelled") };
        if (errorProperties["error"] is JsonObject errorObject)
        {
            errorObject["type"] = "object";
        }
        await File.WriteAllTextAsync(Path.Combine(path, "cli-error.schema.json"), error.ToJsonString(MachineOutput.DocumentOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        return Path.GetFullPath(path);
    }

    private static JsonNode ParameterSchema(CommandDefinition definition, string name)
    {
        var option = definition.Options[name];
        var node = schemaOptions.GetJsonSchemaAsNode(Nullable.GetUnderlyingType(option.ValueType) ?? option.ValueType);
        if (node is JsonObject schema)
        {
            if (name is not ("--app-root" or "--culture") && definition.DefaultValues.TryGetValue(name, out var value) && value is not null
                && (!definition.Ranges.TryGetValue(name, out var bounds) || value is not int number || number >= bounds.Minimum && number <= bounds.Maximum))
            {
                schema["default"] = JsonSerializer.SerializeToNode(value, schemaOptions);
            }

            if (definition.Ranges.TryGetValue(name, out var range))
            {
                schema["minimum"] = range.Minimum;
                schema["maximum"] = range.Maximum;
            }

            if (definition.Choices.TryGetValue(name, out var choices))
            {
                schema["enum"] = option.ValueType == typeof(int)
                    ? new JsonArray(choices.Select(static value => JsonValue.Create(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))).ToArray())
                    : new JsonArray(choices.Select(static value => JsonValue.Create(value)).ToArray());
            }
        }

        return node;
    }

    private static JsonObject EnvelopeSchema(Type payloadType, string? command, string type = "result")
    {
        var schema = schemaOptions.GetJsonSchemaAsNode(typeof(MachineRecord<>).MakeGenericType(payloadType)).AsObject();
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["additionalProperties"] = false;
        var properties = schema["properties"]?.AsObject() ?? throw new InvalidDataException("The envelope schema has no properties.");
        properties["schemaVersion"] = new JsonObject { ["const"] = 2 };
        properties["type"] = new JsonObject { ["const"] = type };
        properties["status"] = new JsonObject { ["enum"] = new JsonArray("success", "running", "error", "cancelled") };
        if (command is not null)
        {
            properties["command"] = new JsonObject { ["const"] = command };
        }

        schema["required"] = new JsonArray("schemaVersion", "command", "type", "status", "time", "result", "error", "operationId");
        return schema;
    }

    private static Type ResultType(string command) => command switch
    {
        "capabilities" or "schema" or "help" => typeof(JsonObject),
        "schemas.export" => typeof(string),
        "version" => typeof(VersionInfo),
        "kill" => typeof(KillReceipt),
        "ports.list" => typeof(SerialPortDescriptor[]),
        "profiles.list" => typeof(SerialProfile[]),
        "profiles.show" => typeof(SerialProfile),
        "profiles.save" or "profiles.rename" or "profiles.delete" => typeof(ConfigurationSnapshot),
        "history.list" => typeof(string[]),
        "host.status" or "workspace.show" or "workspace.set" or "workspace.clear" => typeof(HostStatusDto),
        "host.stop" or "connections.close" or "connections.control-lines" or "connections.clear-buffers" or "connections.break" => typeof(RpcResult),
        "connections.list" => typeof(ConnectionSnapshot[]),
        "connections.open" or "connections.reconnect" => typeof(ConnectionSnapshot),
        "operations.list" => typeof(OperationSnapshot[]),
        "operations.start" => typeof(StartOperationResult),
        "operations.show" or "operations.result" or "operations.cancel" => typeof(OperationSnapshot),
        "operations.wait" => typeof(OperationSnapshot),
        "operations.progress" => typeof(OperationProgressBatch),
        "sessions.list" => typeof(SessionDescriptor[]),
        "sessions.show" => typeof(SessionDetails),
        "sessions.export" => typeof(SessionExportReceipt),
        "sessions.delete" => typeof(SessionDeleteReceipt),
        "send" => typeof(SendReceipt),
        "send.repeat" => typeof(RepeatSendReceipt),
        "monitor" => typeof(MonitorReceipt),
        "loopback.run" => typeof(LoopbackResult),
        "modbus.read" or "modbus.write" => typeof(ModbusReceipt),
        "modbus.scan" or "modbus.poll" => typeof(ModbusBatchResult),
        "xmodem.send" or "xmodem.receive" => typeof(TransferReceipt),
        "sequence.run" => typeof(SerialSequenceProgress),
        "protocol.inspect" => typeof(ProtocolInspectionResult),
        _ => throw new ArgumentException($"Unknown command: {command}", nameof(command)),
    };
}
