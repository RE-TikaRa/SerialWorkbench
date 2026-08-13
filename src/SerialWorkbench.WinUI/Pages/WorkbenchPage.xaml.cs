using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using ScottPlot;
using SerialWorkbench.Domain;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class WorkbenchPage : Page
{
    private const int MaxWavePoints = 2000;
    private readonly WaveformParser waveformParser = new();
    private readonly List<List<double>> channelData = [];
    private readonly List<IPlottable> channelPlots = [];

    public WorkbenchPage() => InitializeComponent();

    public event EventHandler? ConnectRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler? SendRequested;

    public void BindRows(ObservableCollection<TrafficRow> rows) => TrafficListView.ItemsSource = rows;

    public SerialPortDescriptor? SelectedPort => PortComboBox.SelectedItem as SerialPortDescriptor;
    public double BaudRate => BaudRateNumberBox.Value;
    public int DataBits => (int)DataBitsNumberBox.Value;
    public SerialParity Parity => (SerialParity)ParityComboBox.SelectedIndex;
    public SerialStopBits StopBits => (SerialStopBits)StopBitsComboBox.SelectedIndex;
    public SerialHandshake Handshake => (SerialHandshake)HandshakeComboBox.SelectedIndex;
    public bool DtrEnable => DtrCheckBox.IsChecked == true;
    public bool RtsEnable => RtsCheckBox.IsChecked == true;
    public int MonitorFormatIndex => MonitorFormat.SelectedIndex;
    public bool IsPaused => PauseButton.Content?.ToString() == "继续";

    private void ConnectButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ConnectRequested?.Invoke(this, EventArgs.Empty);
    private void PauseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => PauseRequested?.Invoke(this, EventArgs.Empty);
    private void ClearButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);
    private void SendButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SendRequested?.Invoke(this, EventArgs.Empty);

    public string SendText => SendEditor.Text;
    public int SendFormatIndex => SendFormat.SelectedIndex;
    public int SendLineEndingIndex => SendLineEnding.SelectedIndex;
    public int SendChecksumIndex => SendChecksum.SelectedIndex;
    public void SetConnectionBusy(bool busy) => ConnectButton.IsEnabled = !busy;
    public void SetSending(bool sending) => SendButton.IsEnabled = !sending;
    public void ShowSendResult(string message, InfoBarSeverity severity)
    {
        SendStatus.Title = severity == InfoBarSeverity.Success ? "发送完成" : "发送";
        SendStatus.Message = message;
        SendStatus.Severity = severity;
        SendStatus.IsOpen = true;
    }

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
        channelPlots.Clear();
        for (var channel = 0; channel < channelData.Count; channel++)
        {
            var scatter = WavePlot.Plot.Add.Signal(channelData[channel].ToArray());
            scatter.LegendText = $"CH{channel}";
            channelPlots.Add(scatter);
        }

        WavePlot.Plot.Axes.AutoScale();
        WavePlot.Refresh();
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

    private void PlotClearButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        channelData.Clear();
        channelPlots.Clear();
        waveformParser.Reset();
        WavePlot.Plot.Clear();
        WavePlot.Refresh();
    }
}
