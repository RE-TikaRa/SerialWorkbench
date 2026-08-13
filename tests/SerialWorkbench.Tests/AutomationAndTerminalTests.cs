using System.Text;
using SerialWorkbench.Automation;
using SerialWorkbench.Domain;
using SerialWorkbench.Terminal;

namespace SerialWorkbench.Tests;

public sealed class AutomationAndTerminalTests
{
    [Fact]
    public void SendCompilerUsesTheSameExpansionForTextAndLineEnding()
    {
        var item = new SendItem("read", Guid.NewGuid(), "READ ${address}", LineEnding: "\r\n");
        var variables = new Dictionary<string, string> { ["address"] = "17" };

        var bytes = SendCompiler.Compile(item, variables);

        Assert.Equal("READ 17\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void SendCompilerExpandsVariablesBeforeHexParsing()
    {
        var item = new SendItem("binary", Guid.NewGuid(), "AA ${value}", SendContentFormat.Hex);

        var bytes = SendCompiler.Compile(item, new Dictionary<string, string> { ["value"] = "55" });

        Assert.Equal([0xAA, 0x55], bytes);
    }

    [Fact]
    public void ParallelResultUsesDeterministicPriority()
    {
        Assert.Equal(RunResult.RuntimeError, ParallelResultAggregator.Aggregate([RunResult.ValidationFailed, RunResult.RuntimeError, RunResult.TimedOut]));
        Assert.Equal(RunResult.TimedOut, ParallelResultAggregator.Aggregate([RunResult.Cancelled, RunResult.TimedOut]));
        Assert.Equal(RunResult.ValidationFailed, ParallelResultAggregator.Aggregate([RunResult.Passed, RunResult.ValidationFailed]));
        Assert.Equal(RunResult.Cancelled, ParallelResultAggregator.Aggregate([RunResult.Passed, RunResult.Cancelled]));
        Assert.Equal(RunResult.Passed, ParallelResultAggregator.Aggregate([]));
    }

    [Fact]
    public void ParallelExportsRequireExplicitMergeRules()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> branches =
        [
            new Dictionary<string, object?> { ["shared"] = 12, ["items"] = "A" },
            new Dictionary<string, object?> { ["shared"] = 12, ["items"] = "B" },
        ];
        var rules = new Dictionary<string, ExportMergeRule>
        {
            ["shared"] = ExportMergeRule.AllEqual,
            ["items"] = ExportMergeRule.Collect,
        };

        var result = ParallelResultAggregator.MergeExports(branches, rules);

        Assert.Equal(12, result["shared"]);
        Assert.Equal(["A", "B"], Assert.IsType<object?[]>(result["items"]));
        Assert.Throws<InvalidOperationException>(() => ParallelResultAggregator.MergeExports(branches, new Dictionary<string, ExportMergeRule>()));
    }

    [Fact]
    public void TerminalHandlesCursorMovementColorsAndScrolling()
    {
        var terminal = new TerminalState(5, 2);
        terminal.Feed("abc\x1B[31mX\x1B[0m");

        Assert.Equal(TerminalColor.Red, terminal.GetCell(0, 3).Foreground);

        terminal.Feed("\r\nline2\nlast");

        Assert.Equal("last", terminal.GetLine(1));

        terminal.Feed("\x1B[2JZ");
        Assert.Equal("Z", terminal.GetLine(0));
        Assert.Equal(0, terminal.CursorRow);
        Assert.Equal(1, terminal.CursorColumn);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void TerminalRejectsEmptyDimensions(int columns, int rows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalState(columns, rows));
    }
}
