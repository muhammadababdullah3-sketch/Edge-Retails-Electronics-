# Verify-DeliveryReadback.ps1
# Automated readback and SHA-256 integrity verification for ER-RUN-FE-FINAL-20261020-001

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Write-Host "=== Verifying Delivery Package: ER-RUN-FE-FINAL-20261020-001 ===" -ForegroundColor Cyan
Write-Host "Directory: $scriptDir"

$shaFile = Join-Path $scriptDir "export\SHA256SUMS.txt"
if (-not (Test-Path $shaFile)) {
    Write-Error "SHA256SUMS.txt not found!"
    exit 1
}

$lines = Get-Content $shaFile
$failed = 0
$passed = 0

foreach ($line in $lines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "\s+", 2
    $expectedHash = $parts[0].Trim()
    $relPath = $parts[1].Trim()
    $fullPath = Join-Path $scriptDir $relPath

    if (-not (Test-Path $fullPath)) {
        Write-Host "MISSING: $relPath" -ForegroundColor Red
        $failed++
        continue
    }

    $actualHash = (Get-FileHash -Path $fullPath -Algorithm SHA256).Hash.ToLower()
    if ($actualHash -eq $expectedHash.ToLower()) {
        Write-Host "OK: $relPath" -ForegroundColor Green
        $passed++
    } else {
        Write-Host "HASH MISMATCH: $relPath (Expected: $expectedHash, Actual: $actualHash)" -ForegroundColor Red
        $failed++
    }
}

Write-Host "=== Readback Verification Summary ===" -ForegroundColor Cyan
Write-Host "Files Checked: $($passed + $failed)"
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })

if ($failed -eq 0) {
    Write-Host "SUCCESS: 100% of delivery package artifacts verified with cryptographic integrity." -ForegroundColor Green
    exit 0
} else {
    Write-Error "FAILURE: $failed artifact(s) failed verification."
    exit 1
}
