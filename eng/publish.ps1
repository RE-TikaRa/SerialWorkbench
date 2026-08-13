[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot "publish\win-x64"
}

& (Join-Path $PSScriptRoot "test.ps1")

$componentRoot = Reset-RepositoryBuildDirectory (Join-Path $RepositoryRoot "artifacts\publish-components\win-x64")
$outputRoot = Reset-RepositoryBuildDirectory $OutputDirectory
$components = @(
    @{ Name = "host"; Project = "src\SerialWorkbench.Host\SerialWorkbench.Host.csproj" },
    @{ Name = "protocol-host"; Project = "src\SerialWorkbench.ProtocolHost\SerialWorkbench.ProtocolHost.csproj" },
    @{ Name = "cli"; Project = "src\SerialWorkbench.Cli\SerialWorkbench.Cli.csproj" },
    @{ Name = "winui"; Project = "src\SerialWorkbench.WinUI\SerialWorkbench.WinUI.csproj" }
)

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
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot "schemas") -Destination $outputRoot -Recurse

    $requiredFiles = @(
        "SerialWorkbench.exe",
        "SerialWorkbench.Host.exe",
        "SerialWorkbench.ProtocolHost.exe",
        "serial-workbench.exe",
        "SerialWorkbench.pri",
        "App.xbf",
        "MainWindow.xbf",
        "Microsoft.ui.xaml.dll",
        "Microsoft.WindowsAppRuntime.dll",
        "Microsoft.WindowsAppRuntime.pri",
        "LICENSE",
        "README.md"
    )

    $missing = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $outputRoot $_) -PathType Leaf) })
    if ($missing.Count -ne 0) {
        throw "Portable publish is missing: $($missing -join ', ')."
    }

    Write-Host "Portable release: $outputRoot"
}
finally {
    Pop-Location
}
