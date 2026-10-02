param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55546
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
foreach ($tool in @('initdb.exe', 'pg_ctl.exe', 'pg_isready.exe', 'psql.exe', 'createdb.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PgBin $tool))) {
        throw "Required PostgreSQL tool is missing: $(Join-Path $PgBin $tool)"
    }
}

if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    $available = 55547..55899 | Where-Object {
        -not (Get-NetTCPConnection -State Listen -LocalPort $_ -ErrorAction SilentlyContinue)
    } | Select-Object -First 1
    if (-not $available) { throw 'No isolated PostgreSQL rehearsal port is available.' }
    $Port = $available
}

$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('EdgeRetailsPhase3COwnerRecovery_' + [Guid]::NewGuid().ToString('N'))))
$data = Join-Path $runRoot 'data'
$passwordFile = Join-Path $runRoot 'admin.pw'
$postgresLog = Join-Path $runRoot 'postgres.log'
$adminUser = 'er_p3c_recovery'
$secretBytes = New-Object byte[] 32
$randomGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $randomGenerator.GetBytes($secretBytes) } finally { $randomGenerator.Dispose() }
$adminPassword = [BitConverter]::ToString($secretBytes).Replace('-', '')
$previous = @{
    EDGE_RETAILS_DB = $env:EDGE_RETAILS_DB
    EDGE_RETAILS_TEST_DB = $env:EDGE_RETAILS_TEST_DB
    EDGE_RETAILS_PG_BIN = $env:EDGE_RETAILS_PG_BIN
    ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
    PGPASSWORD = $env:PGPASSWORD
}
$started = $false

try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    [IO.File]::WriteAllText($passwordFile, $adminPassword, [Text.UTF8Encoding]::new($false))
    Write-Output 'PHASE3C_PG_INIT_START'
    & (Join-Path $PgBin 'initdb.exe') -D $data --username=$adminUser --pwfile=$passwordFile --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale
    if ($LASTEXITCODE -ne 0) { throw "Disposable PostgreSQL initdb failed with exit code $LASTEXITCODE." }
    Add-Content -LiteralPath (Join-Path $data 'postgresql.conf') -Value @('', "port = $Port", "listen_addresses = '127.0.0.1'", 'max_connections = 60')
    & (Join-Path $PgBin 'pg_ctl.exe') -D $data -l $postgresLog start
    if ($LASTEXITCODE -ne 0) { throw "Disposable PostgreSQL start failed with exit code $LASTEXITCODE." }
    $started = $true

    $env:PGPASSWORD = $adminPassword
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        & (Join-Path $PgBin 'pg_isready.exe') -h 127.0.0.1 -p $Port -U $adminUser | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'Disposable PostgreSQL did not become ready.' }

    $version = & (Join-Path $PgBin 'psql.exe') -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $adminUser -d postgres -tA -c 'SHOW server_version_num'
    if ($LASTEXITCODE -ne 0 -or [int]$version -lt 180000 -or [int]$version -ge 190000) {
        throw "Recovery rehearsal requires PostgreSQL 18; server_version_num was '$version'."
    }
    Write-Output 'Provider = PostgreSQL 18 / Npgsql'
    Write-Output "PostgreSQL server_version_num = $version"

    $zeroDb = 'edge_retails_p3c_zero_latest'
    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $adminUser $zeroDb
    if ($LASTEXITCODE -ne 0) { throw 'Could not create zero-to-latest recovery test database.' }
    $zeroConnection = "Host=127.0.0.1;Port=$Port;Database=$zeroDb;Username=$adminUser;Password=$adminPassword"
    $env:EDGE_RETAILS_DB = $zeroConnection
    $env:EDGE_RETAILS_TEST_DB = $zeroConnection
    $env:ASPNETCORE_ENVIRONMENT = 'Testing'
    $env:EDGE_RETAILS_PG_BIN = $PgBin
    Write-Output 'PHASE3C_PG_ZERO_TO_LATEST_START'
    & dotnet ef database update --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Zero-to-latest EF migration failed with exit code $LASTEXITCODE." }
    $zeroSql = 'SELECT CASE WHEN (SELECT count(*) FROM system.__ef_migrations_history) = 20 AND (SELECT max(to_jsonb(m)->>''MigrationId'') FROM system.__ef_migrations_history m) = ''20260930065058_Phase3COwnerPinAuthorizationConsumption'' AND (SELECT count(*) FROM pg_indexes WHERE schemaname=''audit'' AND tablename=''business_events'' AND indexname=''ux_business_events_pin_recovery_success_operation'' AND indexdef ILIKE ''%UNIQUE%'' AND indexdef ILIKE ''%WHERE%'') = 1 AND (SELECT count(*) FROM pg_indexes WHERE schemaname=''audit'' AND tablename=''business_events'' AND indexname=''ux_business_events_pin_recovery_consumed_nonce'' AND indexdef ILIKE ''%UNIQUE%'' AND indexdef ILIKE ''%WHERE%'') = 1 AND (SELECT count(*) FROM pg_indexes WHERE schemaname=''audit'' AND tablename=''business_events'' AND indexname=''ix_business_events_correlation_id'') = 1 THEN 1 ELSE 0 END;'
    $zeroCheck = & (Join-Path $PgBin 'psql.exe') -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $adminUser -d $zeroDb -tA -c $zeroSql
    if ($LASTEXITCODE -ne 0 -or [int]$zeroCheck -ne 1) { throw 'Zero-to-latest migration history or replay index verification failed.' }
    Write-Output 'PHASE3C_PG_ZERO_TO_LATEST_PASS History=20 ReplayIndex=UniqueFiltered ConsumedNonceIndex=UniqueFiltered GeneralCorrelationIndex=Present'

    $upgradeDb = 'edge_retails_p3c_upgrade'
    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $adminUser $upgradeDb
    if ($LASTEXITCODE -ne 0) { throw 'Could not create existing-database recovery upgrade test database.' }
    $upgradeConnection = "Host=127.0.0.1;Port=$Port;Database=$upgradeDb;Username=$adminUser;Password=$adminPassword"
    $env:EDGE_RETAILS_DB = $upgradeConnection
    $env:EDGE_RETAILS_TEST_DB = $upgradeConnection
    Write-Output 'PHASE3C_PG_EXISTING_DATABASE_UPGRADE_START'
    & dotnet ef database update 20260929100000_Phase3LegacyCategoryUpgradeRecovery --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "18-migration baseline creation failed with exit code $LASTEXITCODE." }
    $seedAudit = "INSERT INTO audit.business_events (id, actor_id, action, entity_type, entity_id, correlation_id, occurred_at, summary) VALUES (gen_random_uuid(), gen_random_uuid(), 'RECOVERY_REHEARSAL', 'TEST', NULL, '00000000-0000-0000-0000-000000000010', now(), 'safe fixture'), (gen_random_uuid(), gen_random_uuid(), 'RECOVERY_REHEARSAL', 'TEST', NULL, '00000000-0000-0000-0000-000000000010', now(), 'safe fixture');"
    & (Join-Path $PgBin 'psql.exe') -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $adminUser -d $upgradeDb -c $seedAudit | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed isolated pre-existing audit correlations.' }
    & dotnet ef database update 20260930053030_Phase3COwnerPinRecoveryReplaySafety --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "18-to-19 migration failed with exit code $LASTEXITCODE." }
    & dotnet ef database update 20260930065058_Phase3COwnerPinAuthorizationConsumption --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "19-to-20 nonce-consumption migration failed with exit code $LASTEXITCODE." }
    $upgradeCheck = & (Join-Path $PgBin 'psql.exe') -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $adminUser -d $upgradeDb -tA -c "SELECT CASE WHEN (SELECT count(*) FROM system.__ef_migrations_history) = 20 AND (SELECT count(*) FROM audit.business_events WHERE action='RECOVERY_REHEARSAL' AND correlation_id='00000000-0000-0000-0000-000000000010') = 2 AND (SELECT count(*) FROM pg_indexes WHERE schemaname='audit' AND tablename='business_events' AND indexname='ux_business_events_pin_recovery_success_operation' AND indexdef ILIKE '%UNIQUE%' AND indexdef ILIKE '%WHERE%') = 1 AND (SELECT count(*) FROM pg_indexes WHERE schemaname='audit' AND tablename='business_events' AND indexname='ux_business_events_pin_recovery_consumed_nonce' AND indexdef ILIKE '%UNIQUE%' AND indexdef ILIKE '%WHERE%') = 1 AND (SELECT count(*) FROM pg_indexes WHERE schemaname='audit' AND tablename='business_events' AND indexname='ix_business_events_correlation_id') = 1 THEN 1 ELSE 0 END;"
    if ($LASTEXITCODE -ne 0 -or [int]$upgradeCheck -ne 1) { throw '18-to-19 upgrade data/index verification failed.' }
    Write-Output 'PHASE3C_PG_EXISTING_DATABASE_UPGRADE_PASS PreservedAuditRows=2 History=20'

    Write-Output 'PHASE3C_PG_RECOVERY_HANDLER_TEST_START'
    & dotnet test .\tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj --configuration Release --no-restore --filter FullyQualifiedName~Phase3OwnerPinRecoveryPostgresTests
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL recovery handler test failed with exit code $LASTEXITCODE." }
    Write-Output 'PHASE3C_PG_RECOVERY_REHEARSAL_PASS'
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
    if ($started) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $data -m immediate stop | Out-Null
    }
    $safePrefix = $tempRoot + '\EdgeRetailsPhase3COwnerRecovery_'
    if (-not $runRoot.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Disposable cleanup target escaped the intended temporary directory: $runRoot"
    }
    if (Test-Path -LiteralPath $runRoot) {
        Start-Sleep -Milliseconds 500
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
