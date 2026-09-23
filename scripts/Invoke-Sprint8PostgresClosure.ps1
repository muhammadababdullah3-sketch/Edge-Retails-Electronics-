param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55432
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$requiredTools = @('initdb.exe','pg_ctl.exe','pg_isready.exe','psql.exe','createdb.exe','pg_dump.exe','pg_restore.exe')
foreach ($tool in $requiredTools) {
    $path = Join-Path $PgBin $tool
    if (-not (Test-Path $path)) {
        throw "Required PostgreSQL tool is missing: $path"
    }
}

if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    throw "Disposable PostgreSQL port $Port is already in use."
}

$runRoot = Join-Path $env:TEMP ('EdgeRetailsSprint8Pg_' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $runRoot 'data'
$pwFile = Join-Path $runRoot 'admin.pw'
$log = Join-Path $runRoot 'postgres.log'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$adminUser = 'er_s8_admin'
$runtimeUser = 'er_s8_runtime'
$maintenanceUser = 'er_s8_maint'
$sourceDb = 'edge_retails_s8_source'

$adminPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$runtimePassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$maintenancePassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($pwFile, $adminPassword, [Text.UTF8Encoding]::new($false))

$started = $false
try {
    Write-Output 'S8PG_INIT_START'

    & (Join-Path $PgBin 'initdb.exe') -D $data --username=$adminUser --pwfile=$pwFile --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale
    if ($LASTEXITCODE -ne 0) {
        throw "initdb failed with exit code $LASTEXITCODE."
    }

    Add-Content -Path (Join-Path $data 'postgresql.conf') -Value @(
        '',
        "port = $Port",
        "listen_addresses = '127.0.0.1'",
        'max_connections = 40'
    )

    & (Join-Path $PgBin 'pg_ctl.exe') -D $data -l $log start
    if ($LASTEXITCODE -ne 0) {
        throw "pg_ctl start failed with exit code $LASTEXITCODE."
    }
    $started = $true

    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        & (Join-Path $PgBin 'pg_isready.exe') -h 127.0.0.1 -p $Port -q
        if ($LASTEXITCODE -eq 0) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not $ready) {
        throw 'Disposable PostgreSQL cluster did not become ready.'
    }
    Write-Output 'S8PG_READY'

    $env:PGPASSWORD = $adminPassword
    $roleSql = "CREATE ROLE $runtimeUser LOGIN PASSWORD '$runtimePassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT; CREATE ROLE $maintenanceUser LOGIN PASSWORD '$maintenancePassword' SUPERUSER;"
    & (Join-Path $PgBin 'psql.exe') --no-password --host 127.0.0.1 --port $Port --username $adminUser --dbname postgres --command $roleSql
    if ($LASTEXITCODE -ne 0) {
        throw "PostgreSQL role creation failed with exit code $LASTEXITCODE."
    }

    & (Join-Path $PgBin 'createdb.exe') --no-password --host 127.0.0.1 --port $Port --username $adminUser --owner $runtimeUser $sourceDb
    if ($LASTEXITCODE -ne 0) {
        throw "Source database creation failed with exit code $LASTEXITCODE."
    }
    Write-Output 'S8PG_IDENTITIES_READY'

    $env:EDGE_RETAILS_DB = "Host=127.0.0.1;Port=$Port;Database=$sourceDb;Username=$runtimeUser;Password=$runtimePassword;Pooling=false"
    dotnet ef database update --project 'src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj' --context EdgeRetailsDbContext --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "Canonical EF migration update failed with exit code $LASTEXITCODE."
    }
    Write-Output 'S8PG_CANONICAL_MIGRATIONS_READY'

    $env:EDGE_RETAILS_TEST_DB_HOST = '127.0.0.1'
    $env:EDGE_RETAILS_TEST_DB_PORT = $Port.ToString()
    $env:EDGE_RETAILS_TEST_DB_NAME = $sourceDb
    $env:EDGE_RETAILS_TEST_DB_USER = $runtimeUser
    $env:EDGE_RETAILS_TEST_DB_PASSWORD = $runtimePassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_USER = $maintenanceUser
    $env:EDGE_RETAILS_TEST_DB_MAINT_PASSWORD = $maintenancePassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_DATABASE = 'postgres'
    $env:EDGE_RETAILS_PG_BIN = $PgBin
    $env:EDGE_RETAILS_SPRINT8_ALLOW_DESTRUCTIVE_CUTOVER_TEST = 'YES_DISPOSABLE_ONLY'

    dotnet test 'tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj' -c Debug --no-build --no-restore -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~EdgeRetails.IntegrationTests.Sprint8'
    if ($LASTEXITCODE -ne 0) {
        throw "Sprint 8 Debug PostgreSQL closure gate failed with exit code $LASTEXITCODE."
    }
    Write-Output 'S8PG_DEBUG_INTEGRATION_PASS'

    dotnet test 'tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj' -c Release --no-build --no-restore -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~EdgeRetails.IntegrationTests.Sprint8'
    if ($LASTEXITCODE -ne 0) {
        throw "Sprint 8 Release PostgreSQL closure gate failed with exit code $LASTEXITCODE."
    }
    Write-Output 'S8PG_RELEASE_INTEGRATION_PASS'
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:EDGE_RETAILS_DB -ErrorAction SilentlyContinue

    foreach ($name in @(
        'EDGE_RETAILS_TEST_DB_HOST',
        'EDGE_RETAILS_TEST_DB_PORT',
        'EDGE_RETAILS_TEST_DB_NAME',
        'EDGE_RETAILS_TEST_DB_USER',
        'EDGE_RETAILS_TEST_DB_PASSWORD',
        'EDGE_RETAILS_TEST_DB_MAINT_USER',
        'EDGE_RETAILS_TEST_DB_MAINT_PASSWORD',
        'EDGE_RETAILS_TEST_DB_MAINT_DATABASE',
        'EDGE_RETAILS_PG_BIN',
        'EDGE_RETAILS_SPRINT8_ALLOW_DESTRUCTIVE_CUTOVER_TEST'
    )) {
        Remove-Item ('Env:' + $name) -ErrorAction SilentlyContinue
    }

    if ($started) {
        try {
            & (Join-Path $PgBin 'pg_ctl.exe') -D $data -m fast stop
        }
        catch {
        }
    }

    try {
        Remove-Item $runRoot -Recurse -Force
    }
    catch {
    }

    Write-Output 'S8PG_CLEANUP_COMPLETE'
}
