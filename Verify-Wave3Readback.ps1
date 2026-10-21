$ErrorActionPreference = "Stop"
$runDir = "C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261017-012"
$manifestPath = Join-Path $runDir "EVIDENCE_MANIFEST.json"

Write-Host "Verifying readback for ER-RUN-20261017-012..." -ForegroundColor Cyan
if (-not (Test-Path $manifestPath)) {
    Write-Error "FAIL: $manifestPath not found"
    exit 1
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$errors = 0
$checked = 0

foreach ($item in $manifest.artifacts) {
    $filePath = Join-Path $runDir $item.path
    if (-not (Test-Path $filePath)) {
        Write-Host "MISSING: $($item.path)" -ForegroundColor Red
        $errors++
        continue
    }
    $hash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $item.sha256) {
        Write-Host "MISMATCH: $($item.path) (expected $($item.sha256), got $hash)" -ForegroundColor Red
        $errors++
    } else {
        $checked++
    }
}

if ($errors -eq 0) {
    Write-Host "PASS: All $checked artifacts match EVIDENCE_MANIFEST.json with 100% SHA-256 equality." -ForegroundColor Green
    exit 0
} else {
    Write-Error "FAIL: $errors errors found during readback verification."
    exit 1
}
