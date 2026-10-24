# Automated Candidate Export Verification for ER-RUN-CATALOG-EXEC-20261021-106
param(
    [string]$RepoRoot = "$PSScriptRoot\..\..\..\..\..\Point of Sale",
    [string]$ExportDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== CANDIDATE EXPORT VERIFICATION: ER-RUN-CATALOG-EXEC-20261021-106 ===" -ForegroundColor Cyan

$invPath = Join-Path $ExportDir "CANDIDATE_FILE_INVENTORY.json"
if (-not (Test-Path $invPath)) {
    Write-Error "CANDIDATE_FILE_INVENTORY.json missing at $invPath"
}

$inv = Get-Content $invPath -Raw | ConvertFrom-Json
Write-Host "Loaded inventory with $($inv.total_candidate_files) files."

function Get-FileSha256($path) {
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hash = $hasher.ComputeHash($bytes)
    return [System.BitConverter]::ToString($hash).Replace("-", "").ToLowerInvariant()
}

$bundlePath = Join-Path $ExportDir $inv.archives.candidate_source_bundle.file
$actualBundleHash = Get-FileSha256 $bundlePath
if ($actualBundleHash -ne $inv.archives.candidate_source_bundle.sha256) {
    Write-Error "Bundle hash mismatch: expected $($inv.archives.candidate_source_bundle.sha256), got $actualBundleHash"
}
Write-Host "[PASS] Source bundle SHA-256 matches: $actualBundleHash" -ForegroundColor Green

$patchPath = Join-Path $ExportDir $inv.archives.candidate_tracked_patch.file
$actualPatchHash = Get-FileSha256 $patchPath
if ($actualPatchHash -ne $inv.archives.candidate_tracked_patch.sha256) {
    Write-Error "Patch hash mismatch: expected $($inv.archives.candidate_tracked_patch.sha256), got $actualPatchHash"
}
Write-Host "[PASS] Tracked patch SHA-256 matches: $actualPatchHash" -ForegroundColor Green

$untrackedPath = Join-Path $ExportDir $inv.archives.candidate_untracked_zip.file
$actualUntrackedHash = Get-FileSha256 $untrackedPath
if ($actualUntrackedHash -ne $inv.archives.candidate_untracked_zip.sha256) {
    Write-Error "Untracked zip hash mismatch: expected $($inv.archives.candidate_untracked_zip.sha256), got $actualUntrackedHash"
}
Write-Host "[PASS] Untracked zip SHA-256 matches: $actualUntrackedHash" -ForegroundColor Green

Write-Host "=== CANDIDATE EXPORT VERIFICATION COMPLETED: 100% OK ===" -ForegroundColor Green
