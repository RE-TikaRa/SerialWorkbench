using SerialWorkbench.Cli;

namespace SerialWorkbench.Tests;

public sealed class CliCommandTests
{
    [Fact]
    public void DeviceCommandsExposeBackgroundExecutionAndPersistentRequests()
    {
        var catalog = CreateCatalog();
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
        var catalog = CreateCatalog();
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
        var parsed = CreateCatalog().Root.Parse(arguments);

        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void ResponseFilesUseTheCommandLibraryTokenizer()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "artifacts", $"cli-response-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "send.rsp");
        File.WriteAllText(file, "send\n--port COM16\n--text \"中文 参数\"\n--baud 19200\n");
        var catalog = CreateCatalog();

        var parsed = catalog.Root.Parse([$"@{file}"]);
        var arguments = catalog.Commands.Single(static item => item.Id == "send").Bind(parsed);

        Assert.Empty(parsed.Errors);
        Assert.Equal("中文 参数", arguments.Get("--text"));
        Assert.Equal(19200, arguments.GetInt("--baud", 115200));
    }

    [Theory]
    [InlineData("01 03 00 00 00 01", "Crc16Modbus", "010300000001840A")]
    [InlineData("01 02 03", "Xor", "01020300")]
    public void SendingAppendsTheSelectedChecksum(string hex, string checksum, string expected)
    {
        var catalog = CreateCatalog();
        var parsed = catalog.Root.Parse(["send", "--port", "COM16", "--hex", hex, "--checksum", checksum]);
        Assert.Empty(parsed.Errors);
        var request = catalog.Commands.Single(static command => command.Id == "send").Bind(parsed).CreateSendRequest(Guid.NewGuid());
        Assert.Equal(expected, Convert.ToHexString(request.Data));
    }

    [Fact]
    public void RepeatedSendingKeepsItsCountAndBackgroundOptions()
    {
        var catalog = CreateCatalog();
        var parsed = catalog.Root.Parse(["send", "repeat", "--port", "COM16", "--text", "测试", "--count", "0", "--interval", "50", "--background"]);
        Assert.Empty(parsed.Errors);
        var arguments = catalog.Commands.Single(static command => command.Id == "send.repeat").Bind(parsed);
        Assert.Equal(0, arguments.GetInt("--count", 1));
        Assert.Equal(50, arguments.GetInt("--interval", 1000));
        Assert.True(arguments.Has("--background"));
        Assert.Equal("测试"u8.ToArray(), arguments.CreateSendRequest(Guid.NewGuid()).Data);
    }

    [Fact]
    public void ProgressQueriesAcceptLargeRevisionValues()
    {
        var catalog = CreateCatalog();
        var parsed = catalog.Root.Parse(["operations", "progress", "--id", Guid.NewGuid().ToString(), "--after", "4294967296", "--count", "25"]);
        Assert.Empty(parsed.Errors);
        var arguments = catalog.Commands.Single(static command => command.Id == "operations.progress").Bind(parsed);
        Assert.Equal(4294967296L, arguments.GetLong("--after", 0));
        Assert.Equal(25, arguments.GetInt("--count", 1000));
    }

    private static CommandCatalog CreateCatalog()
    {
        var catalog = new CommandCatalog();
        foreach (var definition in catalog.Commands)
        {
            definition.Command.SetAction(_ => 0);
        }
        return catalog;
    }
}
