using Microsoft.UI.Xaml.Controls;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class SendPage : Page
{
    public event EventHandler? SendRequested;

    public SendPage() => InitializeComponent();

    private void SendButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SendRequested?.Invoke(this, EventArgs.Empty);

    public string Text => Editor.Text;

    public int FormatIndex => Format.SelectedIndex;

    public int LineEndingIndex => LineEnding.SelectedIndex;
}
