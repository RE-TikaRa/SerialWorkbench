using Microsoft.UI.Xaml.Controls;
namespace SerialWorkbench.WinUI.Pages;

public sealed partial class SessionsPage : Page
{
    public SessionsPage() => InitializeComponent();
    public void SetPaths(string workspacePath, string sessionPath)
    {
        WorkspacePath.Text = workspacePath;
        SessionPath.Text = sessionPath;
    }
}
