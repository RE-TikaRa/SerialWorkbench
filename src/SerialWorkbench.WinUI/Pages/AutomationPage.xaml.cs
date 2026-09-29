using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Domain;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class AutomationPage : Page
{
    public AutomationPage()
    {
        InitializeComponent();
        SequenceJson.Text = SerialSequenceCodec.Serialize(new SerialSequenceDefinition("新建序列", [new SerialSequenceStep([], "hex", 0, 1, 0)]));
    }

    public event EventHandler? RunRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;

    public new string Name => SequenceName.Text.Trim();
    public string DefinitionText => SequenceJson.Text;
    public void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
    }
    public void ShowResult(string message, InfoBarSeverity severity)
    {
        Result.Title = severity == InfoBarSeverity.Success ? "序列完成" : "自动化序列";
        Result.Message = message;
        Result.Severity = severity;
        Result.IsOpen = true;
    }
    public SerialSequenceDefinition Parse() => SerialSequenceCodec.Deserialize(DefinitionText);
    private void RunButton_Click(object sender, RoutedEventArgs e) => RunRequested?.Invoke(this, EventArgs.Empty);
    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);
    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
