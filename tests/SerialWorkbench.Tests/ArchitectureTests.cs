using System.Xml.Linq;

namespace SerialWorkbench.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void WinUiOnlyReferencesTheClientContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", "SerialWorkbench.WinUI", "SerialWorkbench.WinUI.csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .ToArray();
        Assert.DoesNotContain(references, value => value.Contains("Serial.Windows", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, value => value.Contains("Sessions", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, value => value.Contains("Storage", StringComparison.OrdinalIgnoreCase));
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
