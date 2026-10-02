param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55444
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
foreach ($tool in @('initdb.exe', 'pg_ctl.exe', 'pg_isready.exe', 'psql.exe', 'createdb.exe', 'pg_dump.exe', 'pg_restore.exe')) {
    $path = Join-Path $PgBin $tool
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required PostgreSQL tool is missing: $path"
    }
}

if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    $available = 55445..55899 | Where-Object {
        -not (Get-NetTCPConnection -State Listen -LocalPort $_ -ErrorAction SilentlyContinue)
    } | Select-Object -First 1
    if (-not $available) { throw 'No disposable PostgreSQL port is available.' }
    $Port = $available
}

$tempRoot = [IO.Path]::GetFullPath($env:TEMP)
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('EdgeRetailsPhase3Pg_' + [Guid]::NewGuid().ToString('N'))))
$data = Join-Path $runRoot 'data'
$pwFile = Join-Path $runRoot 'admin.pw'
$log = Join-Path $runRoot 'postgres.log'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$adminUser = 'er_p3_admin'
$testDb = 'edge_retails_phase3_test'
$adminPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$backupKey = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$backupDirectory = Join-Path $runRoot 'backups'
$previousBackupKey = $env:EDGE_RETAILS_BACKUP_KEY
$previousBackupDirectory = $env:EDGE_RETAILS_BACKUP_DIR
$previousPgBin = $env:EDGE_RETAILS_PG_BIN
[System.IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
[IO.File]::WriteAllText($pwFile, $adminPassword, [Text.UTF8Encoding]::new($false))
$started = $false

try {
    Write-Output 'PHASE3_PG_INIT_START'
    & (Join-Path $PgBin 'initdb.exe') -D $data --username=$adminUser --pwfile=$pwFile --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale
    if ($LASTEXITCODE -ne 0) { throw "initdb failed with exit code $LASTEXITCODE." }

    Add-Content -Path (Join-Path $data 'postgresql.conf') -Value @(
        '',
        "port = $Port",
        "listen_addresses = '127.0.0.1'",
        'max_connections = 60'
    )
    & (Join-Path $PgBin 'pg_ctl.exe') -D $data -l $log start
    if ($LASTEXITCODE -ne 0) { throw "pg_ctl start failed with exit code $LASTEXITCODE." }
    $started = $true

    $env:PGPASSWORD = $adminPassword
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        & (Join-Path $PgBin 'pg_isready.exe') -h 127.0.0.1 -p $Port -U $adminUser | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $ready) { throw 'Disposable PostgreSQL failed to become ready.' }

    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $adminUser $testDb
    if ($LASTEXITCODE -ne 0) { throw "createdb failed with exit code $LASTEXITCODE." }
    $connectionString = "Host=127.0.0.1;Port=$Port;Database=$testDb;Username=$adminUser;Password=$adminPassword"
    $env:EDGE_RETAILS_TEST_DB = $connectionString
    $env:EDGE_RETAILS_PRODUCTION_STATE_DIR = Join-Path $runRoot 'state'
    $env:EDGE_RETAILS_BACKUP_KEY = $backupKey
    $env:EDGE_RETAILS_BACKUP_DIR = $backupDirectory
    $env:EDGE_RETAILS_PG_BIN = $PgBin

    $serverVersionNum = & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $testDb -tA -c 'SHOW server_version_num'
    if ($LASTEXITCODE -ne 0 -or [int]$serverVersionNum -lt 180000 -or [int]$serverVersionNum -ge 190000) {
        throw "Phase 3 certification requires PostgreSQL 18; server_version_num was '$serverVersionNum'."
    }
    Write-Output 'Provider = PostgreSQL 18 / Npgsql'
    Write-Output "PostgreSQL server_version_num = $serverVersionNum"

    & .\scripts\Invoke-Phase3LegacyCategoryUpgradeRehearsal.ps1 -PgBin $PgBin -Port $Port -AdminUser $adminUser -AdminPassword $adminPassword -RunRoot $runRoot
    if ($LASTEXITCODE -ne 0) { throw 'Legacy category upgrade rehearsal failed.' }

    Write-Output 'PHASE3_PG_APPLY_MIGRATIONS'
    & dotnet ef database update --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release --connection $connectionString
    if ($LASTEXITCODE -ne 0) { throw "Phase 3 migration failed with exit code $LASTEXITCODE." }

    Write-Output 'PHASE3_PG_CERTIFICATION_TESTS'
    & dotnet test .\tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~Phase3PostgresCertificationTests|FullyQualifiedName~Phase3ProductionSafetyPostgresTests|FullyQualifiedName~Phase3UnknownOutcomeReplayIntegrationTests|FullyQualifiedName~Phase3CrashRestartIntegrationTests|FullyQualifiedName~Phase3ServerPostgresOperationalTraceTests' --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Phase 3 PostgreSQL tests failed with exit code $LASTEXITCODE." }
    Write-Output 'PHASE3_DISPOSABLE_POSTGRES_REHEARSAL_PASS'
}
finally {
    $env:EDGE_RETAILS_TEST_DB = $null
    $env:EDGE_RETAILS_PRODUCTION_STATE_DIR = $null
    $env:EDGE_RETAILS_BACKUP_KEY = $previousBackupKey
    $env:EDGE_RETAILS_BACKUP_DIR = $previousBackupDirectory
    $env:EDGE_RETAILS_PG_BIN = $previousPgBin
    $env:PGPASSWORD = $null
    if ($started) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $data -m immediate stop | Out-Null
    }
    $safePrefix = [IO.Path]::GetFullPath($tempRoot).TrimEnd('\') + '\EdgeRetailsPhase3Pg_'
    if (-not $runRoot.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Disposable cleanup target escaped the intended temporary directory: $runRoot"
    }
    if (Test-Path -LiteralPath $runRoot) {
        Start-Sleep -Milliseconds 500
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
