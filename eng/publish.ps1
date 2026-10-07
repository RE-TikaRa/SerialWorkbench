[CmdletBinding()]
param(
    [ValidateSet("full", "lite")]
    [string]$Profile = "full",
    [string]$OutputDirectory,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot "publish\win-x64"
}

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot "test.ps1")
}

$componentRoot = Reset-RepositoryBuildDirectory (Join-Path $RepositoryRoot "artifacts\publish-components\win-x64")
$outputRoot = Resolve-RepositoryBuildPath $OutputDirectory
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$components = @(
    @{ Name = "host"; Project = "src\SerialWorkbench.Host\SerialWorkbench.Host.csproj" },
    @{ Name = "cli"; Project = "src\SerialWorkbench.Cli\SerialWorkbench.Cli.csproj" },
    @{ Name = "tui"; Project = "src\SerialWorkbench.Tui\SerialWorkbench.Tui.csproj" }
)
if ($Profile -eq "full") {
    $components += @{ Name = "winui"; Project = "src\SerialWorkbench.WinUI\SerialWorkbench.WinUI.csproj" }
}

Push-Location $RepositoryRoot
try {
    foreach ($component in $components) {
        $componentDirectory = Join-Path $componentRoot $component.Name
        New-Item -ItemType Directory -Path $componentDirectory | Out-Null
        Invoke-RepositoryDotNet @(
            "publish",
            $component.Project,
            "--configuration",
            "Release",
            "--runtime",
            "win-x64",
            "--self-contained",
            "true",
            "--output",
            $componentDirectory
        )

        Get-ChildItem -LiteralPath $componentDirectory -Force | Copy-Item -Destination $outputRoot -Recurse -Force
    }

    Copy-Item -LiteralPath (Join-Path $RepositoryRoot "LICENSE") -Destination $outputRoot
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot "README.md") -Destination $outputRoot
    $schemaDirectory = Join-Path $outputRoot "schemas"
    New-Item -ItemType Directory -Path $schemaDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot "schemas") -Force | Copy-Item -Destination $schemaDirectory -Recurse -Force

    $requiredFiles = @(
        "SW_HOST.exe",
        "SW_CLI.exe",
        "SW_TUI.exe",
        "LICENSE",
        "README.md"
    )
    if ($Profile -eq "full") {
        $requiredFiles += @(
            "SW.exe",
            "SW.pri",
            "App.xbf",
            "MainWindow.xbf",
            "Microsoft.ui.xaml.dll",
            "Microsoft.WindowsAppRuntime.dll",
            "Microsoft.WindowsAppRuntime.pri"
        )
    }

    $missing = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $outputRoot $_) -PathType Leaf) })
    if ($missing.Count -ne 0) {
        throw "Portable publish is missing: $($missing -join ', ')."
    }

    Write-Host "Portable release ($Profile): $outputRoot"
}
finally {
    Pop-Location
}
