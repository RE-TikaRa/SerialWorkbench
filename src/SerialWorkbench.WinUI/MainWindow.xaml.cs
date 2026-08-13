using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.WinUI.Pages;

namespace SerialWorkbench.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer eventTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private HostRpcClient? client;
    private Guid? connectionId;
    private long lastSequence;
    private bool paused;
    private bool polling;
    private ElementTheme currentTheme;
    private string workspacePath = "尚未选择工作区";
    private string sessionPath = "尚未创建会话";
    private WorkbenchPage? workbenchPage;
    private LoopbackPage? loopbackPage;
    private SettingsPage? settingsPage;
    private SessionsPage? sessionsPage;
    private ModbusPage? modbusPage;

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
        PrepareWorkbenchPage();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ContentFrame.Content = workbenchPage;
        eventTimer.Tick += EventTimer_Tick;
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
        HostInfoBar.IsOpen = false;
        try
        {
            client = await HostEndpoint.ConnectAsync(AppContext.BaseDirectory, true, CancellationToken.None);
            var handshake = await client.HandshakeAsync(new HandshakeRequest(RpcProtocol.MajorVersion, RpcProtocol.MinorVersion, "winui", System.Globalization.CultureInfo.CurrentUICulture.Name), CancellationToken.None);
            if (!handshake.Accepted)
            {
                throw new InvalidOperationException(handshake.Error);
            }

            ConnectionStatusText.Text = $"Host {handshake.HostVersion}";
            SetStatusIndicator("SystemFillColorSuccessBrush");
            var ports = await client.ListPortsAsync(CancellationToken.None);
            if (workbenchPage is not null)
            {
                workbenchPage.PortComboBox.ItemsSource = ports;
            }
            var preferredPort = ports.FirstOrDefault(item => item.PortName.Equals("COM17", StringComparison.OrdinalIgnoreCase))
                ?? (ports.Count > 0 ? ports[0] : null);
            if (workbenchPage is not null)
            {
                workbenchPage.PortComboBox.SelectedItem = preferredPort;
            }
            SetSerialConfigurationEnabled(true);
            eventTimer.Start();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowHostError(ex.Message);
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
                if (workbenchPage is not null)
                {
                    workbenchPage.ConnectButton.Content = "连接";
                }
                ConnectionStatusText.Text = "已断开";
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
            var connection = await client.OpenConnectionAsync(new OpenConnectionRequest(options), CancellationToken.None);
            connectionId = connection.Id;
            if (workbenchPage is not null)
            {
                workbenchPage.ConnectButton.Content = "断开";
            }
            ConnectionStatusText.Text = $"{port.PortName} · {options.BaudRate:N0} baud";
            SetSerialConfigurationEnabled(false);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ShowHostError(ex.Message);
        }
        finally
        {
            SetConnectionBusy(false);
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || connectionId is not { } current)
        {
            workbenchPage?.ShowSendResult("请先连接串口。", InfoBarSeverity.Warning);
            return;
        }

        var sendText = workbenchPage?.SendText ?? string.Empty;
        var sendFormatIndex = workbenchPage?.SendFormatIndex ?? 0;
        var lineEndingIndex = workbenchPage?.SendLineEndingIndex ?? 0;
        workbenchPage?.SetSending(true);
        try
        {
            var data = sendFormatIndex == 1
                ? Protocols.HexCodec.Parse(sendText)
                : Encoding.UTF8.GetBytes(sendText + GetLineEnding(lineEndingIndex));
            data = AppendChecksum(data, workbenchPage?.SendChecksumIndex ?? 0);
            await client.SendAsync(new SendRequest(current, data, "winui.send"), CancellationToken.None);
            var severity = InfoBarSeverity.Success;
            workbenchPage?.ShowSendResult($"已发送 {data.Length:N0} 字节。", severity);
        }
        catch (Exception ex)
        {
            workbenchPage?.ShowSendResult(ex.Message, InfoBarSeverity.Error);
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
        if (polling || paused || client is null)
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
                TrafficRows.Add(TrafficRow.From(item, workbenchPage?.MonitorFormatIndex == 1));
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
            ShowHostError(ex.Message);
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
        CountersText.Text = connection is null ? "RX 0 · TX 0" : $"RX {connection.ReceivedBytes:N0} · TX {connection.TransmittedBytes:N0}";
        sessionPath = status.ActiveSession?.Path ?? "尚未创建会话";
        workspacePath = status.WorkspaceRoot is null ? $"全局数据：{status.DataRoot}" : $"工作区：{status.WorkspaceRoot}";
        sessionsPage?.SetPaths(workspacePath, sessionPath);
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

    private async void CopyHexButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(string.Join(Environment.NewLine, TrafficRows.Select(static item => item.Hex)));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        await Task.CompletedTask;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "Loopback" => typeof(LoopbackPage),
            "Modbus" => typeof(ModbusPage),
            "Sessions" => typeof(SessionsPage),
            "Settings" => typeof(SettingsPage),
            "Receive" => typeof(WorkbenchPage),
            _ => typeof(WorkbenchPage),
        };
        if (tag is "Workbench" or "Receive")
        {
            PrepareWorkbenchPage();
            ContentFrame.Content = workbenchPage;
        }
        else if (tag == "Loopback")
        {
            loopbackPage ??= new LoopbackPage();
            loopbackPage.RunRequested -= LoopbackPage_RunRequested;
            loopbackPage.RunRequested += LoopbackPage_RunRequested;
            ContentFrame.Content = loopbackPage;
        }
        else if (tag == "Modbus")
        {
            modbusPage ??= new ModbusPage();
            modbusPage.SendRequested -= ModbusPage_SendRequested;
            modbusPage.SendRequested += ModbusPage_SendRequested;
            ContentFrame.Content = modbusPage;
        }
        else if (tag == "Settings")
        {
            settingsPage ??= new SettingsPage();
            settingsPage.ChooseWorkspaceRequested -= SettingsPage_ChooseWorkspaceRequested;
            settingsPage.ChooseWorkspaceRequested += SettingsPage_ChooseWorkspaceRequested;
            settingsPage.ThemeChangeRequested -= SettingsPage_ThemeChangeRequested;
            settingsPage.ThemeChangeRequested += SettingsPage_ThemeChangeRequested;
            settingsPage.SetThemeSelection(currentTheme);
            settingsPage.SetWorkspacePath(workspacePath);
            ContentFrame.Content = settingsPage;
        }
        else if (tag == "Sessions")
        {
            sessionsPage ??= new SessionsPage();
            sessionsPage.SetPaths(workspacePath, sessionPath);
            ContentFrame.Content = sessionsPage;
        }
        else if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void PrepareWorkbenchPage()
    {
        workbenchPage ??= new WorkbenchPage();
        workbenchPage.BindRows(TrafficRows);
        workbenchPage.ConnectRequested -= WorkbenchPage_ConnectRequested;
        workbenchPage.ConnectRequested += WorkbenchPage_ConnectRequested;
        workbenchPage.PauseRequested -= WorkbenchPage_PauseRequested;
        workbenchPage.PauseRequested += WorkbenchPage_PauseRequested;
        workbenchPage.ClearRequested -= WorkbenchPage_ClearRequested;
        workbenchPage.ClearRequested += WorkbenchPage_ClearRequested;
        workbenchPage.SendRequested -= WorkbenchPage_SendRequested;
        workbenchPage.SendRequested += WorkbenchPage_SendRequested;
    }

    private void WorkbenchPage_ConnectRequested(object? sender, EventArgs e) => ConnectButton_Click(this, new RoutedEventArgs());
    private void WorkbenchPage_PauseRequested(object? sender, EventArgs e) => PauseButton_Click(this, new RoutedEventArgs());
    private void WorkbenchPage_ClearRequested(object? sender, EventArgs e) => ClearButton_Click(this, new RoutedEventArgs());
    private void WorkbenchPage_SendRequested(object? sender, EventArgs e)
    {
        if (workbenchPage is null)
        {
            return;
        }

        SendButton_Click(this, new RoutedEventArgs());
    }
    private async void SettingsPage_ChooseWorkspaceRequested(object? sender, EventArgs e) => await ChooseWorkspaceAsync();
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
            await client.SendAsync(new SendRequest(current, frame, "winui.modbus"), CancellationToken.None);
            modbusPage.ShowResult($"已发送 {frame.Length:N0} 字节，响应见接收页报文流。", InfoBarSeverity.Success);
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
        ShowHostError(e.Exception.Message);
        e.Handled = true;
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
    }

    private async void ChooseWorkspaceButton_Click(object sender, RoutedEventArgs e)
        => await ChooseWorkspaceAsync();

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

    private async void ClearWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangeWorkspaceAsync(null);
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
            workspacePath = status.WorkspaceRoot is null ? $"全局数据：{status.DataRoot}" : $"工作区：{status.WorkspaceRoot}";
            settingsPage?.SetWorkspacePath(workspacePath);
            sessionPath = "尚未创建会话";
            sessionsPage?.SetPaths(workspacePath, sessionPath);
        }
        catch (Exception ex)
        {
            ShowHostError(ex.Message);
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        eventTimer.Stop();
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
            workbenchPage.BaudRateNumberBox.IsEnabled = enabled;
            workbenchPage.MonitorFormat.IsEnabled = enabled;
            workbenchPage.AdvancedExpander.IsEnabled = enabled;
        }
    }

    private void UpdateTrafficPresentation()
    {
        var empty = TrafficRows.Count == 0;
        if (workbenchPage is not null)
        {
            workbenchPage.MonitorEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
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

    private void ShowHostError(string message)
    {
        ConnectionStatusText.Text = "Host 错误";
        SetStatusIndicator("SystemFillColorCriticalBrush");
        HostInfoBar.Title = "SerialWorkbench Host";
        HostInfoBar.Message = message;
        HostInfoBar.Severity = InfoBarSeverity.Error;
        HostInfoBar.IsOpen = true;
    }

    private void SetStatusIndicator(string brushKey)
    {
        StatusIndicator.Fill = (Brush)Application.Current.Resources[brushKey];
    }

}

public sealed class TrafficRow(
    string time,
    string display,
    string hex,
    Visibility receiveVisibility,
    Visibility transmitVisibility)
{
    public string Time { get; } = time;

    public string Display { get; } = display;

    public string Hex { get; } = hex;

    public Visibility ReceiveVisibility { get; } = receiveVisibility;

    public Visibility TransmitVisibility { get; } = transmitVisibility;

    public static TrafficRow From(SerialTrafficEvent item, bool text)
    {
        var hex = Convert.ToHexString(item.Data);
        var display = text ? Encoding.UTF8.GetString(item.Data) : Protocols.HexCodec.Format(item.Data);
        var receive = item.Direction == SerialDirection.Receive;
        return new TrafficRow(
            item.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            display,
            hex,
            receive ? Visibility.Visible : Visibility.Collapsed,
            receive ? Visibility.Collapsed : Visibility.Visible);
    }
}
