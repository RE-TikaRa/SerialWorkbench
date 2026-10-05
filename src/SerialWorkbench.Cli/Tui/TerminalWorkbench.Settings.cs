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
            app.Run(dialog);
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
        app.Run(dialog);
        dialog.Remove(content);
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

    private SerialConnectionOptions ReadConnectionOptions()
    {
        var descriptor = ports.FirstOrDefault(item => item.PortName == port.Text) ?? throw new InvalidOperationException("请选择串口。");
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
        port.Text = descriptor?.PortName ?? profile.PortName ?? "";
        configuredDeviceId = profile.DeviceInstanceId;
        profileName.Text = profile.Name;
        baud.Value = profile.BaudRate;
        dataBits.Value = profile.DataBits;
        parity.Text = profile.Parity.ToString();
        stopBits.Text = profile.StopBits.ToString();
        handshake.Text = profile.Handshake.ToString();
        encoding.Text = profile.EncodingName;
        role.Text = profile.Role.ToString();
        dtr.Value = profile.DtrEnable ? CheckState.Checked : CheckState.UnChecked;
        rts.Value = profile.RtsEnable ? CheckState.Checked : CheckState.UnChecked;
        rs485.Value = profile.Rs485Mode ? CheckState.Checked : CheckState.UnChecked;
        rtsBefore.Value = profile.RtsBeforeSendMilliseconds;
        rtsAfter.Value = profile.RtsAfterSendMilliseconds;
        autoReconnect.Value = profile.AutoReconnect ? CheckState.Checked : CheckState.UnChecked;
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
