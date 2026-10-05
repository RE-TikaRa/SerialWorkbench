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
    [InlineData(1, "文件与回环")]
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
                    var list = Assert.Single(dialog.SubViews.OfType<ListView>());
                    list.Value = index;
                    stage = 2;
                    list.NewKeyDownEvent(Key.Enter);
                }
                else if (app.TopRunnableView is Dialog tool)
                {
                    Assert.Equal(title, tool.Title);
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
                if (app.TopRunnableView is Dialog tool && tool.Title == "自动化")
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
    [InlineData("文件与回环", "")]
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
        foreach (var title in new[] { "Modbus", "文件与回环", "自动化", "协议分析", "波形" })
        {
            RunDialog(app, workbench, () => workbench.ShowTool(title), dialog =>
            {
                Assert.Equal(title, dialog.Title);
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
        Assert.Contains("COM20 已连接", status.Text, StringComparison.Ordinal);
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
        var status = tabs.TabCollection.SelectMany(static page => page.SubViews).OfType<Label>().Single(static label => label.Id == "traffic-status");
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
