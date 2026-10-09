param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55434
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$requiredTools = @('initdb.exe','pg_ctl.exe','pg_isready.exe','psql.exe','createdb.exe')
foreach ($tool in $requiredTools) {
    $path = Join-Path $PgBin $tool
    if (-not (Test-Path $path)) {
        throw "Required PostgreSQL tool is missing: $path"
    }
}

if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    Write-Output "Port $Port is in use; finding available port..."
    for ($p = 55435; $p -lt 55900; $p++) {
        if (-not (Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue)) {
            $Port = $p
            break
        }
    }
}
Write-Output "Using disposable PostgreSQL port $Port"

$runRoot = Join-Path $env:TEMP ('EdgeRetailsMasterPg_' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $runRoot 'data'
$pwFile = Join-Path $runRoot 'admin.pw'
$log = Join-Path $runRoot 'postgres.log'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$adminUser = 'er_p2_admin'
$testDb = 'edge_retails_phase2_test'
$fullDb = 'edge_retails_full_regression'

$adminPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($pwFile, $adminPassword, [Text.UTF8Encoding]::new($false))

$started = $false
$previousRunnerRoot = $env:EDGE_RETAILS_MASTER_PG_RUN_ROOT
$previousBackupKey = $env:EDGE_RETAILS_BACKUP_KEY
$previousBackupDirectory = $env:EDGE_RETAILS_BACKUP_DIR
$previousProductionStateDirectory = $env:EDGE_RETAILS_PRODUCTION_STATE_DIR
try {
    Write-Output 'PHASE2_PG_INIT_START'

    & (Join-Path $PgBin 'initdb.exe') -D $data --username=$adminUser --pwfile=$pwFile --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale
    if ($LASTEXITCODE -ne 0) {
        throw "initdb failed with exit code $LASTEXITCODE."
    }

    Add-Content -Path (Join-Path $data 'postgresql.conf') -Value @(
        '',
        "port = $Port",
        "listen_addresses = '127.0.0.1'",
        'max_connections = 60'
    )

    $clusterStart = Start-Process (Join-Path $PgBin 'pg_ctl.exe') -WindowStyle Hidden -ArgumentList @('-D', ('"' + $data + '"'), '-l', ('"' + $log + '"'), 'start') -PassThru -RedirectStandardOutput (Join-Path $runRoot 'start.stdout.txt') -RedirectStandardError (Join-Path $runRoot 'start.stderr.txt')
    $clusterStart.WaitForExit()
    # Readiness below verifies successful startup; Windows PowerShell may return a null ExitCode.
    $started = $true

    $env:PGPASSWORD = $adminPassword
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        & (Join-Path $PgBin 'pg_isready.exe') -h 127.0.0.1 -p $Port -U $adminUser | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 300
    }
    if (-not $ready) {
        throw 'Disposable PostgreSQL failed to become ready within timeout.'
    }

    Write-Output 'PHASE2_PG_CREATEDB'
    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $adminUser $testDb
    if ($LASTEXITCODE -ne 0) {
        throw "createdb failed with exit code $LASTEXITCODE."
    }

    $connectionString = "Host=127.0.0.1;Port=$Port;Database=$testDb;Username=$adminUser;Password=$adminPassword"
    $env:EDGE_RETAILS_TEST_DB = $connectionString
    $env:EDGE_RETAILS_MASTER_PG_RUN_ROOT = $runRoot
    $env:EDGE_RETAILS_PRODUCTION_STATE_DIR = (Join-Path $runRoot 'state')

    $serverVersionNum = & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $testDb -tA -c 'SHOW server_version_num'
    if ($LASTEXITCODE -ne 0 -or [int]$serverVersionNum -lt 180000 -or [int]$serverVersionNum -ge 190000) {
        throw "Phase 2 PostgreSQL certification requires PostgreSQL 18; server_version_num was '$serverVersionNum'."
    }
    Write-Output 'Provider = PostgreSQL 18 / Npgsql'
    Write-Output "PostgreSQL server_version_num = $serverVersionNum"

    Write-Output 'PHASE2_PG_APPLY_MIGRATIONS'
    & dotnet ef database update --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --connection $connectionString
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet ef database update failed with exit code $LASTEXITCODE."
    }

    Write-Output 'PHASE2_PG_RUN_INTEGRATION_TESTS'
    & dotnet test .\tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~CustomerKhataSuspensionPostgresTests|FullyQualifiedName~Phase7Pass2ThakaReadContractPostgresTests|FullyQualifiedName~Phase2ApiContractAndSecurityTests"
    if ($LASTEXITCODE -ne 0) {
        throw "Phase 2 PostgreSQL integration tests failed with exit code $LASTEXITCODE."
    }

    Write-Output "CUSTOMER_KHATA_CERTIFICATION_PASS"
}
finally {
    $env:EDGE_RETAILS_MASTER_PG_RUN_ROOT = $previousRunnerRoot
    $env:EDGE_RETAILS_TEST_DB = $null
    $env:EDGE_RETAILS_TEST_DB_HOST = $null
    $env:EDGE_RETAILS_TEST_DB_PORT = $null
    $env:EDGE_RETAILS_TEST_DB_NAME = $null
    $env:EDGE_RETAILS_TEST_DB_USER = $null
    $env:EDGE_RETAILS_TEST_DB_PASSWORD = $null
    $env:EDGE_RETAILS_TEST_DB_MAINT_USER = $null
    $env:EDGE_RETAILS_TEST_DB_MAINT_PASSWORD = $null
    $env:EDGE_RETAILS_TEST_DB_MAINT_DATABASE = $null
    $env:EDGE_RETAILS_PG_BIN = $null
    $env:EDGE_RETAILS_SPRINT8_ALLOW_DESTRUCTIVE_CUTOVER_TEST = $null
    $env:EDGE_RETAILS_BACKUP_KEY = $previousBackupKey
    $env:EDGE_RETAILS_BACKUP_DIR = $previousBackupDirectory
    $env:EDGE_RETAILS_PRODUCTION_STATE_DIR = $previousProductionStateDirectory
    $env:PGPASSWORD = $null

    if ($started) {
        Start-Process (Join-Path $PgBin 'pg_ctl.exe') -WindowStyle Hidden -ArgumentList @('-D', ('"' + $data + '"'), '-m', 'fast', 'stop') -Wait -RedirectStandardOutput (Join-Path $runRoot 'stop.stdout.txt') -RedirectStandardError (Join-Path $runRoot 'stop.stderr.txt')
    }

    Write-Output "Owned certification evidence retained at $runRoot"
}