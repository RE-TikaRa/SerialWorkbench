using SerialWorkbench.Cli;
using SerialWorkbench.Domain;
using SerialWorkbench.Host;
using SerialWorkbench.Storage;

namespace SerialWorkbench.Tests;

public sealed class ConnectionCommandTests
{
    [Fact]
    public async Task ProfileCommandsPreserveDeviceSettingsAcrossRenameAndRestart()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"cli-profiles-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        var token = TestContext.Current.CancellationToken;
        await using (var runtime = new HostRuntime(paths))
        {
            var client = new HostRpcService(runtime);
            var saved = Assert.IsType<ConfigurationSnapshot>(await ConnectionCommands.ExecuteAsync(client,
                Parse("profiles.save", "profiles", "save", "--name", "调试设备", "--port", "COM16", "--baud", "9600", "--encoding", "gb18030",
                    "--dtr", "--device-id", "USB\\DEVICE", "--rs485", "--rts-before", "5"), token));
            var profile = Assert.Single(saved.Profiles);
            Assert.Equal(9600, profile.BaudRate);
            Assert.Equal("gb18030", profile.EncodingName);
            Assert.True(profile.DtrEnable);
            Assert.Equal(5, profile.RtsBeforeSendMilliseconds);
            await ConnectionCommands.ExecuteAsync(client, Parse("profiles.rename", "profiles", "rename", "--name", "调试设备", "--new-name", "控制器"), token);
            var options = await ConnectionCommands.ReadOptionsAsync(client, Parse("connections.open", "connections", "open", "--profile", "控制器"), token);
            Assert.Equal(profile.DeviceInstanceId, options.DeviceInstanceId);
            Assert.Equal(profile.EncodingName, options.EncodingName);
            Assert.Equal(profile.BaudRate, options.BaudRate);
            Assert.True(options.Rs485Mode);
        }
        await using var restored = new HostRuntime(paths);
        var service = new HostRpcService(restored);
        var reloaded = Assert.IsType<SerialProfile>(await ConnectionCommands.ExecuteAsync(service,
            Parse("profiles.show", "profiles", "show", "--name", "控制器"), token));
        Assert.Equal("COM16", reloaded.PortName);
        var deleted = Assert.IsType<ConfigurationSnapshot>(await ConnectionCommands.ExecuteAsync(service,
            Parse("profiles.delete", "profiles", "delete", "--name", "控制器"), token));
        Assert.Empty(deleted.Profiles);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ConnectionCommands.ExecuteAsync(service,
            Parse("profiles.show", "profiles", "show", "--name", "控制器"), token));
    }

    [Fact]
    public void ControlLineArgumentsDistinguishFalseFromAnUnspecifiedLine()
    {
        var arguments = Parse("connections.control-lines", "connections", "control-lines", "--id", Guid.NewGuid().ToString(), "--dtr", "false");
        Assert.Equal(false, arguments.GetBool("--dtr"));
        Assert.Null(arguments.GetBool("--rts"));
    }

    [Theory]
    [InlineData("connections", "open")]
    [InlineData("connections", "open", "--profile")]
    [InlineData("connections", "open", "--port", "COM16", "--profile", "设备")]
    [InlineData("connections", "open", "--profile", "设备", "--baud", "9600")]
    [InlineData("connections", "control-lines", "--id", "00000000-0000-0000-0000-000000000001")]
    [InlineData("connections", "control-lines", "--id", "00000000-0000-0000-0000-000000000001", "--dtr")]
    [InlineData("connections", "control-lines", "--id", "00000000-0000-0000-0000-000000000001", "--dtr", "invalid")]
    [InlineData("connections", "clear-buffers", "--id", "00000000-0000-0000-0000-000000000001")]
    [InlineData("connections", "break", "--id", "00000000-0000-0000-0000-000000000001", "--duration", "0")]
    public void InvalidConnectionCommandsAreRejectedDuringParsing(params string[] arguments) =>
        Assert.NotEmpty(new CommandCatalog().Root.Parse(arguments).Errors);

    private static CommandArguments Parse(string command, params string[] arguments)
    {
        var catalog = new CommandCatalog();
        var parsed = catalog.Root.Parse(arguments);
        Assert.Empty(parsed.Errors);
        return catalog.Commands.Single(item => item.Id == command).Bind(parsed);
    }
}
