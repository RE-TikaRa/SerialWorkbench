namespace SerialWorkbench.Storage;

public sealed class ApplicationPaths
{
    public ApplicationPaths(string applicationRoot, string? workspaceRoot = null)
    {
        ApplicationRoot = NormalizeDirectory(applicationRoot);
        DataRoot = Path.Combine(ApplicationRoot, "data");
        WorkspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? null : NormalizeDirectory(workspaceRoot);
    }

    public string ApplicationRoot { get; }

    public string DataRoot { get; }

    public string? WorkspaceRoot { get; }

    public string SettingsRoot => Path.Combine(DataRoot, "settings");

    public string LibraryRoot => Path.Combine(DataRoot, "library");

    public string ExtensionsRoot => Path.Combine(DataRoot, "extensions");

    public string LogsRoot => Path.Combine(DataRoot, "logs");

    public string CacheRoot => Path.Combine(DataRoot, "cache");

    public string SessionsRoot => Path.Combine(WorkspaceRoot ?? DataRoot, "sessions");

    public string ReportsRoot => Path.Combine(WorkspaceRoot ?? DataRoot, "reports");

    public void EnsureWritable()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(SettingsRoot);
        Directory.CreateDirectory(LibraryRoot);
        Directory.CreateDirectory(ExtensionsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(SessionsRoot);
        Directory.CreateDirectory(ReportsRoot);

        var probe = Path.Combine(DataRoot, $".write-{Guid.NewGuid():N}.tmp");
        using (File.Create(probe, 1, FileOptions.DeleteOnClose))
        {
        }
    }

    public ApplicationPaths WithWorkspace(string? workspaceRoot) => new(ApplicationRoot, workspaceRoot);

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
