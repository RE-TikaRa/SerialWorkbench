using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
    private readonly DispatcherTimer eventTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer portRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer loopSendTimer = new();
    private bool loopSending;
    private HostRpcClient? client;
    private Guid? connectionId;
    private long lastSequence;
    private bool paused;
    private bool polling;
    private bool portRefreshing;
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
    private WorkbenchPage? workbenchPage;
    private LoopbackPage? loopbackPage;
    private SettingsPage? settingsPage;
    private SessionsPage? sessionsPage;
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

    public ObservableCollection<TrafficRow> TrafficRows { get; }

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
                await client.CloseConnectionAsync(current, CancellationToken.None);
                connectionId = null;
                workbenchPage?.StopLoopSend();
                if (workbenchPage is not null)
                {
                    workbenchPage.ConnectButton.Content = "连接";
                }
                workbenchPage?.SetConnectionStatus("未连接串口");
                workbenchPage?.SetTrafficCounts(0, 0);
                SetSerialConfigurationEnabled(true);
                return;
            }

            var selectedPort = workbenchPage?.SelectedPort;
            if (selectedPort is not SerialPortDescriptor port)
            {
                throw new InvalidOperationException("请选择串口。");
            }

            var options = new SerialConnectionOptions(
                port.PortName,
                checked((int)(workbenchPage?.BaudRate ?? 115200)),
                workbenchPage?.DataBits ?? 8,
                workbenchPage?.Parity ?? SerialParity.None,
                workbenchPage?.StopBits ?? SerialStopBits.One,
                workbenchPage?.Handshake ?? SerialHandshake.None,
                workbenchPage?.DtrEnable ?? false,
                workbenchPage?.RtsEnable ?? false);
            SerialPreferenceStore.Save(workbenchPage!.ReadSerialPreference(port.PortName));
            var connection = await client.OpenConnectionAsync(new OpenConnectionRequest(options), CancellationToken.None);
            connectionId = connection.Id;
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
            var result = await client.RunLoopbackAsync(request, CancellationToken.None);
            var message = result.Passed
                ? $"通过 · {result.ReceivedBytes:N0} 字节 · {result.BytesPerSecond / 1024:N1} KiB/s · {result.Duration.TotalMilliseconds:N0} ms"
                : $"失败 · {result.Error} · 首个差异 {result.FirstDifferenceIndex}";
            var severity = result.Passed ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            loopbackPage?.ShowResult(message, severity);
        }
        catch (Exception ex)
        {
            loopbackPage?.ShowResult(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            loopbackPage?.SetRunning(false);
        }
    }

    private async void EventTimer_Tick(object? sender, object e)
    {
        if (polling || paused || replayCancellation is not null || client is null)
        {
            return;
        }

        polling = true;
        try
        {
            var events = await client.ReadEventsAsync(new EventQuery(lastSequence, 1000, connectionId), CancellationToken.None);
            foreach (var item in events)
            {
                lastSequence = Math.Max(lastSequence, item.Sequence);
                TrafficRows.Add(TrafficRow.From(item, workbenchPage?.MonitorFormatIndex == 1, workbenchPage?.SelectedEncoding ?? Encoding.UTF8, workbenchPage?.ShowTimestamp ?? true));
                if (item.Direction == SerialDirection.Receive)
                {
                    workbenchPage?.AppendWaveform(item.Data);
                }
            }

            while (TrafficRows.Count > 20_000)
            {
                TrafficRows.RemoveAt(0);
            }

            if (events.Count > 0)
            {
                UpdateTrafficPresentation();
                workbenchPage?.TrafficListView.ScrollIntoView(TrafficRows[^1]);
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
            return;
        }

        var status = await client.GetStatusAsync(CancellationToken.None);
        var connection = status.Connections.FirstOrDefault(item => item.Id == connectionId);
        workbenchPage?.SetTrafficCounts(connection?.ReceivedBytes ?? 0, connection?.TransmittedBytes ?? 0);
        activeSessionId = status.ActiveSession?.Id;
        workspaceSelected = status.WorkspaceRoot is not null;
        workspacePath = status.WorkspaceRoot is null ? $"全局数据：{status.DataRoot}" : $"工作区：{status.WorkspaceRoot}";
        sessionsPage?.SetWorkspace(workspacePath);
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        paused = !paused;
        if (workbenchPage is not null)
        {
            workbenchPage.PauseButton.Content = paused ? "继续" : "暂停";
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        TrafficRows.Clear();
        UpdateTrafficPresentation();
    }

    private void CopyHexButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(string.Join(Environment.NewLine, TrafficRows.Select(static item => item.Hex)));
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
            "Loopback" => (PageType: typeof(LoopbackPage), Title: "回环检测", Description: "验证串口发送与接收链路是否正常。"),
            "Modbus" => (PageType: typeof(ModbusPage), Title: "Modbus RTU", Description: "构造读写请求帧发送到当前串口，并解析寄存器响应。"),
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
                break;
            case LoopbackPage page:
                loopbackPage = page;
                page.RunRequested -= LoopbackPage_RunRequested;
                page.RunRequested += LoopbackPage_RunRequested;
                break;
            case ModbusPage page:
                modbusPage = page;
                page.SendRequested -= ModbusPage_SendRequested;
                page.SendRequested += ModbusPage_SendRequested;
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

    private async void WorkbenchPage_RefreshPortsRequested(object? sender, EventArgs e) => await RefreshPortsAsync();

    private async void PortRefreshTimer_Tick(object? sender, object e)
    {
        if (client is null || portRefreshing)
        {
            return;
        }

        await RefreshPortsAsync();
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
            var currentName = (combo.SelectedItem as SerialPortDescriptor)?.PortName;
            if (SamePortSet(combo.ItemsSource as IReadOnlyList<SerialPortDescriptor>, ports))
            {
                return;
            }

            combo.ItemsSource = ports;
            var target = currentName is not null
                ? ports.FirstOrDefault(item => item.PortName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
                : serialPreference.PortName is not null
                    ? ports.FirstOrDefault(item => item.PortName.Equals(serialPreference.PortName, StringComparison.OrdinalIgnoreCase))
                    : null;
            combo.SelectedItem = target ?? (ports.Count > 0 ? ports[0] : null);
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

    private static bool SamePortSet(IReadOnlyList<SerialPortDescriptor>? current, IReadOnlyList<SerialPortDescriptor> updated)
    {
        if (current is null || current.Count != updated.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            if (!current[index].PortName.Equals(updated[index].PortName, StringComparison.OrdinalIgnoreCase))
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
                TrafficRows.Add(TrafficRow.From(item, workbenchPage?.MonitorFormatIndex == 1, workbenchPage?.SelectedEncoding ?? Encoding.UTF8, workbenchPage?.ShowTimestamp ?? true));
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

            var csv = await client.ExportSessionCsvAsync(sessionId, CancellationToken.None);
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

    private async Task ReadSessionEventsAsync(Guid sessionId, SessionsPage? page = null)
    {
        page ??= sessionsPage;
        if (client is null || page is null)
        {
            return;
        }

        page.SetEventsLoading(sessionId);
        try
        {
            var events = await client.ReadSessionEventsAsync(new SessionEventQuery(sessionId), CancellationToken.None);
            page.SetEvents(sessionId, events);
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
        if (workbenchPage is not null)
        {
            SerialPreferenceStore.Save(workbenchPage.ReadSerialPreference(workbenchPage.SelectedPort?.PortName));
        }

        if (client is null)
        {
            return;
        }

        if (connectionId is { } current)
        {
            try
            {
                await client.CloseConnectionAsync(current, CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        await client.DisposeAsync();
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
            workbenchPage.AdvancedExpander.IsEnabled = enabled;
        }

        if (client is null)
        {
            return;
        }

        if (enabled)
        {
            portRefreshTimer.Start();
        }
        else
        {
            portRefreshTimer.Stop();
        }
    }

    private void UpdateTrafficPresentation()
    {
        var empty = TrafficRows.Count == 0;
        if (workbenchPage is not null)
        {
            workbenchPage.MonitorEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            workbenchPage.CopyHexButton.IsEnabled = !empty;
        }
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

public sealed class TrafficRow(
    string time,
    string display,
    string hex,
    Visibility receiveVisibility,
    Visibility transmitVisibility,
    Visibility timeVisibility)
{
    public string Time { get; } = time;

    public string Display { get; } = display;

    public string Hex { get; } = hex;

    public Visibility ReceiveVisibility { get; } = receiveVisibility;

    public Visibility TransmitVisibility { get; } = transmitVisibility;

    public Visibility TimeVisibility { get; } = timeVisibility;

    public static TrafficRow From(SerialTrafficEvent item, bool text, Encoding encoding, bool showTime)
    {
        var hex = Convert.ToHexString(item.Data);
        var display = text ? encoding.GetString(item.Data) : Protocols.HexCodec.Format(item.Data);
        var receive = item.Direction == SerialDirection.Receive;
        return new TrafficRow(
            item.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            display,
            hex,
            receive ? Visibility.Visible : Visibility.Collapsed,
            receive ? Visibility.Collapsed : Visibility.Visible,
            showTime ? Visibility.Visible : Visibility.Collapsed);
    }
}
