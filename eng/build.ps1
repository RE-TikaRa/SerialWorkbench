$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

$configuration = $env:Configuration
$env:Configuration = "Release"
Push-Location $RepositoryRoot
try {
    Invoke-RepositoryDotNet @("format", "SerialWorkbench.slnx")
    Invoke-RepositoryDotNet @("build", "SerialWorkbench.slnx", "--configuration", "Release")
    Invoke-RepositoryDotNet @("src/SerialWorkbench.Cli/bin/Release/net10.0-windows10.0.26100.0/serial-workbench.dll", "schemas", "export", "--path", "schemas", "--output", "json")
}
finally {
    Pop-Location
    $env:Configuration = $configuration
}
