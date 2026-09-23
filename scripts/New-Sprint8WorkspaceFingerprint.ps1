param(
    [string]$Root = ".",
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootPath = (Resolve-Path $Root).Path.TrimEnd('\','/')
$outputFull = [IO.Path]::GetFullPath($OutputPath)
$rootPrefix = $rootPath + [IO.Path]::DirectorySeparatorChar

if ($outputFull -eq $rootPath -or
    $outputFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Fingerprint output must be outside the live workspace."
}

$outputDirectory = Split-Path $outputFull -Parent
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw "Fingerprint output directory is invalid."
}
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$roots = @("src","tests","scripts","installer","database","build")
$files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
foreach ($name in $roots) {
    $candidate = Join-Path $rootPath $name
    if (-not (Test-Path $candidate)) { continue }

    Get-ChildItem $candidate -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|artifacts|TestResults|\.vs|\.git)[\\/]' -and
        $_.Name -notlike '*_wpftmp.csproj'
    } | ForEach-Object { $files.Add($_) }
}

$rootFiles = @(
    ".editorconfig",
    "Directory.Build.props",
    "global.json",
    "dotnet-tools.json",
    "EdgeRetails.sln",
    "EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md",
    "EDGE_RETAILS_FRONTEND_PROJECT_BRAIN.md"
)
foreach ($name in $rootFiles) {
    $candidate = Join-Path $rootPath $name
    if (Test-Path $candidate -PathType Leaf) {
        $files.Add((Get-Item $candidate))
    }
}
$entries = foreach ($file in ($files | Sort-Object FullName -Unique)) {
    $relative = $file.FullName.Substring($rootPrefix.Length).Replace('\','/')
    $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $bytes = $stream.Length
        $fileSha = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = ([BitConverter]::ToString($fileSha.ComputeHash($stream))).Replace('-','')
        }
        finally {
            $fileSha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
    [pscustomobject]@{
        path = $relative
        sha256 = $hash
        bytes = $bytes
    }
}

$canonicalLines = @($entries | ForEach-Object {
    "$($_.sha256) $($_.bytes) $($_.path)"
})
$canonical = ($canonicalLines -join [Environment]::NewLine) + [Environment]::NewLine
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $aggregateBytes = [Text.Encoding]::UTF8.GetBytes($canonical)
    $aggregateHash = ([BitConverter]::ToString($sha.ComputeHash($aggregateBytes))).Replace('-','')
}
finally {
    $sha.Dispose()
}
$result = [ordered]@{
    schemaVersion = 1
    root = $rootPath
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    fileCount = @($entries).Count
    aggregateSha256 = $aggregateHash
    files = @($entries)
}

$result | ConvertTo-Json -Depth 8 | Set-Content $outputFull -Encoding UTF8
Write-Host "Sprint 8 workspace fingerprint created."
Write-Host "Files: $(@($entries).Count)"
Write-Host "Aggregate SHA256: $aggregateHash"
Write-Host "Evidence: $outputFull"
