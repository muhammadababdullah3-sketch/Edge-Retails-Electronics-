# Delivery Readback Verification for ER-RUN-LOCALFLOW-20261019-075
param(
    [string]$DeliveryDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== DELIVERY READBACK VERIFICATION: ER-RUN-LOCALFLOW-20261019-075 ===" -ForegroundColor Cyan

$manifestPath = Join-Path $DeliveryDir "EVIDENCE_MANIFEST.json"
if (-not (Test-Path $manifestPath)) {
    Write-Error "EVIDENCE_MANIFEST.json missing at $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$hasher = [System.Security.Cryptography.SHA256]::Create()

$count = 0
foreach ($prop in $manifest.artifacts.PSObject.Properties) {
    $relPath = $prop.Name
    if ($relPath -eq "EVIDENCE_MANIFEST.json") { continue }
    $expected = $prop.Value
    $fullPath = Join-Path $DeliveryDir $relPath
    if (-not (Test-Path $fullPath)) {
        Write-Error "Artifact missing: $relPath"
    }
    $bytes = [System.IO.File]::ReadAllBytes($fullPath)
    $hash = [System.BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
    if ($hash -ne $expected.sha256) {
        Write-Error "SHA-256 mismatch on $relPath : expected $($expected.sha256), got $hash"
    }
    $count++
}

Write-Host "[PASS] All $count delivered artifacts verified bit-for-bit against EVIDENCE_MANIFEST.json!" -ForegroundColor Green
Write-Host "=== DELIVERY READBACK VERIFICATION COMPLETE: 100% OK ===" -ForegroundColor Cyan
