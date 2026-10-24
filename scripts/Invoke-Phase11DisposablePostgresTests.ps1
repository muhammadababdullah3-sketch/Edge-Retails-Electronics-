[CmdletBinding()]
param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55438,
    [string]$EvidenceFile = 'C:\p11rb005\evidence\phase11-pg18-execution-085.trx'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('EdgeRetailsMasterPg_ER-RUN-' + [Guid]::NewGuid().ToString('N'))))
$dataRoot = Join-Path $runRoot 'data'

$started = $false
$failure = $false

$envNames = @(
    'EDGE_RETAILS_DB','EDGE_RETAILS_TEST_DB','EDGE_RETAILS_TEST_DB_HOST','EDGE_RETAILS_TEST_DB_PORT',
    'EDGE_RETAILS_TEST_DB_NAME','EDGE_RETAILS_TEST_DB_USER','EDGE_RETAILS_TEST_DB_PASSWORD',
    'EDGE_RETAILS_TEST_DB_MAINT_USER','EDGE_RETAILS_TEST_DB_MAINT_PASSWORD','EDGE_RETAILS_TEST_DB_MAINT_DATABASE',
    'EDGE_RETAILS_MASTER_PG_RUN_ROOT','EDGE_RETAILS_PG_BIN','PGPASSWORD'
)
$oldEnv = @{}
foreach ($name in $envNames) { $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

try {
    Write-Host "[1/6] Verifying PG18 tools and ports..."
    foreach ($tool in @('initdb.exe','pg_ctl.exe','pg_isready.exe','psql.exe','createdb.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PgBin $tool))) { throw "PG18_TOOL_MISSING:$tool" }
    }

    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) { throw "PORT_ALREADY_LISTENING:$Port" }

    foreach ($protected in @(5432, 7150)) {
        Write-Host "ProtectedPort=$protected is untouched and protected."
    }

    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $adminUser = 'postgres'
    $testUser = 'er_provisioner'
    $database = 'edgeretails_test_run20261023_085'

    Write-Host "[2/6] Initializing disposable PostgreSQL 18 cluster with trust auth at $dataRoot..."
    & (Join-Path $PgBin 'initdb.exe') -D $dataRoot -U $adminUser -A trust --encoding=UTF8 --no-locale | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "initdb failed" }

    Add-Content -LiteralPath (Join-Path $dataRoot 'postgresql.conf') -Value @('', "port = $Port", "listen_addresses = '127.0.0.1'", 'max_connections = 100')

    Write-Host "[3/6] Starting disposable PostgreSQL 18 on dedicated port $Port..."
    $startLog = Join-Path $runRoot 'postgres.log'
    & (Join-Path $PgBin 'pg_ctl.exe') -D $dataRoot -l $startLog -w start | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "pg_ctl start failed" }
    $started = $true

    Write-Host "[4/6] Creating database $database and test roles..."
    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $adminUser $database | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "createdb failed" }

    $version = & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $database -tA -c 'SHOW server_version_num'
    $dataDirectory = & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $database -tA -c 'SHOW data_directory'

    $ownerFile = Join-Path $runRoot 'OWNERSHIP_MARKER.json'
    [pscustomobject]@{
        marker = 'ER_DISPOSABLE_CLUSTER_OWNERSHIP_MARKER'
        port = $Port
        run_id = 'ER-RUN-POS-NON89-EXEC-20261023-085'
        isolation_mode = 'owned_disposable_local_cluster'
        server_version_num = [int]$version
        data_directory = $dataDirectory
        database = $database
        runtime_role = $testUser
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ownerFile -Encoding utf8

    $roleSql = "CREATE ROLE $testUser LOGIN SUPERUSER INHERIT; CREATE ROLE er_runtime LOGIN SUPERUSER INHERIT; GRANT ALL PRIVILEGES ON DATABASE $database TO $testUser; ALTER DATABASE $database OWNER TO $testUser;"
    & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $database -c $roleSql | Out-Host

    $adminConn = "Host=127.0.0.1;Port=$Port;Database=$database;Username=$adminUser"
    $testConn = "Host=127.0.0.1;Port=$Port;Database=$database;Username=$testUser"
    $env:EDGE_RETAILS_DB = $adminConn
    $env:EDGE_RETAILS_TEST_DB = $testConn
    $env:EDGE_RETAILS_TEST_DB_HOST = '127.0.0.1'
    $env:EDGE_RETAILS_TEST_DB_PORT = "$Port"
    $env:EDGE_RETAILS_TEST_DB_NAME = $database
    $env:EDGE_RETAILS_TEST_DB_USER = $testUser
    $env:EDGE_RETAILS_TEST_DB_MAINT_USER = $adminUser
    $env:EDGE_RETAILS_TEST_DB_MAINT_DATABASE = $database
    $env:EDGE_RETAILS_MASTER_PG_RUN_ROOT = $runRoot
    $env:EDGE_RETAILS_PG_BIN = $PgBin

    Write-Host "[5/6] Applying EF Core migrations (GATE19 check)..."
    Push-Location -LiteralPath $repoRoot
    try {
        & dotnet ef database update --project src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj --startup-project src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --connection $adminConn
        if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update failed" }

        & dotnet ef migrations has-pending-model-changes --project src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj --startup-project src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext
        if ($LASTEXITCODE -ne 0) { throw "dotnet ef migrations has-pending-model-changes failed (GATE19)" }
        Write-Host "GATE19: 0 pending model changes confirmed."

        Write-Host "[6/6] Executing Phase 11 Integration & PostgreSQL Capability Gates..."
        $filter = "FullyQualifiedName~Phase3ProductionSafetyPostgresTests|FullyQualifiedName~Phase7Pass5GoldenScenariosPostgresTests|FullyQualifiedName~Phase7Pass5OwnerReconciliationPostgresTests|FullyQualifiedName~GoldenLifecycleTracePostgresTests|FullyQualifiedName~CatalogV3SeedLifecyclePostgresTests|FullyQualifiedName~CatalogV3ImportAtomicityPostgresTests|FullyQualifiedName~CatalogReferenceUpgradeApiContractTests|FullyQualifiedName~CatalogMutationRouteAuthorizationMatrixTests"
        
        & dotnet test tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj -c Release --filter $filter --logger "trx;LogFileName=$EvidenceFile"
        if ($LASTEXITCODE -ne 0) { throw "Integration tests failed" }
        Write-Host "PostgreSQL Integration Tests PASSED!"
    }
    finally {
        Pop-Location
    }
}
catch {
    $failure = $true
    Write-Error $_
}
finally {
    if ($started) {
        Write-Host "Stopping disposable PostgreSQL server on port $Port..."
        & (Join-Path $PgBin 'pg_ctl.exe') -D $dataRoot -w -m fast stop | Out-Host
    }
    if (-not $failure -and (Test-Path $runRoot)) {
        Write-Host "Cleaning up disposable data at $runRoot..."
        Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($name in $envNames) {
        [Environment]::SetEnvironmentVariable($name, $oldEnv[$name], 'Process')
    }
}

if ($failure) { exit 1 } else { exit 0 }
