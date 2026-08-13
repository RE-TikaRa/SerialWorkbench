Set-StrictMode -Version 3.0

$RepositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-RepositoryDotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Resolve-RepositoryBuildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $publishRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot "publish"))
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot "artifacts"))
    $separator = [System.IO.Path]::DirectorySeparatorChar
    $isPublishPath = $resolved.Equals($publishRoot, [StringComparison]::OrdinalIgnoreCase) -or $resolved.StartsWith($publishRoot + $separator, [StringComparison]::OrdinalIgnoreCase)
    $isArtifactsPath = $resolved.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -or $resolved.StartsWith($artifactsRoot + $separator, [StringComparison]::OrdinalIgnoreCase)
    if (-not ($isPublishPath -or $isArtifactsPath)) {
        throw "Build output must stay inside $publishRoot or $artifactsRoot."
    }

    return $resolved
}

function Reset-RepositoryBuildDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolved = Resolve-RepositoryBuildPath $Path
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolved | Out-Null
    return $resolved
}
