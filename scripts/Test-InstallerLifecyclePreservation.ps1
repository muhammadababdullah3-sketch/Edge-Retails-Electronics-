param(
    [Parameter(Mandatory=$true)][string]$PreviousSetup,
    [Parameter(Mandatory=$true)][string]$CurrentSetup,
    [Parameter(Mandatory=$true)][string]$SentinelRoot,
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55433
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Installer lifecycle verification must run elevated on the Windows test machine."
}
foreach ($setup in @($PreviousSetup,$CurrentSetup)) {
    if (-not (Test-Path $setup -PathType Leaf)) { throw "Setup not found: $setup" }
}
$previousHash = (Get-FileHash $PreviousSetup -Algorithm SHA256).Hash
$currentHash = (Get-FileHash $CurrentSetup -Algorithm SHA256).Hash
if ($previousHash -eq $currentHash) { throw "Previous and current setup artifacts are byte-identical." }
$previousVersion = [version](Get-Item $PreviousSetup).VersionInfo.ProductVersion
$currentVersion = [version](Get-Item $CurrentSetup).VersionInfo.ProductVersion
if ($currentVersion -le $previousVersion) {
    throw "Current setup version $currentVersion must be greater than previous setup version $previousVersion."
}
$requiredTools = @('initdb.exe','pg_ctl.exe','pg_isready.exe','psql.exe','createdb.exe')
foreach ($tool in $requiredTools) {
    $toolPath = Join-Path $PgBin $tool
    if (-not (Test-Path $toolPath -PathType Leaf)) { throw "Required PostgreSQL tool missing: $toolPath" }
}
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    throw "Disposable installer-lifecycle PostgreSQL port $Port is already in use."
}

$sentinel = [IO.Path]::GetFullPath($SentinelRoot)
$programFiles = [IO.Path]::GetFullPath($env:ProgramFiles)
if ($sentinel.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) {
    throw "SentinelRoot must represent external operational data, not Program Files."
}
$sentinelFiles = @(
    'backups\lifecycle.erbak',
    'backups\lifecycle.erbak.manifest.json',
    'license\license.json',
    'exports\lifecycle.csv',
    'operational\business-data.keep'
)
foreach ($relative in $sentinelFiles) {
    $path = Join-Path $sentinel $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    Set-Content $path "Edge Retails lifecycle sentinel: $relative" -Encoding UTF8
}
$beforeHashes = @{}
foreach ($relative in $sentinelFiles) {
    $beforeHashes[$relative] = (Get-FileHash (Join-Path $sentinel $relative) -Algorithm SHA256).Hash
}

$runRoot = Join-Path $env:TEMP ('EdgeRetailsInstallerLifecyclePg_' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $runRoot 'data'
$pwFile = Join-Path $runRoot 'admin.pw'
$log = Join-Path $runRoot 'postgres.log'
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$adminUser = 'er_install_admin'
$database = 'edge_retails_installer_lifecycle'
$adminPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($pwFile, $adminPassword, [Text.UTF8Encoding]::new($false))
$started = $false

function Invoke-Setup([string]$path,[string]$action) {
    $args = if ($action -eq 'uninstall') { '/uninstall /quiet /norestart' } else { '/install /quiet /norestart' }
    $process = Start-Process $path -ArgumentList $args -Wait -PassThru
    if ($process.ExitCode -notin @(0,3010)) {
        throw "Setup action '$action' failed with exit code $($process.ExitCode)."
    }
}
function Get-DatabaseOid {
    $env:PGPASSWORD = $adminPassword
    $raw = & (Join-Path $PgBin 'psql.exe') --no-password --tuples-only --no-align --quiet --host 127.0.0.1 --port $Port --username $adminUser --dbname postgres --command "SELECT oid::text FROM pg_database WHERE datname='$database';"
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL OID probe failed." }
    return $raw.Trim()
}
function Assert-Preservation([string]$stage,[string]$expectedOid) {
    $actualOid = Get-DatabaseOid
    if ($actualOid -ne $expectedOid) {
        throw "PostgreSQL database OID changed during '$stage'. Expected $expectedOid, got $actualOid."
    }
    foreach ($relative in $sentinelFiles) {
        $path = Join-Path $sentinel $relative
        if (-not (Test-Path $path -PathType Leaf)) {
            throw "Installer lifecycle deleted operational sentinel during '$stage': $relative"
        }
        $actualHash = (Get-FileHash $path -Algorithm SHA256).Hash
        if ($actualHash -ne $beforeHashes[$relative]) {
            throw "Installer lifecycle modified operational sentinel during '$stage': $relative"
        }
    }
}

try {
    & (Join-Path $PgBin 'initdb.exe') -D $data --username=$adminUser --pwfile=$pwFile --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale
    if ($LASTEXITCODE -ne 0) { throw "initdb failed with exit code $LASTEXITCODE." }
    Add-Content (Join-Path $data 'postgresql.conf') @('',"port = $Port","listen_addresses = '127.0.0.1'")
    & (Join-Path $PgBin 'pg_ctl.exe') -D $data -l $log start
    if ($LASTEXITCODE -ne 0) { throw "pg_ctl start failed with exit code $LASTEXITCODE." }
    $started = $true
    $ready = $false
    for ($i=0; $i -lt 30; $i++) {
        & (Join-Path $PgBin 'pg_isready.exe') -h 127.0.0.1 -p $Port -q
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw "Disposable installer-lifecycle PostgreSQL cluster did not become ready." }

    $env:PGPASSWORD = $adminPassword
    & (Join-Path $PgBin 'createdb.exe') --no-password --host 127.0.0.1 --port $Port --username $adminUser $database
    if ($LASTEXITCODE -ne 0) { throw "Lifecycle sentinel database creation failed." }
    $databaseOid = Get-DatabaseOid
    if ([string]::IsNullOrWhiteSpace($databaseOid)) { throw "Lifecycle database OID is unavailable." }

    Invoke-Setup $PreviousSetup 'install'
    Assert-Preservation 'previous install' $databaseOid
    Invoke-Setup $CurrentSetup 'install'
    Assert-Preservation 'current upgrade' $databaseOid

    $installedExe = Join-Path $env:ProgramFiles 'Edge Retails\EdgeRetails.Desktop.exe'
    if (-not (Test-Path $installedExe -PathType Leaf)) { throw "Upgraded installed executable is missing: $installedExe" }
    & "$PSScriptRoot\SmokeTest-ReleaseExe.ps1" -ExePath $installedExe -ObservationSeconds 3 -ShutdownSeconds 5

    Invoke-Setup $CurrentSetup 'uninstall'
    Assert-Preservation 'current uninstall' $databaseOid
    Write-Host "Installer install/upgrade/uninstall preservation PASS"
    Write-Host "PostgreSQL OID preserved: $databaseOid"
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    if ($started) {
        try { & (Join-Path $PgBin 'pg_ctl.exe') -D $data -m fast stop | Out-Host } catch {}
    }
    try { Remove-Item $runRoot -Recurse -Force } catch {}
}
