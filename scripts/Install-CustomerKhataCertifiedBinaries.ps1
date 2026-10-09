[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$package = Join-Path $repo 'artifacts\production\pending-customer-khata'
$installed = 'C:\Program Files\Edge Retails'
$desktop = Join-Path $repo 'artifacts\production\desktop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator shell required to replace the installed Windows service binaries.'
}
if (Get-CimInstance Win32_Process -Filter "Name='EdgeRetails.Desktop.exe'") {
    throw 'Close Edge Retails after saving pending work before this coordinated upgrade.'
}
foreach ($part in @('server','worker','desktop')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package "$part\EdgeRetails.$((Get-Culture).TextInfo.ToTitleCase($part)).exe"))) {
        throw "Missing certified $part package."
    }
}
$manifest = Get-Content (Join-Path $package 'binary-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files) {
    $path = Join-Path $package $file.path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "Certified package hash mismatch: $($file.path)"
    }
}
# Migration metadata is the only operational database read this installer performs.
# It never applies the review-only SQL, seeds data, or changes connection settings.
Import-Module (Join-Path $PSScriptRoot 'Phase3ConnectionString.psm1') -Force
$config = Get-Content 'C:\ProgramData\EdgeRetails\config.json' -Raw | ConvertFrom-Json
$connection = ConvertFrom-EdgeRetailsNpgsqlConnectionString $config.ConnectionStrings.DefaultConnection
$oldPassword = $env:PGPASSWORD
$oldOptions = $env:PGOPTIONS
try {
    $env:PGPASSWORD = Get-EdgeRetailsConnectionStringValue $connection @('Password','Pwd')
    $env:PGOPTIONS = '-c default_transaction_read_only=on -c statement_timeout=10000'
    $migrations = @(& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -A -t --no-password --set=ON_ERROR_STOP=1 `
        -h (Get-EdgeRetailsConnectionStringValue $connection @('Host')) `
        -p (Get-EdgeRetailsConnectionStringValue $connection @('Port') '5432') `
        -U (Get-EdgeRetailsConnectionStringValue $connection @('Username','User ID')) `
        -d (Get-EdgeRetailsConnectionStringValue $connection @('Database')) `
        -c 'SELECT "MigrationId" FROM system.__ef_migrations_history ORDER BY "MigrationId";')
    if ($LASTEXITCODE -ne 0) { throw 'Production schema metadata preflight failed.' }
    $expected = @(Get-Content (Join-Path $package 'required-migrations.txt'))
    if (@(Compare-Object $expected $migrations).Count -ne 0) {
        throw 'Schema differs from the certified build. Approved schema upgrade is required before binary replacement; no files changed.'
    }
} finally {
    $env:PGPASSWORD = $oldPassword
    $env:PGOPTIONS = $oldOptions
}
$ready = Invoke-RestMethod 'http://127.0.0.1:7150/api/system/ready'
if ($ready.status -ne 'Ready') { throw 'Installed server must be healthy before upgrade.' }
# Retire only this workspace's earlier after-close updater so it cannot overwrite this release.
$previousUpdater = Join-Path $repo 'artifacts\production\pending-supplier-ui\Apply-AfterClose.ps1'
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like ('*-File "' + $previousUpdater + '"*') } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction Stop }
$backup = Join-Path $repo ('artifacts\deployment-backups\customer-khata-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($part in @('server','worker')) {
    Copy-Item -LiteralPath (Join-Path $installed $part) -Destination $backup -Recurse
}
Copy-Item -LiteralPath $desktop -Destination $backup -Recurse
$states = @{}
foreach ($name in @('EdgeRetailsWorker','EdgeRetailsServer')) {
    $states[$name] = (Get-Service $name).Status.ToString()
}
function Copy-Binaries([string]$Source, [string]$Target) {
    Get-ChildItem -LiteralPath $Source | Where-Object { $_.Name -notlike 'appsettings*.json' } |
        Copy-Item -Destination $Target -Recurse -Force
}
try {
    foreach ($name in @('EdgeRetailsWorker','EdgeRetailsServer')) {
        Stop-Service $name
        (Get-Service $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Copy-Binaries (Join-Path $package 'server') (Join-Path $installed 'server')
    Copy-Binaries (Join-Path $package 'worker') (Join-Path $installed 'worker')
    Copy-Binaries (Join-Path $package 'desktop') $desktop
    Start-Service 'EdgeRetailsServer'
    $healthy = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            if ((Invoke-RestMethod 'http://127.0.0.1:7150/api/system/ready').status -eq 'Ready') { $healthy = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthy) { throw 'Updated server did not become ready; rolling binary files back.' }
    if ($states.EdgeRetailsWorker -eq 'Running') { Start-Service 'EdgeRetailsWorker' }
    'APPLIED: certified customer/khata frontend and service binaries; ' + (Get-Date -Format o) |
        Set-Content (Join-Path $package 'deployment-status.txt')
} catch {
    foreach ($name in @('EdgeRetailsWorker','EdgeRetailsServer')) {
        Stop-Service $name -ErrorAction SilentlyContinue
    }
    Copy-Binaries (Join-Path $backup 'server') (Join-Path $installed 'server')
    Copy-Binaries (Join-Path $backup 'worker') (Join-Path $installed 'worker')
    Copy-Binaries (Join-Path $backup 'desktop') $desktop
    foreach ($name in @('EdgeRetailsServer','EdgeRetailsWorker')) {
        if ($states[$name] -eq 'Running') { Start-Service $name }
    }
    throw
}
