using Microsoft.UI.Xaml;

namespace SerialWorkbench.WinUI;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        UnhandledException += App_UnhandledException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        try
        {
            var logsRoot = Path.Combine(AppContext.BaseDirectory, "data", "logs");
            Directory.CreateDirectory(logsRoot);
            File.AppendAllText(
                Path.Combine(logsRoot, "crash.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{args.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
