# Automated Candidate Export Verification for ER-RUN-LOCALFLOW-20261019-079
param(
    [string]$RepoRoot = "$PSScriptRoot\..\..\..\..\..\Point of Sale",
    [string]$ExportDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== CANDIDATE EXPORT VERIFICATION: ER-RUN-LOCALFLOW-20261019-079 ===" -ForegroundColor Cyan

$invPath = Join-Path $ExportDir "CANDIDATE_FILE_INVENTORY.json"
if (-not (Test-Path $invPath)) {
    throw "Inventory file not found: $invPath"
}

$inv = Get-Content $invPath -Raw | ConvertFrom-Json
Write-Host "Verifying $($inv.total_candidate_files) candidate files against repository..." -ForegroundColor Yellow

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

Write-Host "  [OK] All $checked candidate files verified bit-for-bit against workspace!" -ForegroundColor Green
Write-Host "=== CANDIDATE EXPORT VERIFICATION PASSED ===" -ForegroundColor Green
