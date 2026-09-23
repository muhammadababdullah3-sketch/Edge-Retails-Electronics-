param(
    [string]$Root = ".",
    [Parameter(Mandatory=$true)][string]$FingerprintPath
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootPath = (Resolve-Path $Root).Path
$fingerprintFull = [IO.Path]::GetFullPath($FingerprintPath)
if (-not (Test-Path $fingerprintFull -PathType Leaf)) {
    throw "Workspace fingerprint evidence is missing: $fingerprintFull"
}

try {
    $expected = Get-Content $fingerprintFull -Raw | ConvertFrom-Json
}
catch {
    throw "Workspace fingerprint evidence is invalid JSON: $($_.Exception.Message)"
}

if ($expected.schemaVersion -ne 1) {
    throw "Unsupported workspace fingerprint schema."
}

$evidenceDir = Split-Path $fingerprintFull -Parent
$temp = Join-Path $evidenceDir (".verify-" + [Guid]::NewGuid().ToString("N") + ".json")
try {
    & "$PSScriptRoot/New-Sprint8WorkspaceFingerprint.ps1" -Root $rootPath -OutputPath $temp

    $actual = Get-Content $temp -Raw | ConvertFrom-Json
    if ($actual.fileCount -ne $expected.fileCount) {
        throw "Workspace fingerprint file count changed."
    }
    if ($actual.aggregateSha256 -ne $expected.aggregateSha256) {
        throw "Workspace fingerprint SHA-256 mismatch. The live workspace changed after evidence generation."
    }

    $expectedLines = @($expected.files | ForEach-Object { "$($_.sha256) $($_.bytes) $($_.path)" })
    $actualLines = @($actual.files | ForEach-Object { "$($_.sha256) $($_.bytes) $($_.path)" })
    $difference = Compare-Object $expectedLines $actualLines
    if ($difference) {
        throw "Workspace fingerprint file inventory changed."
    }

    Write-Host "Sprint 8 workspace fingerprint verification PASS"
    Write-Host "Aggregate SHA256: $($actual.aggregateSha256)"
}
finally {
    Remove-Item $temp -Force -ErrorAction SilentlyContinue
}
