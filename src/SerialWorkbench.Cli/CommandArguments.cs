using System.CommandLine;
using System.Globalization;
using SerialWorkbench.Domain;

namespace SerialWorkbench.Cli;

public sealed class CommandArguments(CommandDefinition definition, ParseResult result)
{
    public string CommandId => definition.Id;

    public bool Has(string name) => Read(name) switch
    {
        bool value => value,
        null => false,
        _ => true,
    };

    public string? Get(string name) => Read(name) switch
    {
        null => null,
        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
        var value => value.ToString(),
    };

    public int GetInt(string name, int defaultValue) => Read(name) is int value ? value : defaultValue;

    public bool? GetBool(string name) => Read(name) is bool value ? value : null;

    public SerialConnectionOptions ReadSerialOptions() => new(
        Get("--port") ?? throw new ArgumentException("--port is required."),
        GetInt("--baud", 115200),
        GetInt("--data-bits", 8),
        Enum.Parse<SerialParity>(Get("--parity") ?? "None", true),
        Enum.Parse<SerialStopBits>(Get("--stop-bits") ?? "One", true),
        Enum.Parse<SerialHandshake>(Get("--handshake") ?? "None", true),
        Has("--dtr"),
        Has("--rts"),
        Get("--encoding") ?? "utf-8",
        Enum.Parse<SerialConnectionRole>(Get("--role") ?? "Dut", true),
        Get("--device-id"),
        Has("--rs485"),
        GetInt("--rts-before", 0),
        GetInt("--rts-after", 0),
        !Has("--no-reconnect"));

    public string? GetArgument(string name) => result.GetValue<string?>(name);

    private object? Read(string name) => definition.Options.TryGetValue(name, out var option)
        ? result.GetResult(option)?.GetValueOrDefault<object>()
        : null;
}
