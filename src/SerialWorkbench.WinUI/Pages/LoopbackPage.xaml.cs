using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace SerialWorkbench.WinUI.Pages;

public sealed partial class LoopbackPage : Page
{
    private const double CompactLayoutWidth = 360;
    private const double SplitLayoutWidth = 960;
    private const double WideLayoutWidth = 1200;

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
    private void LoopbackPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var state = e.NewSize.Width switch
        {
            < CompactLayoutWidth => "Compact",
            < WideLayoutWidth => "Narrow",
            _ => "Wide",
        };
        VisualStateManager.GoToState(this, state, false);
        VisualStateManager.GoToState(this, e.NewSize.Width < SplitLayoutWidth ? "StackedResults" : "SplitResults", false);
        VisualStateManager.GoToState(this, e.NewSize.Width < 641 ? "CompactPageMargins" : "StandardPageMargins", false);
    }
    private void RunButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => RunRequested?.Invoke(this, EventArgs.Empty);
}
