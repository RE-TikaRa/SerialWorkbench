using System.Text.Json;
using Microsoft.UI.Xaml;

namespace SerialWorkbench.WinUI;

public static class ThemePreference
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "data", "settings", "ui-preferences.json");

    public static ElementTheme Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return ElementTheme.Default;
            }

            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath));
            return document?.Theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return ElementTheme.Default;
        }
    }

    public static void Save(ElementTheme theme)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Document(theme.ToString())));
    }

    private sealed record Document(string Theme);
}
