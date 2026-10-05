using SerialWorkbench.Application;
using SerialWorkbench.Cli.Tui;
using SerialWorkbench.Domain;
using SerialWorkbench.Host;
using SerialWorkbench.Storage;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Tests;

public sealed class TuiTests
{
    [Fact]
    public async Task TableRefreshKeepsSelectedMessagesAfterFilteringAndCapacityChanges()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-selection-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var frame = tabs.SubViews.SelectMany(static view => view.SubViews).OfType<FrameView>().Single(static frame => frame.Title == "报文");
        var table = Assert.Single(frame.SubViews.OfType<TableView>());
        frame.SubViews.OfType<CheckBox>().Single(static checkbox => checkbox.Text == "跟随").Value = CheckState.UnChecked;
        var buffer = new TrafficBuffer(System.Text.Encoding.UTF8);
        var connectionId = Guid.NewGuid();
        buffer.SetPresentation(System.Text.Encoding.UTF8, false, true, 0);
        buffer.Append(Enumerable.Range(1, 5).Select(index => new SerialTrafficEvent(index, DateTimeOffset.UtcNow, index, connectionId,
            SerialDirection.Receive, [(byte)index], "test")).ToArray());
        workbench.SetTrafficRows(buffer.Rows.ToArray());
        table.SetSelection(0, 1, false);
        table.SetSelection(0, 3, true);

        workbench.SetTrafficRows(buffer.Rows.Skip(2).ToArray());

        Assert.Equal(1, table.Value?.SelectedCell.Y);
        Assert.Equal([0, 1], table.GetAllSelectedCells().Select(static cell => cell.Y).Distinct().Order());
        workbench.SetTrafficRows([buffer.Rows[2], buffer.Rows[4]]);
        Assert.Equal([0], table.GetAllSelectedCells().Select(static cell => cell.Y).Distinct());
        workbench.SetTrafficRows([]);
        Assert.Null(table.Value);
    }

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
