$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

Push-Location $RepositoryRoot
try {
    Invoke-RepositoryDotNet @("format", "SerialWorkbench.slnx")
    Invoke-RepositoryDotNet @("build", "SerialWorkbench.slnx", "--configuration", "Release")
}
finally {
    Pop-Location
}
