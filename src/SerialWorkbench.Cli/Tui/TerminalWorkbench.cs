using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Protocols;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiApplication = Terminal.Gui.App.Application;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench : IDisposable
{
    static TerminalWorkbench()
    {
        GlyphSettings.Current = GlyphSettings.Current with
        {
            File = new Rune('f'),
            Folder = new Rune('d'),
            CheckStateChecked = new Rune('x'),
            CheckStateUnChecked = new Rune('o'),
            CheckStateNone = new Rune('-'),
            Selected = new Rune('*'),
            UnSelected = new Rune('o'),
            LeftArrow = new Rune('<'),
            RightArrow = new Rune('>'),
            UpArrow = new Rune('^'),
            DownArrow = new Rune('v'),
            LeftDefaultIndicator = new Rune('>'),
            RightDefaultIndicator = new Rune('<'),
            LeftBracket = new Rune('['),
            RightBracket = new Rune(']'),
            BlocksMeterSegment = new Rune('#'),
            ContinuousMeterSegment = new Rune('#'),
            Stipple = new Rune('.'),
            Diamond = new Rune('*'),
            Close = new Rune('x'),
            Minimize = new Rune('-'),
            Maximize = new Rune('+'),
            Dot = new Rune('.'),
            DottedSquare = new Rune('.'),
            BlackCircle = new Rune('*'),
            IdenticalTo = new Rune('='),
            Move = new Rune('+'),
            SizeHorizontal = new Rune('-'),
            SizeVertical = new Rune('|'),
            SizeTopLeft = new Rune('+'),
            SizeTopRight = new Rune('+'),
            SizeBottomRight = new Rune('+'),
            SizeBottomLeft = new Rune('+'),
            Apple = new Rune('*'),
            AppleBMP = new Rune('*'),
            Copy = new Rune('c'),
        };
        NerdFontsSettings.Current = NerdFontsSettings.Current with { Enable = false };
        FileDialogStyle.DefaultUseUnicodeCharacters = false;
    }

    private readonly IApplication app;
    private readonly IHostRpc client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Window window = new() { Title = "SerialWorkbench", Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Tabs tabs = new() { Y = 2, Width = Dim.Fill(), Height = Dim.Fill(6) };
    private readonly Label status = new() { Y = 1, Width = Dim.Percent(50), Height = 1, Text = "正在连接 Host" };
    private readonly CheckBox backgroundTasks = new() { X = 48, Y = 0, Text = "后台任务" };
    private readonly Label message = new() { Y = 1, Width = Dim.Fill(), Height = 1 };
    private readonly ListView connections = new() { Width = Dim.Fill(), Height = Dim.Fill(2) };
    private readonly ObservableCollection<string> connectionItems = [];
    private readonly TableView traffic = new() { Width = Dim.Fill(), Height = Dim.Fill(1), FullRowSelect = true };
    private readonly TextField input = new() { Id = "send-input", Y = 1, Width = Dim.Fill(12) };
    private readonly DropDownList format = new() { X = 0, Y = 0, Width = 12, ReadOnly = true, Text = "HEX", Source = new ListWrapper<string>(new ObservableCollection<string>(["HEX", "文本"])) };
    private readonly DropDownList direction = new() { X = 14, Width = 12, ReadOnly = true, Text = "全部", Source = new ListWrapper<string>(new ObservableCollection<string>(["全部", "RX", "TX"])) };
    private readonly TextField filter = new() { X = 28, Width = Dim.Fill(1) };
    private readonly TextField sourceFilter = new() { Id = "traffic-source", X = 9, Y = 1, Width = Dim.Fill() };
    private PopoverMenu? trafficMenu;
    private readonly CheckBox follow = new() { X = 0, Y = Pos.AnchorEnd(), Text = "跟随", Value = CheckState.Checked };
    private readonly CheckBox timestamps = new() { X = 12, Y = Pos.AnchorEnd(), Text = "时间", Value = CheckState.Checked };
    private readonly DropDownList copyFormat = new()
    {
        X = 24,
        Y = Pos.AnchorEnd(),
        Width = 16,
        ReadOnly = true,
        Text = "当前显示",
        Source = new ListWrapper<string>(new ObservableCollection<string>(["当前显示", "文本", "HEX", "连续 HEX", "日志"]))
    };
    private readonly DropDownList port = new() { Id = "connection-port", X = 5, Width = 12, ReadOnly = true };
    private readonly NumericUpDown<int> baud = new() { Id = "connection-baud", CanEdit = true, X = 26, Value = 115200, Width = 14 };
    private readonly NumericUpDown<int> dataBits = new() { CanEdit = true, X = 16, Y = 4, Value = 8, Width = 20 };
    private readonly DropDownList parity = EnumSelector(SerialParity.None);
    private readonly DropDownList stopBits = EnumSelector(SerialStopBits.One);
    private readonly DropDownList handshake = EnumSelector(SerialHandshake.None);
    private readonly DropDownList role = EnumSelector(SerialConnectionRole.Dut);
    private readonly CheckBox dtr = new() { X = 16, Y = 26, Text = "DTR" };
    private readonly CheckBox rts = new() { X = 32, Y = 26, Text = "RTS" };
    private readonly CheckBox rs485 = new() { X = 16, Y = 28, Text = "RS-485 RTS 方向控制" };
    private readonly CheckBox autoReconnect = new() { X = 16, Y = 30, Text = "自动重连", Value = CheckState.Checked };
    private readonly NumericUpDown<int> rtsBefore = new() { CanEdit = true, X = 16, Y = 32, Value = 0, Width = 20 };
    private readonly NumericUpDown<int> rtsAfter = new() { CanEdit = true, X = 16, Y = 34, Value = 0, Width = 20 };
    private readonly DropDownList profileSelector = new() { X = 16, Y = 38, Width = 30, ReadOnly = true };
    private readonly TextField profileName = new() { X = 16, Y = 40, Width = 30 };
    private SerialProfile[] profiles = [];
    private long configurationRevision;
    private readonly NumericUpDown<int> hexGap = new() { CanEdit = true, X = 16, Y = 13, Value = 10, Width = 20 };
    private readonly TextField encoding = new() { X = 16, Y = 15, Width = 20, Text = "utf-8" };
    private readonly DropDownList lineEnding = new() { X = 16, Y = 17, Width = 20, Text = "无", ReadOnly = true, Source = new ListWrapper<string>(new ObservableCollection<string>(["无", "CR", "LF", "CRLF"])) };
    private readonly ListView history = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Label workspace = new() { Y = 45, Width = Dim.Fill(), Text = "全局工作区" };
    private readonly ObservableCollection<string> historyItems = [];
    private readonly Dictionary<Guid, TrafficBuffer> buffers = [];
    private IReadOnlyList<ConnectionSnapshot> snapshots = [];
    private IReadOnlyList<SerialPortDescriptor> ports = [];
    private TrafficRow[] visibleRows = [];
    private readonly View workbenchView;
    private readonly View settingsView;
    private Guid? connectionId;
    private Guid? streamId;
    private long sequence;
    private bool polling;
    private bool paused;
    private bool updatingConnections;
    private bool updatingTraffic;
    private object? refreshToken;
    private Task pendingRefresh = Task.CompletedTask;
    private readonly object actionsGate = new();
    private readonly HashSet<Task> pendingActions = [];
    private bool disposed;

    public TerminalWorkbench(IApplication app, IHostRpc client)
    {
        this.app = app;
        this.client = client;
        message.X = Pos.Right(status) + 1;
        var connectionBar = new View { Id = "connection-bar", CanFocus = true, Width = Dim.Fill(), Height = 1 };
        var connect = Button("连接", () => RunUiAsync(OpenConnectionAsync));
        connect.Id = "connection-open";
        connect.X = 42;
        var refreshPorts = Button("刷新", () => RunUiAsync(RefreshPortsAsync));
        refreshPorts.X = Pos.Right(connect) + 1;
        var settings = Button("设置", () => { tabs.Value = settingsView; return Task.CompletedTask; });
        settings.X = Pos.Right(refreshPorts) + 1;
        connectionBar.Add(new Label { Text = "端口" }, port, new Label { Text = "波特率", X = 19 }, baud, connect, refreshPorts, settings);
        connections.SetSource(connectionItems);
        connections.ValueChanged += (_, args) =>
        {
            if (!updatingConnections && args.NewValue is { } index && index >= 0 && index < snapshots.Count)
            {
                connectionId = snapshots[index].Id;
                ResumeLiveTraffic();
                RefreshTraffic();
            }
        };
        var connectionsFrame = new FrameView { Title = "连接", Width = 24, Height = Dim.Fill() };
        var close = Button("关闭", () => RunUiAsync(CloseConnectionAsync));
        close.Y = Pos.AnchorEnd(2);
        var details = Button("详情", () => RunUiAsync(ShowConnectionAsync));
        details.X = Pos.Right(close) + 1;
        details.Y = Pos.AnchorEnd(2);
        var reconnect = Button("重连", () => RunUiAsync(async () =>
        {
            await client.ReconnectConnectionAsync(RequiredConnection(), lifetime.Token).ConfigureAwait(false);
            app.Invoke(() => message.Text = "连接已重连");
        }));
        reconnect.Y = Pos.AnchorEnd();
        connectionsFrame.Add(connections, close, details, reconnect);
        var trafficFrame = new FrameView { Title = "报文", X = Pos.Right(connectionsFrame), Width = Dim.Fill(), Height = Dim.Fill() };
        var filters = new View { CanFocus = true, Height = 2, Width = Dim.Fill() };
        filters.Add(format, direction, filter, new Label { Text = "来源", Y = 1 }, sourceFilter);
        traffic.Y = 2;
        trafficFrame.Add(filters, traffic, follow, timestamps, copyFormat);
        var workbench = new View { Title = "工作台", Width = Dim.Fill(), Height = Dim.Fill() };
        workbenchView = workbench;
        settingsView = BuildSettings();
        workbench.Add(connectionsFrame, trafficFrame);
        tabs.Add(workbench, settingsView, BuildHistory(), BuildModbus(), BuildTransfers(), BuildAutomation(), BuildTasks(), BuildSessions(), BuildProtocol(), BuildWaveform());
        var send = Button("发送", () => RunUiAsync(SendAsync));
        send.X = Pos.AnchorEnd();
        send.Y = 1;
        var sending = BuildSending();
        input.Accepting += (_, args) => { args.Handled = true; _ = RunUiAsync(SendAsync); };
        sending.Add(input, send);
        var shortcuts = new StatusBar([
            new Shortcut(Key.F1, "帮助", ShowHelp),
            new Shortcut(Key.F2, "暂停", TogglePause),
            new Shortcut(Key.F3, "清空", ClearTraffic),
            new Shortcut(Key.F8, "设置", () => tabs.Value = settingsView),
            new Shortcut(Key.F5, "刷新", () => _ = RunUiAsync(RefreshPortsAsync)),
            new Shortcut(Key.F6, "复制", CopySelected),
            new Shortcut(Key.F7, "实时", ResumeLiveTraffic),
            new Shortcut(Key.Q.WithCtrl, "退出", () => app.RequestStop(window)),
        ]);
        window.Add(status, connectionBar, message, tabs, sending, shortcuts);
        format.ValueChanged += (_, _) => RefreshTraffic();
        direction.ValueChanged += (_, _) => RefreshTraffic();
        filter.TextChanged += (_, _) => RefreshTraffic();
        timestamps.ValueChanged += (_, _) => RefreshTraffic();
        sourceFilter.TextChanged += (_, _) => RefreshTraffic();
        window.Initialized += (_, _) => RegisterTrafficMenu();
        traffic.KeyBindings.Add(Key.C.WithCtrl, Command.Copy);
        traffic.KeyBindings.Add(Key.Space.WithCtrl, Command.Context);
        traffic.MouseBindings.Add(MouseFlags.RightButtonClicked, Command.Context);
        traffic.CommandNotBound += (_, args) =>
        {
            if (args.Context?.Command == Command.Copy)
            {
                CopySelected();
                args.Handled = true;
            }
            else if (args.Context?.Command == Command.Context)
            {
                if (args.Context.Binding is MouseBinding { MouseEvent: { } mouse })
                {
                    var point = traffic.ScreenToViewport(mouse.ScreenPosition);
                    if (traffic.ScreenToCell(point.X, point.Y) is { } cell
                        && !traffic.GetAllSelectedCells().Any(selected => selected.Y == cell.Y))
                    {
                        traffic.SetSelection(0, cell.Y, false);
                    }
                    trafficMenu?.MakeVisible(mouse.ScreenPosition);
                }
                else
                {
                    trafficMenu?.MakeVisible();
                }
                args.Handled = true;
            }
        };
        traffic.Accepting += (_, args) => { args.Handled = true; ShowTrafficDetails(); };
        port.ValueChanged += (_, _) => configuredDeviceId = null;
        LimitNumber(hexGap, 0, 60_000);
        LimitNumber(baud, 1, int.MaxValue);
        LimitNumber(dataBits, 5, 8);
        LimitNumber(rtsBefore, 0, 60_000);
        LimitNumber(rtsAfter, 0, 60_000);
        LimitNumber(sendInterval, 1, 600_000);
        LimitNumber(sendCount, 0, 100_000);
        LimitNumber(stepDelay, 0, 600_000);
        LimitNumber(stepRepeat, 1, 10_000);
        LimitNumber(stepWait, 0, 600_000);
        LimitNumber(stepTimeout, 0, 600_000);
        LimitNumber(stepRetries, 0, 100);
        LimitNumber(waveformLength, 2, 64 * 1024);
        traffic.ValueChanged += (_, _) =>
        {
            if (!updatingTraffic && traffic.HasFocus)
            {
                follow.Value = CheckState.UnChecked;
            }
        };
    }

    public Window Window => window;

    public static async Task<int> RunAsync(string applicationRoot, string culture, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("TUI 需要交互式终端。");
            return 2;
        }

        await using var client = await HostEndpoint.ConnectAsync(applicationRoot, true, cancellationToken).ConfigureAwait(false);
        var handshake = await client.HandshakeAsync(new HandshakeRequest(RpcProtocol.MajorVersion, RpcProtocol.MinorVersion, "tui", culture), cancellationToken).ConfigureAwait(false);
        if (!handshake.Accepted)
        {
            throw new InvalidOperationException(handshake.Error);
        }

        using var app = TuiApplication.Create().Init();
        using var workbench = new TerminalWorkbench(app, client);
        using var stopping = cancellationToken.Register(() => app.Invoke(() => app.RequestStop(workbench.window)));
        workbench.refreshToken = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            workbench.pendingRefresh = workbench.RunUiAsync(workbench.PumpAsync);
            return !workbench.lifetime.IsCancellationRequested;
        });
        _ = workbench.RunUiAsync(workbench.RefreshPortsAsync);
        _ = workbench.RunUiAsync(workbench.RefreshSessionsAsync);
        app.Run(workbench.window);
        workbench.lifetime.Cancel();
        workbench.replay?.Cancel();
        await workbench.pendingRefresh.ConfigureAwait(false);
        await workbench.replayTask.ConfigureAwait(false);
        Task[] actions;
        lock (workbench.actionsGate)
        {
            actions = [.. workbench.pendingActions];
        }
        await Task.WhenAll(actions).ConfigureAwait(false);
        return 0;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetime.Cancel();
        replay?.Cancel();
        if (refreshToken is not null)
        {
            app.RemoveTimeout(refreshToken);
        }

        window.Dispose();
        serialSettings.Dispose();
        controlSettings.Dispose();
        profileSettings.Dispose();
        sequenceEditor.Dispose();
        if (trafficMenu is not null)
        {
            app.Popovers?.DeRegister(trafficMenu);
            trafficMenu.Dispose();
        }
        sessionReadGate.Dispose();
        replay?.Dispose();
        lifetime.Dispose();
    }

    private View BuildSettings()
    {
        var view = new View { Title = "设置", Width = Dim.Fill(), Height = Dim.Fill() };
        AddSetting(view, "文本编码", encoding, 0);
        AddSetting(view, "HEX 间隔 ms", hexGap, 2);
        var advanced = Button("串口参数", () => { ShowSettingsDialog("串口参数", serialSettings, 16); return Task.CompletedTask; });
        advanced.Y = 4;
        var controls = Button("控制线", () => { ShowSettingsDialog("控制线与 RS-485", controlSettings, 17); return Task.CompletedTask; });
        controls.X = Pos.Right(advanced) + 1;
        controls.Y = 4;
        var profiles = Button("配置与工作区", () => { ShowSettingsDialog("配置与工作区", profileSettings, 14); return Task.CompletedTask; });
        profiles.X = Pos.Right(controls) + 1;
        profiles.Y = 4;
        BuildSerialSettings();
        BuildControlSettings();
        BuildProfileSettings();
        view.Add(advanced, controls, profiles);
        return view;
    }
    private View BuildHistory()
    {
        history.SetSource(historyItems);
        history.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (history.Value is { } index && index >= 0 && index < historyItems.Count)
            {
                input.Text = historyItems[index];
                input.SetFocus();
            }
        };
        var view = new View { Title = "发送历史", Width = Dim.Fill(), Height = Dim.Fill() };
        view.Add(history);
        return view;
    }

    private async Task PumpAsync()
    {
        if (polling)
        {
            return;
        }

        polling = true;
        try
        {
            var host = await client.GetStatusAsync(lifetime.Token).ConfigureAwait(false);
            if (host.ConfigurationRevision != configurationRevision)
            {
                var configuration = await client.ReadConfigurationAsync(lifetime.Token).ConfigureAwait(false);
                await InvokeUiAsync(() => ApplyConfiguration(configuration)).ConfigureAwait(false);
            }
            if (host.ActiveOperationCount > 0 || displayedTasks.Any(static item => item.State == OperationState.Running))
            {
                await RefreshTasksAsync().ConfigureAwait(false);
            }
            var batch = await client.ReadEventBatchAsync(new EventQuery(sequence, 1000, StreamId: streamId), lifetime.Token).ConfigureAwait(false);
            app.Invoke(() =>
            {
                var selection = connectionId;
                workspace.Text = host.WorkspaceRoot is null ? $"全局数据：{host.DataRoot}" : $"工作区：{host.WorkspaceRoot}";
                snapshots = host.Connections;
                updatingConnections = true;
                var names = snapshots.Select(static snapshot => $"{snapshot.Options.PortName} · {snapshot.State}").ToArray();
                if (!connectionItems.SequenceEqual(names))
                {
                    connectionItems.Clear();
                    foreach (var name in names)
                    {
                        connectionItems.Add(name);
                    }
                }

                foreach (var snapshot in snapshots)
                {
                    if (!buffers.ContainsKey(snapshot.Id))
                    {
                        buffers.Add(snapshot.Id, new TrafficBuffer(Encoding.GetEncoding(snapshot.Options.EncodingName)));
                    }
                }

                connectionId = selection is { } selected && snapshots.Any(item => item.Id == selected) ? selected : snapshots.Count > 0 ? snapshots[0].Id : null;
                connections.Value = connectionId is { } id ? snapshots.ToList().FindIndex(item => item.Id == id) : null;
                updatingConnections = false;
                if (batch.ResetRequired)
                {
                    foreach (var buffer in buffers.Values)
                    {
                        buffer.Clear();
                    }
                }
                foreach (var group in batch.Events.GroupBy(static item => item.ConnectionId))
                {
                    if (buffers.TryGetValue(group.Key, out var buffer))
                    {
                        buffer.Append(group.ToArray());
                    }
                    if (group.Key == connectionId && replayBuffer is null)
                    {
                        foreach (var item in group.Where(static item => item.Direction == SerialDirection.Receive))
                        {
                            FeedWaveform(item);
                        }
                    }
                }

                if (batch.Gap is { } gap)
                {
                    message.Text = $"事件缺失：{gap.FromSequence}–{gap.ToSequence}";
                }

                sequence = batch.NextSequence;
                streamId = batch.StreamId;
                var current = snapshots.FirstOrDefault(item => item.Id == connectionId);
                var statusText = current is null ? "未连接"
                    : $"{current.Options.PortName} {ConnectionStateText(current.State)} RX {current.ReceivedBytes:N0} B TX {current.TransmittedBytes:N0} B";
                if (status.Text != statusText)
                {
                    status.Text = statusText;
                }
                if (!paused && replayBuffer is null && (batch.Events.Count != 0 || selection != connectionId || batch.ResetRequired))
                {
                    RefreshTraffic();
                }
            });
        }
        finally
        {
            polling = false;
        }
    }

    private void RefreshTraffic()
    {
        if (paused)
        {
            return;
        }
        var buffer = replayBuffer ?? (connectionId is { } id ? buffers.GetValueOrDefault(id) : null);
        if (buffer is null)
        {
            SetTrafficRows([]);
            return;
        }

        var current = snapshots.FirstOrDefault(item => item.Id == connectionId);
        buffer.SetPresentation(Encoding.GetEncoding(replayBuffer is not null ? encoding.Text : current?.Options.EncodingName ?? encoding.Text),
            format.Text == "文本", timestamps.Value == CheckState.Checked, hexGap.Value);
        SetTrafficRows(buffer.Rows.Where(row => (direction.Text != "RX" || row.IsReceive) && (direction.Text != "TX" || row.IsTransmit)
            && (sourceFilter.Text.Length == 0 || row.Source.Contains(sourceFilter.Text, StringComparison.OrdinalIgnoreCase))
            && (filter.Text.Length == 0 || row.Display.Contains(filter.Text, StringComparison.OrdinalIgnoreCase) || row.Source.Contains(filter.Text, StringComparison.OrdinalIgnoreCase))).ToArray());
    }

    internal void SetTrafficRows(TrafficRow[] rows)
    {
        var selected = traffic.GetAllSelectedCells().Select(static cell => cell.Y).Where(index => index >= 0 && index < visibleRows.Length)
            .Select(index => visibleRows[index].Identity).ToHashSet();
        var selection = traffic.Value;
        var extended = selection?.Regions.Where(static region => region.IsExtended)
            .SelectMany(region => visibleRows.Skip(Math.Max(0, region.Rectangle.Top)).Take(region.Rectangle.Height)).Select(static row => row.Identity).ToHashSet() ?? [];
        var cursorId = selection is not null && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < visibleRows.Length
            ? visibleRows[selection.SelectedCell.Y].Identity : (TrafficRowIdentity?)null;
        var topId = traffic.RowOffset >= 0 && traffic.RowOffset < visibleRows.Length ? visibleRows[traffic.RowOffset].Identity : (TrafficRowIdentity?)null;
        var rowOffset = traffic.RowOffset;
        var columnOffset = traffic.ColumnOffset;
        var unchanged = visibleRows.SequenceEqual(rows);
        visibleRows = rows;
        updatingTraffic = true;
        try
        {
            if (!unchanged)
            {
                traffic.Table = new EnumerableTableSource<TrafficRow>(visibleRows, new Dictionary<string, Func<TrafficRow, object>>
                {
                    ["时间"] = row => row.ShowTimestamp ? row.Time : "",
                    ["方向"] = row => row.IsReceive ? "RX" : "TX",
                    ["来源"] = row => row.Source,
                    ["内容"] = row => row.Display,
                });
            }
            else
            {
                traffic.RefreshContentSize();
                traffic.SetNeedsDraw();
            }
            if (follow.Value == CheckState.Checked && !paused && visibleRows.Length > 0)
            {
                traffic.MoveCursorToEndOfTable(false, null);
            }
            else
            {
                var cursor = Array.FindIndex(rows, row => row.Identity == cursorId);
                var regions = new List<TableSelectionRegion>();
                for (var index = 0; index < rows.Length; index++)
                {
                    if (!selected.Contains(rows[index].Identity))
                    {
                        continue;
                    }
                    var first = index;
                    var isExtended = extended.Contains(rows[index].Identity);
                    while (index + 1 < rows.Length && selected.Contains(rows[index + 1].Identity) && extended.Contains(rows[index + 1].Identity) == isExtended)
                    {
                        index++;
                    }
                    var point = new System.Drawing.Point(0, first);
                    regions.Add(new TableSelectionRegion(point, new System.Drawing.Rectangle(0, first, traffic.Table?.Columns ?? 4, index - first + 1)) { IsExtended = isExtended });
                }
                cursor = cursor >= 0 ? cursor : regions.FirstOrDefault()?.Origin.Y ?? -1;
                traffic.Value = cursor >= 0 ? new TableSelection(new System.Drawing.Point(selection?.SelectedCell.X ?? 0, cursor), regions) : null;
                var top = Array.FindIndex(rows, row => row.Identity == topId);
                traffic.RowOffset = top >= 0 ? top : rowOffset;
                traffic.ColumnOffset = columnOffset;
            }
        }
        finally
        {
            updatingTraffic = false;
        }
    }

    private async Task RefreshPortsAsync()
    {
        var listed = await client.ListPortsAsync(lifetime.Token).ConfigureAwait(false);
        app.Invoke(() =>
        {
            ports = listed;
            var selected = port.Text;
            var deviceId = configuredDeviceId;
            port.Source = new ListWrapper<string>(new ObservableCollection<string>(ports.Select(static item => item.PortName)));
            port.Text = ports.FirstOrDefault(item => deviceId is not null ? item.DeviceInstanceId == deviceId : item.PortName == selected)?.PortName
                ?? (deviceId is not null ? selected : ports.Count > 0 ? ports[0].PortName : "");
            configuredDeviceId = deviceId;
        });
    }

    private async Task OpenConnectionAsync()
    {
        var options = ReadConnectionOptions();
        var snapshot = await client.OpenConnectionAsync(new OpenConnectionRequest(options), lifetime.Token).ConfigureAwait(false);
        app.Invoke(() =>
        {
            connectionId = snapshot.Id;
            tabs.Value = workbenchView;
            message.Text = $"已连接 {snapshot.Options.PortName}，{snapshot.Options.BaudRate} baud";
            input.SetFocus();
        });
    }

    private async Task CloseConnectionAsync()
    {
        if (connectionId is { } id)
        {
            await client.CloseConnectionAsync(id, lifetime.Token).ConfigureAwait(false);
        }
    }

    private static string ConnectionStateText(ConnectionState state) => state switch
    {
        ConnectionState.Closed => "已断开",
        ConnectionState.Opening => "连接中",
        ConnectionState.Open => "已连接",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.Faulted => "连接失败",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private async Task SendAsync()
    {
        var text = input.Text;
        var request = CreateSendRequest();
        await client.SendAsync(request, lifetime.Token).ConfigureAwait(false);
        var configuration = await client.AddSendHistoryAsync(text, lifetime.Token).ConfigureAwait(false);
        app.Invoke(() =>
        {
            ApplyConfiguration(configuration);
            message.Text = $"已发送 {request.Data.Length} 字节";
        });
    }

    private void TogglePause()
    {
        paused = !paused;
        if (paused)
        {
            SetTrafficRows(visibleRows.Select(static row => row.Snapshot()).ToArray());
        }
        else
        {
            RefreshTraffic();
        }
        message.Text = paused ? "显示已暂停，Host 继续采集" : "继续显示";
    }

    private void ClearTraffic()
    {
        var buffer = replayBuffer ?? (connectionId is { } id ? buffers.GetValueOrDefault(id) : null);
        if (buffer is not null)
        {
            buffer.Clear();
            paused = false;
            RefreshTraffic();
        }
    }

    private void CopySelected()
    {
        var selected = traffic.GetAllSelectedCells().Select(static cell => cell.Y).Distinct().Where(index => index >= 0 && index < visibleRows.Length).Order().Select(index => visibleRows[index]).ToArray();
        var kind = copyFormat.Text switch
        {
            "文本" => TrafficCopyFormat.Text,
            "HEX" => TrafficCopyFormat.Hex,
            "连续 HEX" => TrafficCopyFormat.CompactHex,
            "日志" => TrafficCopyFormat.Log,
            _ => TrafficCopyFormat.CurrentDisplay
        };
        if (app.Clipboard?.TrySetClipboardData(TrafficCopyFormatter.Format(selected, kind)) != true)
        {
            message.Text = "剪贴板不可用";
        }
        else
        {
            message.Text = $"已复制 {selected.Length} 条报文";
        }
    }

    private Task RunUiAsync(Func<Task> action)
    {
        var task = ExecuteUiAsync(action);
        lock (actionsGate)
        {
            pendingActions.Add(task);
        }
        _ = task.ContinueWith(completed =>
        {
            lock (actionsGate)
            {
                pendingActions.Remove(completed);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task ExecuteUiAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested)
            {
                app.Invoke(() => message.Text = ex.Message);
            }
        }
    }

    private Task InvokeUiAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Invoke(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task.WaitAsync(lifetime.Token);
    }

    private static void LimitNumber(NumericUpDown<int> input, int minimum, int maximum) =>
        input.ValueChanging += (_, args) => args.Handled = args.NewValue < minimum || args.NewValue > maximum;

    private static void EnableFormScrolling(View view)
    {
        view.FocusedChanged += (_, _) =>
        {
            if (view.Focused is not { } focused)
            {
                return;
            }
            if (focused.Frame.Top < view.Viewport.Top)
            {
                view.ScrollVertical(focused.Frame.Top - view.Viewport.Top);
            }
            else if (focused.Frame.Bottom > view.Viewport.Bottom)
            {
                view.ScrollVertical(focused.Frame.Bottom - view.Viewport.Bottom);
            }
            if (focused.Frame.Left < view.Viewport.Left)
            {
                view.ScrollHorizontal(focused.Frame.Left - view.Viewport.Left);
            }
            else if (focused.Frame.Right > view.Viewport.Right)
            {
                view.ScrollHorizontal(focused.Frame.Right - view.Viewport.Right);
            }
        };
        view.KeyBindings.Add(Key.PageUp, Command.ScrollUp);
        view.KeyBindings.Add(Key.PageDown, Command.ScrollDown);
        view.MouseBindings.Add(MouseFlags.WheeledUp, Command.ScrollUp);
        view.MouseBindings.Add(MouseFlags.WheeledDown, Command.ScrollDown);
        view.CommandNotBound += (_, args) =>
        {
            if (args.Context?.Command is Command.ScrollUp or Command.ScrollDown)
            {
                var amount = args.Context.Binding is MouseBinding ? 3 : Math.Max(1, view.Viewport.Height - 1);
                view.ScrollVertical(args.Context.Command == Command.ScrollUp ? -amount : amount);
                args.Handled = true;
            }
        };
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, ShadowStyle = null };
        button.Accepting += async (_, args) =>
        {
            args.Handled = true;
            button.Enabled = false;
            try
            {
                await action();
            }
            finally
            {
                if (!lifetime.IsCancellationRequested)
                {
                    app.Invoke(() => button.Enabled = true);
                }
            }
        };
        return button;
    }
}
