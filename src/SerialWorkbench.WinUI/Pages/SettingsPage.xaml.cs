using Microsoft.UI.Xaml.Controls;
namespace SerialWorkbench.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();
    public event EventHandler? ChooseWorkspaceRequested;
    private void ChooseWorkspaceButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ChooseWorkspaceRequested?.Invoke(this, EventArgs.Empty);
    public void SetWorkspacePath(string value) => WorkspacePath.Text = value;
}
