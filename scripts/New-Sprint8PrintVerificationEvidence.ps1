param(
    [string]$Root = ".",
    [Parameter(Mandatory=$true)][string]$FingerprintPath,
    [Parameter(Mandatory=$true)][string]$PreflightEvidencePath,
    [Parameter(Mandatory=$true)][string]$ReleaseManifestPath,
    [Parameter(Mandatory=$true)][string]$ExePath,
    [Parameter(Mandatory=$true)][string]$SetupPath,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateSet("PASS","FAIL","NOT_RUN")][string]$PrintingSoftware = "NOT_RUN",
    [ValidateSet("PASS","FAIL","BLOCKED_EXTERNAL","NOT_RUN")][string]$PrintingA4 = "NOT_RUN",
    [ValidateSet("PASS","FAIL","BLOCKED_EXTERNAL","NOT_RUN")][string]$Printing58mmPhysical = "BLOCKED_EXTERNAL",
    [ValidateSet("PASS","FAIL","BLOCKED_EXTERNAL","NOT_RUN")][string]$Printing80mmPhysical = "BLOCKED_EXTERNAL",
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootPath = (Resolve-Path $Root).Path.TrimEnd('\','/')
$rootPrefix = $rootPath + [IO.Path]::DirectorySeparatorChar
$outputFull = [IO.Path]::GetFullPath($OutputPath)
if ($outputFull -eq $rootPath -or
    $outputFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Print verification evidence must be outside the live workspace."
}
& "$PSScriptRoot/Test-Sprint8WorkspaceFingerprint.ps1" `
    -Root $rootPath -FingerprintPath $FingerprintPath
& "$PSScriptRoot/Test-Sprint8Phase4PreflightEvidence.ps1" `
    -Root $rootPath -FingerprintPath $FingerprintPath `
    -EvidencePath $PreflightEvidencePath -RequirePostgresClosure

foreach ($path in @($ReleaseManifestPath,$ExePath,$SetupPath)) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Required release artifact is missing: $path"
    }
}

$fingerprint = Get-Content ([IO.Path]::GetFullPath($FingerprintPath)) -Raw | ConvertFrom-Json
$preflight = Get-Content ([IO.Path]::GetFullPath($PreflightEvidencePath)) -Raw | ConvertFrom-Json
$manifestFull = [IO.Path]::GetFullPath($ReleaseManifestPath)
$exeFull = [IO.Path]::GetFullPath($ExePath)
$setupFull = [IO.Path]::GetFullPath($SetupPath)
$manifest = Get-Content $manifestFull -Raw | ConvertFrom-Json

$manifestHash = (Get-FileHash $manifestFull -Algorithm SHA256).Hash
$exeHash = (Get-FileHash $exeFull -Algorithm SHA256).Hash
$setupHash = (Get-FileHash $setupFull -Algorithm SHA256).Hash

New-Item -ItemType Directory -Force -Path (Split-Path $outputFull -Parent) | Out-Null
$evidence = [ordered]@{
    schemaVersion = 1
    sprint = 8
    gate = "PrintVerification"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    releaseVersion = $manifest.version
    workspaceFingerprint = $fingerprint.aggregateSha256
    preflightEvidenceSha256 = (Get-FileHash ([IO.Path]::GetFullPath($PreflightEvidencePath)) -Algorithm SHA256).Hash
    releaseManifestSha256 = $manifestHash
    desktopExeSha256 = $exeHash
    setupExeSha256 = $setupHash
    printingSoftware = $PrintingSoftware
    printingA4 = $PrintingA4
    printing58mmPhysical = $Printing58mmPhysical
    printing80mmPhysical = $Printing80mmPhysical
    notes = $Notes
}
$evidence | ConvertTo-Json -Depth 6 | Set-Content $outputFull -Encoding UTF8
Write-Host "Sprint 8 print verification evidence created: $outputFull"
Write-Host "58mm physical: $Printing58mmPhysical"
Write-Host "80mm physical: $Printing80mmPhysical"
