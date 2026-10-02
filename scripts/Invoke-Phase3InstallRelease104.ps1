[CmdletBinding()]
param(
    [string]$ReleaseVersion = '1.0.5',
    [string]$ReleaseRoot,
    [string]$InstallRoot = 'C:\Program Files\Edge Retails',
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($scriptPath)) {
    throw 'Cannot determine the installer script path; invoke this file with PowerShell -File.'
}
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $scriptPath)
if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw 'ReleaseVersion must use major.minor.patch.'
}
if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    $ReleaseRoot = Join-Path $repositoryRoot "artifacts\release-$ReleaseVersion"
}
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $repositoryRoot "artifacts\phase3-certification-20260929\release-$ReleaseVersion-install.json"
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Release $ReleaseVersion installation requires an elevated Administrator shell." }

$release = [IO.Path]::GetFullPath($ReleaseRoot)
$manifestPath = Join-Path $release 'release-manifest.json'
$installer = Join-Path $release 'setup\EdgeRetailsSetup.exe'
foreach ($path in @($manifestPath, $installer)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release asset is missing: $path" }
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne $ReleaseVersion) { throw "Release manifest version '$($manifest.version)' does not match requested $ReleaseVersion." }

function Get-ManifestArtifactHash {
    param([Parameter(Mandatory)][string]$FileName)

    $matches = @($manifest.artifacts | Where-Object { $_.File -ceq $FileName })
    if ($matches.Count -ne 1) { throw "Release manifest must contain exactly one '$FileName' artifact." }
    return [string]$matches[0].Sha256
}

$installerHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash
$expectedInstallerHash = Get-ManifestArtifactHash -FileName 'EdgeRetailsSetup.exe'
if ($installerHash -ne $expectedInstallerHash) {
    throw 'Release setup bundle SHA-256 does not match release-manifest.json.'
}

$servicesBefore = @{}
foreach ($name in @('EdgeRetailsServer', 'EdgeRetailsWorker')) {
    $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
    if ($null -eq $service -or $service.State -ne 'Stopped' -or $service.StartMode -ne 'Disabled') {
        throw "$name must remain Stopped/Disabled during this binary-only upgrade."
    }
    $servicesBefore[$name] = [pscustomobject]@{ State = $service.State; StartMode = $service.StartMode }
}
if (Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue) {
    throw 'Port 7150 must have no listener during binary replacement.'
}

$configPath = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'EdgeRetails\config.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw 'ProgramData runtime configuration is missing.' }
$configHashBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $configPath).Hash
$process = Start-Process -FilePath $installer -ArgumentList @('/quiet', '/norestart') -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Approved Release $ReleaseVersion installer failed with exit code $($process.ExitCode)." }

$binarySpecs = @(
    @{ Name = 'Desktop'; ManifestFile = 'EdgeRetails.Desktop.exe'; Published = (Join-Path $release 'publish\EdgeRetails.Desktop.exe'); Installed = (Join-Path $InstallRoot 'EdgeRetails.Desktop.exe') },
    @{ Name = 'Worker'; ManifestFile = 'EdgeRetails.Worker.exe'; Published = (Join-Path $release 'publish\worker\EdgeRetails.Worker.exe'); Installed = (Join-Path $InstallRoot 'worker\EdgeRetails.Worker.exe') },
    @{ Name = 'WorkerManagedAssembly'; Published = (Join-Path $release 'publish\worker\EdgeRetails.Worker.dll'); Installed = (Join-Path $InstallRoot 'worker\EdgeRetails.Worker.dll') },
    @{ Name = 'WorkerWindowsServiceLifetime'; Published = (Join-Path $release 'publish\worker\Microsoft.Extensions.Hosting.WindowsServices.dll'); Installed = (Join-Path $InstallRoot 'worker\Microsoft.Extensions.Hosting.WindowsServices.dll') },
    @{ Name = 'Server'; ManifestFile = 'EdgeRetails.Server.exe'; Published = (Join-Path $release 'publish\server\EdgeRetails.Server.exe'); Installed = (Join-Path $InstallRoot 'server\EdgeRetails.Server.exe') },
    @{ Name = 'ServerInfrastructure'; Published = (Join-Path $release 'publish\server\EdgeRetails.Infrastructure.dll'); Installed = (Join-Path $InstallRoot 'server\EdgeRetails.Infrastructure.dll') }
)
$binaryResults = foreach ($spec in $binarySpecs) {
    if (-not (Test-Path -LiteralPath $spec.Published -PathType Leaf) -or
        -not (Test-Path -LiteralPath $spec.Installed -PathType Leaf)) {
        throw "$($spec.Name) binary is missing from publish or installed Release."
    }
    $publishedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $spec.Published).Hash
    $installedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $spec.Installed).Hash
    if ($publishedHash -ne $installedHash) { throw "$($spec.Name) installed hash differs from Release $ReleaseVersion." }
    if ($spec.ContainsKey('ManifestFile')) {
        $expectedHash = Get-ManifestArtifactHash -FileName $spec.ManifestFile
        if ($publishedHash -ne $expectedHash) { throw "$($spec.Name) publish hash does not match release-manifest.json." }
    }
    if ($spec.Name -in @('Desktop', 'Worker', 'Server')) {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($spec.Installed)
        if ($version.FileVersion -ne $ReleaseVersion) { throw "$($spec.Name) installed FileVersion is not $ReleaseVersion." }
    }
    [pscustomobject]@{ Name = $spec.Name; Sha256 = $installedHash }
}

$configHashAfter = (Get-FileHash -Algorithm SHA256 -LiteralPath $configPath).Hash
if ($configHashAfter -ne $configHashBefore) { throw 'ProgramData runtime configuration changed during binary upgrade.' }
$servicesAfter = @{}
foreach ($name in @('EdgeRetailsServer', 'EdgeRetailsWorker')) {
    $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
    if ($null -eq $service -or $service.State -ne 'Stopped' -or $service.StartMode -ne 'Disabled') {
        throw "$name changed state/start mode during binary upgrade; restore maintenance hold before proceeding."
    }
    $servicesAfter[$name] = [pscustomobject]@{ State = $service.State; StartMode = $service.StartMode }
}
if (Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue) {
    throw 'Port 7150 must remain listener-free after binary replacement.'
}

$evidence = [pscustomobject]@{
    Status = 'PASS'
    ReleaseVersion = $manifest.version
    InstallerExitCode = $process.ExitCode
    InstallerSha256 = $installerHash
    ProgramDataConfigSha256Before = $configHashBefore
    ProgramDataConfigSha256After = $configHashAfter
    ServicesBefore = $servicesBefore
    ServicesAfter = $servicesAfter
    Binaries = @($binaryResults)
    CompletedUtc = [DateTime]::UtcNow.ToString('o')
}
$evidenceDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($EvidencePath))
[IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
Write-Output "PHASE3_RELEASE_INSTALL_PASS Version=$ReleaseVersion InstallerExitCode=$($process.ExitCode) ConfigPreserved=True Services=Stopped/Disabled"
