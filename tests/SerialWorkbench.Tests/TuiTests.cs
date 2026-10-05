using SerialWorkbench.Application;
using SerialWorkbench.Cli.Tui;
using SerialWorkbench.Domain;
using SerialWorkbench.Host;
using SerialWorkbench.Protocols;
using SerialWorkbench.Storage;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Tests;

public sealed class TuiTests
{
    [Fact]
    public async Task AutomationDialogKeepsCanceledEditsOutOfSavedSteps()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-dialog-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        workbench.ApplySequence(new SerialSequenceDefinition("测试序列", [new SerialSequenceStep([0x01], "hex", 0, 1, 0, null, 2000, 0)]));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var automation = tabs.TabCollection.Single(static page => page.Title == "自动化");
        var table = Assert.Single(automation.SubViews.OfType<TableView>());
        View? editor = null;
        var edits = 0;
        var timedOut = false;
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromSeconds(5), () =>
        {
            timedOut = true;
            app.RequestStop();
            return false;
        });
        app.Iteration += (_, _) =>
        {
            if (timedOut || failure is not null)
            {
                app.RequestStop();
                return;
            }
            try
            {
                if (app.TopRunnableView is Dialog dialog)
                {
                    dialog.Layout(new System.Drawing.Size(80, 24));
                    var content = dialog.SubViews.Single(static view => view.SubViews.OfType<TextField>().Any(static field => field.Id == "sequence-data"));
                    if (editor is not null)
                    {
                        Assert.Same(editor, content);
                    }
                    editor = content;
                    Assert.All(content.SubViews.Where(static view => view.CanFocus), view => Assert.True(content.Viewport.Contains(view.Frame), view.ToString()));
                    var data = content.SubViews.OfType<TextField>().Single(static field => field.Id == "sequence-data");
                    Assert.Equal("01", data.Text);
                    Assert.Same(data, dialog.MostFocused);
                    dialog.NewKeyDownEvent(Key.A.WithCtrl);
                    dialog.NewKeyDownEvent(new Key('0'));
                    dialog.NewKeyDownEvent(new Key(edits == 0 ? '2' : '3'));
                    Assert.Equal(edits == 0 ? "02" : "03", data.Text);
                    if (edits++ == 0)
                    {
                        dialog.Buttons[0].InvokeCommand(Command.Accept);
                    }
                    else
                    {
                        dialog.NewKeyDownEvent(Key.Enter);
                    }
                    Assert.True(dialog.StopRequested);
                }
                else if (edits == 2)
                {
                    app.RequestStop(workbench.Window);
                }
                else
                {
                    Assert.Equal(new byte[] { 0x01 }, Assert.Single(workbench.ReadSequence().Steps).Data);
                    tabs.Value = automation;
                    table.SetFocus();
                    table.NewKeyDownEvent(Key.Enter);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                app.RequestStop();
            }
        };
        app.Run(workbench.Window);
        Assert.Null(failure);
        Assert.False(timedOut);
        Assert.Equal(2, edits);
        Assert.Equal(new byte[] { 0x03 }, Assert.Single(workbench.ReadSequence().Steps).Data);
        Assert.Null(editor?.SuperView);
    }

    [Fact]
    public async Task TrafficFiltersReceiveInputThroughTheWindow()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-filter-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        workbench.Window.Layout(new System.Drawing.Size(80, 24));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var frame = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<FrameView>().Single(static frame => frame.Title == "报文");
        var filters = Assert.Single(frame.SubViews, static view => view is not TableView && view.SubViews.OfType<TextField>().Any());
        foreach (var field in filters.SubViews.OfType<TextField>().Where(static field => !field.ReadOnly))
        {
            field.SetFocus();
            Assert.Same(field, workbench.Window.MostFocused);
            workbench.Window.NewKeyDownEvent(Key.A);
            Assert.Equal("a", field.Text);
        }
        app.End(token);
    }

    [Theory]
    [InlineData("Modbus", "事务参数")]
    [InlineData("文件与回环", "")]
    public async Task ParameterPanelsFollowKeyboardFocus(string pageName, string panelTitle)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-scrolling-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        workbench.Window.Layout(new System.Drawing.Size(80, 24));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        tabs.Value = tabs.TabCollection.Single(page => page.Title == pageName);
        var panel = panelTitle.Length == 0 ? tabs.Value : tabs.Value.SubViews.OfType<FrameView>().Single(frame => frame.Title == panelTitle);
        var last = panel.SubViews.Where(static view => view.CanFocus).OrderBy(static view => view.Frame.Bottom).Last();
        last.SetFocus();
        Assert.True(panel.Viewport.Contains(last.Frame), $"Focused {last.Frame}, viewport {panel.Viewport}");
        var first = panel.SubViews.Where(static view => view.CanFocus).OrderBy(static view => view.Frame.Top).First();
        first.SetFocus();
        Assert.True(panel.Viewport.Contains(first.Frame));
        Assert.Equal(0, panel.Viewport.X);
        app.End(token);
    }

    [Fact]
    public async Task SettingsShortcutAndDropdownKeysReachTheirControls()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-keyboard-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        workbench.Window.Layout(new System.Drawing.Size(80, 24));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        workbench.Window.NewKeyDownEvent(Key.F8);
        Assert.Equal("设置", tabs.Value?.Title);
        var settings = Assert.IsAssignableFrom<View>(tabs.Value);
        Assert.All(settings.SubViews.Where(static view => view.CanFocus), view => Assert.True(view.Frame.Bottom <= settings.Viewport.Height, view.ToString()));
        var sending = workbench.Window.SubViews.OfType<FrameView>().Single();
        var format = sending.SubViews.OfType<DropDownList>().Single(static field => field.Id == "send-format");
        format.SetFocus();
        format.NewKeyDownEvent(Key.CursorDown);
        Assert.Equal("文本", format.Text);
        format.NewKeyDownEvent(Key.CursorUp);
        Assert.Equal("HEX", format.Text);
        app.End(token);
    }

    [Fact]
    public async Task ConnectionActionsHaveSeparateVisibleHitAreas()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-controls-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        workbench.Window.BeginInit();
        workbench.Window.EndInit();
        workbench.Window.Layout(new System.Drawing.Size(80, 24));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var frame = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<FrameView>().Single(static frame => frame.Title == "连接");
        var close = frame.SubViews.OfType<Button>().Single(static button => button.Text == "关闭");
        var reconnect = frame.SubViews.OfType<Button>().Single(static button => button.Text == "重连");
        Assert.True(close.Frame.Bottom <= reconnect.Frame.Top, $"Close {close.Frame}, reconnect {reconnect.Frame}");
        Assert.True(reconnect.Frame.Bottom <= frame.Viewport.Height);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public async Task WorkbenchLayoutKeepsSendingAndTrafficUsable(int width, int height)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-layout-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        workbench.Window.Layout(new System.Drawing.Size(width, height));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        foreach (var page in tabs.TabCollection)
        {
            tabs.Value = page;
            workbench.Window.Layout(new System.Drawing.Size(width, height));
            Assert.True(page.Frame.Width > 0 && page.Frame.Height > 0, page.Title);
            Assert.All(page.SubViews.OfType<Button>(), button =>
            {
                Assert.True(button.Frame.Right <= page.Viewport.Width, $"{page.Title}: {button.Text}");
                Assert.True(button.Frame.Bottom <= page.Viewport.Height || page.ViewportSettings.HasFlag(ViewportSettingsFlags.HasScrollBars), button.Text);
            });
        }
        var sending = workbench.Window.SubViews.OfType<FrameView>().Single();
        Assert.True(sending.Frame.Bottom <= workbench.Window.Viewport.Height);
        var input = sending.SubViews.OfType<TextField>().Single(static field => field.Id == "send-input");
        Assert.True(input.Frame.Width > 0);
        Assert.True(input.Frame.Right <= sending.Viewport.Width);
        var frame = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<FrameView>().Single(static frame => frame.Title == "报文");
        Assert.True(Assert.Single(frame.SubViews.OfType<TableView>()).Frame.Height >= 3);
        var sessionPage = tabs.TabCollection.Single(static page => page.Title == "会话");
        Assert.All(sessionPage.SubViews.OfType<TableView>(), table => Assert.True(table.Frame.Height >= 3));
    }

    [Fact]
    public async Task WaveformDoesNotJoinIncompleteSamplesAcrossReconnectedSegments()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-waveform-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var graph = tabs.SubViews.SelectMany(static view => view.SubViews).OfType<GraphView>().Single();
        var id = Guid.NewGuid();
        workbench.FeedWaveform(new SerialTrafficEvent(1, DateTimeOffset.UtcNow, 1, id, SerialDirection.Receive, "12"u8.ToArray(), "serial", SegmentId: Guid.NewGuid()));
        workbench.FeedWaveform(new SerialTrafficEvent(2, DateTimeOffset.UtcNow, 2, id, SerialDirection.Receive, "3\n"u8.ToArray(), "serial", SegmentId: Guid.NewGuid()));
        var series = Assert.IsType<ScatterSeries>(Assert.Single(graph.Series));
        Assert.Equal(3, Assert.Single(series.Points).Y);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("jsonl")]
    [InlineData("hex")]
    [InlineData("text")]
    [InlineData("binary")]
    public async Task SessionExportKeepsFiltersAndRawBytesAcrossPages(string format)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-export-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        var token = TestContext.Current.CancellationToken;
        var connection = Guid.NewGuid();
        var events = Enumerable.Range(1, 1001).Select(index => new SerialTrafficEvent(index, DateTimeOffset.UtcNow, index, connection,
            index % 2 == 0 ? SerialDirection.Transmit : SerialDirection.Receive, [0x41], "device,rx")).ToArray();
        await runtime.Sessions.AppendManyAsync(events, token);
        var sessionId = runtime.Sessions.ActiveSession!.Id;
        var query = new SerialWorkbench.Ipc.SessionEventQuery(sessionId, MaximumCount: 100, ConnectionId: connection,
            Direction: SerialDirection.Receive, SourceContains: "device", DataContainsHex: "41");
        var path = Path.Combine(paths.DataRoot, "export." + format);
        var result = await SerialWorkbench.Cli.SessionExporter.ExportAsync(new HostRpcService(runtime), query, path, format, token);
        Assert.Equal(new FileInfo(path).Length, result.Bytes);
        if (format == "binary")
        {
            Assert.Equal(501, result.Bytes);
            Assert.All(await File.ReadAllBytesAsync(path, token), value => Assert.Equal(0x41, value));
        }
        else
        {
            var lines = await File.ReadAllLinesAsync(path, token);
            Assert.Equal(format == "csv" ? 502 : 501, lines.Length);
            if (format == "csv")
            {
                Assert.Contains("\"device,rx\"", lines[1], StringComparison.Ordinal);
            }
            if (format == "jsonl")
            {
                using var document = System.Text.Json.JsonDocument.Parse(lines[^1]);
                Assert.Equal(1001, document.RootElement.GetProperty("sequence").GetInt64());
            }
        }
    }

    [Fact]
    public async Task AutomationEditorKeepsStepDataAndResponseConditions()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-automation-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var original = new SerialSequenceDefinition("读取温度", [new SerialSequenceStep([0x01, 0x03], "hex", 100, 2, 50, "01 03", 2000, 3)]);
        workbench.ApplySequence(original);
        var edited = workbench.ReadSequenceStep();
        Assert.Equal(original.Steps[0].Data, edited.Data);
        Assert.Equal(original.Steps[0].ResponseHex, edited.ResponseHex);
        Assert.Equal(3, edited.RetryCount);
        Assert.Equal(2, edited.RepeatCount);
        Assert.Equal("读取温度", workbench.ReadSequence().Name);
        var reloaded = SerialSequenceCodec.Deserialize(SerialSequenceCodec.Serialize(workbench.ReadSequence()));
        Assert.Equal(original.Steps[0].Data, reloaded.Steps[0].Data);
        Assert.Equal(2000, reloaded.Steps[0].ResponseTimeoutMilliseconds);
    }

    [Fact]
    public async Task SendingUsesItsOwnFormatAndAppendsTheSelectedChecksum()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-sending-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var sending = workbench.Window.SubViews.OfType<FrameView>().Single();
        var input = sending.SubViews.OfType<TextField>().Single(static field => field.Id == "send-input");
        var format = sending.SubViews.OfType<DropDownList>().Single(static field => field.Id == "send-format");
        var checksum = sending.SubViews.OfType<DropDownList>().Single(static field => field.Id == "send-checksum");
        input.Text = "01 03 00 00 00 01";
        checksum.Text = "CRC16 Modbus";
        Assert.Equal("010300000001840A", Convert.ToHexString(workbench.CreateSendRequest(Guid.NewGuid()).Data));
        format.Text = "文本";
        input.Text = "测试";
        checksum.Text = "无校验";
        Assert.Equal("测试"u8.ToArray(), workbench.CreateSendRequest(Guid.NewGuid()).Data);
    }

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
        var traffic = tabs.SubViews.SelectMany(static view => view.SubViews).OfType<FrameView>().Where(static frame => frame.Title == "报文")
            .SelectMany(static frame => frame.SubViews).OfType<TableView>();
        Assert.Single(traffic);
        Assert.Single(workbench.Window.SubViews.OfType<StatusBar>());
        Assert.False(app.Initialized);
    }
}
