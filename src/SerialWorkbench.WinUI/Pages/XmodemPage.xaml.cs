using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class XmodemPage : Page
{
    public XmodemPage() => InitializeComponent();
    public event EventHandler? SendRequested;
    public event EventHandler? ReceiveRequested;
    public event EventHandler? CancelRequested;
    public string SelectedPath => FilePath.Text;
    public void SetPath(string path) => FilePath.Text = path;
    public void ShowResult(string message, InfoBarSeverity severity) { Result.Title = severity == InfoBarSeverity.Success ? "传输完成" : "XMODEM"; Result.Message = message; Result.Severity = severity; Result.IsOpen = true; }
    public void SetProgress(string text) => ProgressText.Text = text;
    public void SetRunning(bool running)
    {
        SendButton.IsEnabled = !running;
        ReceiveButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
    }
    private void SendButton_Click(object sender, RoutedEventArgs e) => SendRequested?.Invoke(this, EventArgs.Empty);
    private void ReceiveButton_Click(object sender, RoutedEventArgs e) => ReceiveRequested?.Invoke(this, EventArgs.Empty);
    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
