using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Globalization;

namespace SerialWorkbench.Cli;

public sealed class ExecutableHelpAction(string executableName) : SynchronousCommandLineAction
{
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult parseResult)
    {
        var output = parseResult.InvocationConfiguration.Output;
        using var rendered = new StringWriter(CultureInfo.InvariantCulture);
        parseResult.InvocationConfiguration.Output = rendered;
        int exitCode;
        try
        {
            exitCode = new HelpAction().Invoke(parseResult);
        }
        finally
        {
            parseResult.InvocationConfiguration.Output = output;
        }

        // System.CommandLine uses the managed assembly name in usage lines.
        var prefix = "  " + parseResult.RootCommandResult.Command.Name;
        using var lines = new StringReader(rendered.ToString());
        while (lines.ReadLine() is { } line)
        {
            output.WriteLine(line.StartsWith(prefix + " ", StringComparison.Ordinal)
                ? "  " + executableName + line[prefix.Length..] : line);
        }
        return exitCode;
    }
}
