using System.Collections.ObjectModel;
using SerialWorkbench.Domain;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private string? configuredDeviceId;

    private SerialConnectionOptions ReadConnectionOptions()
    {
        var descriptor = ports.FirstOrDefault(item => item.PortName == port.Text) ?? throw new InvalidOperationException("请选择串口。");
        return new SerialConnectionOptions(descriptor.PortName, baud.Value, dataBits.Value, parity.Value ?? SerialParity.None,
            stopBits.Value ?? SerialStopBits.One, handshake.Value ?? SerialHandshake.None,
            dtr.Value == CheckState.Checked, rts.Value == CheckState.Checked, encoding.Text, role.Value ?? SerialConnectionRole.Dut,
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
        parity.Value = profile.Parity;
        stopBits.Value = profile.StopBits;
        handshake.Value = profile.Handshake;
        encoding.Text = profile.EncodingName;
        role.Value = profile.Role;
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
