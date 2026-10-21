# Automated Delivery Readback Verification for ER-RUN-LOCALFLOW-20261019-076
param(
    [string]$RunDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== DELIVERY READBACK VERIFICATION: ER-RUN-LOCALFLOW-20261019-076 ===" -ForegroundColor Cyan

$indexPath = Join-Path $RunDir "DELIVERY_INDEX.json"
if (-not (Test-Path $indexPath)) {
    throw "DELIVERY_INDEX.json not found in $RunDir"
}

$index = Get-Content $indexPath -Raw | ConvertFrom-Json
Write-Host "Verifying $($index.files.Count) deposited artifacts against manifest..." -ForegroundColor Yellow

$checked = 0
$mismatches = @()

foreach ($entry in $index.files) {
    $rel = $entry.path
    $expectedSha = $entry.sha256.ToLowerInvariant()
    $full = Join-Path $RunDir $rel
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
    throw "$($mismatches.Count) delivery file mismatches detected!"
}

Write-Host "  [OK] All $checked deposited files verified bit-for-bit!" -ForegroundColor Green
Write-Host "=== DELIVERY READBACK VERIFICATION PASSED ===" -ForegroundColor Green
