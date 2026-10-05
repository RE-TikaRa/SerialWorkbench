using System.Text.Json;

namespace SerialWorkbench.Storage;

public sealed class WorkspacePreferenceStore(ApplicationPaths paths)
{
    private readonly string filePath = Path.Combine(paths.DataRoot, "settings", "workspace.json");

    public string? Load()
    {
        try
        {
            return File.Exists(filePath) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(filePath))?.Path : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string? path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new Document(path)), cancellationToken).ConfigureAwait(false);
    }

    private sealed record Document(string? Path);
}
