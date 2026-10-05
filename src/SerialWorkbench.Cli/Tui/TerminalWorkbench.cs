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
    private readonly Window window = new() { Title = "SerialWorkbench", BorderStyle = LineStyle.None, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Tabs tabs = new() { Width = Dim.Fill(), Height = Dim.Fill(2), TabLineStyle = LineStyle.Single };
    private readonly Label status = new() { Id = "connection-status", Width = Dim.Fill(), Height = 1, Text = "未连接" };
    private readonly CheckBox backgroundTasks = new() { Text = "后台任务" };
    private readonly Label message = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly ListView connections = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly ObservableCollection<string> connectionItems = [];
    private readonly TableView traffic = new() { Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
    private readonly TextField input = new() { Id = "send-input" };
    private readonly DropDownList format = new() { Width = 12, ReadOnly = true, Text = "HEX", Source = new ListWrapper<string>(new ObservableCollection<string>(["HEX", "文本"])) };
    private readonly DropDownList direction = new() { Width = 12, ReadOnly = true, Text = "全部", Source = new ListWrapper<string>(new ObservableCollection<string>(["全部", "RX", "TX"])) };
    private readonly TextField filter = new() { Id = "traffic-filter" };
    private readonly TextField sourceFilter = new() { Id = "traffic-source" };
    private PopoverMenu? trafficMenu;
    private readonly CheckBox follow = new() { Text = "跟随", Value = CheckState.Checked };
    private readonly CheckBox timestamps = new() { Text = "时间", Value = CheckState.Checked };
    private readonly DropDownList copyFormat = new()
    {
        Width = 16,
        ReadOnly = true,
        Text = "当前显示",
        Source = new ListWrapper<string>(new ObservableCollection<string>(["当前显示", "文本", "HEX", "连续 HEX", "日志"]))
    };
    private readonly DropDownList port = new() { Id = "connection-port", Width = 12, ReadOnly = true };
    private readonly NumericUpDown<int> baud = new() { Id = "connection-baud", CanEdit = true, Value = 115200, Width = 14 };
    private readonly NumericUpDown<int> dataBits = new() { CanEdit = true, Value = 8, Width = 20 };
    private readonly DropDownList parity = EnumSelector(SerialParity.None);
    private readonly DropDownList stopBits = EnumSelector(SerialStopBits.One);
    private readonly DropDownList handshake = EnumSelector(SerialHandshake.None);
    private readonly DropDownList role = EnumSelector(SerialConnectionRole.Dut);
    private readonly CheckBox dtr = new() { Text = "DTR" };
    private readonly CheckBox rts = new() { Text = "RTS" };
    private readonly CheckBox rs485 = new() { Text = "RS-485 RTS 方向控制" };
    private readonly CheckBox autoReconnect = new() { Text = "自动重连", Value = CheckState.Checked };
    private readonly NumericUpDown<int> rtsBefore = new() { CanEdit = true, Value = 0, Width = 20 };
    private readonly NumericUpDown<int> rtsAfter = new() { CanEdit = true, Value = 0, Width = 20 };
    private readonly DropDownList profileSelector = new() { Width = 30, ReadOnly = true };
    private readonly TextField profileName = new() { Width = 30 };
    private SerialProfile[] profiles = [];
    private long configurationRevision;
    private readonly NumericUpDown<int> hexGap = new() { CanEdit = true, Value = 10, Width = 20 };
    private readonly TextField encoding = new() { Width = 20, Text = "utf-8" };
    private readonly DropDownList lineEnding = new() { Width = 20, Text = "无", ReadOnly = true, Source = new ListWrapper<string>(new ObservableCollection<string>(["无", "CR", "LF", "CRLF"])) };
    private readonly ListView history = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Label workspace = new() { Width = Dim.Fill(), Text = "全局工作区" };
    private readonly ObservableCollection<string> historyItems = [];
    private readonly Dictionary<Guid, TrafficBuffer> buffers = [];
    private IReadOnlyList<ConnectionSnapshot> snapshots = [];
    private IReadOnlyList<SerialPortDescriptor> ports = [];
    private TrafficRow[] visibleRows = [];
    private readonly View workbenchView;
    private readonly View settingsView;
    private readonly View[] tools;
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
        var title = new Label { Text = "SerialWorkbench", Height = 1 };
        status.X = Pos.Right(title) + 2;
        tabs.Y = Pos.Bottom(title);
        connections.SetSource(connectionItems);
        connections.ValueChanged += (_, args) =>
        {
            if (!updatingConnections && args.NewValue is { } index && index >= 0 && index < snapshots.Count)
            {
                connectionId = snapshots[index].Id;
                ApplyConnectionOptions(snapshots[index].Options);
                ResumeLiveTraffic();
                RefreshTraffic();
            }
        };
        workbenchView = BuildWorkbench();
        settingsView = BuildSettings();
        BuildConnectionSettings();
        BuildTrafficSettings();
        BuildSendSettings();
        tabs.Add(workbenchView, BuildHistory(), BuildTasks(), BuildSessions(), settingsView);
        var workspaceNumber = '1';
        foreach (var page in tabs.TabCollection)
        {
            page.Title = $"{workspaceNumber} {page.Title}";
            page.HotKey = new Key(workspaceNumber++);
        }
        tools = [BuildModbus(), BuildTransfers(), BuildAutomation(), BuildProtocol(), BuildWaveform()];
        foreach (var tool in tools)
        {
            tool.CanFocus = true;
        }
        BuildShortcuts();
        window.Add(title, status, message, tabs, shortcuts, terminalSize);
        window.SubViewLayout += (_, _) => UpdateTerminalSize();
        tabs.Value = workbenchView;
        UpdateShortcutHints();
        format.ValueChanged += (_, _) => RefreshTraffic();
        direction.ValueChanged += (_, _) => RefreshTraffic();
        filter.TextChanged += (_, _) => RefreshTraffic();
        timestamps.ValueChanged += (_, _) => RefreshTraffic();
        sourceFilter.TextChanged += (_, _) => RefreshTraffic();
        follow.ValueChanged += (_, _) => UpdateConnectionStatus();
        window.Initialized += (_, _) => RegisterTrafficMenu();
        traffic.KeyBindings.Add(Key.C.WithCtrl, Command.Copy);
        traffic.KeyBindings.Add(Key.Space.WithCtrl, Command.Context);
        traffic.KeyBindings.Add(Key.F.WithCtrl, Command.Edit);
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
            else if (args.Context?.Command == Command.Edit)
            {
                ShowSettingsDialog("报文显示与筛选", trafficSettings, 18);
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

        foreach (var command in shortcutCommands.Where(static item => item.SuperView is null))
        {
            command.Dispose();
        }
        window.Dispose();
        serialSettings.Dispose();
        controlSettings.Dispose();
        profileSettings.Dispose();
        connectionSettings.Dispose();
        trafficSettings.Dispose();
        sendSettings.Dispose();
        sequenceEditor.Dispose();
        foreach (var tool in tools)
        {
            tool.Dispose();
        }
        if (trafficMenu is not null)
        {
            app.Popovers?.DeRegister(trafficMenu);
            trafficMenu.Dispose();
        }
        sessionReadGate.Dispose();
        replay?.Dispose();
        lifetime.Dispose();
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
                SetConnections(host.Connections);
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
                UpdateConnectionStatus();
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

    internal void SetConnections(IReadOnlyList<ConnectionSnapshot> current)
    {
        var selected = connectionId;
        snapshots = current;
        updatingConnections = true;
        try
        {
            var names = snapshots.Select(static snapshot => $"{snapshot.Options.PortName} {ConnectionStateText(snapshot.State)}").ToArray();
            if (!connectionItems.SequenceEqual(names))
            {
                connectionItems.Clear();
                foreach (var name in names)
                {
                    connectionItems.Add(name);
                }
            }
            foreach (var id in buffers.Keys.Where(id => !snapshots.Any(item => item.Id == id)).ToArray())
            {
                buffers.Remove(id);
            }
            foreach (var snapshot in snapshots)
            {
                if (!buffers.ContainsKey(snapshot.Id))
                {
                    buffers.Add(snapshot.Id, new TrafficBuffer(Encoding.GetEncoding(snapshot.Options.EncodingName)));
                }
            }
            connectionId = selected is { } existing && snapshots.Any(item => item.Id == existing) ? existing : snapshots.Count > 0 ? snapshots[0].Id : null;
            connections.Value = connectionId is { } selectedId ? snapshots.ToList().FindIndex(item => item.Id == selectedId) : null;
            managedConnections.Value = connections.Value;
            if (connectionId != selected && snapshots.FirstOrDefault(item => item.Id == connectionId) is { } changed)
            {
                ApplyConnectionOptions(changed.Options);
                ResetWaveform();
            }
            UpdateConnectionStatus();
        }
        finally
        {
            updatingConnections = false;
        }
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
        UpdateConnectionStatus();
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
            SetConnections([.. snapshots.Where(item => item.Id != snapshot.Id), snapshot]);
            ApplyConnectionOptions(snapshot.Options);
            ResumeLiveTraffic();
            tabs.Value = workbenchView;
            message.Text = $"已连接 {snapshot.Options.PortName}，{snapshot.Options.BaudRate} baud";
            if (connectionDialog is not null)
            {
                connectionDialog.Result = 1;
                app.RequestStop(connectionDialog);
            }
            else
            {
                input.SetFocus();
            }
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
        UpdateConnectionStatus();
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
