using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    private bool suppressThemeChange;

    public SettingsPage() => InitializeComponent();

    public event EventHandler? ChooseWorkspaceRequested;

    public event EventHandler<ElementTheme>? ThemeChangeRequested;

    private void ChooseWorkspaceButton_Click(object sender, RoutedEventArgs e) => ChooseWorkspaceRequested?.Invoke(this, EventArgs.Empty);

    public void SetWorkspacePath(string value) => WorkspacePath.Text = value;

    public void SetThemeSelection(ElementTheme theme)
    {
        suppressThemeChange = true;
        ThemeSelector.SelectedIndex = theme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        suppressThemeChange = false;
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressThemeChange)
        {
            return;
        }

        var theme = ThemeSelector.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ThemeChangeRequested?.Invoke(this, theme);
    }
}
