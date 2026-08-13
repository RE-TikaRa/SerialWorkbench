using System.Text;
using System.Text.RegularExpressions;
using SerialWorkbench.Domain;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.Automation;

public static partial class SendCompiler
{
    public static byte[] Compile(SendItem item, IReadOnlyDictionary<string, string>? variables = null)
    {
        var expanded = VariablePattern().Replace(item.Content, match => Resolve(match.Groups[1].Value, variables));
        byte[] content = item.Format switch
        {
            SendContentFormat.Text => Encoding.GetEncoding(item.EncodingName).GetBytes(expanded),
            SendContentFormat.Hex => HexCodec.Parse(expanded),
            _ => throw new ArgumentOutOfRangeException(nameof(item)),
        };

        if (string.IsNullOrEmpty(item.LineEnding))
        {
            return content;
        }

        var lineEnding = Encoding.GetEncoding(item.EncodingName).GetBytes(item.LineEnding);
        return [.. content, .. lineEnding];
    }

    private static string Resolve(string name, IReadOnlyDictionary<string, string>? variables)
    {
        if (string.Equals(name, "timestamp", StringComparison.OrdinalIgnoreCase))
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (variables is not null && variables.TryGetValue(name, out var value))
        {
            return value;
        }

        throw new KeyNotFoundException($"Variable '{name}' was not provided.");
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_.-]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}
