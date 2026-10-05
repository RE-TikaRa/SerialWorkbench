using SerialWorkbench.Cli.Tui;
using SerialWorkbench.Host;
using SerialWorkbench.Storage;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace SerialWorkbench.Tests;

public sealed class TuiTests
{
    [Fact]
    public async Task WorkbenchUsesNativeControlsWithoutInitializingATerminal()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));

        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var traffic = tabs.SubViews.SelectMany(static view => view.SubViews).OfType<FrameView>()
            .SelectMany(static frame => frame.SubViews).OfType<TableView>();
        Assert.Single(traffic);
        Assert.Single(workbench.Window.SubViews.OfType<StatusBar>());
        Assert.False(app.Initialized);
    }
}
