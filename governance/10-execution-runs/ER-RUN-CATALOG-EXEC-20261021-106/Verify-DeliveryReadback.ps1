# Delivery Readback Verification for ER-RUN-CATALOG-EXEC-20261021-106 (PowerShell)
param(
    [string]$DeliveryDir = "$PSScriptRoot"
)

$ErrorActionPreference = "Stop"
Write-Host "=== DELIVERY READBACK VERIFICATION: ER-RUN-CATALOG-EXEC-20261021-106 ===" -ForegroundColor Cyan

$manifestPath = Join-Path $DeliveryDir "EVIDENCE_MANIFEST.json"
if (-not (Test-Path $manifestPath)) {
    Write-Error "EVIDENCE_MANIFEST.json missing at $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$count = 0

function Get-FileSha256($path) {
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hash = $hasher.ComputeHash($bytes)
    return [System.BitConverter]::ToString($hash).Replace("-", "").ToLowerInvariant()
}

foreach ($prop in $manifest.artifacts.PSObject.Properties) {
    $relPath = $prop.Name
    $meta = $prop.Value
    if ($relPath -eq "EVIDENCE_MANIFEST.json") { continue }

    $fullPath = Join-Path $DeliveryDir $relPath
    if (-not (Test-Path $fullPath)) {
        Write-Error "Missing delivered artifact: $relPath"
    }

    $actualHash = Get-FileSha256 $fullPath
    if ($actualHash -ne $meta.sha256) {
        Write-Error "Hash mismatch on $relPath : expected $($meta.sha256) but got $actualHash"
    }
    $count++
}

Write-Host "[PASS] All $count delivered artifacts verified bit-for-bit against EVIDENCE_MANIFEST.json!" -ForegroundColor Green
Write-Host "=== DELIVERY READBACK VERIFICATION COMPLETE: 100% OK ===" -ForegroundColor Green
