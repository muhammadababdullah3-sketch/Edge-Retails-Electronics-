# Automated Delivery Readback Verification for ER-RUN-085-ROOT-REVIEW-20261023-108
param(
    [string]$RunDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== DELIVERY READBACK VERIFICATION: ER-RUN-085-ROOT-REVIEW-20261023-108 ===" -ForegroundColor Cyan

$manifestPath = Join-Path $RunDir "MANIFEST.json"
if (-not (Test-Path $manifestPath)) {
    throw "MANIFEST.json not found: $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
Write-Host "Verifying $($manifest.total_deposited_files) deposited review artifacts..." -ForegroundColor Yellow

$checked = 0
$mismatches = @()

foreach ($entry in $manifest.files) {
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
    throw "$($mismatches.Count) artifact mismatches detected!"
}

Write-Host "  [OK] All $checked review artifacts verified bit-for-bit against manifest!" -ForegroundColor Green
Write-Host "=== DELIVERY READBACK VERIFICATION PASSED ===" -ForegroundColor Green
