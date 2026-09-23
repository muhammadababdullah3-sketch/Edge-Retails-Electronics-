param(
    [string]$Root = ".",
    [Parameter(Mandatory=$true)][string]$FingerprintPath,
    [Parameter(Mandatory=$true)][string]$EvidencePath,
    [switch]$RequirePostgresClosure
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootPath = (Resolve-Path $Root).Path
$fingerprintFull = [IO.Path]::GetFullPath($FingerprintPath)
$evidenceFull = [IO.Path]::GetFullPath($EvidencePath)
if (-not (Test-Path $evidenceFull -PathType Leaf)) {
    throw "Phase 4 preflight evidence is missing: $evidenceFull"
}

& "$PSScriptRoot/Test-Sprint8WorkspaceFingerprint.ps1" `
    -Root $rootPath -FingerprintPath $fingerprintFull

try {
    $fingerprint = Get-Content $fingerprintFull -Raw | ConvertFrom-Json
    $evidence = Get-Content $evidenceFull -Raw | ConvertFrom-Json
}
catch {
    throw "Phase 4 preflight evidence could not be parsed: $($_.Exception.Message)"
}

$failures = @()
if ($evidence.schemaVersion -ne 1) { $failures += "Unsupported preflight schema." }
if ($evidence.sprint -ne 8) { $failures += "Sprint must equal 8." }
if ($evidence.gate -ne "Phase4Preflight") { $failures += "Evidence gate is not Phase4Preflight." }
if ($evidence.status -ne "PASS") { $failures += "Preflight status is not PASS." }
if ($evidence.workspaceFingerprint -ne $fingerprint.aggregateSha256) {
    $failures += "Preflight is not bound to the current workspace fingerprint."
}
foreach ($name in @(
    "efModelSync","phase1Static","phase2Static","phase3Static",
    "installerDataPreservation","architectureAudit","postgresClientReadiness"
)) {
    if ($evidence.$name -ne "PASS") { $failures += "$name is not PASS." }
}
foreach ($name in @("debugBuild","releaseBuild")) {
    if ($evidence.$name.status -ne "PASS" -or
        $evidence.$name.warnings -ne 0 -or
        $evidence.$name.errors -ne 0) {
        $failures += "$name is not clean."
    }
}
foreach ($name in @("unitDebug","unitRelease")) {
    if ($evidence.$name.status -ne "PASS" -or
        $evidence.$name.failed -ne 0 -or
        $evidence.$name.passed -le 0) {
        $failures += "$name is not PASS."
    }
}
if ($RequirePostgresClosure -and $evidence.postgresClosure -ne "PASS") {
    $failures += "Fresh PostgreSQL closure evidence is required."
}
if ($RequirePostgresClosure) {
    foreach ($name in @("backup","restore","cutover")) {
        if ($evidence.$name -ne "PASS") {
            $failures += "$name is not PASS under fresh PostgreSQL closure."
        }
    }
}
foreach ($name in @(
    "rollbackSafety","discardSafety","maintenanceBarrier",
    "businessWriteBarrier","licenseRuntime"
)) {
    if ($evidence.$name -ne "PASS") {
        $failures += "$name is not PASS."
    }
}
if ($evidence.productionScreenCount -ne 17) {
    $failures += "Production screen count must remain exactly 17."
}
if ($evidence.gitOperationsPerformed -ne $false) {
    $failures += "Preflight reports Git operations were performed."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Sprint 8 Phase 4 preflight evidence validation failed."
}

Write-Host "Sprint 8 Phase 4 preflight evidence PASS"
Write-Host "Workspace fingerprint: $($evidence.workspaceFingerprint)"
Write-Host "Debug unit tests: $($evidence.unitDebug.passed)/$($evidence.unitDebug.total)"
Write-Host "Release unit tests: $($evidence.unitRelease.passed)/$($evidence.unitRelease.total)"
