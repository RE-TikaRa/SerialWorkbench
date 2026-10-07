using System.Collections.ObjectModel;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private string? configuredDeviceId;
    private readonly View serialSettings = new() { CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly View controlSettings = new() { CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly View profileSettings = new() { CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly View connectionSettings = new() { CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill(1) };
    private readonly ListView managedConnections = new() { Width = Dim.Fill(), Height = Dim.Fill(3) };
    private readonly Label connectionMessage = new() { Id = "connection-message", Y = Pos.AnchorEnd(), Width = Dim.Fill(), Height = 1 };
    private Dialog? connectionDialog;
    private bool connectionOpened;

    internal View ConnectionSettings => connectionSettings;

    private SerialPortDescriptor? SelectedPort => ports.FirstOrDefault(item => item.DisplayName == port.Text);

    private View BuildSettings()
    {
        var view = new View { Title = "设置", Width = Dim.Fill(), Height = Dim.Fill() };
        AddSetting(view, "文本编码", encoding, 0);
        AddSetting(view, "HEX 间隔 ms", hexGap, 2);
        var connect = Button("连接管理", () => { ShowConnections(); return Task.CompletedTask; });
        connect.Y = Pos.Bottom(hexGap) + 1;
        var advanced = Button("串口参数", () => { ShowSettingsDialog("串口参数", serialSettings, 16); return Task.CompletedTask; });
        advanced.X = Pos.Right(connect) + 1;
        advanced.Y = Pos.Top(connect);
        var controls = Button("控制线", () => { ShowSettingsDialog("控制线与 RS-485", controlSettings, 17); return Task.CompletedTask; });
        controls.Y = Pos.Bottom(connect) + 1;
        var profiles = Button("配置与工作区", () => { ShowSettingsDialog("配置与工作区", profileSettings, 14); return Task.CompletedTask; });
        profiles.X = Pos.Right(controls) + 1;
        profiles.Y = Pos.Top(controls);
        var display = Button("报文显示", () => { ShowSettingsDialog("报文显示与筛选", trafficSettings, 18); return Task.CompletedTask; });
        display.Y = Pos.Bottom(controls) + 1;
        var sending = Button("发送设置", () => { ShowSettingsDialog("发送设置", sendSettings, 16); return Task.CompletedTask; });
        sending.X = Pos.Right(display) + 1;
        sending.Y = Pos.Top(display);
        backgroundTasks.Y = Pos.Bottom(display) + 1;
        BuildSerialSettings();
        BuildControlSettings();
        BuildProfileSettings();
        view.Add(connect, advanced, controls, profiles, display, sending, backgroundTasks);
        return view;
    }

    private void BuildConnectionSettings()
    {
        AddSetting(connectionSettings, "端口", port, 0);
        port.Width = Dim.Fill(1);
        AddSetting(connectionSettings, "波特率", baud, 2);
        var open = Button("连接", () => RunUiAsync(OpenConnectionAsync));
        open.Id = "connection-open";
        open.Y = Pos.Bottom(baud) + 1;
        var refresh = Button("刷新", () => RunUiAsync(RefreshPortsAsync));
        refresh.X = Pos.Right(open) + 1;
        refresh.Y = Pos.Top(open);
        var parameters = Button("串口参数", () => { ShowSettingsDialog("串口参数", serialSettings, 16); return Task.CompletedTask; });
        parameters.Y = Pos.Top(open);
        parameters.X = Pos.Right(refresh) + 1;
        var profiles = Button("配置与工作区", () => { ShowSettingsDialog("配置与工作区", profileSettings, 14); return Task.CompletedTask; });
        profiles.Y = Pos.Bottom(parameters) + 1;
        managedConnections.Y = Pos.Bottom(profiles) + 1;
        managedConnections.SetSource(connectionItems);
        managedConnections.ValueChanged += (_, args) =>
        {
            if (!updatingConnections)
            {
                connections.Value = args.NewValue;
            }
        };
        var close = Button("断开", () => RunUiAsync(CloseConnectionAsync));
        close.Y = Pos.Bottom(managedConnections) + 1;
        var details = Button("详情", () => RunUiAsync(ShowConnectionAsync));
        details.X = Pos.Right(close) + 1;
        details.Y = Pos.Top(close);
        var reconnect = Button("重连", () => RunUiAsync(async () =>
        {
            await client.ReconnectConnectionAsync(RequiredConnection(), lifetime.Token).ConfigureAwait(false);
            app.Invoke(() => message.Text = "连接已重连");
        }));
        reconnect.X = Pos.Right(details) + 1;
        reconnect.Y = Pos.Top(close);
        connectionSettings.Add(open, refresh, parameters, profiles, managedConnections, close, details, reconnect, connectionMessage);
        message.TextChanged += (_, _) =>
        {
            if (connectionDialog is not null)
            {
                connectionMessage.Text = message.Text;
            }
        };
    }

    private void ShowConnections()
    {
        using var dialog = new Dialog { Title = "连接管理", Width = Dim.Percent(90), Height = Dim.Percent(85) };
        managedConnections.Value = connections.Value;
        connectionMessage.Text = "";
        connectionDialog = dialog;
        connectionOpened = false;
        dialog.Add(connectionSettings);
        dialog.AddButton(new Button { Text = "关闭", ShadowStyle = null });
        try
        {
            RunDialog(dialog);
        }
        finally
        {
            dialog.Remove(connectionSettings);
            connectionDialog = null;
        }
        if (connectionOpened)
        {
            input.SetFocus();
        }
    }

    private void BuildSerialSettings()
    {
        AddSetting(serialSettings, "数据位", dataBits, 0);
        AddSetting(serialSettings, "校验", parity, 2);
        AddSetting(serialSettings, "停止位", stopBits, 4);
        AddSetting(serialSettings, "流控", handshake, 6);
        AddSetting(serialSettings, "设备角色", role, 8);
        autoReconnect.X = 16;
        autoReconnect.Y = 10;
        serialSettings.Add(autoReconnect);
    }

    private void BuildControlSettings()
    {
        dtr.X = 0;
        dtr.Y = 0;
        rts.X = 16;
        rts.Y = 0;
        rs485.X = 0;
        rs485.Y = 2;
        AddSetting(controlSettings, "发送前延时 ms", rtsBefore, 4);
        AddSetting(controlSettings, "发送后延时 ms", rtsAfter, 6);
        var update = Button("更新 DTR/RTS", () => RunUiAsync(() => client.SetControlLinesAsync(RequiredConnection(),
            new SerialControlLines(dtr.Value == CheckState.Checked, rts.Value == CheckState.Checked), lifetime.Token)));
        update.Y = 8;
        var clearReceive = Button("清空 RX", () => RunUiAsync(() => client.ClearBuffersAsync(RequiredConnection(), true, false, lifetime.Token)));
        clearReceive.Y = 10;
        var clearTransmit = Button("清空 TX", () => RunUiAsync(() => client.ClearBuffersAsync(RequiredConnection(), false, true, lifetime.Token)));
        clearTransmit.X = Pos.Right(clearReceive) + 1;
        clearTransmit.Y = 10;
        var sendBreak = Button("BREAK 100 ms", () => RunUiAsync(() => client.SendBreakAsync(RequiredConnection(), 100, lifetime.Token)));
        sendBreak.X = Pos.Right(clearTransmit) + 1;
        sendBreak.Y = 10;
        controlSettings.Add(dtr, rts, rs485, update, clearReceive, clearTransmit, sendBreak);
    }

    private void BuildProfileSettings()
    {
        AddSetting(profileSettings, "连接配置", profileSelector, 0);
        AddSetting(profileSettings, "配置名称", profileName, 2);
        var load = Button("应用", () =>
        {
            var selected = profiles.FirstOrDefault(item => item.Name == profileSelector.Text);
            if (selected is not null)
            {
                ApplySerialProfile(selected);
            }
            return Task.CompletedTask;
        });
        load.Y = 4;
        var save = Button("保存", () => RunUiAsync(async () =>
        {
            var options = ReadConnectionOptions();
            var profile = new SerialProfile(profileName.Text, options.PortName, options.BaudRate, options.DataBits, options.Parity, options.StopBits,
                options.Handshake, options.EncodingName, options.DtrEnable, options.RtsEnable, options.Role, options.DeviceInstanceId, options.Rs485Mode,
                options.RtsBeforeSendMilliseconds, options.RtsAfterSendMilliseconds, options.AutoReconnect);
            var original = profiles.Any(item => item.Name == profileName.Text) ? profileName.Text : null;
            var saved = await client.SaveSerialProfileAsync(new SaveSerialProfileRequest(profile, original), lifetime.Token).ConfigureAwait(false);
            await InvokeUiAsync(() => { ApplyConfiguration(saved); profileSelector.Text = profile.Name; }).ConfigureAwait(false);
        }));
        save.X = Pos.Right(load) + 1;
        save.Y = 4;
        var delete = Button("删除", () => RunUiAsync(async () =>
        {
            var saved = await client.DeleteSerialProfileAsync(profileSelector.Text, lifetime.Token).ConfigureAwait(false);
            await InvokeUiAsync(() => ApplyConfiguration(saved)).ConfigureAwait(false);
        }));
        delete.X = Pos.Right(save) + 1;
        delete.Y = 4;
        var rename = Button("重命名", () => RunUiAsync(async () =>
        {
            var profile = profiles.FirstOrDefault(item => item.Name == profileSelector.Text) ?? throw new InvalidOperationException("请选择配置。");
            var saved = await client.SaveSerialProfileAsync(new SaveSerialProfileRequest(profile with { Name = profileName.Text }, profile.Name), lifetime.Token).ConfigureAwait(false);
            await InvokeUiAsync(() => { ApplyConfiguration(saved); profileSelector.Text = profileName.Text; }).ConfigureAwait(false);
        }));
        rename.X = Pos.Right(delete) + 1;
        rename.Y = 4;
        workspace.Y = 6;
        var choose = Button("选择工作区", () => RunUiAsync(async () =>
        {
            using var dialog = new OpenDialog { Title = "选择工作区", OpenMode = OpenMode.Directory, Path = Environment.CurrentDirectory, AllowsMultipleSelection = false };
            RunDialog(dialog);
            if (!dialog.Canceled)
            {
                await client.SetWorkspaceAsync(new SetWorkspaceRequest(dialog.Path), lifetime.Token).ConfigureAwait(false);
                await RefreshSessionsAsync().ConfigureAwait(false);
            }
        }));
        choose.Y = 8;
        var clear = Button("全局工作区", () => RunUiAsync(async () =>
        {
            await client.SetWorkspaceAsync(new SetWorkspaceRequest(null), lifetime.Token).ConfigureAwait(false);
            await RefreshSessionsAsync().ConfigureAwait(false);
        }));
        clear.X = Pos.Right(choose) + 1;
        clear.Y = 8;
        profileSettings.Add(load, save, delete, rename, workspace, choose, clear);
    }

    private void ShowSettingsDialog(string title, View content, int height)
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(90), Height = height };
        dialog.Add(content);
        dialog.AddButton(new Button { Text = "关闭", ShadowStyle = null });
        try
        {
            RunDialog(dialog);
        }
        finally
        {
            dialog.Remove(content);
        }
    }

    private static void AddSetting(View view, string label, View input, int row)
    {
        input.X = 16;
        input.Y = row;
        if (input is TextField)
        {
            input.Width = Dim.Fill(1);
        }
        view.Add(new Label { Text = label, Y = row }, input);
    }

    private static DropDownList EnumSelector<T>(T value) where T : struct, Enum => new()
    {
        ReadOnly = true,
        Source = new ListWrapper<string>(new ObservableCollection<string>(Enum.GetNames<T>())),
        Text = value.ToString(),
    };

    internal SerialConnectionOptions ReadConnectionOptions()
    {
        var descriptor = SelectedPort ?? throw new InvalidOperationException("请选择串口。");
        return new SerialConnectionOptions(descriptor.PortName, baud.Value, dataBits.Value, Enum.Parse<SerialParity>(parity.Text),
            Enum.Parse<SerialStopBits>(stopBits.Text), Enum.Parse<SerialHandshake>(handshake.Text),
            dtr.Value == CheckState.Checked, rts.Value == CheckState.Checked, encoding.Text, Enum.Parse<SerialConnectionRole>(role.Text),
            configuredDeviceId ?? descriptor.DeviceInstanceId, rs485.Value == CheckState.Checked, rtsBefore.Value, rtsAfter.Value,
            autoReconnect.Value == CheckState.Checked);
    }

    private void ApplySerialProfile(SerialProfile profile)
    {
        var descriptor = ports.FirstOrDefault(item => profile.DeviceInstanceId is not null
            ? item.DeviceInstanceId?.Equals(profile.DeviceInstanceId, StringComparison.OrdinalIgnoreCase) == true : item.PortName == profile.PortName);
        var portName = descriptor?.PortName ?? profile.PortName ?? "";
        profileName.Text = profile.Name;
        ApplyConnectionOptions(new SerialConnectionOptions(portName, profile.BaudRate, profile.DataBits, profile.Parity, profile.StopBits,
            profile.Handshake, profile.DtrEnable, profile.RtsEnable, profile.EncodingName, profile.Role, profile.DeviceInstanceId,
            profile.Rs485Mode, profile.RtsBeforeSendMilliseconds, profile.RtsAfterSendMilliseconds, profile.AutoReconnect));
    }

    private void ApplyConnectionOptions(SerialConnectionOptions options)
    {
        port.Text = ports.FirstOrDefault(item => item.PortName == options.PortName)?.DisplayName ?? options.PortName;
        configuredDeviceId = options.DeviceInstanceId;
        baud.Value = options.BaudRate;
        dataBits.Value = options.DataBits;
        parity.Text = options.Parity.ToString();
        stopBits.Text = options.StopBits.ToString();
        handshake.Text = options.Handshake.ToString();
        encoding.Text = options.EncodingName;
        role.Text = options.Role.ToString();
        dtr.Value = options.DtrEnable ? CheckState.Checked : CheckState.UnChecked;
        rts.Value = options.RtsEnable ? CheckState.Checked : CheckState.UnChecked;
        rs485.Value = options.Rs485Mode ? CheckState.Checked : CheckState.UnChecked;
        rtsBefore.Value = options.RtsBeforeSendMilliseconds;
        rtsAfter.Value = options.RtsAfterSendMilliseconds;
        autoReconnect.Value = options.AutoReconnect ? CheckState.Checked : CheckState.UnChecked;
    }

    private void ApplyConfiguration(ConfigurationSnapshot configuration)
    {
        configurationRevision = configuration.Revision;
        if (!profiles.SequenceEqual(configuration.Profiles))
        {
            var selected = profileSelector.Text;
            profiles = configuration.Profiles.ToArray();
            profileSelector.Source = new ListWrapper<string>(new ObservableCollection<string>(profiles.Select(static item => item.Name)));
            profileSelector.Text = profiles.Any(item => item.Name == selected) ? selected : profiles.FirstOrDefault()?.Name ?? "";
        }
        if (!historyItems.SequenceEqual(configuration.SendHistory))
        {
            var selected = history.Value is { } index && index >= 0 && index < historyItems.Count ? historyItems[index] : null;
            historyItems.Clear();
            foreach (var item in configuration.SendHistory)
            {
                historyItems.Add(item);
            }
            history.Value = selected is not null && historyItems.Contains(selected) ? historyItems.IndexOf(selected) : null;
        }
    }
}
