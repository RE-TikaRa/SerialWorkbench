using System.Text.Json;
using System.Xml.Linq;

namespace SerialWorkbench.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void WinUiOnlyReferencesTheClientContractAndUiPackages()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", "SerialWorkbench.WinUI", "SerialWorkbench.WinUI.csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .ToArray();
        var packages = project.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .ToArray();

        Assert.DoesNotContain(references, value => value.Contains("Serial.Windows", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, value => value.Contains("Sessions", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, value => value.Contains("Storage", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["CommunityToolkit.Mvvm", "Microsoft.WindowsAppSDK", "ScottPlot.WinUI"], packages);
    }

    [Fact]
    public void GeneralInterfaceUsesNativeWinUiControls()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xamlFiles = Directory.GetFiles(Path.Combine(repositoryRoot, "src", "SerialWorkbench.WinUI"), "*.xaml", SearchOption.AllDirectories);
        var xaml = string.Join(Environment.NewLine, xamlFiles.Select(File.ReadAllText));

        Assert.DoesNotContain("<Canvas", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Win2D", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Direct2D", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<NavigationView", xaml, StringComparison.Ordinal);
        Assert.Contains("<TabView", xaml, StringComparison.Ordinal);
        Assert.Contains("<ListView", xaml, StringComparison.Ordinal);
        Assert.Contains("<NumberBox", xaml, StringComparison.Ordinal);
        Assert.Contains("<InfoBar", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetAndCliSchemasAreValidJsonSchemaDocuments()
    {
        var repositoryRoot = FindRepositoryRoot();
        var schemaFiles = Directory.GetFiles(Path.Combine(repositoryRoot, "schemas"), "*.schema.json", SearchOption.TopDirectoryOnly);

        Assert.Equal(9, schemaFiles.Length);
        foreach (var schemaFile in schemaFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(schemaFile));
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
            Assert.True(document.RootElement.TryGetProperty("$id", out _));
            Assert.True(document.RootElement.TryGetProperty("title", out _));
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SerialWorkbench.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("SerialWorkbench repository root was not found.");
    }
}
