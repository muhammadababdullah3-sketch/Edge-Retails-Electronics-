param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55535
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
    for ($p = 55536; $p -lt 55990; $p++) {
        if (-not (Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue)) {
            $Port = $p
            break
        }
    }
}
Write-Output "Using disposable PostgreSQL port $Port"

$runRoot = Join-Path $env:TEMP ('EdgeRetailsPhase4Pg_' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $runRoot 'data'
$pwFile = Join-Path $runRoot 'admin.pw'
$log = Join-Path $runRoot 'postgres.log'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$adminUser = 'er_p4_admin'
$testDb = 'edge_retails_phase4_test'

$adminPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($pwFile, $adminPassword, [Text.UTF8Encoding]::new($false))

$runtimeUser = 'er_p4_runtime'
$runtimePassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')

$started = $false
try {
    Write-Output 'PHASE4_PG_INIT_START'

    & (Join-Path $PgBin 'initdb.exe') -D $data --username=$adminUser --pwfile=$pwFile --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale
    if ($LASTEXITCODE -ne 0) {
        throw "initdb failed with exit code $LASTEXITCODE."
    }

    Add-Content -Path (Join-Path $data 'postgresql.conf') -Value @(
        '',
        "port = $Port",
        "listen_addresses = '127.0.0.1'",
        'max_connections = 60',
        'fsync = on',
        'full_page_writes = on',
        'synchronous_commit = on'
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

    Write-Output 'PHASE4_PG_CREATEDB'
    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $adminUser $testDb
    if ($LASTEXITCODE -ne 0) {
        throw "createdb failed with exit code $LASTEXITCODE."
    }

    Write-Output 'PHASE4_PG_CREATE_RUNTIME_USER'
    $createUserSql = @"
CREATE USER $runtimeUser WITH PASSWORD '$runtimePassword' CREATEDB;
GRANT ALL PRIVILEGES ON DATABASE $testDb TO $runtimeUser;
GRANT pg_read_all_data, pg_write_all_data TO $runtimeUser;
"@
    & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $testDb -c $createUserSql
    if ($LASTEXITCODE -ne 0) {
        throw "Creating runtime user failed."
    }

    $connectionString = "Host=127.0.0.1;Port=$Port;Database=$testDb;Username=$adminUser;Password=$adminPassword"
    $env:EDGE_RETAILS_TEST_DB = $connectionString
    $env:EDGE_RETAILS_PRODUCTION_STATE_DIR = (Join-Path $runRoot 'state')
    $env:EDGE_RETAILS_TEST_DB_HOST = '127.0.0.1'
    $env:EDGE_RETAILS_TEST_DB_PORT = $Port.ToString()
    $env:EDGE_RETAILS_TEST_DB_NAME = $testDb
    $env:EDGE_RETAILS_TEST_DB_USER = $runtimeUser
    $env:EDGE_RETAILS_TEST_DB_PASSWORD = $runtimePassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_USER = $adminUser
    $env:EDGE_RETAILS_TEST_DB_MAINT_PASSWORD = $adminPassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_DATABASE = 'postgres'
    $env:EDGE_RETAILS_PG_BIN = $PgBin
    $env:EDGE_RETAILS_SPRINT8_ALLOW_DESTRUCTIVE_CUTOVER_TEST = 'YES_DISPOSABLE_ONLY'

    Write-Output 'PHASE4_PG_APPLY_MIGRATIONS'
    & dotnet ef database update --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --connection $connectionString
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet ef database update failed with exit code $LASTEXITCODE."
    }

    Write-Output 'PHASE4_PG_GRANT_RUNTIME_SCHEMA'
    $grantSql = @"
GRANT ALL ON SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty TO $runtimeUser;
GRANT ALL ON ALL TABLES IN SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty TO $runtimeUser;
GRANT ALL ON ALL SEQUENCES IN SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty TO $runtimeUser;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty GRANT ALL ON TABLES TO $runtimeUser;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit, catalog, finance, identity, inventory, parties, purchasing, sales, system, thaka, warranty GRANT ALL ON SEQUENCES TO $runtimeUser;
"@
    & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $testDb -c $grantSql
    if ($LASTEXITCODE -ne 0) {
        throw "Granting schema privileges to runtime user failed."
    }

    Write-Output 'PHASE4_PG_VERIFY_SCHEMA_TABLES'
    $tableCountSql = @"
SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('catalog', 'inventory', 'parties', 'finance', 'warranty', 'sales', 'identity', 'system');
"@
    $tableCount = (& (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $testDb -t -A -c $tableCountSql).Trim()
    Write-Output "Verified Table Count: $tableCount"
    if ([int]$tableCount -lt 63) {
        throw "Expected at least 63 canonical tables (including system.terminals), but found $tableCount."
    }

    Write-Output 'PHASE4_PG_VERIFY_TERMINALS_TABLE'
    $terminalsCheckSql = @"
SELECT count(*) FROM information_schema.tables WHERE table_schema = 'system' AND table_name = 'terminals';
"@
    $terminalsCount = (& (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $testDb -t -A -c $terminalsCheckSql).Trim()
    if ([int]$terminalsCount -ne 1) {
        throw "Terminals table system.terminals not found in PostgreSQL database."
    }

    Write-Output 'PHASE4_RUN_PHASE4_UNIT_TESTS'
    & dotnet test .\tests\EdgeRetails.UnitTests\EdgeRetails.UnitTests.csproj -c Release --filter "FullyQualifiedName~Phase4GatewayAndTerminalUnitTests|FullyQualifiedName~Phase4UnknownOutcomeAndRevalidationTests" --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Phase 4 Unit tests failed with exit code $LASTEXITCODE."
    }

    Write-Output 'PHASE4_RUN_PHASE4_INTEGRATION_TESTS'
    $phase4IntegrationOutput = @(& dotnet test .\tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Phase4LanServerIntegrationTests|FullyQualifiedName~Phase4MultiTerminalConcurrencyTests" --no-restore 2>&1)
    $phase4IntegrationExitCode = $LASTEXITCODE
    $phase4IntegrationOutput | Write-Output
    if ($phase4IntegrationExitCode -ne 0) {
        throw "Phase 4 Integration and Concurrency tests failed with exit code $phase4IntegrationExitCode."
    }
    $phase4Summary = ($phase4IntegrationOutput | Where-Object { $_ -match 'Passed!.*Total:' } | Select-Object -Last 1).ToString()
    if ([string]::IsNullOrWhiteSpace($phase4Summary)) {
        throw 'Phase 4 Integration and Concurrency produced no PASS summary; refusing to certify.'
    }
    if ($phase4Summary -notmatch 'Total:\s*37') {
        throw "Phase 4 Integration and Concurrency expected 37 executed tests (including mandatory security regressions), but summary was: $phase4Summary"
    }
    if ($phase4Summary -match 'Skipped:\s*[1-9]') {
        throw "Phase 4 Integration and Concurrency contains skipped mandatory tests: $phase4Summary"
    }
    if ($phase4Summary -notmatch 'Failed:\s*0') {
        throw "Phase 4 Integration and Concurrency contains failures: $phase4Summary"
    }

    Write-Output 'PHASE4_RUN_PHASE3_SAFETY_REGRESSION_TESTS'
    & dotnet test .\tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Phase3ProductionSafetyPostgresTests|FullyQualifiedName~Phase3CrashRestartIntegrationTests|FullyQualifiedName~Phase3UnknownOutcomeReplayIntegrationTests|FullyQualifiedName~Phase3RestoreInterruptionIntegrationTests|FullyQualifiedName~Phase3DatabaseSafetyNegativeIntegrationTests|FullyQualifiedName~PostgresBackupRestoreIntegrationTests|FullyQualifiedName~PostgresRestoreCutoverLiveClosureTests" --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Phase 3 safety regression tests failed with exit code $LASTEXITCODE."
    }

    Write-Output 'PHASE4_RUN_PHASE2_REGRESSION_TESTS'
    & dotnet test .\tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Phase2TransactionalPostgresTests|FullyQualifiedName~Phase2ConcurrencyPostgresTests|FullyQualifiedName~Phase2ReconciliationPostgresTests|FullyQualifiedName~Phase1PostgresIntegrationTests|FullyQualifiedName~SalesPurchasingTransactionalPostgresTests|FullyQualifiedName~SerializedSalesPurchasingPostgresTests|FullyQualifiedName~ShopHolderOperationalPostgresTests|FullyQualifiedName~ArchitectureDependencyTests" --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Phase 2 regression tests failed on Phase 4 schema with exit code $LASTEXITCODE."
    }

    Write-Output 'PHASE4_MULTI_TERMINAL_REHEARSAL_PASS'
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
    $env:EDGE_RETAILS_PRODUCTION_STATE_DIR = $null
    $env:PGPASSWORD = $null

    if ($started) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $data -m immediate stop | Out-Null
    }

    if (Test-Path $runRoot) {
        Start-Sleep -Milliseconds 500
        Remove-Item -Path $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
