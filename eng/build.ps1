$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

$configuration = $env:Configuration
$env:Configuration = "Release"
Push-Location $RepositoryRoot
try {
    Invoke-RepositoryDotNet @("format", "SerialWorkbench.slnx")
    Invoke-RepositoryDotNet @("build", "SerialWorkbench.slnx", "--configuration", "Release")
}
finally {
    Pop-Location
    $env:Configuration = $configuration
}
