using System.CommandLine;
using System.Globalization;

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

    private object? Read(string name) => definition.Options.TryGetValue(name, out var option)
        ? result.GetResult(option)?.GetValueOrDefault<object>()
        : null;
}
