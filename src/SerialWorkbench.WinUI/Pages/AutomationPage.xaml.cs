using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialWorkbench.Domain;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class AutomationPage : Page
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public AutomationPage()
    {
        InitializeComponent();
        SequenceJson.Text = JsonSerializer.Serialize(new SerialSequenceDefinition("新建序列", [new SerialSequenceStep([], "hex", 0, 1, 0)]), jsonOptions);
    }

    public event EventHandler? RunRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;

    public new string Name => SequenceName.Text.Trim();
    public string DefinitionText => SequenceJson.Text;
    public void SetRunning(bool running) { RunButton.IsEnabled = !running; }
    public void ShowResult(string message, InfoBarSeverity severity) { Result.Title = severity == InfoBarSeverity.Success ? "序列完成" : "自动化序列"; Result.Message = message; Result.Severity = severity; Result.IsOpen = true; }
    public SerialSequenceDefinition Parse() => JsonSerializer.Deserialize<SerialSequenceDefinition>(DefinitionText, jsonOptions) ?? throw new InvalidDataException("序列定义为空。");
    private void RunButton_Click(object sender, RoutedEventArgs e) => RunRequested?.Invoke(this, EventArgs.Empty);
    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);
    private void CancelButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
