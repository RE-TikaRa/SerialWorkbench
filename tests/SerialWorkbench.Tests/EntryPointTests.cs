using System.Diagnostics;
using System.Text.Json;

namespace SerialWorkbench.Tests;

public sealed class EntryPointTests
{
    [Fact]
    public async Task CliWithoutArgumentsShowsHelp()
    {
        var result = await InvokeAsync("SW_CLI");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SW_CLI", result.Output, StringComparison.Ordinal);
        Assert.Contains("ports", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("--agent")]
    [InlineData("--output", "json")]
    public async Task CliMachineHelpKeepsTheVersionedContract(params string[] arguments)
    {
        var result = await InvokeAsync("SW_CLI", arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("help", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TuiHelpDescribesItsOwnOptions()
    {
        var result = await InvokeAsync("SW_TUI", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SW_TUI", result.Output, StringComparison.Ordinal);
        Assert.Contains("--app-root", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ports", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task TuiRequiresAnInteractiveTerminal()
    {
        var result = await InvokeAsync("SW_TUI");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("TUI 需要交互式终端", result.Error, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(string application, params string[] arguments)
    {
        var token = TestContext.Current.CancellationToken;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, application + ".exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, await output, await error);
    }
}
