using Microsoft.UI.Xaml.Controls;
namespace SerialWorkbench.WinUI.Pages;

public sealed partial class LoopbackPage : Page
{
    public LoopbackPage() => InitializeComponent();
    public event EventHandler? RunRequested;
    public int LengthValue => checked((int)Length.Value);
    public int IterationsValue => checked((int)Iterations.Value);
    public int PatternIndex => Pattern.SelectedIndex;
    public void SetRunning(bool running) => RunButton.IsEnabled = !running;
    public void ShowResult(string message, InfoBarSeverity severity)
    {
        Result.Title = severity == InfoBarSeverity.Success ? "回环通过" : "回环检测";
        Result.Message = message;
        Result.Severity = severity;
        Result.IsOpen = true;
    }
    private void RunButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => RunRequested?.Invoke(this, EventArgs.Empty);
}
