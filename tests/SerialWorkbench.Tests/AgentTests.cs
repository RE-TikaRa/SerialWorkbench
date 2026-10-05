using System.Text.Json;
using SerialWorkbench.Cli;
using SerialWorkbench.Domain;

namespace SerialWorkbench.Tests;

public sealed class AgentTests
{
    [Fact]
    public void JsonLinesContainOneCompleteRecordAndEscapePayloadNewlines()
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        MachineOutput.Write("jsonl", "send", new SendReceipt(true, 2, "0D0A", "line\n中文"), writer: writer);
        var line = writer.ToString();

        Assert.Single(line.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(line);
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("result", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("line\n中文", document.RootElement.GetProperty("result").GetProperty("error").GetString());
        Assert.DoesNotContain('\u001b', line);
    }

    [Fact]
    public void FailedRecordsHaveTheSameEnvelopeAndKeepBusyOwnerDetails()
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var connectionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var error = new WorkbenchError("CONNECTION_BUSY", "占用中", connectionId, operationId, "modbus.poll");

        MachineOutput.WriteError("jsonl", "send", error, writer);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("result").ValueKind);
        Assert.Equal(connectionId, root.GetProperty("error").GetProperty("connectionId").GetGuid());
        Assert.Equal(operationId, root.GetProperty("operationId").GetGuid());
    }

    [Fact]
    public void CommandSchemasDescribeConcretePayloadsAndMatchTheCommandTree()
    {
        var catalog = new CommandCatalog();
        var capabilities = AgentDiscovery.Capabilities();
        Assert.Equal(catalog.Commands.Count, capabilities["commands"]?.AsArray().Count);
        foreach (var command in catalog.Commands)
        {
            var schema = AgentDiscovery.Schema(command.Id);
            Assert.Equal(command.Id, schema["output"]?["properties"]?["command"]?["const"]?.GetValue<string>());
            Assert.Equal(2, schema["output"]?["properties"]?["schemaVersion"]?["const"]?.GetValue<int>());
            Assert.NotNull(schema["output"]?["properties"]?["result"]);
        }

        var modbus = AgentDiscovery.Schema("modbus.read");
        Assert.NotNull(modbus["output"]?["properties"]?["result"]?["anyOf"]?[0]?["properties"]?["responseFrame"]);
        Assert.Equal(247, modbus["input"]?["properties"]?["--slave"]?["maximum"]?.GetValue<int>());
    }
}
