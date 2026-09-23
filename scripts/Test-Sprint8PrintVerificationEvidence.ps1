param(
    [string]$Root = ".",
    [Parameter(Mandatory=$true)][string]$FingerprintPath,
    [Parameter(Mandatory=$true)][string]$PreflightEvidencePath,
    [Parameter(Mandatory=$true)][string]$ReleaseManifestPath,
    [Parameter(Mandatory=$true)][string]$ExePath,
    [Parameter(Mandatory=$true)][string]$SetupPath,
    [Parameter(Mandatory=$true)][string]$EvidencePath,
    [switch]$RequireSoftwarePass,
    [switch]$RequirePhysicalPass
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootPath = (Resolve-Path $Root).Path
& "$PSScriptRoot/Test-Sprint8WorkspaceFingerprint.ps1" `
    -Root $rootPath -FingerprintPath $FingerprintPath
& "$PSScriptRoot/Test-Sprint8Phase4PreflightEvidence.ps1" `
    -Root $rootPath -FingerprintPath $FingerprintPath `
    -EvidencePath $PreflightEvidencePath -RequirePostgresClosure

foreach ($path in @($ReleaseManifestPath,$ExePath,$SetupPath,$EvidencePath)) {
    if (-not (Test-Path $path -PathType Leaf)) { throw "Required evidence/artifact is missing: $path" }
}

$fingerprint = Get-Content ([IO.Path]::GetFullPath($FingerprintPath)) -Raw | ConvertFrom-Json
$evidence = Get-Content ([IO.Path]::GetFullPath($EvidencePath)) -Raw | ConvertFrom-Json
$failures = @()

if ($evidence.schemaVersion -ne 1 -or $evidence.sprint -ne 8 -or $evidence.gate -ne "PrintVerification") {
    $failures += "Print verification evidence identity/schema is invalid."
}
if ($evidence.workspaceFingerprint -ne $fingerprint.aggregateSha256) {
    $failures += "Print evidence is not bound to the current workspace fingerprint."
}
if ($evidence.preflightEvidenceSha256 -ne (Get-FileHash ([IO.Path]::GetFullPath($PreflightEvidencePath)) -Algorithm SHA256).Hash) {
    $failures += "Print evidence is not bound to the current preflight evidence."
}
if ($evidence.releaseManifestSha256 -ne (Get-FileHash ([IO.Path]::GetFullPath($ReleaseManifestPath)) -Algorithm SHA256).Hash) {
    $failures += "Release manifest hash changed after print evidence generation."
}
if ($evidence.desktopExeSha256 -ne (Get-FileHash ([IO.Path]::GetFullPath($ExePath)) -Algorithm SHA256).Hash) {
    $failures += "Desktop EXE hash changed after print evidence generation."
}
if ($evidence.setupExeSha256 -ne (Get-FileHash ([IO.Path]::GetFullPath($SetupPath)) -Algorithm SHA256).Hash) {
    $failures += "Setup EXE hash changed after print evidence generation."
}

$allowed = @("PASS","FAIL","BLOCKED_EXTERNAL","NOT_RUN")
foreach ($name in @("printingA4","printing58mmPhysical","printing80mmPhysical")) {
    if ($evidence.$name -notin $allowed) { $failures += "$name has an invalid status." }
}
if ($evidence.printingSoftware -notin @("PASS","FAIL","NOT_RUN")) {
    $failures += "printingSoftware has an invalid status."
}
if ($RequireSoftwarePass -and ($evidence.printingSoftware -ne "PASS" -or $evidence.printingA4 -ne "PASS")) {
    $failures += "Software/A4 printing certification is not PASS."
}
if ($RequirePhysicalPass -and
    ($evidence.printing58mmPhysical -ne "PASS" -or $evidence.printing80mmPhysical -ne "PASS")) {
    $failures += "Mandatory physical 58mm/80mm printing is not PASS."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Sprint 8 print verification evidence validation failed."
}
Write-Host "Sprint 8 print verification evidence PASS"
Write-Host "58mm: $($evidence.printing58mmPhysical); 80mm: $($evidence.printing80mmPhysical); A4: $($evidence.printingA4)"
