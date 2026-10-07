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
    public async Task TerminalColorDetectionPreservesDraftsSelectionAndErrors()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-terminal-theme-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(80, 24);
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        var input = SendingPanel(workbench).SubViews.OfType<TextField>().Single(static field => field.Id == "send-input");
        input.Text = "测试 e\u0301";
        input.SetFocus();
        await workbench.RunUiAsync(() => Task.FromException(new IOException("设备已断开")));
        app.LayoutAndDraw(true);
        var message = workbench.Window.SubViews.OfType<Label>().Single(static label => label.Id == "feedback-message");
        var setColors = driver.GetType().GetMethod("SetDefaultAttribute", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(setColors);
        foreach (var background in new[] { "#f5f6fa", "#171b2b" })
        {
            setColors.Invoke(driver, [new Terminal.Gui.Drawing.Attribute("#404040", background)]);
            app.LayoutAndDraw(true);
            var expectedBackground = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                ? new Terminal.Gui.Drawing.Color(background) : Terminal.Gui.Drawing.Color.None;
            Assert.Equal(expectedBackground, workbench.Window.GetScheme().Normal.Background);
            Assert.Equal("测试 e\u0301", input.Text);
            Assert.Same(input, workbench.Window.MostFocused);
            Assert.Equal("设备已断开", message.Text);
            Assert.NotEqual(input.GetScheme().Normal, message.GetScheme().Normal);
        }
        app.End(token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActivityFeedbackWaitsForSlowActionsAndStopsAfterAllActionsFinish(bool animated)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-activity-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var settings = tabs.TabCollection.Single(static page => page.Title == "5 设置");
        settings.SubViews.OfType<CheckBox>().Single(static checkbox => checkbox.Id == "tui-animations").Value = animated ? CheckState.Checked : CheckState.UnChecked;
        var spinner = Assert.Single(workbench.Window.SubViews.OfType<SpinnerView>());
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task quick = Task.CompletedTask;
        Task firstAction = Task.CompletedTask;
        Task secondAction = Task.CompletedTask;
        var stage = 0;
        var timedOut = false;
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { timedOut = true; app.RequestStop(); return false; });
        app.Iteration += (_, _) =>
        {
            if (failure is not null || timedOut)
            {
                app.RequestStop();
                return;
            }
            try
            {
                if (stage == 0)
                {
                    quick = workbench.RunUiAsync(() => Task.CompletedTask, "快速刷新");
                    stage = 1;
                }
                else if (stage == 1 && quick.IsCompleted)
                {
                    Assert.False(spinner.Visible);
                    firstAction = workbench.RunUiAsync(() => first.Task, "刷新端口");
                    secondAction = workbench.RunUiAsync(() => second.Task, "读取会话");
                    Assert.False(spinner.Visible);
                    app.AddTimeout(TimeSpan.FromMilliseconds(220), () => { stage = 2; return false; });
                    stage = -1;
                }
                else if (stage == 2)
                {
                    Assert.Equal(animated, spinner.Visible);
                    Assert.Equal(animated, spinner.AutoSpin);
                    Assert.All(spinner.Sequence.SelectMany(static frame => frame), value => Assert.True(char.IsAscii(value)));
                    first.SetResult();
                    stage = 3;
                }
                else if (stage == 3 && firstAction.IsCompleted)
                {
                    Assert.Equal(animated, spinner.AutoSpin);
                    second.SetResult();
                    stage = 4;
                }
                else if (stage == 4 && secondAction.IsCompleted)
                {
                    Assert.False(spinner.Visible);
                    Assert.False(spinner.AutoSpin);
                    stage = 5;
                    app.RequestStop(workbench.Window);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                first.TrySetResult();
                second.TrySetResult();
                app.RequestStop();
            }
        };
        app.Run(workbench.Window);
        Assert.Null(failure);
        Assert.False(timedOut);
        Assert.Equal(5, stage);
        await Task.WhenAll(quick, firstAction, secondAction);
    }

    [Fact]
    public async Task AnErrorReplacesTransientFeedbackAndKeepsItsStyle()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-feedback-error-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var title = workbench.Window.SubViews.OfType<Label>().Single(static label => label.Id == "workbench-title");
        var stage = 0;
        var timedOut = false;
        Task failed = Task.CompletedTask;
        Terminal.Gui.Drawing.Scheme? errorStyle = null;
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { timedOut = true; app.RequestStop(); return false; });
        app.Iteration += (_, _) =>
        {
            if (failure is not null || timedOut)
            {
                app.RequestStop();
                return;
            }
            try
            {
                var message = workbench.Window.SubViews.OfType<Label>().Single(static label => label.Id == "feedback-message");
                if (stage == 1)
                {
                    Assert.Equal(title.GetScheme(), message.GetScheme());
                    failed = workbench.RunUiAsync(() => Task.FromException(new IOException("设备已断开")));
                    stage = 2;
                }
                else if (stage == 2 && failed.IsCompleted)
                {
                    errorStyle = message.GetScheme();
                    app.AddTimeout(TimeSpan.FromMilliseconds(2100), () => { stage = 3; return false; });
                    stage = -1;
                }
                else if (stage == 3)
                {
                    Assert.Equal("设备已断开", message.Text);
                    Assert.Equal(errorStyle, message.GetScheme());
                    stage = 4;
                    app.RequestStop(workbench.Window);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                app.RequestStop();
            }
        };
        workbench.ShowMessage("已复制", title.GetScheme(), true);
        stage = 1;
        app.Run(workbench.Window);
        Assert.Null(failure);
        Assert.False(timedOut);
        Assert.Equal(4, stage);
        await failed;
    }

    [Theory]
    [InlineData(60, 20)]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public async Task FocusAndSelectionRemainDistinctAcrossTheWorkbench(int width, int height)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-focus-style-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(width, height);
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var frame = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<FrameView>().Single(static frame => frame.Title == "报文");
        var table = Assert.Single(frame.SubViews.OfType<TableView>());
        var sending = SendingPanel(workbench);
        var input = Assert.Single(sending.SubViews.OfType<TextField>(), static field => field.Id == "send-input");
        var buffer = new TrafficBuffer(System.Text.Encoding.UTF8);
        buffer.SetPresentation(System.Text.Encoding.UTF8, false, true, 0);
        buffer.Append(Enumerable.Range(1, 3).Select(index => new SerialTrafficEvent(index, DateTimeOffset.UtcNow, index,
            Guid.Empty, SerialDirection.Receive, [(byte)(0x40 + index)], "serial")).ToArray());
        workbench.SetTrafficRows(buffer.Rows.ToArray());
        table.SetFocus();
        table.SetSelection(0, 0, false);
        table.SetSelection(0, 1, true);
        app.LayoutAndDraw(true);
        var trafficStatus = Assert.Single(Assert.IsAssignableFrom<View>(frame.SuperView).SubViews.OfType<Label>(), static label => label.Id == "traffic-status");
        Assert.Contains("已选 2 条", trafficStatus.Text, StringComparison.Ordinal);
        Assert.NotEqual(frame.Border.View?.GetScheme().Normal, sending.Border.View?.GetScheme().Normal);
        var focused = table.GetScheme().Focus;
        input.Text = "测试";
        input.SetFocus();
        app.LayoutAndDraw(true);
        Assert.NotEqual(frame.Border.View?.GetScheme().Normal, sending.Border.View?.GetScheme().Normal);
        Assert.NotEqual(focused, table.GetScheme().Active);
        Assert.Equal([0, 1], table.GetAllSelectedCells().Select(static cell => cell.Y).Distinct().Order());
        Assert.Contains("已选 2 条", trafficStatus.Text, StringComparison.Ordinal);
        Assert.Equal("测试", input.Text);
        Assert.Contains("41", driver.ToString(), StringComparison.Ordinal);
        Assert.Contains("42", driver.ToString(), StringComparison.Ordinal);
        Assert.Contains("43", driver.ToString(), StringComparison.Ordinal);
        app.End(token);
    }

    [Theory]
    [InlineData(60, 20)]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public async Task PortSelectorDisplaysDeviceNamesAndOpensTheSelectedPort(int width, int height)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-port-names-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(width, height);
        var bluetooth = new SerialPortDescriptor("COM3", "COM3 - 蓝牙串口", "BTH\\DEVICE");
        var adapter = new SerialPortDescriptor("COM20", "COM20 - USB-SERIAL CH340", "USB\\CH340");
        workbench.SetPorts([bluetooth, adapter]);
        var port = Assert.Single(workbench.ConnectionSettings.SubViews.OfType<DropDownList>());
        var snapshot = new ConnectionSnapshot(Guid.NewGuid(), new SerialConnectionOptions(adapter.PortName, DeviceInstanceId: adapter.DeviceInstanceId),
            ConnectionState.Open, 0, 0, 0, 0, 0, null, null);
        workbench.SetConnections([snapshot]);
        RunDialog(app, workbench, () =>
        {
            app.LayoutAndDraw(true);
            Assert.Contains("COM20", driver.ToString(), StringComparison.Ordinal);
            Assert.Contains("已连接", driver.ToString(), StringComparison.Ordinal);
            workbench.Window.NewKeyDownEvent(Key.F4);
        }, dialog =>
        {
            Assert.Equal("连接管理", dialog.Title);
            Assert.Equal(adapter.DisplayName, port.Text);
            Assert.Equal(adapter.PortName, workbench.ReadConnectionOptions().PortName);
            Assert.Equal(adapter.DeviceInstanceId, workbench.ReadConnectionOptions().DeviceInstanceId);
            app.LayoutAndDraw(true);
            Assert.Contains(adapter.DisplayName, driver.ToString(), StringComparison.Ordinal);
            port.SetFocus();
            dialog.NewKeyDownEvent(Key.CursorUp);
            Assert.Equal(bluetooth.DisplayName, port.Text);
            Assert.Equal(bluetooth.PortName, workbench.ReadConnectionOptions().PortName);
            Assert.Equal(bluetooth.DeviceInstanceId, workbench.ReadConnectionOptions().DeviceInstanceId);
        });
    }

    [Fact]
    public async Task PortRefreshPreservesSelectionWhenDescriptionsAndPortNumbersChange()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-port-selection-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var bluetooth = new SerialPortDescriptor("COM3", "COM3 - 蓝牙串口", "BTH\\DEVICE");
        var adapter = new SerialPortDescriptor("COM20", "COM20 - USB-SERIAL CH340", "USB\\CH340");
        workbench.SetPorts([bluetooth, adapter]);
        var port = Assert.Single(workbench.ConnectionSettings.SubViews.OfType<DropDownList>());
        port.Text = adapter.DisplayName;
        var renamed = adapter with { DisplayName = "COM20 - 调试设备" };
        workbench.SetPorts([renamed, bluetooth]);
        Assert.Equal(renamed.DisplayName, port.Text);
        Assert.Equal(adapter.PortName, workbench.ReadConnectionOptions().PortName);
        var snapshot = new ConnectionSnapshot(Guid.NewGuid(), new SerialConnectionOptions(adapter.PortName, DeviceInstanceId: adapter.DeviceInstanceId),
            ConnectionState.Open, 0, 0, 0, 0, 0, null, null);
        workbench.SetConnections([snapshot]);
        var reconnected = adapter with { PortName = "COM24", DisplayName = "COM24 - USB-SERIAL CH340" };
        workbench.SetPorts([bluetooth, reconnected]);
        Assert.Equal(reconnected.DisplayName, port.Text);
        Assert.Equal("COM24", workbench.ReadConnectionOptions().PortName);
        Assert.Equal(adapter.DeviceInstanceId, workbench.ReadConnectionOptions().DeviceInstanceId);
        workbench.SetPorts([]);
        Assert.Throws<InvalidOperationException>(() => workbench.ReadConnectionOptions());
        workbench.SetPorts([bluetooth, reconnected]);
        Assert.Equal(reconnected.DisplayName, port.Text);
        Assert.Equal(adapter.DeviceInstanceId, workbench.ReadConnectionOptions().DeviceInstanceId);
    }

    [Fact]
    public async Task SwitchingSharedConnectionsUsesTheirEncodingWithoutOverwritingDraftsOnRefresh()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-shared-settings-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var first = new ConnectionSnapshot(Guid.NewGuid(), new SerialConnectionOptions("COM16", 9600, EncodingName: "gb18030"),
            ConnectionState.Open, 0, 0, 0, 0, 0, null, null);
        var second = first with { Id = Guid.NewGuid(), Options = new SerialConnectionOptions("COM20", 115200) };
        var sending = SendingPanel(workbench);
        var input = sending.SubViews.OfType<TextField>().Single(static input => input.Id == "send-input");
        sending.SubViews.OfType<DropDownList>().Single(static input => input.Id == "send-format").Text = "文本";
        input.Text = "测试";
        workbench.SetConnections([first, second]);
        Assert.Equal(first.Id, workbench.CreateSendRequest().ConnectionId);
        Assert.Equal("B2E2CAD4", Convert.ToHexString(workbench.CreateSendRequest().Data));
        var baud = Assert.Single(workbench.ConnectionSettings.SubViews.OfType<NumericUpDown<int>>());
        Assert.Equal(9600, baud.Value);
        baud.Value = 19200;
        workbench.SetConnections([first, second]);
        Assert.Equal(19200, baud.Value);
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var connections = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<FrameView>()
            .Single(static frame => frame.Title == "连接").SubViews.OfType<ListView>().Single();
        connections.Value = 1;
        Assert.Equal(115200, baud.Value);
        Assert.Equal(second.Id, workbench.CreateSendRequest().ConnectionId);
        Assert.Equal("测试"u8.ToArray(), workbench.CreateSendRequest().Data);
        workbench.SetConnections([first]);
        Assert.Equal(first.Id, workbench.CreateSendRequest().ConnectionId);
        Assert.Equal(9600, baud.Value);
    }

    [Fact]
    public async Task WorkbenchControlsUseTextIndicators()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-indicators-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(120, 40);
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        foreach (var page in tabs.TabCollection)
        {
            tabs.Value = page;
            app.LayoutAndDraw(true);
            var screen = driver.ToString();
            if (page.Title == "1 工作台")
            {
                Assert.Contains("[ 发送 ]", screen, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("[ 发送 ]", screen, StringComparison.Ordinal);
            }
            Assert.All(screen.EnumerateRunes(), rune => Assert.True(
                System.Text.Rune.GetUnicodeCategory(rune) != System.Globalization.UnicodeCategory.OtherSymbol
                || rune.Value is >= 0x2500 and <= 0x257F, $"{page.Title}: U+{rune.Value:X}"));
        }
        app.End(token);
    }

    [Fact]
    public async Task WorkspaceShortcutsKeepAllPageTitlesVisible()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-titles-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(80, 24);
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        workbench.Window.Layout(new System.Drawing.Size(80, 24));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        Assert.Equal(["1 工作台", "2 发送历史", "3 任务", "4 会话", "5 设置"], tabs.TabCollection.Select(static page => page.Title));
        foreach (var tab in tabs.TabCollection)
        {
            workbench.Window.NewKeyDownEvent(tab.HotKey.WithAlt);
            Assert.Same(tab, tabs.Value);
            app.LayoutAndDraw(true);
            foreach (var title in tabs.TabCollection.Select(static page => page.Title))
            {
                Assert.True(driver.ToString().Contains(title, StringComparison.Ordinal), $"Missing {title}\n{driver.ToString()}");
            }
        }
        app.End(token);
    }

    [Theory]
    [InlineData(0, "Modbus")]
    [InlineData(1, "传输与回环")]
    [InlineData(2, "自动化")]
    [InlineData(3, "协议分析")]
    [InlineData(4, "波形")]
    public async Task ToolsOpenFromTheKeyboardAndReturnToTheSelectedWorkspace(int index, string title)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-tools-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(80, 24);
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var page = tabs.TabCollection.ElementAt(1);
        var stage = 0;
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
                if (app.TopRunnableView is Dialog dialog && dialog.Title == "工具")
                {
                    var groups = Assert.Single(dialog.SubViews.OfType<Tabs>());
                    Assert.Equal(["设备操作", "数据分析"], groups.TabCollection.Select(static group => group.Title));
                    groups.Value = groups.TabCollection.ElementAt(index < 3 ? 0 : 1);
                    var list = Assert.Single(Assert.IsAssignableFrom<View>(groups.Value).SubViews.OfType<ListView>());
                    list.SetFocus();
                    list.Value = index < 3 ? index : index - 3;
                    stage = 2;
                    list.NewKeyDownEvent(Key.Enter);
                }
                else if (app.TopRunnableView is Dialog tool)
                {
                    Assert.Equal($"工具 / {title}", tool.Title);
                    Assert.Same(workbench.GetTool(title), Assert.Single(tool.SubViews, static view => view is not Button));
                    stage = 3;
                    app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                }
                else if (stage == 0)
                {
                    tabs.Value = page;
                    stage = 1;
                    workbench.Window.NewKeyDownEvent(Key.F9);
                }
                else if (stage == 3)
                {
                    Assert.Same(page, tabs.Value);
                    Assert.Null(workbench.GetTool(title).SuperView);
                    app.RequestStop(workbench.Window);
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
        Assert.Equal(3, stage);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task RunningTasksSortFirstWithoutChangingTheSelectedTask()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-task-selection-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var now = DateTimeOffset.UtcNow;
        var request = new OperationRequest("modbus.poll", Guid.Empty, "{}");
        var finished = new OperationSnapshot(Guid.NewGuid(), request, OperationState.Succeeded, ExecutionOutcome.Confirmed, now, now);
        var running = finished with { Id = Guid.NewGuid(), State = OperationState.Running, UpdatedUtc = now.AddMinutes(-1) };
        workbench.SetTasks([finished, running]);
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var page = tabs.TabCollection.Single(static view => view.Title == "3 任务");
        var table = Assert.Single(page.SubViews.OfType<TableView>());
        Assert.Equal(running.Id.ToString()[..8], table.Table?[0, 0]);
        Assert.Equal("运行中", table.Table?[0, 2]);
        table.SetSelection(0, 1, false);
        var selected = finished with { State = OperationState.Failed, Error = new WorkbenchError("TIMEOUT", "超时"), UpdatedUtc = now.AddSeconds(1) };
        var another = running with { Id = Guid.NewGuid(), UpdatedUtc = now.AddSeconds(2) };
        workbench.SetTasks([selected, another, running]);
        Assert.Equal(selected.Id.ToString()[..8], table.Table?[2, 0]);
        Assert.Equal(2, table.Value?.SelectedCell.Y);
        Assert.Equal("失败", table.Table?[2, 2]);
        Assert.Contains("失败", page.SubViews.OfType<Label>().Single(static label => label.Id != "task-summary").Text, StringComparison.Ordinal);
        Assert.Contains("运行 2", page.SubViews.OfType<Label>().Single(static label => label.Id == "task-summary").Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomationDialogKeepsCanceledEditsOutOfSavedSteps()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-dialog-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        workbench.ApplySequence(new SerialSequenceDefinition("测试序列", [new SerialSequenceStep([0x01], "hex", 0, 1, 0, null, 2000, 0)]));
        var automation = workbench.GetTool("自动化");
        var table = Assert.Single(automation.SubViews.OfType<TableView>());
        View? editor = null;
        var edits = 0;
        var opened = false;
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
                if (app.TopRunnableView is Dialog tool && tool.Title == "工具 / 自动化")
                {
                    if (edits == 2)
                    {
                        app.RequestStop(tool);
                    }
                    else
                    {
                        Assert.Equal(new byte[] { 0x01 }, Assert.Single(workbench.ReadSequence().Steps).Data);
                        table.SetFocus();
                        table.NewKeyDownEvent(Key.Enter);
                    }
                }
                else if (app.TopRunnableView is Dialog dialog)
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
                else if (!opened)
                {
                    opened = true;
                    workbench.ShowTool("自动化");
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
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(80, 24);
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        RunDialog(app, workbench, () =>
        {
            workbench.Window.NewKeyDownEvent(Key.F8);
            Assert.IsAssignableFrom<View>(tabs.Value).SubViews.OfType<Button>().Single(static button => button.Text == "报文显示").InvokeCommand(Command.Accept);
        }, dialog =>
        {
            Assert.Equal("报文显示与筛选", dialog.Title);
            Assert.Equal(workbench.Window.GetScheme().Normal, dialog.GetScheme().Normal);
            foreach (var field in workbench.TrafficSettings.SubViews.OfType<TextField>().Where(static field => !field.ReadOnly))
            {
                field.SetFocus();
                Assert.Same(field, dialog.MostFocused);
                dialog.NewKeyDownEvent(Key.A);
                Assert.Equal("a", field.Text);
            }
        });
    }

    [Theory]
    [InlineData("Modbus", "事务参数")]
    [InlineData("传输与回环", "")]
    public async Task ParameterPanelsFollowKeyboardFocus(string pageName, string panelTitle)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-scrolling-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var page = workbench.GetTool(pageName);
        using var dialog = new Dialog { Width = Dim.Fill(), Height = Dim.Fill() };
        dialog.Add(page);
        var token = Assert.IsType<SessionToken>(app.Begin(dialog));
        dialog.Layout(new System.Drawing.Size(80, 24));
        var panel = panelTitle.Length == 0 ? Assert.Single(page.SubViews)
            : Assert.Single(page.SubViews.OfType<Tabs>()).TabCollection.Single(section => section.Title == panelTitle);
        var last = panel.SubViews.Where(static view => view.CanFocus).OrderBy(static view => view.Frame.Bottom).Last();
        last.SetFocus();
        Assert.True(panel.Viewport.Contains(last.Frame), $"Focused {last.Frame}, viewport {panel.Viewport}");
        Assert.Equal(0, page.Viewport.Y);
        var first = panel.SubViews.Where(static view => view.CanFocus).OrderBy(static view => view.Frame.Top).First();
        first.SetFocus();
        Assert.True(panel.Viewport.Contains(first.Frame));
        Assert.Equal(0, panel.Viewport.X);
        app.End(token);
        dialog.Remove(page);
    }

    [Theory]
    [InlineData(60, 20)]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public async Task ToolsKeepTheirControlsAccessibleAtTheMinimumTerminalSize(int width, int height)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-tool-layout-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(width, height);
        foreach (var title in new[] { "Modbus", "传输与回环", "自动化", "协议分析", "波形" })
        {
            RunDialog(app, workbench, () => workbench.ShowTool(title), dialog =>
            {
                Assert.Equal($"工具 / {title}", dialog.Title);
                var tool = workbench.GetTool(title);
                var sections = tool.SubViews.OfType<Tabs>().SingleOrDefault();
                foreach (var page in sections?.TabCollection ?? [tool])
                {
                    if (sections is not null)
                    {
                        sections.Value = page;
                    }
                    app.LayoutAndDraw(true);
                    foreach (var container in page.SubViews.Where(static view => view.SubViews.Count > 0).Prepend(page))
                    {
                        foreach (var control in container.SubViews.Where(static view => view.CanFocus))
                        {
                            control.SetFocus();
                            Assert.True(container.Viewport.Contains(control.Frame), $"{title}: {control}, {control.Frame}, {container.Viewport}");
                        }
                    }
                }
            });
        }
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
        Assert.Equal("5 设置", tabs.Value?.Title);
        var settings = Assert.IsAssignableFrom<View>(tabs.Value);
        Assert.All(settings.SubViews.Where(static view => view.CanFocus), view => Assert.True(view.Frame.Bottom <= settings.Viewport.Height, view.ToString()));
        var sending = SendingPanel(workbench);
        workbench.Window.NewKeyDownEvent(Key.D1.WithAlt);
        var format = sending.SubViews.OfType<DropDownList>().Single(static field => field.Id == "send-format");
        format.SetFocus();
        format.NewKeyDownEvent(Key.CursorDown);
        Assert.Equal("文本", format.Text);
        format.NewKeyDownEvent(Key.CursorUp);
        Assert.Equal("HEX", format.Text);
        app.End(token);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    [InlineData(60, 20)]
    public async Task ConnectionManagementIsAccessibleAcrossWorkspaces(int width, int height)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-connect-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(width, height);
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        foreach (var page in tabs.TabCollection)
        {
            RunDialog(app, workbench, () =>
            {
                tabs.Value = page;
                if (page.Title == "1 工作台")
                {
                    SendingPanel(workbench).SubViews.OfType<DropDownList>().Single().SetFocus();
                }
                workbench.Window.NewKeyDownEvent(Key.F4);
            }, dialog =>
            {
                Assert.Equal("连接管理", dialog.Title);
                var content = workbench.ConnectionSettings;
                Assert.All(content.SubViews, view => Assert.True(content.Viewport.Contains(view.Frame), $"{page.Title}: {view}, {view.Frame}, {content.Viewport}"));
                var buttons = content.SubViews.OfType<Button>().ToArray();
                foreach (var button in buttons)
                {
                    Assert.DoesNotContain(buttons.Where(other => other != button), other => button.Frame.IntersectsWith(other.Frame));
                }
                var port = Assert.Single(content.SubViews.OfType<DropDownList>());
                port.SetFocus();
                Assert.Same(port, dialog.MostFocused);
            });
        }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    [InlineData(60, 20)]
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
        var sending = SendingPanel(workbench);
        Assert.Equal(3, sending.Frame.Height);
        Assert.True(sending.Frame.Bottom <= Assert.IsAssignableFrom<View>(sending.SuperView).Viewport.Height);
        var input = sending.SubViews.OfType<TextField>().Single(static field => field.Id == "send-input");
        Assert.True(input.Frame.Width > 0);
        Assert.True(input.Frame.Right <= sending.Viewport.Width);
        var frame = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<FrameView>().Single(static frame => frame.Title == "报文");
        Assert.True(Assert.Single(frame.SubViews.OfType<TableView>()).Frame.Height >= 7);
        Assert.True(frame.Frame.Bottom <= sending.Frame.Top - 1);
        var sessionPage = tabs.TabCollection.Single(static page => page.Title == "4 会话");
        Assert.All(sessionPage.SubViews.OfType<TableView>(), table => Assert.True(table.Frame.Height >= 3));
    }

    [Fact]
    public async Task ResizingTheWorkbenchPreservesDraftInputAndRestoresFocus()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-resize-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(120, 40);
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var page = Assert.IsAssignableFrom<View>(tabs.Value);
        Assert.Equal("1 工作台", page.Title);
        var sending = SendingPanel(workbench);
        var input = Assert.Single(sending.SubViews.OfType<TextField>(), static field => field.Id == "send-input");
        input.Text = "测试 e\u0301";
        Assert.Single(sending.SubViews.OfType<DropDownList>()).Text = "文本";
        input.SetFocus();
        var minimum = Assert.Single(workbench.Window.SubViews, static view => view.Id == "terminal-size");
        var connections = Assert.Single(page.SubViews.OfType<FrameView>(), static frame => frame.Title == "连接");
        var traffic = Assert.Single(page.SubViews.OfType<FrameView>(), static frame => frame.Title == "报文");
        foreach (var size in new[] { (120, 40), (80, 24), (60, 20), (40, 20), (60, 12), (80, 24), (120, 40) })
        {
            driver.SetScreenSize(size.Item1, size.Item2);
            app.LayoutAndDraw(true);
            var tooSmall = size.Item1 < 60 || size.Item2 < 20;
            Assert.Equal(tooSmall, minimum.Visible);
            Assert.Equal(!tooSmall, tabs.Visible);
            Assert.Equal("测试 e\u0301"u8.ToArray(), workbench.CreateSendRequest(Guid.NewGuid()).Data);
            if (tooSmall)
            {
                Assert.Contains("终端至少需要 60 列、20 行", driver.ToString(), StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(size.Item1 >= 80, connections.Visible);
                Assert.True(traffic.Frame.Width >= size.Item1 * (size.Item1 >= 80 ? 0.7 : 0.9));
                Assert.Same(input, workbench.Window.MostFocused);
                Assert.Contains("SerialWorkbench", driver.ToString(), StringComparison.Ordinal);
            }
        }
        app.End(token);
    }

    [Fact]
    public async Task TrafficPresentationDistinguishesConnectionFilteringAndPause()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-empty-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var frame = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<FrameView>().Single(static frame => frame.Title == "报文");
        var empty = Assert.Single(frame.SubViews.OfType<Label>());
        Assert.Equal("未连接", empty.Text);
        workbench.SetConnections([new ConnectionSnapshot(Guid.NewGuid(), new SerialConnectionOptions("COM20"), ConnectionState.Open, 12, 8, 2, 1, 0, null, null)]);
        Assert.Equal("等待数据", empty.Text);
        workbench.TrafficSettings.SubViews.OfType<TextField>().Single(static field => field.Id == "traffic-filter").Text = "missing";
        Assert.Equal("无匹配报文", empty.Text);
        var status = Assert.Single(workbench.Window.SubViews.OfType<Label>(), static label => label.Id == "connection-status");
        Assert.Contains("COM20", status.Text, StringComparison.Ordinal);
        Assert.Contains("已连接", status.Text, StringComparison.Ordinal);
        Assert.Contains("115200 8N1", status.Text, StringComparison.Ordinal);
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        workbench.Window.NewKeyDownEvent(Key.F2);
        Assert.Equal("显示已暂停", empty.Text);
        workbench.Window.NewKeyDownEvent(Key.F7);
        Assert.Equal("无匹配报文", empty.Text);
        app.End(token);
    }

    [Theory]
    [InlineData(60, 20)]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public async Task ContextualHintsStayVisibleWhileOtherShortcutsRemainAvailable(int width, int height)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-hints-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(width, height);
        var token = Assert.IsType<SessionToken>(app.Begin(workbench.Window));
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var bar = Assert.Single(workbench.Window.SubViews.OfType<StatusBar>());
        var sending = SendingPanel(workbench);
        var input = Assert.Single(sending.SubViews.OfType<TextField>(), static field => field.Id == "send-input");
        input.SetFocus();
        AssertHints(Key.F4, Key.F8);
        workbench.Window.NewKeyDownEvent(Key.D1);
        workbench.Window.NewKeyDownEvent(Key.D2);
        Assert.Equal("12", input.Text);
        workbench.Window.NewKeyDownEvent(Key.F2);
        var status = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<Label>().Single(static label => label.Id == "traffic-mode");
        Assert.Contains("暂停", status.Text, StringComparison.Ordinal);
        workbench.Window.NewKeyDownEvent(Key.F2);
        var table = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<FrameView>()
            .Single(static frame => frame.Title == "报文").SubViews.OfType<TableView>().Single();
        table.SetFocus();
        AssertHints(Key.F6, Key.F7);
        workbench.Window.NewKeyDownEvent(Key.F8);
        Assert.Equal("5 设置", tabs.Value?.Title);
        AssertHints(Key.F4, Key.F9);
        app.End(token);

        void AssertHints(Key first, Key second)
        {
            app.LayoutAndDraw(true);
            var visible = bar.SubViews.OfType<Shortcut>().Where(static item => item.Visible).ToArray();
            Assert.InRange(visible.Length, 3, 5);
            Assert.Contains(visible, item => item.Key == first);
            Assert.Contains(visible, item => item.Key == second);
            Assert.All(visible, item => Assert.True(bar.Viewport.Contains(item.Frame), $"{item.Title}: {item.Frame}, {bar.Viewport}"));
            Assert.Contains("退出", driver.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulConnectionReturnsToTheWorkbenchAndFocusesSending(bool fromSettings)
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-connection-success-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(80, 24);
        var tabs = Assert.Single(workbench.Window.SubViews.OfType<Tabs>());
        var input = Assert.Single(SendingPanel(workbench).SubViews.OfType<TextField>(), static field => field.Id == "send-input");
        input.Text = "55 AA";
        var snapshot = new ConnectionSnapshot(Guid.NewGuid(), new SerialConnectionOptions("COM20"), ConnectionState.Open, 0, 0, 0, 0, 0, null, null);
        var stage = 0;
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
                    Assert.Equal("连接管理", dialog.Title);
                    stage = 2;
                    workbench.ApplyOpenedConnection(snapshot);
                    Assert.True(dialog.StopRequested);
                }
                else if (stage == 0)
                {
                    stage = 1;
                    if (fromSettings)
                    {
                        workbench.Window.NewKeyDownEvent(Key.F8);
                        Assert.IsAssignableFrom<View>(tabs.Value).SubViews.OfType<Button>()
                            .Single(static button => button.Text == "连接管理").InvokeCommand(Command.Accept);
                    }
                    else
                    {
                        workbench.Window.NewKeyDownEvent(Key.F4);
                    }
                }
                else if (stage == 2)
                {
                    Assert.Equal("1 工作台", tabs.Value?.Title);
                    Assert.Same(input, workbench.Window.MostFocused);
                    var request = workbench.CreateSendRequest();
                    Assert.Equal(snapshot.Id, request.ConnectionId);
                    Assert.Equal(new byte[] { 0x55, 0xAA }, request.Data);
                    Assert.Null(workbench.ConnectionSettings.SuperView);
                    stage = 3;
                    app.RequestStop(workbench.Window);
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
        Assert.Equal(3, stage);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task FailedConnectionKeepsTheDialogOpenAndDisplaysItsError()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-connection-error-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create().Init();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var driver = Assert.IsAssignableFrom<Terminal.Gui.Drivers.IDriver>(app.Driver);
        driver.SetScreenSize(80, 24);
        var stage = 0;
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
                    Assert.Equal("连接管理", dialog.Title);
                    if (stage == 1)
                    {
                        stage = 2;
                        workbench.ConnectionSettings.SubViews.OfType<Button>().Single(static button => button.Id == "connection-open").InvokeCommand(Command.Accept);
                    }
                    else
                    {
                        var message = Assert.Single(workbench.ConnectionSettings.SubViews.OfType<Label>(), static label => label.Id == "connection-message");
                        Assert.Equal("请选择串口。", message.Text);
                        Assert.False(dialog.StopRequested);
                        stage = 3;
                        app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                    }
                }
                else if (stage == 0)
                {
                    stage = 1;
                    workbench.Window.NewKeyDownEvent(Key.F4);
                }
                else if (stage == 3)
                {
                    app.RequestStop(workbench.Window);
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
        Assert.Equal(3, stage);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task WaveformDoesNotJoinIncompleteSamplesAcrossReconnectedSegments()
    {
        var paths = new ApplicationPaths(Path.Combine(AppContext.BaseDirectory, "artifacts", $"tui-waveform-{Guid.NewGuid():N}"));
        paths.EnsureWritable();
        await using var runtime = new HostRuntime(paths);
        using var app = Terminal.Gui.App.Application.Create();
        using var workbench = new TerminalWorkbench(app, new HostRpcService(runtime));
        var graph = Assert.Single(workbench.GetTool("波形").SubViews.OfType<GraphView>());
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
        var sending = SendingPanel(workbench);
        var input = sending.SubViews.OfType<TextField>().Single(static field => field.Id == "send-input");
        var format = sending.SubViews.OfType<DropDownList>().Single(static field => field.Id == "send-format");
        var checksum = workbench.SendSettings.SubViews.OfType<DropDownList>().Single(static field => field.Id == "send-checksum");
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
        workbench.TrafficSettings.SubViews.OfType<CheckBox>().Single(static checkbox => checkbox.Text == "跟随").Value = CheckState.UnChecked;
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

    private static FrameView SendingPanel(TerminalWorkbench workbench) => Assert.Single(
        Assert.Single(workbench.Window.SubViews.OfType<Tabs>()).TabCollection.SelectMany(static page => page.SubViews).OfType<FrameView>(),
        static frame => frame.Id == "send-panel");

    private static void RunDialog(IApplication app, TerminalWorkbench workbench, Action open, Action<Dialog> inspect)
    {
        var stage = 0;
        var timedOut = false;
        Exception? failure = null;
        var timeout = app.AddTimeout(TimeSpan.FromSeconds(5), () =>
        {
            timedOut = true;
            app.RequestStop();
            return false;
        });
        EventHandler<Terminal.Gui.App.EventArgs<IApplication?>> iteration = (_, _) =>
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
                    inspect(dialog);
                    stage = 2;
                    app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                }
                else if (stage == 0)
                {
                    stage = 1;
                    open();
                }
                else if (stage == 2)
                {
                    app.RequestStop(workbench.Window);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                app.RequestStop();
            }
        };
        app.Iteration += iteration;
        try
        {
            app.Run(workbench.Window);
        }
        finally
        {
            app.Iteration -= iteration;
            if (timeout is not null)
            {
                app.RemoveTimeout(timeout);
            }
        }
        Assert.Null(failure);
        Assert.Equal(2, stage);
        Assert.False(timedOut);
    }
}
