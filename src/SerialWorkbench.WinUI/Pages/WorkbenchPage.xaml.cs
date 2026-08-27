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
    private const double WideLayoutWidth = 900;
    private const int MaxWavePoints = 2000;
    private readonly WaveformParser waveformParser = new();
    private readonly List<List<double>> channelData = [];
    private ThemeSettings? themeSettings;
    private bool viewSelectionInitialized;
    private readonly ObservableCollection<SerialProfile> profiles = new(SerialProfileStore.Load());
    private readonly ObservableCollection<TrafficRow> visibleRows = [];
    private ObservableCollection<TrafficRow>? sourceRows;

    public WorkbenchPage()
    {
        InitializeComponent();
        ProfileComboBox.ItemsSource = profiles;
        UpdateProfileActions();
        ViewSelector.SelectedItem = MonitorSelectorItem;
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
    public event EventHandler? ProfileNewRequested;
    public event EventHandler? ProfileRenameRequested;
    public event EventHandler? ProfileDeleteRequested;
    public event EventHandler? ProfileApplyRequested;
    public event EventHandler? ControlLinesChangedRequested;
    public event EventHandler? ClearReceiveRequested;
    public event EventHandler? ClearTransmitRequested;
    public event EventHandler? BreakRequested;

    public void BindRows(ObservableCollection<TrafficRow> rows)
    {
        sourceRows = rows;
        TrafficListView.ItemsSource = visibleRows;
        SendHistory.ItemsSource = sendHistory;
        RefreshTrafficFilter();
    }

    public IReadOnlyList<TrafficRow> VisibleRows => visibleRows;

    public void RefreshTrafficFilter()
    {
        var query = TrafficSearch?.Text.Trim();
        var direction = TrafficDirection?.SelectedIndex ?? 0;
        visibleRows.Clear();
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

                visibleRows.Add(row);
            }
        }

        var hasSourceRows = sourceRows?.Count > 0;
        var empty = visibleRows.Count == 0;
        MonitorEmptyTitle.Text = hasSourceRows ? "没有匹配的报文" : "等待串口数据";
        MonitorEmptyDescription.Text = hasSourceRows ? "调整方向或搜索条件以查看其他报文。" : "连接设备后，收发报文会显示在这里。";
        MonitorEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CopyHexButton.IsEnabled = !empty;
    }

    public IReadOnlyList<SerialProfile> Profiles => profiles;

    public SerialProfile? SelectedProfile => ProfileComboBox.SelectedItem as SerialProfile;

    public SerialPortDescriptor? SelectedPort => PortComboBox.SelectedItem as SerialPortDescriptor;
    public double BaudRate =>
        int.TryParse(BaudRateComboBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate) && rate > 0
            ? rate
            : 115200;
    public int DataBits => (int)DataBitsNumberBox.Value;
    public SerialParity Parity => (SerialParity)ParityComboBox.SelectedIndex;
    public SerialStopBits StopBits => (SerialStopBits)StopBitsComboBox.SelectedIndex;
    public SerialHandshake Handshake => (SerialHandshake)HandshakeComboBox.SelectedIndex;
    public bool DtrEnable => DtrCheckBox.IsChecked == true;
    public bool RtsEnable => RtsCheckBox.IsChecked == true;
    public int MonitorFormatIndex => MonitorFormat.SelectedIndex;
    public Encoding SelectedEncoding => EncodingComboBox.SelectedIndex switch
    {
        1 => Encoding.ASCII,
        2 => Encoding.GetEncoding("GB2312"),
        3 => Encoding.GetEncoding("GBK"),
        4 => Encoding.Unicode,
        _ => Encoding.UTF8,
    };
    public bool IsPaused => PauseButton.Content?.ToString() == "继续";
    public bool ShowTimestamp => TimestampToggle.IsChecked == true;

    public void ApplySerialPreference(SerialPreference preference)
    {
        BaudRateComboBox.Text = preference.BaudRate.ToString(CultureInfo.InvariantCulture);
        DataBitsNumberBox.Value = preference.DataBits;
        ParityComboBox.SelectedIndex = (int)preference.Parity;
        StopBitsComboBox.SelectedIndex = (int)preference.StopBits;
        HandshakeComboBox.SelectedIndex = (int)preference.Handshake;
        EncodingComboBox.SelectedIndex = preference.EncodingName.ToLowerInvariant() switch
        {
            "us-ascii" => 1,
            "gb2312" => 2,
            "gbk" => 3,
            "utf-16" or "unicode" => 4,
            _ => 0,
        };
        DtrCheckBox.IsChecked = preference.DtrEnable;
        RtsCheckBox.IsChecked = preference.RtsEnable;
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
        BaudRateComboBox.Text = profile.BaudRate.ToString(CultureInfo.InvariantCulture);
        DataBitsNumberBox.Value = profile.DataBits;
        ParityComboBox.SelectedIndex = (int)profile.Parity;
        StopBitsComboBox.SelectedIndex = (int)profile.StopBits;
        HandshakeComboBox.SelectedIndex = (int)profile.Handshake;
        EncodingComboBox.SelectedIndex = profile.EncodingName.ToLowerInvariant() switch
        {
            "us-ascii" => 1,
            "gb2312" => 2,
            "gbk" => 3,
            "utf-16" or "unicode" => 4,
            _ => 0,
        };
        DtrCheckBox.IsChecked = profile.DtrEnable;
        RtsCheckBox.IsChecked = profile.RtsEnable;
        if (profile.PortName is not null && PortComboBox.ItemsSource is IReadOnlyList<SerialPortDescriptor> ports)
        {
            PortComboBox.SelectedItem = ports.FirstOrDefault(item => item.PortName.Equals(profile.PortName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public SerialProfile ReadSerialProfile(string name) => new(
        name,
        SelectedPort?.PortName,
        checked((int)BaudRate),
        DataBits,
        Parity,
        StopBits,
        Handshake,
        SelectedEncoding.WebName,
        DtrEnable,
        RtsEnable);

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
        checked((int)BaudRate),
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
        PlotSampleType.SelectedIndex);

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

    private void NewProfileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ProfileNewRequested?.Invoke(this, EventArgs.Empty);

    private void RenameProfileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ProfileRenameRequested?.Invoke(this, EventArgs.Empty);

    private void DeleteProfileButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ProfileDeleteRequested?.Invoke(this, EventArgs.Empty);
    private void ControlLineCheckBox_Changed(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ControlLinesChangedRequested?.Invoke(this, EventArgs.Empty);
    private void ClearReceiveButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearReceiveRequested?.Invoke(this, EventArgs.Empty);
    private void ClearTransmitButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearTransmitRequested?.Invoke(this, EventArgs.Empty);
    private void BreakButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => BreakRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateProfileActions()
    {
        var selected = SelectedProfile is not null;
        ApplyProfileButton.IsEnabled = selected;
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
    public void SetControlActionsEnabled(bool enabled)
    {
        DtrCheckBox.IsEnabled = enabled;
        RtsCheckBox.IsEnabled = enabled;
        ClearReceiveButton.IsEnabled = enabled;
        ClearTransmitButton.IsEnabled = enabled;
        BreakButton.IsEnabled = enabled;
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
        if (PlotFrameLength is not null)
        {
            PlotFrameLength.IsEnabled = binary;
        }

        if (PlotSampleType is not null)
        {
            PlotSampleType.IsEnabled = binary;
        }
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
