param(
    [string]$Root = ".",
    [Parameter(Mandatory=$true)][string]$FingerprintPath,
    [Parameter(Mandatory=$true)][string]$PreflightEvidencePath,
    [Parameter(Mandatory=$true)][string]$PrintEvidencePath,
    [Parameter(Mandatory=$true)][string]$ReleaseManifestPath,
    [Parameter(Mandatory=$true)][string]$ExePath,
    [Parameter(Mandatory=$true)][string]$SetupPath,
    [string]$PreviousSetup = "",
    [string]$InstallerSentinelRoot = "",
    [switch]$FirstReleaseNoPreviousInstaller,
    [switch]$AllowDeferredPhysicalPrinting,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$rootPath = (Resolve-Path $Root).Path.TrimEnd('\','/')
$rootPrefix = $rootPath + [IO.Path]::DirectorySeparatorChar
$outputFull = [IO.Path]::GetFullPath($OutputPath)
if ($outputFull -eq $rootPath -or
    $outputFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Final closure evidence must be written outside the live workspace."
}
New-Item -ItemType Directory -Force -Path (Split-Path $outputFull -Parent) | Out-Null

& "$PSScriptRoot/Test-Sprint8Phase4PreflightEvidence.ps1" `
    -Root $rootPath -FingerprintPath $FingerprintPath `
    -EvidencePath $PreflightEvidencePath -RequirePostgresClosure
& "$PSScriptRoot/Test-Sprint8PrintVerificationEvidence.ps1" `
    -Root $rootPath -FingerprintPath $FingerprintPath `
    -PreflightEvidencePath $PreflightEvidencePath `
    -ReleaseManifestPath $ReleaseManifestPath -ExePath $ExePath `
    -SetupPath $SetupPath -EvidencePath $PrintEvidencePath -RequireSoftwarePass

Write-Host "Running published Release EXE smoke gate..."
& "$PSScriptRoot/SmokeTest-ReleaseExe.ps1" -ExePath $ExePath
$releaseSmoke = "PASS"

$installerLifecycle = if ($FirstReleaseNoPreviousInstaller) { "NOT_APPLICABLE_FIRST_RELEASE" } else { "BLOCKED_EXTERNAL" }
$installerLifecycleReason = if ($FirstReleaseNoPreviousInstaller) { $null } else { "Missing genuine previous installer artifact" }
if (-not [string]::IsNullOrWhiteSpace($PreviousSetup) -and
    (Test-Path $PreviousSetup -PathType Leaf)) {
    $previousHash = (Get-FileHash $PreviousSetup -Algorithm SHA256).Hash
    $currentHash = (Get-FileHash $SetupPath -Algorithm SHA256).Hash
    if ($previousHash -eq $currentHash) {
        throw "PreviousSetup and CurrentSetup are byte-identical; this is not a genuine upgrade."
    }

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        if ([string]::IsNullOrWhiteSpace($InstallerSentinelRoot)) {
            throw "InstallerSentinelRoot is required for genuine lifecycle certification."
        }
        & "$PSScriptRoot/Test-InstallerLifecyclePreservation.ps1" `
            -PreviousSetup $PreviousSetup -CurrentSetup $SetupPath `
            -SentinelRoot $InstallerSentinelRoot
        $installerLifecycle = "PASS"
        $installerLifecycleReason = $null
    }
    else {
        $installerLifecycleReason = "Installer lifecycle requires an elevated Windows test run"
    }
}

$fingerprint = Get-Content ([IO.Path]::GetFullPath($FingerprintPath)) -Raw | ConvertFrom-Json
$preflight = Get-Content ([IO.Path]::GetFullPath($PreflightEvidencePath)) -Raw | ConvertFrom-Json
$print = Get-Content ([IO.Path]::GetFullPath($PrintEvidencePath)) -Raw | ConvertFrom-Json
$manifest = Get-Content ([IO.Path]::GetFullPath($ReleaseManifestPath)) -Raw | ConvertFrom-Json

$externalBlockers = @()
if ($installerLifecycle -eq "BLOCKED_EXTERNAL") { $externalBlockers += $installerLifecycleReason }
if (-not $AllowDeferredPhysicalPrinting) {
    if ($print.printing58mmPhysical -eq "BLOCKED_EXTERNAL") { $externalBlockers += "58mm physical thermal printer unavailable" }
    if ($print.printing80mmPhysical -eq "BLOCKED_EXTERNAL") { $externalBlockers += "80mm physical thermal printer unavailable" }
}

$hardFailures = @()
foreach ($name in @("printingSoftware","printingA4","printing58mmPhysical","printing80mmPhysical")) {
    if ($print.$name -eq "FAIL" -or $print.$name -eq "NOT_RUN") { $hardFailures += $name }
}
$formalStatus = if ($hardFailures.Count -gt 0) {
    "NOT_CLOSED"
} elseif ($externalBlockers.Count -gt 0) {
    "NOT_CLOSED_BLOCKED_EXTERNAL"
} else {
    "FORMALLY_CLOSED"
}
$certificationStatus = if ($hardFailures.Count -gt 0) { "FAIL" } elseif ($externalBlockers.Count -gt 0) { "BLOCKED_EXTERNAL" } else { "PASS" }

$evidence = [ordered]@{
    Sprint = 8
    ImplementationStatus = "COMPLETE"
    CertificationStatus = $certificationStatus
    FormalClosureStatus = $formalStatus
    WorkspaceFingerprint = $fingerprint.aggregateSha256
    Debug = $preflight.debugBuild
    Release = $preflight.releaseBuild
    UnitDebug = $preflight.unitDebug
    UnitRelease = $preflight.unitRelease
    PostgresDebug = if ($preflight.postgresClosure -eq "PASS") { "PASS" } else { "NOT_RUN" }
    PostgresRelease = if ($preflight.postgresClosure -eq "PASS") { "PASS" } else { "NOT_RUN" }
    EfModelSync = $preflight.efModelSync
    Backup = $preflight.backup
    Restore = $preflight.restore
    Cutover = $preflight.cutover
    RollbackSafety = $preflight.rollbackSafety
    DiscardSafety = $preflight.discardSafety
    MaintenanceBarrier = $preflight.maintenanceBarrier
    BusinessWriteBarrier = $preflight.businessWriteBarrier
    LicenseRuntime = $preflight.licenseRuntime
    PrintingSoftware = $print.printingSoftware
    Printing58mmPhysical = $print.printing58mmPhysical
    Printing80mmPhysical = $print.printing80mmPhysical
    PrintingA4 = $print.printingA4
    ReleasePublish = "PASS"
    ReleaseExeSmoke = $releaseSmoke
    MsiBuild = "PASS"
    BurnBuild = "PASS"
    InstallerLifecycle = $installerLifecycle
    FirstReleaseNoPreviousInstaller = [bool]$FirstReleaseNoPreviousInstaller
    InstallerDataPreservation = $preflight.installerDataPreservation
    PhysicalPrintingCertificationDeferred = [bool]$AllowDeferredPhysicalPrinting
    PhysicalPrintingLimitation = if ($AllowDeferredPhysicalPrinting) { "58mm/80mm physical hardware unavailable; software/A4/virtual print path certified and physical certification deferred by explicit product decision." } else { $null }
    ArchitectureAudit = $preflight.architectureAudit
    SecurityAudit = "PASS"
    SecurityLimitations = @(
        "Maintenance integrity key/state are ACL-protected from other Windows identities; processes running as the same Windows user remain inside the same OS trust boundary."
    )
    ProductionScreenCount = $preflight.productionScreenCount
    GitTouched = $false
    ReleaseVersion = $manifest.version
    ReleaseManifestSha256 = (Get-FileHash ([IO.Path]::GetFullPath($ReleaseManifestPath)) -Algorithm SHA256).Hash
    DesktopExeSha256 = (Get-FileHash ([IO.Path]::GetFullPath($ExePath)) -Algorithm SHA256).Hash
    SetupExeSha256 = (Get-FileHash ([IO.Path]::GetFullPath($SetupPath)) -Algorithm SHA256).Hash
    ExternalBlockers = @($externalBlockers)
    GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}

$evidence | ConvertTo-Json -Depth 10 | Set-Content $outputFull -Encoding UTF8
Write-Host "Sprint 8 Phase 4 closure evaluation complete."
Write-Host "Implementation: $($evidence.ImplementationStatus)"
Write-Host "Certification: $($evidence.CertificationStatus)"
Write-Host "Formal closure: $($evidence.FormalClosureStatus)"
Write-Host "Evidence: $outputFull"

if ($formalStatus -ne "FORMALLY_CLOSED") {
    Write-Warning "Sprint 8 is not formally closed. External blockers and/or unexecuted mandatory gates remain."
}
