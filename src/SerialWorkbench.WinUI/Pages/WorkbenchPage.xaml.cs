using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Domain;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class WorkbenchPage : Page
{
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
    public void SetConnectionBusy(bool busy) => ConnectButton.IsEnabled = !busy;
    public void SetSending(bool sending) => SendButton.IsEnabled = !sending;
    public void ShowSendResult(string message, InfoBarSeverity severity)
    {
        SendStatus.Title = severity == InfoBarSeverity.Success ? "发送完成" : "发送";
        SendStatus.Message = message;
        SendStatus.Severity = severity;
        SendStatus.IsOpen = true;
    }
}
