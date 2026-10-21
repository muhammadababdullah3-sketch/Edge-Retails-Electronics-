# Generate-RunEvidenceManifest.ps1
# Computes SHA-256 hashes and byte counts for all files in ER-RUN-20261012-004.

param(
    [string]$RunDir = "C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261012-004",
    [string]$GeneratedUtc = "2026-10-13T06:22:00Z"
)

$ErrorActionPreference = "Stop"

$sha256 = [System.Security.Cryptography.SHA256]::Create()

function Get-FileSha256Hex([string]$filePath) {
    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    $hashBytes = $sha256.ComputeHash($bytes)
    return ($hashBytes | ForEach-Object { $_.ToString("x2") }) -join ""
}

$allFiles = Get-ChildItem -Path $RunDir -File -Recurse | Where-Object {
    $_.Name -ne "EVIDENCE_MANIFEST.json" -and $_.Name -ne "EVIDENCE_INDEX.csv"
}

$fileEntries = @()
$csvLines = @("path,sha256,size_bytes")

foreach ($file in ($allFiles | Sort-Object FullName)) {
    $relPath = $file.FullName.Substring($RunDir.Length).TrimStart("\", "/").Replace("\", "/")
    $hash = Get-FileSha256Hex $file.FullName
    $size = $file.Length

    $fileEntries += [ordered]@{
        path = $relPath
        sha256 = $hash
        size_bytes = $size
    }

    $csvLines += "$relPath,$hash,$size"
}

$manifestObj = [ordered]@{
    run_id = "ER-RUN-20261012-004"
    generated_utc = $GeneratedUtc
    total_files = $fileEntries.Count
    files = $fileEntries
}

$manifestJson = $manifestObj | ConvertTo-Json -Depth 5

# Write to Run Root
$rootManifestPath = Join-Path $RunDir "EVIDENCE_MANIFEST.json"
$rootCsvPath = Join-Path $RunDir "EVIDENCE_INDEX.csv"
[System.IO.File]::WriteAllText($rootManifestPath, $manifestJson, [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllLines($rootCsvPath, $csvLines, [System.Text.Encoding]::UTF8)

# Mirror to delivery/ directory
$deliveryDir = Join-Path $RunDir "delivery"
if (Test-Path $deliveryDir) {
    $delManifestPath = Join-Path $deliveryDir "EVIDENCE_MANIFEST.json"
    $delCsvPath = Join-Path $deliveryDir "EVIDENCE_INDEX.csv"
    [System.IO.File]::WriteAllText($delManifestPath, $manifestJson, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllLines($delCsvPath, $csvLines, [System.Text.Encoding]::UTF8)
}

Write-Host "Generated EVIDENCE_MANIFEST.json and EVIDENCE_INDEX.csv with $($fileEntries.Count) files."
