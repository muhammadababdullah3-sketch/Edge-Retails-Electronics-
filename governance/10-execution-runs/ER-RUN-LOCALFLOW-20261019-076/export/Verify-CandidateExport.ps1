# Automated Candidate Export Verification for ER-RUN-LOCALFLOW-20261019-076
param(
    [string]$RepoRoot = "$PSScriptRoot\..\..\..\..\..\Point of Sale",
    [string]$ExportDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== CANDIDATE EXPORT VERIFICATION: ER-RUN-LOCALFLOW-20261019-076 ===" -ForegroundColor Cyan

$invPath = Join-Path $ExportDir "CANDIDATE_FILE_INVENTORY.json"
if (-not (Test-Path $invPath)) {
    throw "Inventory file not found: $invPath"
}

$inv = Get-Content $invPath -Raw | ConvertFrom-Json
Write-Host "Verifying archive hashes from manifest..." -ForegroundColor Yellow

$patchPath = Join-Path $ExportDir "candidate_tracked.patch"
$untrackedPath = Join-Path $ExportDir "candidate_untracked.zip"
$bundlePath = Join-Path $ExportDir "candidate_source_bundle.zip"

$actualPatchSha = (Get-FileHash -Path $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
$actualUntrackedSha = (Get-FileHash -Path $untrackedPath -Algorithm SHA256).Hash.ToLowerInvariant()
$actualBundleSha = (Get-FileHash -Path $bundlePath -Algorithm SHA256).Hash.ToLowerInvariant()

if ($actualPatchSha -ne "b32887cfe1633f7366f7f6dd5f70ab0643bb3af9b4f34974df7122a699455a4f".ToLowerInvariant()) {
    throw "Tracked patch SHA mismatch! Expected: b32887cfe1633f7366f7f6dd5f70ab0643bb3af9b4f34974df7122a699455a4f, Actual: $actualPatchSha"
}
Write-Host "  [OK] candidate_tracked.patch SHA256 matches: $actualPatchSha" -ForegroundColor Green

if ($actualUntrackedSha -ne "fd6fe3726a0ec8877541c94cb2c0af9215114ef1d663b79c3c6cc3719ee80c7d".ToLowerInvariant()) {
    throw "Untracked zip SHA mismatch! Expected: fd6fe3726a0ec8877541c94cb2c0af9215114ef1d663b79c3c6cc3719ee80c7d, Actual: $actualUntrackedSha"
}
Write-Host "  [OK] candidate_untracked.zip SHA256 matches: $actualUntrackedSha" -ForegroundColor Green

if ($actualBundleSha -ne "85cf4e912f70d22e969dbcf804a539410e8b727987ba7d5ac9162f703d154ddf".ToLowerInvariant()) {
    throw "Source bundle SHA mismatch! Expected: 85cf4e912f70d22e969dbcf804a539410e8b727987ba7d5ac9162f703d154ddf, Actual: $actualBundleSha"
}
Write-Host "  [OK] candidate_source_bundle.zip SHA256 matches: $actualBundleSha" -ForegroundColor Green

Write-Host "Verifying 329 candidate files on disk..." -ForegroundColor Yellow
$checked = 0
$mismatches = @()

foreach ($entry in $inv.files) {
    $rel = $entry.path
    $expectedSha = $entry.sha256.ToLowerInvariant()
    $full = Join-Path $RepoRoot $rel
    if (-not (Test-Path $full)) {
        $mismatches += "MISSING: $rel"
        continue
    }
    $actualSha = (Get-FileHash -Path $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha -ne $expectedSha) {
        $mismatches += "HASH_MISMATCH: $rel (Expected: $expectedSha, Actual: $actualSha)"
    }
    $checked++
}

if ($mismatches.Count -gt 0) {
    $mismatches | ForEach-Object { Write-Host "  [FAIL] $_" -ForegroundColor Red }
    throw "$($mismatches.Count) file mismatches detected!"
}

Write-Host "  [OK] All $checked candidate files verified against workspace on disk!" -ForegroundColor Green
Write-Host "=== CANDIDATE EXPORT VERIFICATION PASSED ===" -ForegroundColor Green
