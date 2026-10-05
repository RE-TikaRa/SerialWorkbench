using SerialWorkbench.Cli;

namespace SerialWorkbench.Tests;

public sealed class CliCommandTests
{
    [Fact]
    public void DeviceCommandsExposeBackgroundExecutionAndPersistentRequests()
    {
        var catalog = new CommandCatalog();
        var parsed = catalog.Root.Parse(["send", "--port", "COM16", "--text", "测试", "--background", "--request-id", "send-001"]);
        Assert.Empty(parsed.Errors);
        var definition = catalog.Commands.Single(static item => item.Id == "send");
        var arguments = definition.Bind(parsed);
        Assert.True(arguments.Has("--background"));
        Assert.Equal("send-001", arguments.Get("--request-id"));
    }

    [Fact]
    public void TypedOptionsPreserveBooleanFlagsAndQuotedText()
    {
        var catalog = new CommandCatalog();
        var parsed = catalog.Root.Parse(["send", "--port", "COM16", "--dtr", "--text", "温度 25", "--baud", "9600"]);
        Assert.Empty(parsed.Errors);
        var arguments = catalog.Commands.Single(static item => item.Id == "send").Bind(parsed);

        Assert.True(arguments.Has("--dtr"));
        Assert.False(arguments.Has("--rts"));
        Assert.Equal("温度 25", arguments.Get("--text"));
        Assert.Equal(9600, arguments.GetInt("--baud", 115200));
    }

    [Theory]
    [InlineData("send", "--port", "COM16", "--hex", "01", "--unknown")]
    [InlineData("send", "--port", "COM16", "--hex", "01", "--baud", "-1")]
    [InlineData("send", "--port", "COM16", "--hex", "01", "--baud", "abc")]
    [InlineData("send", "--port", "COM16")]
    [InlineData("send", "--port", "COM16", "--hex", "01", "--text", "x")]
    [InlineData("modbus", "read", "--port", "COM16", "--slave", "248")]
    [InlineData("modbus", "write", "--port", "COM16", "--function", "abc")]
    [InlineData("modbus", "write", "--port", "COM16", "--value", "abc")]
    public void InvalidCommandsAreRejectedDuringParsing(params string[] arguments)
    {
        var parsed = new CommandCatalog().Root.Parse(arguments);

        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void ResponseFilesUseTheCommandLibraryTokenizer()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "artifacts", $"cli-response-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "send.rsp");
        File.WriteAllText(file, "send\n--port COM16\n--text \"中文 参数\"\n--baud 19200\n");
        var catalog = new CommandCatalog();

        var parsed = catalog.Root.Parse([$"@{file}"]);
        var arguments = catalog.Commands.Single(static item => item.Id == "send").Bind(parsed);

        Assert.Empty(parsed.Errors);
        Assert.Equal("中文 参数", arguments.Get("--text"));
        Assert.Equal(19200, arguments.GetInt("--baud", 115200));
    }
}
