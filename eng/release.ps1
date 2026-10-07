[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [ValidatePattern("^[A-Za-z0-9._-]+$")]
    [string]$Version = "local"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot "artifacts\release"
}

$outputRoot = Reset-RepositoryBuildDirectory $OutputDirectory
$fullDirectory = Join-Path $outputRoot "full"
$liteDirectory = Join-Path $outputRoot "lite"

& (Join-Path $PSScriptRoot "publish.ps1") -Profile full -OutputDirectory $fullDirectory
& (Join-Path $PSScriptRoot "publish.ps1") -Profile lite -OutputDirectory $liteDirectory -SkipTests

$packages = @(
    @{ Name = "SerialWorkbench-$Version-win-x64-full.zip"; Directory = $fullDirectory },
    @{ Name = "SerialWorkbench-$Version-win-x64-lite.zip"; Directory = $liteDirectory }
)

foreach ($package in $packages) {
    $archivePath = Join-Path $outputRoot $package.Name
    Compress-Archive -Path (Join-Path $package.Directory "*") -DestinationPath $archivePath -CompressionLevel Optimal
    $hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    "$($hash.Hash)  $($package.Name)" | Set-Content -LiteralPath "$archivePath.sha256" -Encoding ascii
}

Remove-Item -LiteralPath $fullDirectory, $liteDirectory -Recurse -Force
Write-Host "Release packages: $outputRoot"
