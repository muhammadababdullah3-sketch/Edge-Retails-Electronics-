param(
    [string]$Root = ".",
    [string]$EvidencePath = "docs/Sprint7_Closure_Evidence.json"
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootPath = (Resolve-Path $Root).Path
$path = Join-Path $rootPath $EvidencePath
if (-not (Test-Path $path -PathType Leaf)) {
    throw "Sprint 7 structured closure evidence is missing: $path"
}

try {
    $raw = Get-Content $path -Raw
    $evidence = $raw | ConvertFrom-Json
}
catch {
    throw "Sprint 7 closure evidence is not valid JSON: $($_.Exception.Message)"
}

$failures = @()
if ($evidence.sprint -ne 7) { $failures += "Sprint must equal 7." }
if ($evidence.status -ne "CLOSED") { $failures += "Status must be CLOSED." }
if ($evidence.postgresqlGate -ne "PASS") { $failures += "Real PostgreSQL gate must be PASS." }
if ($evidence.debugBuild.warnings -ne 0 -or $evidence.debugBuild.errors -ne 0) {
    $failures += "Debug build evidence is not 0 warnings / 0 errors."
}
if ($evidence.releaseBuild.warnings -ne 0 -or $evidence.releaseBuild.errors -ne 0) {
    $failures += "Release build evidence is not 0 warnings / 0 errors."
}
if ($evidence.unitTests.failed -ne 0 -or
    $evidence.unitTests.debugPassed -le 0 -or
    $evidence.unitTests.releasePassed -le 0) {
    $failures += "Unit-test evidence is incomplete or failed."
}
if ($evidence.postgresIntegration.failed -ne 0 -or
    $evidence.postgresIntegration.debugPassed -le 0 -or
    $evidence.postgresIntegration.releasePassed -le 0) {
    $failures += "PostgreSQL integration evidence is incomplete or failed."
}
if ($evidence.efPendingModelChanges -ne $false) {
    $failures += "EF pending model changes must be false."
}
if (-not $evidence.migrationsApplied -or @($evidence.migrationsApplied).Count -lt 1) {
    $failures += "Applied migration evidence is missing."
}
if ($evidence.gitTouched -ne $false) {
    $failures += "GitTouched must be false."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Sprint 7 structured closure evidence validation failed."
}

$hash = (Get-FileHash $path -Algorithm SHA256).Hash
Write-Host "Sprint 7 closure evidence PASS"
Write-Host "Evidence SHA256: $hash"

[pscustomobject]@{
    Sprint = 7
    Status = "CLOSED"
    PostgreSql = "PASS"
    MigrationCompatibility = "PASS"
    EfPendingModelChanges = "NONE"
    DebugWarnings = 0
    DebugErrors = 0
    ReleaseWarnings = 0
    ReleaseErrors = 0
    UnitTests = "PASS"
    GitTouched = $false
    SourceEvidenceSha256 = $hash
}
