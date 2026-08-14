using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialWorkbench.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    private bool suppressThemeChange;

    public SettingsPage()
    {
        InitializeComponent();
        var assembly = typeof(SettingsPage).Assembly;
        AppVersion.Text = assembly.GetName().Version?.ToString() ?? "未知";
        DotnetVersion.Text = RuntimeInformation.FrameworkDescription;
        WindowsAppRuntimeVersion.Text = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "WindowsAppSDKVersion").Value;
        SystemVersion.Text = $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}";
        ApplicationPath.Text = AppContext.BaseDirectory;
    }

    public event EventHandler? ChooseWorkspaceRequested;
    public event EventHandler? ClearWorkspaceRequested;

    public event EventHandler<ElementTheme>? ThemeChangeRequested;

    private void SettingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        VisualStateManager.GoToState(this, e.NewSize.Width < 960 ? "StackedSettings" : "WideSettings", false);
        VisualStateManager.GoToState(this, e.NewSize.Width < 641 ? "CompactPageMargins" : "StandardPageMargins", false);
    }

    private void ChooseWorkspaceButton_Click(object sender, RoutedEventArgs e) => ChooseWorkspaceRequested?.Invoke(this, EventArgs.Empty);
    private void ClearWorkspaceButton_Click(object sender, RoutedEventArgs e) => ClearWorkspaceRequested?.Invoke(this, EventArgs.Empty);

    public void SetWorkspace(string path, bool selected)
    {
        WorkspacePath.Text = path;
        ClearWorkspaceButton.IsEnabled = selected;
    }

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
