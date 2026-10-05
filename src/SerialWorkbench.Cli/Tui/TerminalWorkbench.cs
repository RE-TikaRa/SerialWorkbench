using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Protocols;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiApplication = Terminal.Gui.App.Application;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench : IDisposable
{
    private readonly IApplication app;
    private readonly IHostRpc client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Window window = new() { Title = "SerialWorkbench", Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Tabs tabs = new() { Y = 2, Width = Dim.Fill(), Height = Dim.Fill(4) };
    private readonly Label status = new() { Y = 0, Width = Dim.Fill(16), Text = "正在连接 Host" };
    private readonly CheckBox backgroundTasks = new() { X = Pos.AnchorEnd(15), Y = 0, Text = "后台任务" };
    private readonly Label message = new() { Y = 1, Width = Dim.Fill() };
    private readonly ListView connections = new() { Width = Dim.Fill(), Height = Dim.Fill(1) };
    private readonly ObservableCollection<string> connectionItems = [];
    private readonly TableView traffic = new() { Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true };
    private readonly TextField input = new() { X = 0, Width = Dim.Fill(12) };
    private readonly DropDownList format = new() { X = 0, Y = 0, Width = 12, ReadOnly = true, Text = "HEX", Source = new ListWrapper<string>(new ObservableCollection<string>(["HEX", "文本"])) };
    private readonly DropDownList direction = new() { X = 14, Width = 12, ReadOnly = true, Text = "全部", Source = new ListWrapper<string>(new ObservableCollection<string>(["全部", "RX", "TX"])) };
    private readonly TextField filter = new() { X = 28, Width = Dim.Fill(1) };
    private readonly CheckBox follow = new() { X = 0, Y = Pos.AnchorEnd(), Text = "跟随", Value = CheckState.Checked };
    private readonly CheckBox timestamps = new() { X = 12, Y = Pos.AnchorEnd(), Text = "时间", Value = CheckState.Checked };
    private readonly OptionSelector<TrafficCopyFormat> copyFormat = new() { X = 24, Y = Pos.AnchorEnd(), Orientation = Orientation.Horizontal };
    private readonly DropDownList port = new() { X = 16, Y = 0, Width = Dim.Fill(1), ReadOnly = true };
    private readonly NumericUpDown<int> baud = new() { X = 16, Y = 2, Value = 115200, Width = 20 };
    private readonly NumericUpDown<int> dataBits = new() { X = 16, Y = 4, Value = 8, Width = 20 };
    private readonly OptionSelector<SerialParity> parity = new() { X = 16, Y = 6, Orientation = Orientation.Horizontal };
    private readonly OptionSelector<SerialStopBits> stopBits = new() { X = 16, Y = 9, Orientation = Orientation.Horizontal };
    private readonly OptionSelector<SerialHandshake> handshake = new() { X = 16, Y = 20, Orientation = Orientation.Horizontal };
    private readonly OptionSelector<SerialConnectionRole> role = new() { X = 16, Y = 23, Orientation = Orientation.Horizontal };
    private readonly CheckBox dtr = new() { X = 16, Y = 26, Text = "DTR" };
    private readonly CheckBox rts = new() { X = 32, Y = 26, Text = "RTS" };
    private readonly CheckBox rs485 = new() { X = 16, Y = 28, Text = "RS-485 RTS 方向控制" };
    private readonly CheckBox autoReconnect = new() { X = 16, Y = 30, Text = "自动重连", Value = CheckState.Checked };
    private readonly NumericUpDown<int> rtsBefore = new() { X = 16, Y = 32, Value = 0, Width = 20 };
    private readonly NumericUpDown<int> rtsAfter = new() { X = 16, Y = 34, Value = 0, Width = 20 };
    private readonly DropDownList profileSelector = new() { X = 16, Y = 38, Width = 30, ReadOnly = true };
    private readonly TextField profileName = new() { X = 16, Y = 40, Width = 30 };
    private SerialProfile[] profiles = [];
    private long configurationRevision;
    private readonly NumericUpDown<int> hexGap = new() { X = 16, Y = 13, Value = 10, Width = 20 };
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
        close.Y = Pos.AnchorEnd();
        connectionsFrame.Add(connections, close);
        var trafficFrame = new FrameView { Title = "报文", X = Pos.Right(connectionsFrame), Width = Dim.Fill(), Height = Dim.Fill() };
        var filters = new View { Height = 1, Width = Dim.Fill() };
        filters.Add(format, direction, filter);
        traffic.Y = 1;
        trafficFrame.Add(filters, traffic, follow, timestamps, copyFormat);
        var workbench = new View { Title = "工作台", Width = Dim.Fill(), Height = Dim.Fill() };
        workbenchView = workbench;
        settingsView = BuildSettings();
        workbench.Add(connectionsFrame, trafficFrame);
        tabs.Add(workbench, settingsView, BuildHistory(), BuildModbus(), BuildTransfers(), BuildTasks(), BuildSessions(), BuildProtocol(), BuildWaveform());
        var send = Button("发送", () => RunUiAsync(SendAsync));
        send.X = Pos.AnchorEnd();
        var sending = new FrameView { Title = "发送 · 输入框 Enter 发送", Y = Pos.AnchorEnd(4), Height = 3, Width = Dim.Fill() };
        input.Accepting += (_, args) => { args.Handled = true; _ = RunUiAsync(SendAsync); };
        sending.Add(input, send);
        var shortcuts = new StatusBar([
            new Shortcut(Key.F2, "暂停", TogglePause),
            new Shortcut(Key.F3, "清空", ClearTraffic),
            new Shortcut(Key.F4, "设置", () => tabs.Value = settingsView),
            new Shortcut(Key.F5, "刷新", () => _ = RunUiAsync(RefreshPortsAsync)),
            new Shortcut(Key.F6, "复制", CopySelected),
            new Shortcut(Key.F7, "实时", ResumeLiveTraffic),
            new Shortcut(Key.Q.WithCtrl, "退出", () => app.RequestStop(window)),
        ]);
        window.Add(status, backgroundTasks, message, tabs, sending, shortcuts);
        format.ValueChanged += (_, _) => RefreshTraffic();
        direction.ValueChanged += (_, _) => RefreshTraffic();
        filter.TextChanged += (_, _) => RefreshTraffic();
        timestamps.ValueChanged += (_, _) => RefreshTraffic();
        port.ValueChanged += (_, _) => configuredDeviceId = null;
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
        sessionReadGate.Dispose();
        replay?.Dispose();
        lifetime.Dispose();
    }

    private View BuildSettings()
    {
        var view = new View { Title = "设置", Width = Dim.Fill(), Height = Dim.Fill(), ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        view.Add(new Label { Text = "端口", Y = 0 }, port,
            new Label { Text = "波特率", Y = 2 }, baud,
            new Label { Text = "数据位", Y = 4 }, dataBits,
            new Label { Text = "校验", Y = 6 }, parity,
            new Label { Text = "停止位", Y = 9 }, stopBits,
            new Label { Text = "HEX 间隔 ms", Y = 13 }, hexGap,
            new Label { Text = "文本编码", Y = 15 }, encoding,
            new Label { Text = "行尾", Y = 17 }, lineEnding,
            new Label { Text = "流控", Y = 20 }, handshake,
            new Label { Text = "设备角色", Y = 23 }, role,
            dtr, rts, rs485, autoReconnect,
            new Label { Text = "发送前延时 ms", Y = 32 }, rtsBefore,
            new Label { Text = "发送后延时 ms", Y = 34 }, rtsAfter,
            new Label { Text = "连接配置", Y = 38 }, profileSelector,
            new Label { Text = "配置名称", Y = 40 }, profileName);
        var open = Button("打开连接", () => RunUiAsync(OpenConnectionAsync));
        open.Y = 36;
        var controlLines = Button("更新 DTR/RTS", () => RunUiAsync(() => client.SetControlLinesAsync(RequiredConnection(),
            new SerialControlLines(dtr.Value == CheckState.Checked, rts.Value == CheckState.Checked), lifetime.Token)));
        controlLines.X = Pos.Right(open) + 1;
        controlLines.Y = 36;
        var loadProfile = Button("应用配置", () =>
        {
            var selected = profiles.FirstOrDefault(item => item.Name == profileSelector.Text);
            if (selected is not null)
            {
                ApplySerialProfile(selected);
            }
            return Task.CompletedTask;
        });
        loadProfile.Y = 42;
        var saveProfile = Button("保存配置", () => RunUiAsync(async () =>
        {
            var options = ReadConnectionOptions();
            var profile = new SerialProfile(profileName.Text, options.PortName, options.BaudRate, options.DataBits, options.Parity, options.StopBits,
                options.Handshake, options.EncodingName, options.DtrEnable, options.RtsEnable, options.Role, options.DeviceInstanceId, options.Rs485Mode,
                options.RtsBeforeSendMilliseconds, options.RtsAfterSendMilliseconds, options.AutoReconnect);
            var original = profiles.Any(item => item.Name == profileName.Text) ? profileName.Text : null;
            var saved = await client.SaveSerialProfileAsync(new SaveSerialProfileRequest(profile, original), lifetime.Token).ConfigureAwait(false);
            await InvokeUiAsync(() => ApplyConfiguration(saved)).ConfigureAwait(false);
        }));
        saveProfile.Y = 42;
        saveProfile.X = Pos.Right(loadProfile) + 1;
        var deleteProfile = Button("删除配置", () => RunUiAsync(async () =>
        {
            var saved = await client.DeleteSerialProfileAsync(profileSelector.Text, lifetime.Token).ConfigureAwait(false);
            await InvokeUiAsync(() => ApplyConfiguration(saved)).ConfigureAwait(false);
        }));
        deleteProfile.Y = 42;
        deleteProfile.X = Pos.Right(saveProfile) + 1;
        var chooseWorkspace = Button("选择工作区", () => RunUiAsync(async () =>
        {
            using var dialog = new OpenDialog { Title = "选择工作区", OpenMode = OpenMode.Directory, Path = Environment.CurrentDirectory, AllowsMultipleSelection = false };
            app.Run(dialog);
            if (!dialog.Canceled)
            {
                await client.SetWorkspaceAsync(new SetWorkspaceRequest(dialog.Path), lifetime.Token).ConfigureAwait(false);
                await RefreshSessionsAsync().ConfigureAwait(false);
            }
        }));
        chooseWorkspace.Y = 47;
        var clearWorkspace = Button("全局工作区", () => RunUiAsync(async () =>
        {
            await client.SetWorkspaceAsync(new SetWorkspaceRequest(null), lifetime.Token).ConfigureAwait(false);
            await RefreshSessionsAsync().ConfigureAwait(false);
        }));
        clearWorkspace.Y = 47;
        clearWorkspace.X = Pos.Right(chooseWorkspace) + 1;
        view.SetContentSize(new System.Drawing.Size(100, 50));
        view.Add(open, controlLines, loadProfile, saveProfile, deleteProfile, workspace, chooseWorkspace, clearWorkspace);
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
                            FeedWaveform(item.Data);
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
                status.Text = current is null ? $"Host · {host.ClientCount} 客户端 · 未选择连接"
                    : $"{current.Options.PortName} · {current.State} · {current.Options.BaudRate} · RX {current.ReceivedBytes:N0} B / {current.ReceivedBytesPerSecond:N0} B/s · TX {current.TransmittedBytes:N0} B";
                if (!paused && replayBuffer is null)
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
        app.Invoke(() => { connectionId = snapshot.Id; tabs.Value = workbenchView; });
    }

    private async Task CloseConnectionAsync()
    {
        if (connectionId is { } id)
        {
            await client.CloseConnectionAsync(id, lifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task SendAsync()
    {
        var id = connectionId ?? throw new InvalidOperationException("请先连接串口。");
        var text = input.Text;
        var ending = lineEnding.Text switch { "CR" => "\r", "LF" => "\n", "CRLF" => "\r\n", _ => "" };
        var data = format.Text == "HEX" ? HexCodec.Parse(text) : Encoding.GetEncoding(encoding.Text).GetBytes(text + ending);
        await client.SendAsync(new SendRequest(id, data, "tui.send"), lifetime.Token).ConfigureAwait(false);
        var configuration = await client.AddSendHistoryAsync(text, lifetime.Token).ConfigureAwait(false);
        app.Invoke(() =>
        {
            ApplyConfiguration(configuration);
            message.Text = $"已发送 {data.Length} 字节";
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
        if (app.Clipboard?.TrySetClipboardData(TrafficCopyFormatter.Format(selected, copyFormat.Value ?? TrafficCopyFormat.CurrentDisplay)) != true)
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

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text };
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
