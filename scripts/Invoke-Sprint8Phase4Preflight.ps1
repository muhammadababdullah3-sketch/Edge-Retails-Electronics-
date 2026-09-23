param(
    [string]$Root = ".",
    [Parameter(Mandatory=$true)][string]$FingerprintPath,
    [Parameter(Mandatory=$true)][string]$EvidencePath,
    [string]$PgBin = "C:\\Program Files\\PostgreSQL\\18\\bin",
    [switch]$RunPostgresClosure
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootPath = (Resolve-Path $Root).Path.TrimEnd('\','/')
$rootPrefix = $rootPath + [IO.Path]::DirectorySeparatorChar
$evidenceFull = [IO.Path]::GetFullPath($EvidencePath)
if ($evidenceFull -eq $rootPath -or
    $evidenceFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Phase 4 evidence must be written outside the live workspace."
}
New-Item -ItemType Directory -Force -Path (Split-Path $evidenceFull -Parent) | Out-Null
Set-Location $rootPath

function Invoke-DotNetCapture([string[]]$Arguments, [string]$Label) {
    $output = @(& dotnet @Arguments 2>&1)
    $code = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($code -ne 0) { throw "$Label failed with exit code $code." }
    return ,$output
}
function Get-BuildSummary($Output, [string]$Label) {
    $text = ($Output | Out-String)
    $warningMatch = [regex]::Match($text, '(?m)^\s*(\d+)\s+Warning\(s\)')
    $errorMatch = [regex]::Match($text, '(?m)^\s*(\d+)\s+Error\(s\)')
    if (-not $warningMatch.Success -or -not $errorMatch.Success) {
        throw "$Label build summary could not be parsed."
    }
    return [ordered]@{
        status = "PASS"
        warnings = [int]$warningMatch.Groups[1].Value
        errors = [int]$errorMatch.Groups[1].Value
    }
}

function Get-TestSummary($Output, [string]$Label) {
    $text = ($Output | Out-String)
    $match = [regex]::Match(
        $text,
        'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)')
    if (-not $match.Success) { throw "$Label test summary could not be parsed." }
    $failed = [int]$match.Groups[1].Value
    $passed = [int]$match.Groups[2].Value
    $skipped = [int]$match.Groups[3].Value
    $total = [int]$match.Groups[4].Value
    if ($failed -ne 0 -or $skipped -ne 0 -or $passed -le 0 -or $total -ne $passed) {
        throw "$Label did not produce an all-passing, non-skipped test result."
    }
    return [ordered]@{
        status = "PASS"
        failed = $failed
        passed = $passed
        skipped = $skipped
        total = $total
    }
}
function Invoke-FilteredTestGate([string]$Filter, [string]$Label) {
    $output = Invoke-DotNetCapture @(
        "test","tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj",
        "-c","Release","--no-build","--no-restore","-v","minimal",
        "--filter",$Filter
    ) $Label
    return Get-TestSummary $output $Label
}
Write-Host "Validating Sprint 7 structured closure evidence..."
& "$PSScriptRoot/Test-Sprint7ClosureEvidence.ps1" -Root $rootPath | Out-Host

Write-Host "Validating current Sprint 8 workspace fingerprint..."
& "$PSScriptRoot/Test-Sprint8WorkspaceFingerprint.ps1" -Root $rootPath -FingerprintPath $FingerprintPath
$fingerprint = Get-Content ([IO.Path]::GetFullPath($FingerprintPath)) -Raw | ConvertFrom-Json
$sprint7Path = Join-Path $rootPath "docs/Sprint7_Closure_Evidence.json"
$sprint7Hash = (Get-FileHash $sprint7Path -Algorithm SHA256).Hash

& dotnet restore "EdgeRetails.sln"
if ($LASTEXITCODE -ne 0) { throw "Solution restore failed." }

& dotnet clean "EdgeRetails.sln" -c Debug -v minimal
if ($LASTEXITCODE -ne 0) { throw "Debug clean failed." }
& dotnet clean "EdgeRetails.sln" -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "Release clean failed." }

$debugBuildOutput = Invoke-DotNetCapture @(
    "build","EdgeRetails.sln","-c","Debug","--no-restore","-warnaserror","-v","minimal"
) "Debug build"
$debugBuild = Get-BuildSummary $debugBuildOutput "Debug"

$debugTestOutput = Invoke-DotNetCapture @(
    "test","tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj",
    "-c","Debug","--no-build","--no-restore","-v","minimal"
) "Debug unit tests"
$debugTests = Get-TestSummary $debugTestOutput "Debug"

$releaseBuildOutput = Invoke-DotNetCapture @(
    "build","EdgeRetails.sln","-c","Release","--no-restore","-warnaserror","-v","minimal"
) "Release build"
$releaseBuild = Get-BuildSummary $releaseBuildOutput "Release"

$releaseTestOutput = Invoke-DotNetCapture @(
    "test","tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj",
    "-c","Release","--no-build","--no-restore","-v","minimal"
) "Release unit tests"
$releaseTests = Get-TestSummary $releaseTestOutput "Release"

$restoreSafetyTests = Invoke-FilteredTestGate `
    "FullyQualifiedName~Sprint8RestoreCutoverSafetyTests" "Restore cutover/discard safety tests"
$maintenanceBarrierTests = Invoke-FilteredTestGate `
    "FullyQualifiedName~Sprint8InfrastructureHardeningTests" "Maintenance integrity hardening tests"
$businessWriteBarrierTests = Invoke-FilteredTestGate `
    "FullyQualifiedName~Sprint8Phase2HardeningTests" "Business write barrier hardening tests"
$licenseRuntimeTests = Invoke-FilteredTestGate `
    "FullyQualifiedName~Sprint8LicenseTests" "License runtime tests"

& "$PSScriptRoot/Verify-EfModelSync.ps1" `
    -Project "src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj" `
    -StartupProject "src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj"
& "$PSScriptRoot/Verify-Sprint8Phase1.ps1" -Root $rootPath
& "$PSScriptRoot/Verify-Sprint8Phase2.ps1" -Root $rootPath
& "$PSScriptRoot/Verify-Sprint8Phase3.ps1" -Root $rootPath
& "$PSScriptRoot/Verify-InstallerDataPreservation.ps1" -Root $rootPath
& "$PSScriptRoot/Verify-Sprint8Architecture.ps1" -Root $rootPath
& "$PSScriptRoot/Test-PostgresClientReadiness.ps1" -PgBin $PgBin

$postgresClosure = "NOT_RUN"
if ($RunPostgresClosure) {
    & "$PSScriptRoot/Invoke-Sprint8PostgresClosure.ps1" -PgBin $PgBin
    $postgresClosure = "PASS"
}
& "$PSScriptRoot/Test-Sprint8WorkspaceFingerprint.ps1" -Root $rootPath -FingerprintPath $FingerprintPath

$evidence = [ordered]@{
    schemaVersion = 1
    sprint = 8
    gate = "Phase4Preflight"
    status = "PASS"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    workspaceFingerprint = $fingerprint.aggregateSha256
    workspaceFileCount = $fingerprint.fileCount
    sprint7EvidenceSha256 = $sprint7Hash
    debugBuild = $debugBuild
    releaseBuild = $releaseBuild
    unitDebug = $debugTests
    unitRelease = $releaseTests
    efModelSync = "PASS"
    phase1Static = "PASS"
    phase2Static = "PASS"
    phase3Static = "PASS"
    installerDataPreservation = "PASS"
    architectureAudit = "PASS"
    postgresClientReadiness = "PASS"
    postgresClosure = $postgresClosure
    backup = if ($postgresClosure -eq "PASS") { "PASS" } else { "NOT_RUN" }
    restore = if ($postgresClosure -eq "PASS") { "PASS" } else { "NOT_RUN" }
    cutover = if ($postgresClosure -eq "PASS") { "PASS" } else { "NOT_RUN" }
    rollbackSafety = $restoreSafetyTests.status
    discardSafety = $restoreSafetyTests.status
    maintenanceBarrier = $maintenanceBarrierTests.status
    businessWriteBarrier = $businessWriteBarrierTests.status
    licenseRuntime = $licenseRuntimeTests.status
    productionScreenCount = 17
    gitOperationsPerformed = $false
}

$evidence | ConvertTo-Json -Depth 8 | Set-Content $evidenceFull -Encoding UTF8
Write-Host "Sprint 8 Phase 4 preflight PASS"
Write-Host "Evidence: $evidenceFull"
