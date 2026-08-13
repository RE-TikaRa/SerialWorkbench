$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

& (Join-Path $PSScriptRoot "build.ps1")

Push-Location $RepositoryRoot
try {
    Invoke-RepositoryDotNet @("test", "SerialWorkbench.slnx", "--configuration", "Release", "--no-build")
}
finally {
    Pop-Location
}
