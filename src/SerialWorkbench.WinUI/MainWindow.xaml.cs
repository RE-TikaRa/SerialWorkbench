using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.WinUI.Pages;

namespace SerialWorkbench.WinUI;

public sealed partial class MainWindow : Window
{
    private const int MaxReconnectAttempts = 5;
    private const int SessionEventPageSize = 1000;
    private readonly DispatcherTimer eventTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer portRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer loopSendTimer = new();
    private bool loopSending;
    private HostRpcClient? client;
    private Guid? connectionId;
    private readonly Dictionary<Guid, ConnectionContext> connectionContexts = [];
    private SerialConnectionOptions? reconnectOptions;
    private int reconnectAttempts;
    private bool reconnectInProgress;
    private long lastSequence;
    private bool paused;
    private long pauseBaselineBytes;
    private long currentTrafficBytes;
    private bool polling;
    private bool portRefreshing;
    private Decoder? textDecoder;
    private ElementTheme currentTheme;
    private bool workspaceSelected;
    private string workspacePath = "尚未选择工作区";
    private Guid? activeSessionId;
    private readonly object replayGate = new();
    private CancellationTokenSource? replayCancellation;
    private Guid? replaySessionId;
    private bool replayPaused;
    private int replayCurrent;
    private int replayTotal;
    private long replaySequence;
    private TaskCompletionSource<bool> replayStateChanged = CreateReplaySignal();
    private int sessionEventRequestVersion;
    private Guid? sessionEventSessionId;
    private long sessionEventAfterSequence;
    private SessionEventFilter sessionEventFilter = new(null, null, null);
    private WorkbenchPage? workbenchPage;
    private ConnectionsPage? connectionsPage;
    private LoopbackPage? loopbackPage;
    private SettingsPage? settingsPage;
    private SessionsPage? sessionsPage;
    private TerminalPage? terminalPage;
    private AutomationPage? automationPage;
    private XmodemPage? xmodemPage;
    private Action? sequenceCancel;
    private Action? loopbackCancel;
    private Action? xmodemCancel;
    private CancellationTokenSource? modbusScanCancellation;
    private CancellationTokenSource? modbusPollCancellation;
    private ModbusPage? modbusPage;
    private readonly SerialPreference serialPreference = SerialPreferenceStore.Load();
    private readonly string? preferredWorkspace = WorkspacePreferenceStore.Load();

    public MainWindow()
    {
        InitializeComponent();
        currentTheme = ThemePreference.Load();
        RootGrid.RequestedTheme = currentTheme;
        Title = "SerialWorkbench";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "SerialWorkbench.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 860));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TrafficRows = [];
        UpdateTrafficPresentation();
        ContentFrame.Navigate(typeof(WorkbenchPage), null, new SuppressNavigationTransitionInfo());
        Navigation.SelectedItem = Navigation.MenuItems[0];
        eventTimer.Tick += EventTimer_Tick;
        portRefreshTimer.Tick += PortRefreshTimer_Tick;
        loopSendTimer.Tick += LoopSendTimer_Tick;
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;
    }

    public ObservableCollection<TrafficRow> TrafficRows { get; private set; }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await ConnectHostAsync();
    }

    private async Task ConnectHostAsync()
    {
        SetConnectionBusy(true);
        ErrorInfoBar.IsOpen = false;
        workbenchPage?.SetConnectionStatus("正在连接服务…");
        try
        {
            client = await HostEndpoint.ConnectAsync(AppContext.BaseDirectory, true, CancellationToken.None);
            var handshake = await client.HandshakeAsync(new HandshakeRequest(RpcProtocol.MajorVersion, RpcProtocol.MinorVersion, "winui", System.Globalization.CultureInfo.CurrentUICulture.Name), CancellationToken.None);
            if (!handshake.Accepted)
            {
                throw new InvalidOperationException(handshake.Error);
            }

            workbenchPage?.SetConnectionStatus("未连接串口");
            await RefreshPortsAsync();
            workbenchPage?.ApplySerialPreference(serialPreference);
            SetSerialConfigurationEnabled(true);
            if (preferredWorkspace is not null && !workspaceSelected)
            {
                await ChangeWorkspaceAsync(preferredWorkspace);
            }
            eventTimer.Start();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            workbenchPage?.SetConnectionStatus("服务连接失败");
            ShowError(ex.Message);
        }
        finally
        {
            SetConnectionBusy(false);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (client is null)
        {
            await ConnectHostAsync();
            return;
        }

        SetConnectionBusy(true);
        try
        {
            if (connectionId is { } current)
            {
                await CloseConnectionFromUiAsync(current);
                return;
            }

            reconnectOptions = null;
            reconnectAttempts = 0;

            var selectedPort = workbenchPage?.SelectedPort;
            if (selectedPort is not SerialPortDescriptor port)
            {
                throw new InvalidOperationException("请选择串口。");
            }

            var encodingName = workbenchPage?.SelectedEncoding.WebName ?? "utf-8";
            var options = new SerialConnectionOptions(
                port.PortName,
                checked((int)(workbenchPage?.BaudRate ?? 115200)),
                workbenchPage?.DataBits ?? 8,
                workbenchPage?.Parity ?? SerialParity.None,
                workbenchPage?.StopBits ?? SerialStopBits.One,
                workbenchPage?.Handshake ?? SerialHandshake.None,
                workbenchPage?.DtrEnable ?? false,
                workbenchPage?.RtsEnable ?? false,
                encodingName,
                workbenchPage?.Role ?? SerialConnectionRole.Dut,
                port.DeviceInstanceId,
                workbenchPage?.Rs485Mode ?? false,
                workbenchPage?.RtsBeforeSendMilliseconds ?? 0,
                workbenchPage?.RtsAfterSendMilliseconds ?? 0);
            SerialPreferenceStore.Save(workbenchPage!.ReadSerialPreference(port.PortName));
            var connection = await client.OpenConnectionAsync(new OpenConnectionRequest(options), CancellationToken.None);
            connectionContexts[connection.Id] = new ConnectionContext(connection);
            ActivateConnection(connection.Id);
            if (workbenchPage is not null)
            {
                workbenchPage.ConnectButton.Content = "断开";
            }
            workbenchPage?.SetConnectionStatus($"{port.PortName} · {options.BaudRate:N0} baud");
            SetSerialConfigurationEnabled(false);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetConnectionBusy(false);
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SendCurrentAsync() && workbenchPage is not null)
        {
            workbenchPage.AddSendHistory(workbenchPage.SendText);
        }
    }

    private async Task<bool> SendCurrentAsync()
    {
        if (client is null || connectionId is not { } current)
        {
            workbenchPage?.ShowSendResult("请先连接串口。", InfoBarSeverity.Warning);
            return false;
        }

        var sendText = workbenchPage?.SendText ?? string.Empty;
        var sendFormatIndex = workbenchPage?.SendFormatIndex ?? 0;
        var lineEndingIndex = workbenchPage?.SendLineEndingIndex ?? 0;
        workbenchPage?.SetSending(true);
        try
        {
            var encoding = workbenchPage?.SelectedEncoding ?? Encoding.UTF8;
            var data = sendFormatIndex == 1
                ? Protocols.HexCodec.Parse(sendText)
                : encoding.GetBytes(sendText + GetLineEnding(lineEndingIndex));
            data = AppendChecksum(data, workbenchPage?.SendChecksumIndex ?? 0);
            await client.SendAsync(new SendRequest(current, data, "winui.send"), CancellationToken.None);
            workbenchPage?.ShowSendResult($"已发送 {data.Length:N0} 字节。", InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            workbenchPage?.ShowSendResult(ex.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            workbenchPage?.SetSending(false);
        }
    }

    private async void LoopbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || connectionId is not { } current)
        {
            loopbackPage?.ShowResult("请先连接 TX-RX 已回接的串口。", InfoBarSeverity.Warning);
            return;
        }

        if (loopbackPage is not null)
        {
            loopbackPage.SetRunning(true);
        }
        try
        {
            var request = new LoopbackRequest(
                current,
                checked(loopbackPage?.LengthValue ?? 64),
                checked(loopbackPage?.IterationsValue ?? 1),
                5000,
                loopbackPage is not null ? loopbackPage.PatternIndex switch
                {
                    0 => LoopbackPattern.Incrementing,
                    1 => LoopbackPattern.Fixed,
                    2 => LoopbackPattern.Random,
                    _ => LoopbackPattern.Incrementing,
                } : LoopbackPattern.Incrementing,
                0x534257);
            using var cancellation = new CancellationTokenSource();
            loopbackCancel = cancellation.Cancel;
            var result = await client.RunLoopbackAsync(request, cancellation.Token);
            var message = result.Passed
                ? $"通过 · {result.ReceivedBytes:N0} 字节 · {result.BytesPerSecond / 1024:N1} KiB/s · {result.Duration.TotalMilliseconds:N0} ms"
                : $"失败 · {result.Error} · 首个差异 {result.FirstDifferenceIndex}";
            var severity = result.Passed ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            loopbackPage?.ShowResult(message, severity);
            await RefreshStatusAsync();
            if (activeSessionId is { } sessionId && sessionsPage?.SelectedSession?.Id == sessionId)
            {
                await ReadLoopbackResultsAsync(sessionId, sessionsPage);
            }
        }
        catch (OperationCanceledException)
        {
            loopbackPage?.ShowResult("回环检测已停止。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            loopbackPage?.ShowResult(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            loopbackCancel = null;
            loopbackPage?.SetRunning(false);
        }
    }

    private async void EventTimer_Tick(object? sender, object e)
    {
        if (polling || replayCancellation is not null || client is null)
        {
            return;
        }

        polling = true;
        try
        {
            var anyEvents = false;
            foreach (var context in connectionContexts.Values.ToArray())
            {
                var isCurrent = context.Snapshot.Id == connectionId;
                var events = await client.ReadEventsAsync(new EventQuery(context.LastSequence, 1000, context.Snapshot.Id), CancellationToken.None);
                foreach (var item in events)
                {
                    anyEvents = true;
                    if (isCurrent && paused)
                    {
                        continue;
                    }

                    context.LastSequence = Math.Max(context.LastSequence, item.Sequence);
                    var encoding = Encoding.GetEncoding(context.Snapshot.Options.EncodingName);
                    var row = TrafficRow.From(item, workbenchPage?.MonitorFormatIndex == 1, encoding, workbenchPage?.ShowTimestamp ?? true, context.TextDecoder);
                    context.TrafficRows.Add(row);
                    while (context.TrafficRows.Count > 20_000)
                    {
                        context.TrafficRows.RemoveAt(0);
                    }

                    if (isCurrent)
                    {
                        lastSequence = context.LastSequence;
                        terminalPage?.AppendRow(row);
                        if (item.Direction == SerialDirection.Receive)
                        {
                            workbenchPage?.AppendWaveform(item.Data);
                        }
                    }
                }
            }

            if (anyEvents)
            {
                UpdateTrafficPresentation();
                if (TrafficRows.Count > 0 && !paused)
                {
                    workbenchPage?.TrafficListView.ScrollIntoView(TrafficRows[^1]);
                }

                await RefreshStatusAsync();
            }
        }
        catch (Exception ex)
        {
            workbenchPage?.SetConnectionStatus("服务连接中断");
            ShowError(ex.Message);
            eventTimer.Stop();
        }
        finally
        {
            polling = false;
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (client is null)
        {
            replayCancellation?.Dispose();
            sequenceCancel = null;
            return;
        }

        var status = await client.GetStatusAsync(CancellationToken.None);
        SyncConnectionContexts(status.Connections);
        var connection = status.Connections.FirstOrDefault(item => item.Id == connectionId);
        if (connectionId is { } current && (connection is null || connection.State is ConnectionState.Faulted or ConnectionState.Closed))
        {
            await HandleConnectionFaultAsync(current, connection, connection?.Error).ConfigureAwait(true);
            connection = null;
        }

        workbenchPage?.SetTrafficCounts(connection?.ReceivedBytes ?? 0, connection?.TransmittedBytes ?? 0);
        currentTrafficBytes = (connection?.ReceivedBytes ?? 0) + (connection?.TransmittedBytes ?? 0);
        workbenchPage?.SetPauseState(paused, paused ? Math.Max(0, currentTrafficBytes - pauseBaselineBytes) : 0);
        activeSessionId = status.ActiveSession?.Id;
        workspaceSelected = status.WorkspaceRoot is not null;
        workspacePath = status.WorkspaceRoot is null ? $"全局数据：{status.DataRoot}" : $"工作区：{status.WorkspaceRoot}";
        sessionsPage?.SetWorkspace(workspacePath);
        connectionsPage?.SetConnections(status.Connections, connectionId);
        connectionsPage?.SetHostMetrics(status.LatestEventSequence, status.PendingSessionEvents);
    }

    private void SyncConnectionContexts(IReadOnlyList<ConnectionSnapshot> snapshots)
    {
        var activeIds = snapshots.Select(static item => item.Id).ToHashSet();
        foreach (var snapshot in snapshots)
        {
            if (connectionContexts.TryGetValue(snapshot.Id, out var context))
            {
                context.Snapshot = snapshot;
            }
            else
            {
                connectionContexts[snapshot.Id] = new ConnectionContext(snapshot);
            }
        }

        foreach (var id in connectionContexts.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            connectionContexts.Remove(id);
        }
    }

    private void SaveCurrentContext()
    {
        if (connectionId is not { } current || !connectionContexts.TryGetValue(current, out var context))
        {
            return;
        }

        context.LastSequence = lastSequence;
        context.Paused = paused;
        context.PauseBaselineBytes = pauseBaselineBytes;
        context.CurrentTrafficBytes = currentTrafficBytes;
    }

    private void ActivateConnection(Guid id)
    {
        if (!connectionContexts.TryGetValue(id, out var context))
        {
            return;
        }

        SaveCurrentContext();
        connectionId = id;
        TrafficRows = context.TrafficRows;
        lastSequence = context.LastSequence;
        textDecoder = context.TextDecoder;
        paused = context.Paused;
        pauseBaselineBytes = context.PauseBaselineBytes;
        currentTrafficBytes = context.CurrentTrafficBytes;
        workbenchPage?.BindRows(TrafficRows);
        terminalPage?.BindRows(TrafficRows);
        if (workbenchPage is not null)
        {
            workbenchPage.ConnectButton.Content = "断开";
        }
        workbenchPage?.ApplySerialProfile(new SerialProfile(
            context.Snapshot.Options.Role.ToString(),
            context.Snapshot.Options.PortName,
            context.Snapshot.Options.BaudRate,
            context.Snapshot.Options.DataBits,
            context.Snapshot.Options.Parity,
            context.Snapshot.Options.StopBits,
            context.Snapshot.Options.Handshake,
            context.Snapshot.Options.EncodingName,
            context.Snapshot.Options.DtrEnable,
            context.Snapshot.Options.RtsEnable,
            context.Snapshot.Options.Role,
            context.Snapshot.Options.DeviceInstanceId,
            context.Snapshot.Options.Rs485Mode,
            context.Snapshot.Options.RtsBeforeSendMilliseconds,
            context.Snapshot.Options.RtsAfterSendMilliseconds));
        workbenchPage?.SetConnectionStatus($"{context.Snapshot.Options.PortName} · {context.Snapshot.Options.BaudRate:N0} baud");
        workbenchPage?.SetTrafficCounts(context.Snapshot.ReceivedBytes, context.Snapshot.TransmittedBytes);
        workbenchPage?.SetPauseState(paused, paused ? Math.Max(0, currentTrafficBytes - pauseBaselineBytes) : 0);
        SetSerialConfigurationEnabled(false);
    }

    private void ResetCurrentConnection()
    {
        connectionId = null;
        textDecoder = null;
        TrafficRows = [];
        lastSequence = 0;
        currentTrafficBytes = 0;
        pauseBaselineBytes = 0;
        paused = false;
        workbenchPage?.BindRows(TrafficRows);
        terminalPage?.BindRows(TrafficRows);
        workbenchPage?.StopLoopSend();
        if (workbenchPage is not null)
        {
            workbenchPage.ConnectButton.Content = "连接";
            workbenchPage.SetConnectionStatus("未连接串口");
            workbenchPage.SetTrafficCounts(0, 0);
            workbenchPage.SetPauseState(false, 0);
        }

        SetSerialConfigurationEnabled(true);
    }

    private async Task CloseConnectionFromUiAsync(Guid id)
    {
        if (client is null)
        {
            return;
        }

        if (id == connectionId)
        {
            loopbackCancel?.Invoke();
            xmodemCancel?.Invoke();
            workbenchPage?.StopLoopSend();
        }

        try
        {
            await client.CloseConnectionAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        var wasCurrent = id == connectionId;
        connectionContexts.Remove(id);
        if (wasCurrent)
        {
            var next = connectionContexts.Values.OrderBy(static item => item.Snapshot.Options.PortName).FirstOrDefault();
            if (next is null)
            {
                reconnectOptions = null;
                ResetCurrentConnection();
            }
            else
            {
                ActivateConnection(next.Snapshot.Id);
            }
        }

        await RefreshStatusAsync();
        if (connectionsPage is not null)
        {
            await RefreshConnectionsPageAsync(connectionsPage);
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        paused = !paused;
        if (paused)
        {
            pauseBaselineBytes = currentTrafficBytes;
        }

        workbenchPage?.SetPauseState(paused, 0);
    }

    private async Task HandleConnectionFaultAsync(Guid current, ConnectionSnapshot? snapshot, string? error)
    {
        loopbackCancel?.Invoke();
        sequenceCancel?.Invoke();
        xmodemCancel?.Invoke();
        workbenchPage?.StopLoopSend();
        try
        {
            await client!.CloseConnectionAsync(current, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        connectionContexts.Remove(current);
        var next = connectionContexts.Values.OrderBy(static item => item.Snapshot.Options.PortName).FirstOrDefault();
        reconnectOptions = snapshot?.Options.DeviceInstanceId is not null ? snapshot.Options : null;
        reconnectAttempts = 0;
        if (next is null)
        {
            ResetCurrentConnection();
            workbenchPage?.SetConnectionStatus(reconnectOptions is not null ? "设备已断开，等待重连" : "串口已断开");
        }
        else
        {
            ActivateConnection(next.Snapshot.Id);
        }

        connectionsPage?.SetConnections(connectionContexts.Values.Select(static item => item.Snapshot).ToArray(), connectionId);
        ShowMessage("串口已断开", error ?? "设备连接已经结束。", InfoBarSeverity.Warning);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        TrafficRows.Clear();
        UpdateTrafficPresentation();
    }

    private void CopyHexButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        var rows = workbenchPage?.VisibleRows ?? TrafficRows;
        package.SetText(string.Join(Environment.NewLine, rows.Select(static item => item.Hex)));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var destination = tag switch
        {
            "Terminal" => (PageType: typeof(TerminalPage), Title: "串口终端", Description: "使用当前串口连接进行文本或 HEX 交互。"),
            "Connections" => (PageType: typeof(ConnectionsPage), Title: "连接管理", Description: "查看、切换和关闭已打开的串口连接。"),
            "Loopback" => (PageType: typeof(LoopbackPage), Title: "回环检测", Description: "验证串口发送与接收链路是否正常。"),
            "Modbus" => (PageType: typeof(ModbusPage), Title: "Modbus RTU", Description: "构造读写请求帧发送到当前串口，并解析寄存器响应。"),
            "ProtocolInspector" => (PageType: typeof(ProtocolInspectorPage), Title: "协议帧", Description: "使用协议模板查看地址、功能码、长度、CRC 和异常字段。"),
            "Automation" => (PageType: typeof(AutomationPage), Title: "自动化", Description: "编辑、保存并运行串口发送序列。"),
            "Xmodem" => (PageType: typeof(XmodemPage), Title: "文件传输", Description: "使用 XMODEM-CRC 发送或接收文件。"),
            "Sessions" => (PageType: typeof(SessionsPage), Title: "会话记录", Description: "浏览和管理已保存的串口工作记录。"),
            "Settings" => (PageType: typeof(SettingsPage), Title: "设置", Description: "配置工作区、外观和应用行为。"),
            _ => (PageType: typeof(WorkbenchPage), Title: "工作台", Description: "连接串口、收发报文并查看实时波形。"),
        };
        PageTitleText.Text = destination.Title;
        PageDescriptionText.Text = destination.Description;
        if (ContentFrame.CurrentSourcePageType != destination.PageType)
        {
            ContentFrame.Navigate(destination.PageType, null, args.RecommendedNavigationTransitionInfo);
        }
    }

    private void ContentShell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 641;
        var horizontal = compact ? 12 : 24;
        PageHeader.Margin = new Thickness(horizontal, horizontal, horizontal, 0);
        ErrorInfoBar.Margin = new Thickness(horizontal, compact ? 12 : 18, horizontal, 0);
    }

    private async void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        switch (e.Content)
        {
            case WorkbenchPage page:
                workbenchPage = page;
                page.BindRows(TrafficRows);
                UpdateTrafficPresentation();
                page.ConnectRequested -= WorkbenchPage_ConnectRequested;
                page.ConnectRequested += WorkbenchPage_ConnectRequested;
                page.PauseRequested -= WorkbenchPage_PauseRequested;
                page.PauseRequested += WorkbenchPage_PauseRequested;
                page.ClearRequested -= WorkbenchPage_ClearRequested;
                page.ClearRequested += WorkbenchPage_ClearRequested;
                page.CopyHexRequested -= WorkbenchPage_CopyHexRequested;
                page.CopyHexRequested += WorkbenchPage_CopyHexRequested;
                page.SendRequested -= WorkbenchPage_SendRequested;
                page.SendRequested += WorkbenchPage_SendRequested;
                page.RefreshPortsRequested -= WorkbenchPage_RefreshPortsRequested;
                page.RefreshPortsRequested += WorkbenchPage_RefreshPortsRequested;
                page.LoopSendStarted -= WorkbenchPage_LoopSendStarted;
                page.LoopSendStarted += WorkbenchPage_LoopSendStarted;
                page.LoopSendStopped -= WorkbenchPage_LoopSendStopped;
                page.LoopSendStopped += WorkbenchPage_LoopSendStopped;
                page.ProfileNewRequested -= WorkbenchPage_ProfileNewRequested;
                page.ProfileNewRequested += WorkbenchPage_ProfileNewRequested;
                page.ProfileRenameRequested -= WorkbenchPage_ProfileRenameRequested;
                page.ProfileRenameRequested += WorkbenchPage_ProfileRenameRequested;
                page.ProfileDeleteRequested -= WorkbenchPage_ProfileDeleteRequested;
                page.ProfileDeleteRequested += WorkbenchPage_ProfileDeleteRequested;
                page.ProfileApplyRequested -= WorkbenchPage_ProfileApplyRequested;
                page.ProfileApplyRequested += WorkbenchPage_ProfileApplyRequested;
                page.ControlLinesChangedRequested -= WorkbenchPage_ControlLinesChangedRequested;
                page.ControlLinesChangedRequested += WorkbenchPage_ControlLinesChangedRequested;
                page.ClearReceiveRequested -= WorkbenchPage_ClearReceiveRequested;
                page.ClearReceiveRequested += WorkbenchPage_ClearReceiveRequested;
                page.ClearTransmitRequested -= WorkbenchPage_ClearTransmitRequested;
                page.ClearTransmitRequested += WorkbenchPage_ClearTransmitRequested;
                page.BreakRequested -= WorkbenchPage_BreakRequested;
                page.BreakRequested += WorkbenchPage_BreakRequested;
                break;
            case LoopbackPage page:
                loopbackPage = page;
                page.RunRequested -= LoopbackPage_RunRequested;
                page.RunRequested += LoopbackPage_RunRequested;
                page.CancelRequested -= LoopbackPage_CancelRequested;
                page.CancelRequested += LoopbackPage_CancelRequested;
                break;
            case ConnectionsPage page:
                connectionsPage = page;
                page.RefreshRequested -= ConnectionsPage_RefreshRequested;
                page.RefreshRequested += ConnectionsPage_RefreshRequested;
                page.OpenRequested -= ConnectionsPage_OpenRequested;
                page.OpenRequested += ConnectionsPage_OpenRequested;
                page.ConnectionSelected -= ConnectionsPage_ConnectionSelected;
                page.ConnectionSelected += ConnectionsPage_ConnectionSelected;
                page.CloseRequested -= ConnectionsPage_CloseRequested;
                page.CloseRequested += ConnectionsPage_CloseRequested;
                await RefreshConnectionsPageAsync(page);
                break;
            case TerminalPage page:
                terminalPage = page;
                page.BindRows(TrafficRows);
                page.SendRequested -= TerminalPage_SendRequested;
                page.SendRequested += TerminalPage_SendRequested;
                break;
            case AutomationPage page:
                automationPage = page;
                page.RunRequested -= AutomationPage_RunRequested;
                page.RunRequested += AutomationPage_RunRequested;
                page.SaveRequested -= AutomationPage_SaveRequested;
                page.SaveRequested += AutomationPage_SaveRequested;
                page.CancelRequested -= AutomationPage_CancelRequested;
                page.CancelRequested += AutomationPage_CancelRequested;
                break;
            case XmodemPage page:
                xmodemPage = page;
                page.SendRequested -= XmodemPage_SendRequested;
                page.SendRequested += XmodemPage_SendRequested;
                page.ReceiveRequested -= XmodemPage_ReceiveRequested;
                page.ReceiveRequested += XmodemPage_ReceiveRequested;
                page.CancelRequested -= XmodemPage_CancelRequested;
                page.CancelRequested += XmodemPage_CancelRequested;
                break;
            case ModbusPage page:
                modbusPage = page;
                page.SendRequested -= ModbusPage_SendRequested;
                page.SendRequested += ModbusPage_SendRequested;
                page.ScanRequested -= ModbusPage_ScanRequested;
                page.ScanRequested += ModbusPage_ScanRequested;
                page.ScanCancelRequested -= ModbusPage_ScanCancelRequested;
                page.ScanCancelRequested += ModbusPage_ScanCancelRequested;
                page.PollRequested -= ModbusPage_PollRequested;
                page.PollRequested += ModbusPage_PollRequested;
                page.PollCancelRequested -= ModbusPage_PollCancelRequested;
                page.PollCancelRequested += ModbusPage_PollCancelRequested;
                break;
            case SettingsPage page:
                settingsPage = page;
                page.ChooseWorkspaceRequested -= SettingsPage_ChooseWorkspaceRequested;
                page.ChooseWorkspaceRequested += SettingsPage_ChooseWorkspaceRequested;
                page.ClearWorkspaceRequested -= SettingsPage_ClearWorkspaceRequested;
                page.ClearWorkspaceRequested += SettingsPage_ClearWorkspaceRequested;
                page.ThemeChangeRequested -= SettingsPage_ThemeChangeRequested;
                page.ThemeChangeRequested += SettingsPage_ThemeChangeRequested;
                page.SetThemeSelection(currentTheme);
                page.SetWorkspace(workspacePath, workspaceSelected);
                break;
            case SessionsPage page:
                sessionsPage = page;
                page.RefreshRequested -= SessionsPage_RefreshRequested;
                page.RefreshRequested += SessionsPage_RefreshRequested;
                page.SessionSelected -= SessionsPage_SessionSelected;
                page.SessionSelected += SessionsPage_SessionSelected;
                page.EventFilterChanged -= SessionsPage_EventFilterChanged;
                page.EventFilterChanged += SessionsPage_EventFilterChanged;
                page.LoadMoreEventsRequested -= SessionsPage_LoadMoreEventsRequested;
                page.LoadMoreEventsRequested += SessionsPage_LoadMoreEventsRequested;
                page.RevealRequested -= SessionsPage_RevealRequested;
                page.RevealRequested += SessionsPage_RevealRequested;
                page.ExportRequested -= SessionsPage_ExportRequested;
                page.ExportRequested += SessionsPage_ExportRequested;
                page.ReplayRequested -= SessionsPage_ReplayRequested;
                page.ReplayRequested += SessionsPage_ReplayRequested;
                page.ReplayPauseRequested -= SessionsPage_ReplayPauseRequested;
                page.ReplayPauseRequested += SessionsPage_ReplayPauseRequested;
                page.ReplayStopRequested -= SessionsPage_ReplayStopRequested;
                page.ReplayStopRequested += SessionsPage_ReplayStopRequested;
                page.DeleteRequested -= SessionsPage_DeleteRequested;
                page.DeleteRequested += SessionsPage_DeleteRequested;
                page.SetWorkspace(workspacePath);
                await RefreshSessionsAsync(page);
                break;
        }
    }

    private async Task RefreshConnectionsPageAsync(ConnectionsPage page)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            page.SetPorts(await client.ListPortsAsync(CancellationToken.None));
            var status = await client.GetStatusAsync(CancellationToken.None);
            SyncConnectionContexts(status.Connections);
            page.SetConnections(status.Connections, connectionId);
            page.SetHostMetrics(status.LatestEventSequence, status.PendingSessionEvents);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void ConnectionsPage_RefreshRequested(object? sender, EventArgs e)
    {
        if (connectionsPage is not null)
        {
            await RefreshConnectionsPageAsync(connectionsPage);
        }
    }

    private void ConnectionsPage_ConnectionSelected(object? sender, Guid id)
    {
        ActivateConnection(id);
        connectionsPage?.SetConnections(connectionContexts.Values.Select(static item => item.Snapshot).ToArray(), connectionId);
    }

    private async void ConnectionsPage_OpenRequested(object? sender, EventArgs e)
    {
        if (client is null || connectionsPage?.SelectedPort is not { } port)
        {
            return;
        }

        SetConnectionBusy(true);
        try
        {
            var role = connectionsPage.RoleIndex switch
            {
                1 => SerialConnectionRole.Debug,
                2 => SerialConnectionRole.Controller,
                3 => SerialConnectionRole.Loopback,
                _ => SerialConnectionRole.Dut,
            };
            var options = new SerialConnectionOptions(
                port.PortName,
                connectionsPage.BaudRate,
                8,
                SerialParity.None,
                SerialStopBits.One,
                SerialHandshake.None,
                false,
                false,
                workbenchPage?.SelectedEncoding.WebName ?? "utf-8",
                role,
                port.DeviceInstanceId,
                false,
                0,
                0);
            var connection = await client.OpenConnectionAsync(new OpenConnectionRequest(options), CancellationToken.None);
            connectionContexts[connection.Id] = new ConnectionContext(connection);
            ActivateConnection(connection.Id);
            await RefreshStatusAsync();
            await RefreshConnectionsPageAsync(connectionsPage);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetConnectionBusy(false);
        }
    }

    private async void ConnectionsPage_CloseRequested(object? sender, Guid id) => await CloseConnectionFromUiAsync(id);

    private async void WorkbenchPage_RefreshPortsRequested(object? sender, EventArgs e) => await RefreshPortsAsync();

    private async void PortRefreshTimer_Tick(object? sender, object e)
    {
        if (client is null || portRefreshing)
        {
            return;
        }

        await RefreshPortsAsync();
        await RefreshStatusAsync();
    }

    private async Task RefreshPortsAsync()
    {
        if (client is null || workbenchPage is null || portRefreshing)
        {
            return;
        }

        portRefreshing = true;
        try
        {
            var ports = await client.ListPortsAsync(CancellationToken.None);
            var combo = workbenchPage.PortComboBox;
            var currentPort = combo.SelectedItem as SerialPortDescriptor;
            var currentName = currentPort?.PortName;
            if (SamePortSet(combo.ItemsSource as IReadOnlyList<SerialPortDescriptor>, ports))
            {
                await TryReconnectAsync(ports).ConfigureAwait(true);
                return;
            }

            combo.ItemsSource = ports;
            var target = currentPort?.DeviceInstanceId is not null
                ? ports.FirstOrDefault(item => item.DeviceInstanceId?.Equals(currentPort.DeviceInstanceId, StringComparison.OrdinalIgnoreCase) == true)
                    ?? (currentName is not null ? ports.FirstOrDefault(item => item.PortName.Equals(currentName, StringComparison.OrdinalIgnoreCase)) : null)
                : serialPreference.DeviceInstanceId is not null
                    ? ports.FirstOrDefault(item => item.DeviceInstanceId?.Equals(serialPreference.DeviceInstanceId, StringComparison.OrdinalIgnoreCase) == true)
                        ?? (serialPreference.PortName is not null ? ports.FirstOrDefault(item => item.PortName.Equals(serialPreference.PortName, StringComparison.OrdinalIgnoreCase)) : null)
                    : currentName is not null
                        ? ports.FirstOrDefault(item => item.PortName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
                        : serialPreference.PortName is not null
                            ? ports.FirstOrDefault(item => item.PortName.Equals(serialPreference.PortName, StringComparison.OrdinalIgnoreCase))
                            : null;
            combo.SelectedItem = target ?? (ports.Count > 0 ? ports[0] : null);
            await TryReconnectAsync(ports).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            portRefreshing = false;
        }
    }

    private async Task TryReconnectAsync(IReadOnlyList<SerialPortDescriptor> ports)
    {
        if (client is null
            || reconnectOptions is not { DeviceInstanceId: { } deviceInstanceId }
            || reconnectInProgress
            || reconnectAttempts >= MaxReconnectAttempts)
        {
            return;
        }

        var port = ports.FirstOrDefault(item => item.DeviceInstanceId?.Equals(deviceInstanceId, StringComparison.OrdinalIgnoreCase) == true);
        if (port is null)
        {
            return;
        }

        reconnectInProgress = true;
        reconnectAttempts++;
        var activate = connectionId is null;
        try
        {
            if (activate)
            {
                workbenchPage?.SetConnectionStatus($"正在重连 {port.PortName} ({reconnectAttempts}/{MaxReconnectAttempts})");
            }

            var options = reconnectOptions with { PortName = port.PortName };
            var connection = await client.OpenConnectionAsync(new OpenConnectionRequest(options), CancellationToken.None);
            connectionContexts[connection.Id] = new ConnectionContext(connection);
            reconnectOptions = null;
            reconnectAttempts = 0;
            if (activate)
            {
                ActivateConnection(connection.Id);
                workbenchPage?.SetConnectionStatus($"{port.PortName} · {options.BaudRate:N0} baud · 已重连");
            }
            connectionsPage?.SetConnections(connectionContexts.Values.Select(static item => item.Snapshot).ToArray(), connectionId);
        }
        catch (Exception ex)
        {
            if (activate)
            {
                workbenchPage?.SetConnectionStatus(reconnectAttempts >= MaxReconnectAttempts ? "自动重连失败，请手动连接" : "设备已断开，等待重连");
                if (reconnectAttempts >= MaxReconnectAttempts)
                {
                    ShowMessage("自动重连失败", ex.Message, InfoBarSeverity.Error);
                }
            }
        }
        finally
        {
            reconnectInProgress = false;
        }
    }

    private static bool SamePortSet(IReadOnlyList<SerialPortDescriptor>? current, IReadOnlyList<SerialPortDescriptor> updated)
    {
        if (current is null || current.Count != updated.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            if (!current[index].PortName.Equals(updated[index].PortName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current[index].DeviceInstanceId, updated[index].DeviceInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void WorkbenchPage_ConnectRequested(object? sender, EventArgs e) => ConnectButton_Click(this, new RoutedEventArgs());
    private void WorkbenchPage_PauseRequested(object? sender, EventArgs e) => PauseButton_Click(this, new RoutedEventArgs());
    private void WorkbenchPage_ClearRequested(object? sender, EventArgs e) => ClearButton_Click(this, new RoutedEventArgs());
    private void WorkbenchPage_CopyHexRequested(object? sender, EventArgs e) => CopyHexButton_Click(this, new RoutedEventArgs());
    private void WorkbenchPage_SendRequested(object? sender, EventArgs e)
    {
        if (workbenchPage is null)
        {
            return;
        }

        SendButton_Click(this, new RoutedEventArgs());
    }

    private void WorkbenchPage_LoopSendStarted(object? sender, EventArgs e)
    {
        if (workbenchPage is null)
        {
            return;
        }

        if (client is null || connectionId is null)
        {
            workbenchPage.ShowSendResult("请先连接串口。", InfoBarSeverity.Warning);
            workbenchPage.StopLoopSend();
            return;
        }

        workbenchPage.AddSendHistory(workbenchPage.SendText);
        loopSendTimer.Interval = TimeSpan.FromMilliseconds(workbenchPage.LoopIntervalMs);
        loopSendTimer.Start();
    }

    private void WorkbenchPage_LoopSendStopped(object? sender, EventArgs e) => loopSendTimer.Stop();

    private async void WorkbenchPage_ControlLinesChangedRequested(object? sender, EventArgs e)
    {
        if (client is null || connectionId is not { } current || workbenchPage is null)
        {
            return;
        }

        try
        {
            await client.SetControlLinesAsync(current, new SerialControlLines(workbenchPage.DtrEnable, workbenchPage.RtsEnable), CancellationToken.None);
            workbenchPage.ShowSendResult("DTR/RTS 已更新。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            workbenchPage.ShowSendResult(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void WorkbenchPage_ClearReceiveRequested(object? sender, EventArgs e) => await ClearSerialBuffersAsync(receive: true, transmit: false);

    private async void WorkbenchPage_ClearTransmitRequested(object? sender, EventArgs e) => await ClearSerialBuffersAsync(receive: false, transmit: true);

    private async void WorkbenchPage_BreakRequested(object? sender, EventArgs e)
    {
        if (client is null || connectionId is not { } current || workbenchPage is null)
        {
            return;
        }

        try
        {
            await client.SendBreakAsync(current, 100, CancellationToken.None);
            workbenchPage.ShowSendResult("BREAK 已发送 100 ms。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            workbenchPage.ShowSendResult(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task ClearSerialBuffersAsync(bool receive, bool transmit)
    {
        if (client is null || connectionId is not { } current || workbenchPage is null)
        {
            return;
        }

        try
        {
            await client.ClearBuffersAsync(current, receive, transmit, CancellationToken.None);
            workbenchPage.ShowSendResult(receive ? "RX 缓冲区已清空。" : "TX 缓冲区已清空。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            workbenchPage.ShowSendResult(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void WorkbenchPage_ProfileNewRequested(object? sender, EventArgs e)
    {
        if (workbenchPage is not { } page)
        {
            return;
        }

        var name = await PromptProfileNameAsync("新建连接配置", "");
        if (name is null)
        {
            return;
        }

        if (page.Profiles.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError($"连接配置“{name}”已经存在。");
            return;
        }

        var profile = page.ReadSerialProfile(name);
        var profiles = page.Profiles.Append(profile).ToArray();
        SerialProfileStore.Save(profiles);
        page.AddProfile(profile);
    }

    private async void WorkbenchPage_ProfileRenameRequested(object? sender, EventArgs e)
    {
        if (workbenchPage is not { } page || page.SelectedProfile is not { } current)
        {
            return;
        }

        var name = await PromptProfileNameAsync("重命名连接配置", current.Name);
        if (name is null || name.Equals(current.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (page.Profiles.Any(item => item != current && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError($"连接配置“{name}”已经存在。");
            return;
        }

        var renamed = current with { Name = name };
        var profiles = page.Profiles.Select(item => item == current ? renamed : item).ToArray();
        SerialProfileStore.Save(profiles);
        page.ReplaceProfile(renamed);
    }

    private async void WorkbenchPage_ProfileDeleteRequested(object? sender, EventArgs e)
    {
        if (workbenchPage is not { } page || page.SelectedProfile is not { } profile)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = page.XamlRoot,
            Title = "删除连接配置",
            Content = $"将删除连接配置“{profile.Name}”。当前串口连接不会断开。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var profiles = page.Profiles.Where(item => item != profile).ToArray();
        SerialProfileStore.Save(profiles);
        page.RemoveSelectedProfile();
    }

    private void WorkbenchPage_ProfileApplyRequested(object? sender, EventArgs e)
    {
        if (workbenchPage?.SelectedProfile is { } profile)
        {
            workbenchPage.ApplySerialProfile(profile);
        }
    }

    private async void TerminalPage_SendRequested(object? sender, EventArgs e)
    {
        if (terminalPage is null)
        {
            return;
        }

        if (client is null || connectionId is not { } current)
        {
            terminalPage.ShowSendResult("请先连接串口。", InfoBarSeverity.Warning);
            return;
        }

        var input = terminalPage.InputText;
        terminalPage.SetSending(true);
        try
        {
            var data = terminalPage.InputFormatIndex == 1
                ? Protocols.HexCodec.Parse(input)
                : (workbenchPage?.SelectedEncoding ?? Encoding.UTF8).GetBytes(input + GetLineEnding(terminalPage.LineEndingIndex));
            await client.SendAsync(new SendRequest(current, data, "terminal.send"), CancellationToken.None);
            terminalPage.AddHistory(input);
            terminalPage.ShowSendResult($"已发送 {data.Length:N0} 字节。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            terminalPage.ShowSendResult(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            terminalPage.SetSending(false);
        }
    }

    private async void AutomationPage_RunRequested(object? sender, EventArgs e)
    {
        if (automationPage is null || client is null || connectionId is not { } current)
        {
            automationPage?.ShowResult("请先连接串口。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var sequence = automationPage.Parse();
            using var cancellation = new CancellationTokenSource();
            sequenceCancel = cancellation.Cancel;
            automationPage.SetRunning(true);
            var result = await client.RunSequenceAsync(current, sequence, cancellation.Token);
            automationPage.ShowResult(result.Completed ? "序列已完成。" : result.Error ?? "序列未完成。", result.Completed ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            automationPage.ShowResult("序列已取消。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            automationPage.ShowResult(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            sequenceCancel = null;
            automationPage.SetRunning(false);
        }
    }

    private void AutomationPage_CancelRequested(object? sender, EventArgs e) => sequenceCancel?.Invoke();

    private void AutomationPage_SaveRequested(object? sender, EventArgs e)
    {
        if (automationPage is null || string.IsNullOrWhiteSpace(workspacePath) || workspacePath.StartsWith("全局数据：", StringComparison.Ordinal))
        {
            automationPage?.ShowResult("请先选择工作区。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var sequence = automationPage.Parse();
            var root = workspacePath["工作区：".Length..];
            var directory = Path.Combine(root, "sequences");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{sequence.Name}.json"), automationPage.DefinitionText);
            automationPage.ShowResult("序列已保存到工作区。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            automationPage.ShowResult(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void XmodemPage_SendRequested(object? sender, EventArgs e)
    {
        if (xmodemPage is null || client is null || connectionId is not { } current)
        {
            xmodemPage?.ShowResult("请先连接串口。", InfoBarSeverity.Warning);
            return;
        }

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        xmodemPage.SetPath(file.Path);
        xmodemPage.SetRunning(true);
        try
        {
            var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
            using var cancellation = new CancellationTokenSource();
            xmodemCancel = cancellation.Cancel;
            var result = await client.SendXmodemAsync(current, buffer.ToArray(), cancellation.Token);
            xmodemPage.SetProgress($"{result.Blocks:N0} blocks · {result.Retries:N0} retries · {result.BytesTransferred:N0} bytes");
            xmodemPage.ShowResult(result.Success ? "文件发送完成。" : result.Error ?? "文件发送失败。", result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (OperationCanceledException) { xmodemPage.ShowResult("传输已停止。", InfoBarSeverity.Warning); }
        catch (Exception ex) { xmodemPage.ShowResult(ex.Message, InfoBarSeverity.Error); }
        finally { xmodemCancel = null; xmodemPage.SetRunning(false); }
    }

    private async void XmodemPage_ReceiveRequested(object? sender, EventArgs e)
    {
        if (xmodemPage is null || client is null || connectionId is not { } current)
        {
            xmodemPage?.ShowResult("请先连接串口。", InfoBarSeverity.Warning);
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "received.bin" };
        picker.FileTypeChoices.Add("二进制文件", [".bin"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        xmodemPage.SetPath(file.Path);
        xmodemPage.SetRunning(true);
        try
        {
            using var cancellation = new CancellationTokenSource();
            xmodemCancel = cancellation.Cancel;
            var result = await client.ReceiveXmodemAsync(current, cancellation.Token);
            await Windows.Storage.FileIO.WriteBytesAsync(file, result.Data);
            xmodemPage.SetProgress($"{result.Result.Blocks:N0} blocks · {result.Result.Retries:N0} retries · {result.Result.BytesTransferred:N0} bytes");
            xmodemPage.ShowResult(result.Result.Success ? "文件接收完成。" : result.Result.Error ?? "文件接收失败。", result.Result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (OperationCanceledException) { xmodemPage.ShowResult("传输已停止。", InfoBarSeverity.Warning); }
        catch (Exception ex) { xmodemPage.ShowResult(ex.Message, InfoBarSeverity.Error); }
        finally { xmodemCancel = null; xmodemPage.SetRunning(false); }
    }

    private void XmodemPage_CancelRequested(object? sender, EventArgs e) => xmodemCancel?.Invoke();

    private async void LoopSendTimer_Tick(object? sender, object e)
    {
        if (loopSending)
        {
            return;
        }

        if (client is null || connectionId is null)
        {
            workbenchPage?.StopLoopSend();
            return;
        }

        loopSending = true;
        try
        {
            await SendCurrentAsync();
        }
        finally
        {
            loopSending = false;
        }
    }
    private async void SettingsPage_ChooseWorkspaceRequested(object? sender, EventArgs e) => await ChooseWorkspaceAsync();
    private async void SettingsPage_ClearWorkspaceRequested(object? sender, EventArgs e) => await ChangeWorkspaceAsync(null);

    private async void SessionsPage_RefreshRequested(object? sender, EventArgs e) => await RefreshSessionsAsync();

    private async void SessionsPage_SessionSelected(object? sender, Guid sessionId) => await ReadSessionEventsAsync(sessionId);

    private async void SessionsPage_EventFilterChanged(object? sender, SessionEventFilter filter)
    {
        if (sessionsPage?.SelectedSession is { } session)
        {
            await ReadSessionEventsAsync(session.Id, sessionsPage, filter);
        }
    }

    private async void SessionsPage_LoadMoreEventsRequested(object? sender, EventArgs e)
    {
        if (sessionsPage?.SelectedSession is { } session)
        {
            await ReadSessionEventsAsync(session.Id, sessionsPage, sessionsPage.EventFilter, true);
        }
    }

    private async void SessionsPage_ReplayRequested(object? sender, Guid sessionId) => await ReplaySessionAsync(sessionId);

    private void SessionsPage_ReplayPauseRequested(object? sender, Guid sessionId)
    {
        lock (replayGate)
        {
            if (replaySessionId != sessionId || replayCancellation is null)
            {
                return;
            }

            replayPaused = !replayPaused;
            SignalReplayStateChanged();
            sessionsPage?.SetReplayState(sessionId, true, replayPaused, replayCurrent, replaySequence, replayTotal);
        }
    }

    private void SessionsPage_ReplayStopRequested(object? sender, Guid sessionId)
    {
        lock (replayGate)
        {
            if (replaySessionId != sessionId || replayCancellation is null)
            {
                return;
            }

            replayCancellation.Cancel();
            SignalReplayStateChanged();
        }
    }

    private async Task ReplaySessionAsync(Guid sessionId)
    {
        if (client is null || sessionsPage?.SelectedSession is not { } session || session.Id != sessionId)
        {
            return;
        }

        CancellationTokenSource cancellation;
        lock (replayGate)
        {
            if (replayCancellation is not null)
            {
                return;
            }

            cancellation = new CancellationTokenSource();
            replayCancellation = cancellation;
            replaySessionId = sessionId;
            replayPaused = false;
            replayCurrent = 0;
            replayTotal = 0;
            replaySequence = 0;
            replayStateChanged = CreateReplaySignal();
        }

        var events = Array.Empty<SerialTrafficEvent>();
        var current = 0;
        long sequence = 0;
        try
        {
            events = [.. await client.ReadAllSessionEventsAsync(sessionId, cancellation.Token)];
            replayTotal = events.Length;
            sessionsPage.SetReplayState(sessionId, true, false, 0, 0, events.Length);
            for (var index = 0; index < events.Length; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var item = events[index];
                if (index > 0)
                {
                    await WaitReplayDelayAsync(item.Utc - events[index - 1].Utc, cancellation.Token);
                }

                await WaitReplayIfPausedAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var row = TrafficRow.From(item, workbenchPage?.MonitorFormatIndex == 1, workbenchPage?.SelectedEncoding ?? Encoding.UTF8, workbenchPage?.ShowTimestamp ?? true);
                TrafficRows.Add(row);
                terminalPage?.AppendRow(row);
                while (TrafficRows.Count > 20_000)
                {
                    TrafficRows.RemoveAt(0);
                }

                current = index + 1;
                sequence = item.Sequence;
                replayCurrent = current;
                replaySequence = sequence;
                UpdateTrafficPresentation();
                workbenchPage?.TrafficListView.ScrollIntoView(TrafficRows[^1]);
                sessionsPage.SetReplayState(sessionId, true, IsReplayPaused(), current, sequence, events.Length);
            }

            sessionsPage.SetReplayState(sessionId, false, false, current, sequence, events.Length);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            sessionsPage.SetReplayState(sessionId, false, false, current, sequence, events.Length);
        }
        catch (Exception ex)
        {
            sessionsPage.SetReplayState(sessionId, false, false, current, sequence, events.Length);
            ShowError(ex.Message);
        }
        finally
        {
            lock (replayGate)
            {
                if (ReferenceEquals(replayCancellation, cancellation))
                {
                    replayCancellation = null;
                    replaySessionId = null;
                    replayPaused = false;
                    SignalReplayStateChanged();
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task WaitReplayDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var remaining = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        while (remaining > TimeSpan.Zero)
        {
            await WaitReplayIfPausedAsync(cancellationToken);
            var started = Stopwatch.GetTimestamp();
            var delayTask = Task.Delay(remaining, cancellationToken);
            var stateTask = GetReplayStateChangedTask();
            if (await Task.WhenAny(delayTask, stateTask).ConfigureAwait(true) == delayTask)
            {
                await delayTask.ConfigureAwait(true);
                return;
            }

            remaining -= Stopwatch.GetElapsedTime(started);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task WaitReplayIfPausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task stateTask;
            lock (replayGate)
            {
                if (!replayPaused)
                {
                    return;
                }

                stateTask = replayStateChanged.Task;
            }

            await stateTask.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private Task<bool> GetReplayStateChangedTask()
    {
        lock (replayGate)
        {
            return replayStateChanged.Task;
        }
    }

    private bool IsReplayPaused()
    {
        lock (replayGate)
        {
            return replayPaused;
        }
    }

    private void SignalReplayStateChanged()
    {
        var signal = replayStateChanged;
        replayStateChanged = CreateReplaySignal();
        signal.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateReplaySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SessionsPage_RevealRequested(object? sender, string path)
    {
        try
        {
            var argument = $"/select,\"{path}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void SessionsPage_ExportRequested(object? sender, Guid sessionId)
    {
        if (client is null || sessionsPage?.SelectedSession is not { } session || session.Id != sessionId)
        {
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(session.Path)}.csv",
            };
            picker.FileTypeChoices.Add("CSV 文件", [".csv"]);
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            if (string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(session.Path), StringComparison.OrdinalIgnoreCase))
            {
                ShowError("导出文件不能覆盖原会话。");
                return;
            }

            var csv = await client.ExportSessionCsvAsync(new SessionEventQuery(sessionId), CancellationToken.None);
            await Windows.Storage.FileIO.WriteTextAsync(file, csv, Windows.Storage.Streams.UnicodeEncoding.Utf8);
            ShowMessage("导出完成", $"已导出 {Path.GetFileName(file.Path)}。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void SessionsPage_DeleteRequested(object? sender, Guid sessionId)
    {
        if (client is null || sessionsPage is null)
        {
            return;
        }

        try
        {
            await client.DeleteSessionAsync(sessionId, CancellationToken.None);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task RefreshSessionsAsync(SessionsPage? page = null)
    {
        page ??= sessionsPage;
        if (client is null || page is null)
        {
            return;
        }

        page.SetBusy(true);
        try
        {
            var sessions = await client.ListSessionsAsync(CancellationToken.None);
            var selectedId = page.SetSessions(sessions, activeSessionId);
            if (selectedId is { } id)
            {
                await ReadSessionEventsAsync(id, page);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            page.SetBusy(false);
        }
    }

    private async Task ReadSessionEventsAsync(Guid sessionId, SessionsPage? page = null, SessionEventFilter? filter = null, bool append = false)
    {
        page ??= sessionsPage;
        if (client is null || page is null)
        {
            return;
        }

        var requestVersion = ++sessionEventRequestVersion;
        var selectedFilter = filter ?? page.EventFilter;
        if (!append || sessionEventSessionId != sessionId || sessionEventFilter != selectedFilter)
        {
            sessionEventSessionId = sessionId;
            sessionEventFilter = selectedFilter;
            sessionEventAfterSequence = 0;
            append = false;
        }

        page.SetEventsLoading(sessionId, append);
        try
        {
            var events = await client.ReadSessionEventsAsync(new SessionEventQuery(
                sessionId,
                SessionEventPageSize,
                sessionEventAfterSequence,
                Direction: selectedFilter.Direction,
                SourceContains: selectedFilter.SourceContains,
                DataContainsHex: selectedFilter.DataContainsHex), CancellationToken.None);
            if (requestVersion != sessionEventRequestVersion || page.SelectedSession?.Id != sessionId)
            {
                return;
            }

            if (events.Count > 0)
            {
                sessionEventAfterSequence = events[^1].Sequence;
            }

            if (append)
            {
                page.AppendEvents(sessionId, events, events.Count == SessionEventPageSize);
            }
            else
            {
                page.SetEvents(sessionId, events, events.Count == SessionEventPageSize);
                await ReadLoopbackResultsAsync(sessionId, page);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task ReadLoopbackResultsAsync(Guid sessionId, SessionsPage page)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            var results = await client.ReadLoopbackResultsAsync(sessionId, CancellationToken.None);
            page.SetLoopbackResults(sessionId, results);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SettingsPage_ThemeChangeRequested(object? sender, ElementTheme theme)
    {
        currentTheme = theme;
        RootGrid.RequestedTheme = theme;
        ThemePreference.Save(theme);
    }
    private void LoopbackPage_RunRequested(object? sender, EventArgs e)
    {
        if (loopbackPage is null)
        {
            return;
        }

        LoopbackButton_Click(this, new RoutedEventArgs());
    }

    private void LoopbackPage_CancelRequested(object? sender, EventArgs e) => loopbackCancel?.Invoke();

    private void ModbusPage_ScanCancelRequested(object? sender, EventArgs e) => modbusScanCancellation?.Cancel();

    private void ModbusPage_PollCancelRequested(object? sender, EventArgs e) => modbusPollCancellation?.Cancel();

    private async void ModbusPage_PollRequested(object? sender, EventArgs e)
    {
        if (modbusPage is null)
        {
            return;
        }

        if (client is null || connectionId is not { } current)
        {
            modbusPage.ShowResult("请先连接串口。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var slave = modbusPage.PollSlaveValue;
            var function = modbusPage.PollFunctionValue;
            var address = modbusPage.PollAddressValue;
            var quantity = modbusPage.PollQuantityValue;
            var maximumQuantity = function is 1 or 2 ? 2000 : 125;
            if (quantity < 1 || quantity > maximumQuantity)
            {
                throw new ArgumentException($"功能码 {function:D2} 的数量必须在 1 到 {maximumQuantity} 之间。");
            }

            var count = modbusPage.PollCountValue;
            var interval = modbusPage.PollIntervalMilliseconds;
            var timeout = modbusPage.PollTimeoutMilliseconds;
            using var cancellation = new CancellationTokenSource();
            modbusPollCancellation = cancellation;
            modbusPage.ClearPollResults();
            modbusPage.SetPolling(true);
            var failed = 0;
            var completed = 0;
            var totalDurationMilliseconds = 0d;
            try
            {
                for (var sample = 1; sample <= count; sample++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var frame = SerialWorkbench.Modbus.ModbusRtuCodec.BuildReadRequest(slave, function, address, quantity);
                    var result = await client.RunModbusAsync(new ModbusTransactionRequest(current, frame, slave, function, timeout), cancellation.Token);
                    modbusPage.AddPollResult(sample, result);
                    completed++;
                    totalDurationMilliseconds += result.Duration.TotalMilliseconds;
                    if (!result.Success)
                    {
                        failed++;
                    }

                    modbusPage.SetPollStatus($"已完成 {sample} / {count} · 失败 {failed}");
                    if (interval > 0 && sample < count)
                    {
                        await Task.Delay(interval, cancellation.Token);
                    }
                }

                var success = count - failed;
                var average = completed == 0 ? 0 : totalDurationMilliseconds / completed;
                modbusPage.ShowPollResult($"轮询完成 · 成功 {success} · 失败 {failed} · 成功率 {(double)success / count:P0} · 平均 {average:N0} ms", failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                var average = completed == 0 ? 0 : totalDurationMilliseconds / completed;
                modbusPage.ShowPollResult($"轮询已停止 · 已完成 {completed} 次 · 失败 {failed} · 平均 {average:N0} ms", InfoBarSeverity.Warning);
            }
            finally
            {
                modbusPage.SetPolling(false);
                modbusPollCancellation = null;
            }
        }
        catch (Exception ex)
        {
            modbusPage.ShowPollResult(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ModbusPage_ScanRequested(object? sender, EventArgs e)
    {
        if (modbusPage is null)
        {
            return;
        }

        if (client is null || connectionId is not { } current)
        {
            modbusPage.ShowResult("请先连接串口。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var first = modbusPage.ScanFromValue;
            var last = modbusPage.ScanToValue;
            if (first > last)
            {
                throw new ArgumentException("起始从站不能大于结束从站。");
            }

            var address = modbusPage.ScanAddressValue;
            var timeout = modbusPage.ScanTimeoutMilliseconds;
            var interval = modbusPage.ScanIntervalMilliseconds;
            using var cancellation = new CancellationTokenSource();
            modbusScanCancellation = cancellation;
            modbusPage.ClearScanResults();
            modbusPage.SetScanning(true);
            var responses = 0;
            try
            {
                for (var slave = first; slave <= last; slave++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var frame = SerialWorkbench.Modbus.ModbusRtuCodec.BuildReadRequest(slave, 3, address, 1);
                    var result = await client.RunModbusAsync(new ModbusTransactionRequest(current, frame, slave, 3, timeout), cancellation.Token);
                    if (result.Success || result.ExceptionCode is not null)
                    {
                        modbusPage.AddScanResult(slave, result);
                        responses++;
                    }

                    modbusPage.SetScanStatus($"已扫描 {slave} / {last} · 响应 {responses}");
                    if (interval > 0 && slave < last)
                    {
                        await Task.Delay(interval, cancellation.Token);
                    }
                }

                modbusPage.ShowScanResult($"扫描完成 · 响应 {responses} / {last - first + 1}", InfoBarSeverity.Success);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                modbusPage.ShowScanResult($"扫描已停止 · 已收到 {responses} 个响应。", InfoBarSeverity.Warning);
            }
            finally
            {
                modbusPage.SetScanning(false);
                modbusScanCancellation = null;
            }
        }
        catch (Exception ex)
        {
            modbusPage.ShowScanResult(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ModbusPage_SendRequested(object? sender, EventArgs e)
    {
        if (modbusPage?.RequestFrame is not { } frame)
        {
            return;
        }

        if (client is null || connectionId is not { } current)
        {
            modbusPage.ShowResult("请先连接串口。", InfoBarSeverity.Warning);
            return;
        }

        modbusPage.SetSending(true);
        try
        {
            var request = new ModbusTransactionRequest(current, frame, modbusPage.SlaveAddressValue, modbusPage.FunctionCodeValue);
            var response = await client.RunModbusAsync(request, CancellationToken.None);
            modbusPage.ShowResponse(response);
        }
        catch (Exception ex)
        {
            modbusPage.ShowResult(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            modbusPage.SetSending(false);
        }
    }


    private void ContentFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        ShowError(e.Exception.Message);
        e.Handled = true;
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    }

    private async Task ChooseWorkspaceAsync()
    {
        if (client is null)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        await ChangeWorkspaceAsync(folder.Path);
    }

    private async Task<string?> PromptProfileNameAsync(string title, string initialName)
    {
        if (workbenchPage is null)
        {
            return null;
        }

        var editor = new TextBox
        {
            Text = initialName,
            PlaceholderText = "配置名称",
            MinWidth = 320,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = workbenchPage.XamlRoot,
            Title = title,
            Content = editor,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var name = editor.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("配置名称不能为空。");
            return null;
        }

        return name;
    }

    private async Task ChangeWorkspaceAsync(string? path)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            var status = await client.SetWorkspaceAsync(new SetWorkspaceRequest(path), CancellationToken.None);
            WorkspacePreferenceStore.Save(path);
            workspaceSelected = status.WorkspaceRoot is not null;
            workspacePath = status.WorkspaceRoot is null ? $"全局数据：{status.DataRoot}" : $"工作区：{status.WorkspaceRoot}";
            settingsPage?.SetWorkspace(workspacePath, workspaceSelected);
            activeSessionId = status.ActiveSession?.Id;
            sessionsPage?.SetWorkspace(workspacePath);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        eventTimer.Stop();
        portRefreshTimer.Stop();
        loopSendTimer.Stop();
        loopbackCancel?.Invoke();
        xmodemCancel?.Invoke();
        modbusScanCancellation?.Cancel();
        modbusPollCancellation?.Cancel();
        if (workbenchPage is not null)
        {
            SerialPreferenceStore.Save(workbenchPage.ReadSerialPreference(workbenchPage.SelectedPort?.PortName));
        }

        if (client is null)
        {
            return;
        }

        foreach (var id in connectionContexts.Keys.ToArray())
        {
            try
            {
                await client.CloseConnectionAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        await client.DisposeAsync();
        connectionContexts.Clear();
        connectionId = null;
        loopbackCancel = null;
        textDecoder = null;
        replayCancellation?.Dispose();
        modbusScanCancellation?.Dispose();
        modbusPollCancellation?.Dispose();
        sequenceCancel = null;
    }

    private void SetConnectionBusy(bool busy)
    {
        workbenchPage?.SetConnectionBusy(busy);
    }

    private void SetSerialConfigurationEnabled(bool enabled)
    {
        if (workbenchPage is not null)
        {
            workbenchPage.PortComboBox.IsEnabled = enabled;
            workbenchPage.RefreshPortsButton.IsEnabled = enabled;
            workbenchPage.BaudRateComboBox.IsEnabled = enabled;
            workbenchPage.MonitorFormat.IsEnabled = enabled;
            workbenchPage.RoleComboBox.IsEnabled = enabled;
            workbenchPage.AdvancedExpander.IsEnabled = enabled;
            workbenchPage.Rs485Expander.IsEnabled = enabled;
            workbenchPage.SetControlActionsEnabled(!enabled);
        }

        if (client is null)
        {
            return;
        }

        portRefreshTimer.Start();
    }

    private void UpdateTrafficPresentation()
    {
        workbenchPage?.RefreshTrafficFilter();
    }

    private static string GetLineEnding(int index) => index switch
    {
        1 => "\r",
        2 => "\n",
        3 => "\r\n",
        _ => "",
    };

    private static byte[] AppendChecksum(byte[] data, int index)
    {
        switch (index)
        {
            case 1:
                return [.. data, Protocols.Checksums.Xor(data)];
            case 2:
                return [.. data, Protocols.Checksums.Sum8(data)];
            case 3:
                var modbus = Protocols.Checksums.Crc16Modbus(data);
                return [.. data, (byte)modbus, (byte)(modbus >> 8)];
            case 4:
                var xmodem = Protocols.Checksums.Crc16XModem(data);
                return [.. data, (byte)(xmodem >> 8), (byte)xmodem];
            case 5:
                var crc32 = Protocols.Checksums.Crc32(data);
                return [.. data, (byte)crc32, (byte)(crc32 >> 8), (byte)(crc32 >> 16), (byte)(crc32 >> 24)];
            default:
                return data;
        }
    }

    private void ShowError(string message)
    {
        ShowMessage("操作失败", message, InfoBarSeverity.Error);
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        ErrorInfoBar.Title = title;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.Severity = severity;
        ErrorInfoBar.IsOpen = true;
    }

}

internal sealed class ConnectionContext(ConnectionSnapshot snapshot)
{
    public ConnectionSnapshot Snapshot { get; set; } = snapshot;

    public ObservableCollection<TrafficRow> TrafficRows { get; } = [];

    public long LastSequence { get; set; }

    public Decoder? TextDecoder { get; } = Encoding.GetEncoding(snapshot.Options.EncodingName).GetDecoder();

    public bool Paused { get; set; }

    public long PauseBaselineBytes { get; set; }

    public long CurrentTrafficBytes { get; set; }
}

public sealed class TrafficRow(
    string time,
    string display,
    string hex,
    string source,
    bool isReceive,
    Visibility receiveVisibility,
    Visibility transmitVisibility,
    Visibility timeVisibility)
{
    public string Time { get; } = time;

    public string Display { get; } = display;

    public string Hex { get; } = hex;

    public string Source { get; } = source;

    public bool IsReceive { get; } = isReceive;

    public Visibility ReceiveVisibility { get; } = receiveVisibility;

    public Visibility TransmitVisibility { get; } = transmitVisibility;

    public Visibility TimeVisibility { get; } = timeVisibility;

    public static TrafficRow From(SerialTrafficEvent item, bool text, Encoding encoding, bool showTime, Decoder? decoder = null)
    {
        var hex = Convert.ToHexString(item.Data);
        var display = text
            ? DecodeText(item.Data, encoding, item.Direction == SerialDirection.Receive ? decoder : null)
            : Protocols.HexCodec.Format(item.Data);
        var receive = item.Direction == SerialDirection.Receive;
        return new TrafficRow(
            item.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            display,
            hex,
            item.Source,
            receive,
            receive ? Visibility.Visible : Visibility.Collapsed,
            receive ? Visibility.Collapsed : Visibility.Visible,
            showTime ? Visibility.Visible : Visibility.Collapsed);
    }

    private static string DecodeText(byte[] data, Encoding encoding, Decoder? decoder)
    {
        if (decoder is null)
        {
            return encoding.GetString(data);
        }

        var charCount = decoder.GetCharCount(data, 0, data.Length, false);
        var chars = new char[charCount];
        decoder.GetChars(data, 0, data.Length, chars, 0, false);
        return new string(chars);
    }
}
