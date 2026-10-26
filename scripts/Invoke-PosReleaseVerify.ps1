[CmdletBinding()]
param(
    [switch]$FullIntegration
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & dotnet build EdgeRetails.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    & dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }

    & dotnet test tests/EdgeRetails.Desktop.PerformanceTests/EdgeRetails.Desktop.PerformanceTests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Desktop performance tests failed.' }

    if ($FullIntegration) {
        & (Join-Path $PSScriptRoot 'Invoke-CatalogPostgresRehearsal.ps1') -Port 55438 -RunFullIntegrationTests
        if ($LASTEXITCODE -ne 0) { throw 'Full PostgreSQL integration rehearsal failed.' }
    }

    Write-Host 'POS release verification passed (build + unit + desktop).'
    if (-not $FullIntegration) {
        Write-Host 'Optional: run with -FullIntegration for owned PG18 integration (requires free port 55438).'
    }
}
finally {
    Pop-Location
}
