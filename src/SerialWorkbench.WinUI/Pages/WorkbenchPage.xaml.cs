using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScottPlot;
using SerialWorkbench.Domain;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class WorkbenchPage : Page
{
    private const double CompactLayoutWidth = 560;
    private const double WideLayoutWidth = 800;
    private const int MaxWavePoints = 2000;
    private readonly WaveformParser waveformParser = new();
    private readonly List<List<double>> channelData = [];
    private ThemeSettings? themeSettings;
    private bool viewSelectionInitialized;
    private bool serialConfigurationEnabled = true;
    private bool renamingProfile;
    private int baudRate = 115200;
    private readonly ObservableCollection<SerialProfile> profiles = new(SerialProfileStore.Load());
    private readonly ObservableCollection<TrafficRow> visibleRows = [];
    private readonly TrafficCopyMenu copyMenu;
    private ObservableCollection<TrafficRow>? sourceRows;

    public WorkbenchPage()
    {
        InitializeComponent();
        copyMenu = new TrafficCopyMenu(TrafficListView);
        foreach (var box in (NumberBox[])[DataBitsNumberBox, RtsBeforeSendNumberBox, RtsAfterSendNumberBox, PlotFrameLength, LoopIntervalNumberBox])
        {
            box.ValueChanged += NumberBoxInput.KeepLastValue;
        }

        ProfileComboBox.ItemsSource = profiles;
        UpdateProfileActions();
        ViewSelector.SelectedItem = MonitorSelectorItem;
        TrafficDirection.SelectedIndex = 0;
        PlotMode.SelectedIndex = 0;
        HandshakeComboBox.SelectedIndex = 0;
        Loaded += WorkbenchPage_Loaded;
        ActualThemeChanged += WorkbenchPage_ActualThemeChanged;
    }

    private readonly ObservableCollection<string> sendHistory = new(SendHistoryStore.Load());

    public event EventHandler? ConnectRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler? CopyHexRequested;
    public event EventHandler? SendRequested;
    public event EventHandler? RefreshPortsRequested;
    public event EventHandler? LoopSendStarted;
    public event EventHandler? LoopSendStopped;
    public event EventHandler<string>? ProfileNewRequested;
    public event EventHandler<string>? ProfileRenameRequested;
    public event EventHandler? ProfileDeleteRequested;
    public event EventHandler? ProfileApplyRequested;
    public event EventHandler? ControlLinesChangedRequested;
    public event EventHandler? ClearReceiveRequested;
    public event EventHandler? ClearTransmitRequested;
    public event EventHandler? BreakRequested;
    public event EventHandler? TrafficViewChanged;

    public void BindRows(ObservableCollection<TrafficRow> rows)
    {
        sourceRows = rows;
        TrafficListView.ItemsSource = visibleRows;
        SendHistory.ItemsSource = sendHistory;
        RefreshTrafficFilter();
    }

    public IReadOnlyList<TrafficRow> VisibleRows => visibleRows;

    public void FreezeRows()
    {
        var selected = copyMenu.CaptureSelection();
        for (var index = 0; index < visibleRows.Count; index++)
        {
            visibleRows[index] = visibleRows[index].Snapshot();
        }

        copyMenu.RestoreSelection(selected);
    }

    public void RefreshTrafficFilter()
    {
        var query = TrafficSearch.Text.Trim();
        var direction = TrafficDirection.SelectedIndex;
        var selected = copyMenu.CaptureSelection();
        var filtered = new List<TrafficRow>();
        if (sourceRows is not null)
        {
            foreach (var row in sourceRows)
            {
                if (direction == 1 && !row.IsReceive || direction == 2 && row.IsReceive)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(query)
                    && !row.Display.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    && !row.Hex.Contains(query.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)
                    && !row.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    continue;
                }

                filtered.Add(row);
            }
        }

        TrafficSelection.UpdateRows(visibleRows, filtered);
        copyMenu.RestoreSelection(selected);
        var hasSourceRows = sourceRows?.Count > 0;
        var empty = visibleRows.Count == 0;
        MonitorEmptyTitle.Text = hasSourceRows ? "没有匹配的报文" : "等待串口数据";
        MonitorEmptyDescription.Text = hasSourceRows ? "调整方向或搜索条件以查看其他报文。" : "连接设备后，收发报文会显示在这里。";
        MonitorEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CopyHexButton.IsEnabled = !empty;
        if (!empty && !copyMenu.HasSelection)
        {
            TrafficListView.ScrollIntoView(visibleRows[^1]);
        }
    }

    public IReadOnlyList<SerialProfile> Profiles => profiles;

    public SerialProfile? SelectedProfile => ProfileComboBox.SelectedItem as SerialProfile;

    public SerialPortDescriptor? SelectedPort => PortComboBox.SelectedItem as SerialPortDescriptor;
    public int BaudRate => baudRate;
    public int DataBits => (int)DataBitsNumberBox.Value;
    public SerialParity Parity => (SerialParity)ParityComboBox.SelectedIndex;
    public SerialStopBits StopBits => (SerialStopBits)StopBitsComboBox.SelectedIndex;
    public SerialHandshake Handshake => (SerialHandshake)HandshakeComboBox.SelectedIndex;
    public SerialConnectionRole Role => (SerialConnectionRole)RoleComboBox.SelectedIndex;
    public bool DtrEnable => DtrToggle.IsChecked == true;
    public bool RtsEnable => RtsToggle.IsChecked == true;
    public bool Rs485Mode => Rs485ModeCheckBox.IsChecked == true;
    public int RtsBeforeSendMilliseconds => checked((int)RtsBeforeSendNumberBox.Value);
    public int RtsAfterSendMilliseconds => checked((int)RtsAfterSendNumberBox.Value);
    public int MonitorFormatIndex => MonitorFormat.SelectedIndex;
    public Encoding SelectedEncoding => EncodingComboBox.SelectedIndex switch
    {
        1 => Encoding.ASCII,
        2 => Encoding.GetEncoding("GBK"),
        3 => Encoding.Unicode,
        _ => Encoding.UTF8,
    };
    public bool ShowTimestamp => TimestampToggle.IsChecked == true;

    public void ApplySerialPreference(SerialPreference preference)
    {
        SetBaudRate(preference.BaudRate);
        DataBitsNumberBox.Value = preference.DataBits;
        ParityComboBox.SelectedIndex = (int)preference.Parity;
        StopBitsComboBox.SelectedIndex = (int)preference.StopBits;
        HandshakeComboBox.SelectedIndex = (int)preference.Handshake;
        RoleComboBox.SelectedIndex = (int)preference.Role;
        EncodingComboBox.SelectedIndex = preference.EncodingName.ToLowerInvariant() switch
        {
            "us-ascii" => 1,
            "gb2312" or "gbk" => 2,
            "utf-16" or "unicode" => 3,
            _ => 0,
        };
        DtrToggle.IsChecked = preference.DtrEnable;
        RtsToggle.IsChecked = preference.RtsEnable;
        Rs485ModeCheckBox.IsChecked = preference.Rs485Mode;
        RtsBeforeSendNumberBox.Value = preference.RtsBeforeSendMilliseconds;
        RtsAfterSendNumberBox.Value = preference.RtsAfterSendMilliseconds;
        MonitorFormat.SelectedIndex = preference.MonitorFormatIndex;
        SendFormat.SelectedIndex = preference.SendFormatIndex;
        SendLineEnding.SelectedIndex = preference.SendLineEndingIndex;
        SendChecksum.SelectedIndex = preference.SendChecksumIndex;
        LoopIntervalNumberBox.Value = preference.LoopIntervalMilliseconds;
        PlotMode.SelectedIndex = preference.PlotModeIndex;
        PlotFrameLength.Value = preference.PlotFrameLength;
        PlotSampleType.SelectedIndex = preference.PlotSampleTypeIndex;
    }

    public void ApplySerialProfile(SerialProfile profile)
    {
        SetBaudRate(profile.BaudRate);
        DataBitsNumberBox.Value = profile.DataBits;
        ParityComboBox.SelectedIndex = (int)profile.Parity;
        StopBitsComboBox.SelectedIndex = (int)profile.StopBits;
        HandshakeComboBox.SelectedIndex = (int)profile.Handshake;
        RoleComboBox.SelectedIndex = (int)profile.Role;
        EncodingComboBox.SelectedIndex = profile.EncodingName.ToLowerInvariant() switch
        {
            "us-ascii" => 1,
            "gb2312" or "gbk" => 2,
            "utf-16" or "unicode" => 3,
            _ => 0,
        };
        DtrToggle.IsChecked = profile.DtrEnable;
        RtsToggle.IsChecked = profile.RtsEnable;
        Rs485ModeCheckBox.IsChecked = profile.Rs485Mode;
        RtsBeforeSendNumberBox.Value = profile.RtsBeforeSendMilliseconds;
        RtsAfterSendNumberBox.Value = profile.RtsAfterSendMilliseconds;
        if ((profile.PortName is not null || profile.DeviceInstanceId is not null)
            && PortComboBox.ItemsSource is IReadOnlyList<SerialPortDescriptor> ports)
        {
            PortComboBox.SelectedItem = profile.DeviceInstanceId is not null
                ? ports.FirstOrDefault(item => item.DeviceInstanceId?.Equals(profile.DeviceInstanceId, StringComparison.OrdinalIgnoreCase) == true)
                    ?? (profile.PortName is not null ? ports.FirstOrDefault(item => item.PortName.Equals(profile.PortName, StringComparison.OrdinalIgnoreCase)) : null)
                : ports.FirstOrDefault(item => item.PortName.Equals(profile.PortName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public SerialProfile ReadSerialProfile(string name) => new(
        name,
        SelectedPort?.PortName,
        BaudRate,
        DataBits,
        Parity,
        StopBits,
        Handshake,
        SelectedEncoding.WebName,
        DtrEnable,
        RtsEnable,
        Role,
        SelectedPort?.DeviceInstanceId,
        Rs485Mode,
        RtsBeforeSendMilliseconds,
        RtsAfterSendMilliseconds);

    public void AddProfile(SerialProfile profile)
    {
        profiles.Add(profile);
        ProfileComboBox.SelectedItem = profile;
    }

    public void ReplaceProfile(SerialProfile profile)
    {
        if (SelectedProfile is not { } current)
        {
            return;
        }

        var index = profiles.IndexOf(current);
        if (index < 0)
        {
            return;
        }

        profiles[index] = profile;
        ProfileComboBox.SelectedItem = profile;
    }

    public void RemoveSelectedProfile()
    {
        if (SelectedProfile is { } profile)
        {
            profiles.Remove(profile);
        }
    }

    public SerialPreference ReadSerialPreference(string? portName) => new(
        portName,
        BaudRate,
        DataBits,
        Parity,
        StopBits,
        Handshake,
        SelectedEncoding.WebName,
        DtrEnable,
        RtsEnable,
        MonitorFormatIndex,
        SendFormatIndex,
        SendLineEndingIndex,
        SendChecksumIndex,
        LoopIntervalMs,
        PlotMode.SelectedIndex,
        (int)PlotFrameLength.Value,
        PlotSampleType.SelectedIndex,
        Role,
        SelectedPort?.DeviceInstanceId,
        Rs485Mode,
        RtsBeforeSendMilliseconds,
        RtsAfterSendMilliseconds);

    private void ConnectButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ConnectRequested?.Invoke(this, EventArgs.Empty);
    private void RefreshPortsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => RefreshPortsRequested?.Invoke(this, EventArgs.Empty);
    private void PauseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => PauseRequested?.Invoke(this, EventArgs.Empty);
    private void ClearButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);
    private void CopyHexButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => CopyHexRequested?.Invoke(this, EventArgs.Empty);
    private void TrafficFilter_Changed(object sender, SelectionChangedEventArgs e) => RefreshTrafficFilter();
    private void TrafficSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshTrafficFilter();
    private void SendButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SendRequested?.Invoke(this, EventArgs.Empty);
    private void LoopSendToggle_Checked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LoopSendStarted?.Invoke(this, EventArgs.Empty);
    private void LoopSendToggle_Unchecked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => LoopSendStopped?.Invoke(this, EventArgs.Empty);

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProfileActions();

    private void ApplyProfileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ProfileApplyRequested?.Invoke(this, EventArgs.Empty);

    private async void AdvancedButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AdvancedDialog.XamlRoot = XamlRoot;
        AdvancedDialog.RequestedTheme = ActualTheme;
        await AdvancedDialog.ShowAsync();
    }

    private void NewProfileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ShowProfileNameFlyout(NewProfileButton, false, "");

    private void RenameProfileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ShowProfileNameFlyout(RenameProfileButton, true, SelectedProfile?.Name ?? "");

    private void ShowProfileNameFlyout(FrameworkElement target, bool rename, string name)
    {
        renamingProfile = rename;
        ProfileNameBox.Text = name;
        ProfileNameError.Visibility = Visibility.Collapsed;
        ProfileNameFlyout.ShowAt(target);
    }

    private void ProfileNameBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            SaveProfileName();
        }
    }

    private void SaveProfileNameButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SaveProfileName();

    private void SaveProfileName()
    {
        var name = ProfileNameBox.Text.Trim();
        var current = renamingProfile ? SelectedProfile : null;
        var error = name.Length == 0
            ? "配置名称不能为空。"
            : profiles.Any(item => item != current && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ? $"连接配置“{name}”已经存在。"
                : null;
        if (error is not null)
        {
            ProfileNameError.Text = error;
            ProfileNameError.Visibility = Visibility.Visible;
            return;
        }

        ProfileNameFlyout.Hide();
        (renamingProfile ? ProfileRenameRequested : ProfileNewRequested)?.Invoke(this, name);
    }

    private void DeleteProfileFlyout_Opening(object sender, object e) =>
        DeleteProfileText.Text = $"将删除连接配置“{SelectedProfile?.Name}”。当前串口连接不会断开。";

    private void ConfirmDeleteProfileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        DeleteProfileFlyout.Hide();
        ProfileDeleteRequested?.Invoke(this, EventArgs.Empty);
    }
    private void ControlLineToggle_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ControlLinesChangedRequested?.Invoke(this, EventArgs.Empty);
    private void ClearReceiveButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearReceiveRequested?.Invoke(this, EventArgs.Empty);
    private void ClearTransmitButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearTransmitRequested?.Invoke(this, EventArgs.Empty);
    private void BreakButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => BreakRequested?.Invoke(this, EventArgs.Empty);
    private void HandshakeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRtsAvailability();
    private void Rs485ModeCheckBox_Changed(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => UpdateRtsAvailability();
    private void MonitorFormat_SelectionChanged(object sender, SelectionChangedEventArgs e) => TrafficViewChanged?.Invoke(this, EventArgs.Empty);
    private void TimestampToggle_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => TrafficViewChanged?.Invoke(this, EventArgs.Empty);

    private void SendEditor_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter
            && SendButton.IsEnabled
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            SendRequested?.Invoke(this, EventArgs.Empty);
        }
    }


    private void BaudRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems is [string value] && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate) && rate > 0)
        {
            baudRate = rate;
        }
    }

    private void BaudRateComboBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (int.TryParse(args.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate) && rate > 0)
        {
            baudRate = rate;
            return;
        }

        sender.Text = baudRate.ToString(CultureInfo.InvariantCulture);
        args.Handled = true;
    }

    private void SetBaudRate(int value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        baudRate = value;
        BaudRateComboBox.SelectedItem = BaudRateComboBox.Items.FirstOrDefault(item => (string)item == text);
        BaudRateComboBox.Text = text;
    }

    private void UpdateRtsAvailability() => RtsToggle.IsEnabled = !Rs485Mode && Handshake is SerialHandshake.None or SerialHandshake.XOnXOff;

    private void UpdateProfileActions()
    {
        var selected = SelectedProfile is not null;
        ApplyProfileButton.IsEnabled = selected && serialConfigurationEnabled;
        RenameProfileButton.IsEnabled = selected;
        DeleteProfileButton.IsEnabled = selected;
    }

    private void SendHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SendHistory.SelectedItem is string entry)
        {
            SendEditor.Text = entry;
        }
    }

    public void AddSendHistory(string entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return;
        }

        sendHistory.Remove(entry);
        sendHistory.Insert(0, entry);
        while (sendHistory.Count > 20)
        {
            sendHistory.RemoveAt(sendHistory.Count - 1);
        }

        SendHistoryStore.Save(sendHistory);
    }

    public string SendText => SendEditor.Text;
    public int SendFormatIndex => SendFormat.SelectedIndex;
    public int SendLineEndingIndex => SendLineEnding.SelectedIndex;
    public int SendChecksumIndex => SendChecksum.SelectedIndex;
    public int LoopIntervalMs => (int)LoopIntervalNumberBox.Value;
    public bool IsLoopSending => LoopSendToggle.IsChecked == true;
    public void StopLoopSend() => LoopSendToggle.IsChecked = false;
    public void SetConnectionBusy(bool busy) => ConnectButton.IsEnabled = !busy;
    public void SetSerialConfigurationEnabled(bool enabled)
    {
        serialConfigurationEnabled = enabled;
        ConnectButton.Content = enabled ? "连接" : "断开";
        PortComboBox.IsEnabled = enabled;
        RefreshPortsButton.IsEnabled = enabled;
        BaudRateComboBox.IsEnabled = enabled;
        RoleComboBox.IsEnabled = enabled;
        DataBitsNumberBox.IsEnabled = enabled;
        ParityComboBox.IsEnabled = enabled;
        StopBitsComboBox.IsEnabled = enabled;
        HandshakeComboBox.IsEnabled = enabled;
        EncodingComboBox.IsEnabled = enabled;
        Rs485ModeCheckBox.IsEnabled = enabled;
        RtsBeforeSendNumberBox.IsEnabled = enabled;
        RtsAfterSendNumberBox.IsEnabled = enabled;
        ClearReceiveButton.IsEnabled = !enabled;
        ClearTransmitButton.IsEnabled = !enabled;
        BreakButton.IsEnabled = !enabled;
        UpdateProfileActions();
    }
    public void SetConnectionStatus(string status) => ConnectionStatusText.Text = status;
    public void SetTrafficCounts(long received, long transmitted) => CountersText.Text = $"RX {received:N0} · TX {transmitted:N0}";
    public void SetPauseState(bool paused, long pendingBytes)
    {
        PauseButton.Content = paused
            ? pendingBytes > 0 ? $"继续 · {FormatBytes(pendingBytes)}" : "继续"
            : "暂停";
    }
    public void SetSending(bool sending) => SendButton.IsEnabled = !sending;
    public void ShowSendResult(string message, InfoBarSeverity severity)
    {
        SendStatus.Title = severity == InfoBarSeverity.Success ? "发送完成" : "发送";
        SendStatus.Message = message;
        SendStatus.Severity = severity;
        SendStatus.IsOpen = true;
    }

    public void HideSendResult() => SendStatus.IsOpen = false;

    public void SetPorts(IReadOnlyList<SerialPortDescriptor> ports, SerialPreference preference)
    {
        var currentPort = SelectedPort;
        var currentName = currentPort?.PortName;
        if (SamePortSet(PortComboBox.ItemsSource as IReadOnlyList<SerialPortDescriptor>, ports))
        {
            return;
        }

        PortComboBox.ItemsSource = ports;
        var target = currentPort?.DeviceInstanceId is not null
            ? ports.FirstOrDefault(item => item.DeviceInstanceId?.Equals(currentPort.DeviceInstanceId, StringComparison.OrdinalIgnoreCase) == true)
                ?? (currentName is not null ? ports.FirstOrDefault(item => item.PortName.Equals(currentName, StringComparison.OrdinalIgnoreCase)) : null)
            : preference.DeviceInstanceId is not null
                ? ports.FirstOrDefault(item => item.DeviceInstanceId?.Equals(preference.DeviceInstanceId, StringComparison.OrdinalIgnoreCase) == true)
                    ?? (preference.PortName is not null ? ports.FirstOrDefault(item => item.PortName.Equals(preference.PortName, StringComparison.OrdinalIgnoreCase)) : null)
                : currentName is not null
                    ? ports.FirstOrDefault(item => item.PortName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
                    : preference.PortName is not null
                        ? ports.FirstOrDefault(item => item.PortName.Equals(preference.PortName, StringComparison.OrdinalIgnoreCase))
                        : null;
        PortComboBox.SelectedItem = target ?? (ports.Count > 0 ? ports[0] : null);
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

    private static string FormatBytes(long value) => value switch
    {
        >= 1024 * 1024 => $"{value / 1024d / 1024d:N1} MiB",
        >= 1024 => $"{value / 1024d:N1} KiB",
        _ => $"{value:N0} B",
    };

    public void AppendWaveform(byte[] data)
    {
        if (PlotRunning.IsChecked != true)
        {
            return;
        }

        waveformParser.Mode = PlotMode.SelectedIndex == 1 ? WaveformMode.BinaryFrame : WaveformMode.CsvText;
        waveformParser.FrameLength = (int)PlotFrameLength.Value;
        waveformParser.SampleType = (WaveformSampleType)PlotSampleType.SelectedIndex;

        var samples = waveformParser.Feed(data);
        if (samples.Count == 0)
        {
            return;
        }

        foreach (var sample in samples)
        {
            for (var channel = 0; channel < sample.Length; channel++)
            {
                EnsureChannel(channel);
                var series = channelData[channel];
                series.Add(sample[channel]);
                if (series.Count > MaxWavePoints)
                {
                    series.RemoveRange(0, series.Count - MaxWavePoints);
                }
            }
        }

        RedrawWaveform();
    }

    private void EnsureChannel(int channel)
    {
        while (channelData.Count <= channel)
        {
            channelData.Add([]);
        }
    }

    private void RedrawWaveform()
    {
        WavePlot.Plot.Clear();
        for (var channel = 0; channel < channelData.Count; channel++)
        {
            var scatter = WavePlot.Plot.Add.Signal(channelData[channel].ToArray());
            scatter.LegendText = $"CH{channel}";
        }

        WavePlot.Plot.Axes.AutoScale();
        WavePlot.Refresh();
    }

    private void WorkbenchPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (themeSettings is null)
        {
            themeSettings = ThemeSettings.CreateForWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            themeSettings.Changed += ThemeSettings_Changed;
        }

        ApplyPlotTheme();
    }

    private void WorkbenchPage_ActualThemeChanged(FrameworkElement sender, object args) => ApplyPlotTheme();

    private void ThemeSettings_Changed(ThemeSettings sender, object args) => ApplyPlotTheme();

    private void WorkbenchPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var state = e.NewSize.Width switch
        {
            < CompactLayoutWidth => "Compact",
            < WideLayoutWidth => "Narrow",
            _ => "Wide",
        };
        VisualStateManager.GoToState(this, state, false);
        VisualStateManager.GoToState(this, e.NewSize.Width < 641 ? "CompactPageMargins" : "StandardPageMargins", false);
        VisualStateManager.GoToState(this, e.NewSize.Height < 600 ? "CompactHeight" : "StandardHeight", false);
    }

    private void ApplyPlotTheme()
    {
        PlotStyle style = themeSettings?.HighContrast == true
            ? CreateHighContrastPlotStyle()
            : ActualTheme == ElementTheme.Dark
                ? new ScottPlot.PlotStyles.Dark()
                : new ScottPlot.PlotStyles.Light();
        style.Apply(WavePlot.Plot);
        RedrawWaveform();
    }

    private static PlotStyle CreateHighContrastPlotStyle()
    {
        var background = GetSystemColor("SystemColorWindowColor");
        var foreground = GetSystemColor("SystemColorWindowTextColor");
        var highlight = GetSystemColor("SystemColorHighlightColor");
        return new PlotStyle
        {
            Palette = new ScottPlot.Palettes.Custom([foreground, highlight], "Windows high contrast"),
            FigureBackgroundColor = background,
            DataBackgroundColor = background,
            AxisColor = foreground,
            GridMajorLineColor = foreground.WithOpacity(.25),
            LegendBackgroundColor = background,
            LegendFontColor = foreground,
            LegendOutlineColor = foreground,
        };
    }

    private static ScottPlot.Color GetSystemColor(string key)
    {
        var color = (Windows.UI.Color)Application.Current.Resources[key];
        return new ScottPlot.Color(color.R, color.G, color.B, color.A);
    }

    private void PlotMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var binary = PlotMode.SelectedIndex == 1;
        PlotFrameLength.IsEnabled = binary;
        PlotSampleType.IsEnabled = binary;
    }

    private void ViewSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var monitor = sender.SelectedItem == MonitorSelectorItem;
        MonitorView.Visibility = monitor ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        PlotView.Visibility = monitor ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

        if (viewSelectionInitialized)
        {
            (monitor ? MonitorViewTransition : PlotViewTransition).Begin();
        }

        viewSelectionInitialized = true;
    }

    private void PlotClearButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        channelData.Clear();
        waveformParser.Reset();
        WavePlot.Plot.Clear();
        WavePlot.Refresh();
    }
}
