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

$runRoot = Join-Path $env:TEMP ('EdgeRetailsPhase2Pg_' + [Guid]::NewGuid().ToString('N'))
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

    & (Join-Path $PgBin 'pg_ctl.exe') -D $data -l $log start
    if ($LASTEXITCODE -ne 0) {
        throw "pg_ctl start failed with exit code $LASTEXITCODE."
    }
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
    & dotnet test .\tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Phase2TransactionalPostgresTests|FullyQualifiedName~Phase2ConcurrencyPostgresTests|FullyQualifiedName~Phase2ReconciliationPostgresTests|FullyQualifiedName~Phase1PostgresIntegrationTests|FullyQualifiedName~SalesPurchasingTransactionalPostgresTests|FullyQualifiedName~SerializedSalesPurchasingPostgresTests|FullyQualifiedName~ShopHolderOperationalPostgresTests|FullyQualifiedName~ArchitectureDependencyTests|FullyQualifiedName~Phase2PostgresCertificationTests|FullyQualifiedName~Phase2ApiContractAndSecurityTests"
    if ($LASTEXITCODE -ne 0) {
        throw "Phase 2 PostgreSQL integration tests failed with exit code $LASTEXITCODE."
    }

    Write-Output 'PHASE2_PG_CREATE_FULL_REGRESSION_DATABASE'
    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $adminUser $fullDb
    if ($LASTEXITCODE -ne 0) {
        throw "createdb for full solution regression failed with exit code $LASTEXITCODE."
    }

    $runtimeUser = 'er_p2_full_runtime'
    $runtimePassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    Write-Output 'PHASE2_PG_CREATE_FULL_REGRESSION_RUNTIME_USER'
    $createRuntimeUserSql = @"
CREATE USER $runtimeUser WITH PASSWORD '$runtimePassword' CREATEDB;
GRANT ALL PRIVILEGES ON DATABASE $fullDb TO $runtimeUser;
GRANT pg_read_all_data, pg_write_all_data TO $runtimeUser;
"@
    & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $fullDb -c $createRuntimeUserSql
    if ($LASTEXITCODE -ne 0) {
        throw 'Creating runtime user for full solution regression failed.'
    }

    $fullConnectionString = "Host=127.0.0.1;Port=$Port;Database=$fullDb;Username=$adminUser;Password=$adminPassword"
    $env:EDGE_RETAILS_TEST_DB = $fullConnectionString
    $env:EDGE_RETAILS_TEST_DB_HOST = '127.0.0.1'
    $env:EDGE_RETAILS_TEST_DB_PORT = $Port.ToString()
    $env:EDGE_RETAILS_TEST_DB_NAME = $fullDb
    $env:EDGE_RETAILS_TEST_DB_USER = $runtimeUser
    $env:EDGE_RETAILS_TEST_DB_PASSWORD = $runtimePassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_USER = $adminUser
    $env:EDGE_RETAILS_TEST_DB_MAINT_PASSWORD = $adminPassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_DATABASE = 'postgres'
    $env:EDGE_RETAILS_PG_BIN = $PgBin
    $env:EDGE_RETAILS_SPRINT8_ALLOW_DESTRUCTIVE_CUTOVER_TEST = 'YES_DISPOSABLE_ONLY'
    $env:EDGE_RETAILS_BACKUP_KEY = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $env:EDGE_RETAILS_BACKUP_DIR = Join-Path $runRoot 'full-regression-backups'
    $env:EDGE_RETAILS_PRODUCTION_STATE_DIR = Join-Path $runRoot 'full-regression-state'
    [System.IO.Directory]::CreateDirectory($env:EDGE_RETAILS_BACKUP_DIR) | Out-Null
    Write-Output "Full regression database = $fullDb"
    Write-Output 'PHASE2_PG_APPLY_MIGRATIONS_FOR_FULL_REGRESSION'
    & dotnet ef database update --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --connection $fullConnectionString
    if ($LASTEXITCODE -ne 0) {
        throw "Full regression database migration failed with exit code $LASTEXITCODE."
    }

    Write-Output 'PHASE2_PG_GRANT_FULL_REGRESSION_RUNTIME_SCHEMA'
    $grantRuntimeSchemaSql = @"
GRANT ALL ON SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty TO $runtimeUser;
GRANT ALL ON ALL TABLES IN SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty TO $runtimeUser;
GRANT ALL ON ALL SEQUENCES IN SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty TO $runtimeUser;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty GRANT ALL ON TABLES TO $runtimeUser;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty GRANT ALL ON SEQUENCES TO $runtimeUser;
"@
    & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $fullDb -c $grantRuntimeSchemaSql
    if ($LASTEXITCODE -ne 0) {
        throw 'Granting schema privileges to full regression runtime user failed.'
    }

    Write-Output 'FULL_SOLUTION_REGRESSION'
    & dotnet test .\EdgeRetails.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Full solution regression failed with exit code $LASTEXITCODE."
    }

    Write-Output 'PHASE2_PG_INSPECT_SCHEMA'
    $inspectSql = @"
SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema IN ('catalog', 'inventory', 'parties', 'finance', 'warranty', 'sales', 'identity', 'system') ORDER BY table_schema, table_name;
"@
    & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $testDb -c $inspectSql

    Write-Output 'PHASE2_DISPOSABLE_POSTGRES_REHEARSAL_PASS'
}
finally {
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
        & (Join-Path $PgBin 'pg_ctl.exe') -D $data -m immediate stop | Out-Null
    }

    if (Test-Path $runRoot) {
        Start-Sleep -Milliseconds 500
        Remove-Item -Path $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
