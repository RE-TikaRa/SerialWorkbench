using System.Diagnostics;
using System.Text.Json;
using SerialWorkbench.Cli;

namespace SerialWorkbench.Tests;

public sealed class KillCommandTests
{
    [Theory]
    [InlineData("C:/app", "C:/app/SW.exe", true)]
    [InlineData("C:/app/", "c:/APP/sw_tui.EXE", true)]
    [InlineData("C:/app", "C:/app/SW_CLI.exe", true)]
    [InlineData("C:/app", "C:/app/SW_HOST.exe", true)]
    [InlineData("C:/app", "C:/app/tools/SW.exe", false)]
    [InlineData("C:/app", "C:/app-other/SW.exe", false)]
    [InlineData("C:/app", "C:/other/SW_HOST.exe", false)]
    [InlineData("C:/app", "C:/app/other.exe", false)]
    [InlineData("C:/app", "C:/app/SW.dll", false)]
    public void ProcessSelectionRequiresTheApplicationDirectoryAndExecutableName(string root, string path, bool expected) =>
        Assert.Equal(expected, KillCommand.IsProjectProcess(root, path));

    [Fact]
    public async Task KillWithoutProcessesReturnsSuccessfullyAndExitsItsCaller()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "artifacts", $"kill-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var result = await EntryPointTests.InvokeAsync("SW_CLI", "--agent", "kill", "--app-root", root);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("kill", document.RootElement.GetProperty("command").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        var receipt = document.RootElement.GetProperty("result");
        Assert.Equal(0, receipt.GetProperty("processes").GetArrayLength());
        var callerId = receipt.GetProperty("callerProcessId").GetInt32();
        Assert.NotEqual(Environment.ProcessId, callerId);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(callerId));
    }

    [Fact]
    public async Task KillStopsOnlyTheSelectedInstallationsProcessesAndHandlesAnUnresponsiveHost()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "artifacts", $"kill-processes-{Guid.NewGuid():N}");
        var otherRoot = Path.Combine(root, "other");
        Directory.CreateDirectory(otherRoot);
        var processes = new List<Process>();
        try
        {
            foreach (var name in new[] { "SW", "SW_TUI", "SW_CLI", "SW_HOST" })
            {
                processes.Add(StartWaitingProcess(root, name));
            }
            var other = StartWaitingProcess(otherRoot, "SW_CLI");
            processes.Add(other);
            var targetIds = processes.Take(4).Select(static process => process.Id).ToArray();
            var result = await EntryPointTests.InvokeAsync("SW_CLI", "--agent", "kill", "--app-root", root, "--timeout", "500");

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Error);
            using var document = JsonDocument.Parse(result.Output);
            Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
            var receipt = document.RootElement.GetProperty("result");
            var stopped = receipt.GetProperty("processes").EnumerateArray().ToArray();
            Assert.Equal(targetIds.Order(), stopped.Select(static item => item.GetProperty("processId").GetInt32()).Order());
            Assert.All(stopped, item => Assert.True(item.GetProperty("stopped").GetBoolean()));
            Assert.True(stopped.Single(static item => item.GetProperty("name").GetString() == "SW_HOST").GetProperty("forced").GetBoolean());
            Assert.False(receipt.GetProperty("hostStoppedGracefully").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, receipt.GetProperty("gracefulShutdownError").ValueKind);
            Assert.False(other.HasExited);
            Assert.All(processes.Take(4), process => Assert.True(process.HasExited));
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                }
                process.Dispose();
            }
        }
    }

    private static Process StartWaitingProcess(string root, string name)
    {
        var path = Path.Combine(root, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), path);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/q");
        process.StartInfo.ArgumentList.Add("/k");
        Assert.True(process.Start());
        Assert.False(process.HasExited);
        return process;
    }
}
