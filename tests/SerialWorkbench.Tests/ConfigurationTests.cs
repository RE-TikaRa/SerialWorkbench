using SerialWorkbench.Domain;
using SerialWorkbench.Host;
using SerialWorkbench.Storage;

namespace SerialWorkbench.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public async Task ClientsShareProfilesAndHistoryWithoutOverwritingEachOthersRecords()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"configuration-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        var token = TestContext.Current.CancellationToken;
        var device = new SerialProfile("设备", "COM16", 115200, 8, SerialParity.None, SerialStopBits.One, SerialHandshake.None, "utf-8", false, false);
        await using (var runtime = new HostRuntime(paths))
        {
            var first = new HostRpcService(runtime);
            var second = new HostRpcService(runtime);
            await Task.WhenAll(first.SaveSerialProfileAsync(new SaveSerialProfileRequest(device), token),
                second.SaveSerialProfileAsync(new SaveSerialProfileRequest(device with { Name = "控制器", Role = SerialConnectionRole.Controller }), token));
            await first.AddSendHistoryAsync("测试", token);
            await second.AddSendHistoryAsync("01 03 00 00", token);
            var configuration = await first.AddSendHistoryAsync("测试", token);
            Assert.Equal(2, configuration.Profiles.Count);
            Assert.Equal(["测试", "01 03 00 00"], configuration.SendHistory);
            Assert.Equal(configuration.Revision, (await second.GetStatusAsync(token)).ConfigurationRevision);

            var renamed = await first.SaveSerialProfileAsync(new SaveSerialProfileRequest(device with { Name = "调试设备" }, device.Name), token);
            Assert.Contains(renamed.Profiles, static item => item.Name == "控制器");
            Assert.DoesNotContain(renamed.Profiles, static item => item.Name == "设备");
            await second.DeleteSerialProfileAsync("控制器", token);
        }

        await using var restored = new HostRuntime(paths);
        var saved = await new HostRpcService(restored).ReadConfigurationAsync(token);
        Assert.Equal("调试设备", Assert.Single(saved.Profiles).Name);
        Assert.Equal(["测试", "01 03 00 00"], saved.SendHistory);
    }
}
