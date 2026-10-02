param(
    [Parameter(Mandatory)][string]$PgBin,
    [Parameter(Mandatory)][int]$Port,
    [Parameter(Mandatory)][string]$AdminUser,
    [Parameter(Mandatory)][string]$AdminPassword,
    [Parameter(Mandatory)][string]$RunRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$psql = Join-Path $PgBin 'psql.exe'
$cases = @(
    @{ Label = 'same_initial'; SecondName = 'Fridge'; SecondSymbol = 'F001' },
    @{ Label = 'different_initial'; SecondName = 'Bulb'; SecondSymbol = 'B000' }
)

foreach ($case in $cases) {
    $sourceDb = 'edge_retails_phase3_' + $case.Label
    $restoredDb = $sourceDb + '_restored'
    $sourceConnection = "Host=127.0.0.1;Port=$Port;Database=$sourceDb;Username=$AdminUser;Password=$AdminPassword"
    $restoredConnection = "Host=127.0.0.1;Port=$Port;Database=$restoredDb;Username=$AdminUser;Password=$AdminPassword"
    Write-Output "PHASE3_PG_LEGACY_CATEGORY_CASE_START=$($case.Label)"

    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $AdminUser $sourceDb
    if ($LASTEXITCODE -ne 0) { throw "Legacy source createdb failed for $($case.Label)." }
    & dotnet ef database update 20260923125420_Phase5PurchaseHistoryOrderingIndex --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release --connection $sourceConnection
    if ($LASTEXITCODE -ne 0) { throw "Legacy baseline migration failed for $($case.Label), exit $LASTEXITCODE." }
    $baselineCount = & $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $sourceDb -tA -c 'SELECT count(*) FROM system.__ef_migrations_history;'
    if ($LASTEXITCODE -ne 0 -or [int]$baselineCount -ne 13) { throw "Legacy baseline history must contain 13 rows for $($case.Label); found '$baselineCount'." }

    $seedSql = "INSERT INTO catalog.categories (id, name, is_active) VALUES ('00000000-0000-0000-0000-000000000001', 'Fan', true), ('00000000-0000-0000-0000-000000000002', '$($case.SecondName)', false);"
    & $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $sourceDb -c $seedSql | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Legacy category seed failed for $($case.Label)." }
    $categorySnapshotSql = 'SELECT id::text || ''|'' || name || ''|'' || is_active::text FROM catalog.categories ORDER BY id;'
    $sourceRows = @(& $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $sourceDb -tA -c $categorySnapshotSql)
    if ($LASTEXITCODE -ne 0 -or $sourceRows.Count -ne 2) { throw "Legacy source row census failed for $($case.Label)." }

    # NEW_COVERAGE: prove that the released migration fails for each legacy
    # data shape, and that PostgreSQL rolls the failed attempt back fully.
    $expectedFailureLog = Join-Path $RunRoot ("expected_23505_$($case.Label).log")
    & dotnet ef database update 20260925142150_Phase1SemanticProductIdentity --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release --connection $sourceConnection *> $expectedFailureLog
    $expectedFailureExit = $LASTEXITCODE
    $failureText = Get-Content -LiteralPath $expectedFailureLog -Raw
    if ($expectedFailureExit -eq 0 -or $failureText -notmatch '23505' -or $failureText -notmatch 'ix_categories_identity_symbol') { throw "Immutable released migration did not fail with the expected unique-index error for $($case.Label)." }
    $historyAfterFailure = & $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $sourceDb -tA -c 'SELECT count(*) FROM system.__ef_migrations_history;'
    $rowsAfterFailure = @(& $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $sourceDb -tA -c $categorySnapshotSql)
    $newColumnCount = & $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $sourceDb -tA -c "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'catalog' AND table_name = 'categories' AND column_name = 'identity_symbol';"
    if ($LASTEXITCODE -ne 0 -or [int]$historyAfterFailure -ne 13 -or [int]$newColumnCount -ne 0 -or ($rowsAfterFailure -join ';') -ne ($sourceRows -join ';')) { throw "Immutable migration failure did not roll back cleanly for $($case.Label)." }
    Write-Output "PHASE3_PG_LEGACY_CATEGORY_ORIGINAL_RED_PASS=$($case.Label) SQLSTATE=23505 Rollback=PASS"

    $backup = Join-Path $RunRoot ("pre_upgrade_$($case.Label).dump")
    & (Join-Path $PgBin 'pg_dump.exe') --format=custom --no-password --host 127.0.0.1 --port $Port --username $AdminUser --file $backup $sourceDb
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $backup) -or (Get-Item -LiteralPath $backup).Length -le 0) { throw "Legacy backup failed for $($case.Label)." }
    $toc = & (Join-Path $PgBin 'pg_restore.exe') --list $backup
    if ($LASTEXITCODE -ne 0 -or -not (($toc | Out-String) -match '__ef_migrations_history')) { throw "Legacy backup TOC failed for $($case.Label)." }
    & (Join-Path $PgBin 'pg_restore.exe') --file NUL $backup
    if ($LASTEXITCODE -ne 0) { throw "Legacy backup full archive read failed for $($case.Label)." }
    $backupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backup).Hash
    Write-Output "PHASE3_PG_LEGACY_CATEGORY_BACKUP_SHA256_$($case.Label)=$backupHash"

    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $AdminUser -T template0 $restoredDb
    if ($LASTEXITCODE -ne 0) { throw "Restore target createdb failed for $($case.Label)." }
    & (Join-Path $PgBin 'pg_restore.exe') --exit-on-error --no-owner --no-privileges --no-password --host 127.0.0.1 --port $Port --username $AdminUser --dbname $restoredDb $backup
    if ($LASTEXITCODE -ne 0) { throw "Actual PostgreSQL restore failed for $($case.Label)." }
    $restoredHistory = & $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $restoredDb -tA -c 'SELECT count(*) FROM system.__ef_migrations_history;'
    $restoredRows = @(& $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $restoredDb -tA -c $categorySnapshotSql)
    if ($LASTEXITCODE -ne 0 -or [int]$restoredHistory -ne 13 -or ($restoredRows -join ';') -ne ($sourceRows -join ';')) { throw "Backup restore did not preserve baseline rows/history for $($case.Label)." }
    Write-Output "PHASE3_PG_LEGACY_CATEGORY_BACKUP_RESTORE_PASS=$($case.Label)"

    # HARNESS_CORRECTION: the guarded pre-step requires a backup whose archive
    # database identity matches its target. Take a fresh verified snapshot of
    # the restored clone; the source-to-clone restore proof remains above.
    $cloneBackup = Join-Path $RunRoot ("pre_upgrade_restored_$($case.Label).dump")
    & (Join-Path $PgBin 'pg_dump.exe') --format=custom --no-password --host 127.0.0.1 --port $Port --username $AdminUser --file $cloneBackup $restoredDb
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $cloneBackup) -or (Get-Item -LiteralPath $cloneBackup).Length -le 0) { throw "Restored-clone backup failed for $($case.Label)." }
    $cloneToc = & (Join-Path $PgBin 'pg_restore.exe') --list $cloneBackup
    if ($LASTEXITCODE -ne 0 -or -not (($cloneToc | Out-String) -match '__ef_migrations_history')) { throw "Restored-clone backup TOC failed for $($case.Label)." }
    & (Join-Path $PgBin 'pg_restore.exe') --file NUL $cloneBackup
    if ($LASTEXITCODE -ne 0) { throw "Restored-clone full archive read failed for $($case.Label)." }
    $cloneBackupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $cloneBackup).Hash

    $previousDb = $env:EDGE_RETAILS_DB
    $env:EDGE_RETAILS_DB = $restoredConnection
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-Phase3LegacyCategoryPreMigration.ps1 -Mode Disposable -BackupPath $cloneBackup -BackupSha256 $cloneBackupHash -Apply
        if ($LASTEXITCODE -ne 0) { throw "Pre-migration staging failed for $($case.Label), exit $LASTEXITCODE." }
    }
    finally { $env:EDGE_RETAILS_DB = $previousDb }
    $staged = & $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $restoredDb -tA -c 'SELECT CASE WHEN (SELECT count(*) FROM system.phase3_legacy_category_hold) = 2 AND (SELECT count(*) FROM catalog.categories) = 0 AND (SELECT count(*) FROM system.__ef_migrations_history) = 13 THEN 1 ELSE 0 END;'
    if ($LASTEXITCODE -ne 0 -or [int]$staged -ne 1) { throw "Pre-migration staging invariant failed for $($case.Label)." }

    & dotnet ef database update 20260930065058_Phase3COwnerPinAuthorizationConsumption --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release --connection $restoredConnection
    if ($LASTEXITCODE -ne 0) { throw "Legacy forward upgrade failed for $($case.Label), exit $LASTEXITCODE." }
    $schemaSql = "SELECT CASE WHEN (SELECT count(*) FROM system.__ef_migrations_history) = 20 AND (SELECT count(*) FROM catalog.categories) = 2 AND (SELECT count(*) FROM catalog.categories WHERE id = '00000000-0000-0000-0000-000000000001' AND name = 'Fan' AND is_active AND identity_symbol = 'F000') = 1 AND (SELECT count(*) FROM catalog.categories WHERE id = '00000000-0000-0000-0000-000000000002' AND name = '$($case.SecondName)' AND NOT is_active AND identity_symbol = '$($case.SecondSymbol)') = 1 AND NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'system' AND table_name = 'phase3_legacy_category_hold') AND (SELECT count(*) FROM pg_indexes WHERE schemaname = 'catalog' AND tablename = 'categories' AND indexname = 'ix_categories_identity_symbol' AND indexdef ILIKE '%UNIQUE%') = 1 AND (SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'catalog' AND table_name = 'categories' AND column_name = 'identity_symbol') = 'NO' AND (SELECT count(*) FROM pg_constraint WHERE conname = 'ck_categories_symbol_uppercase' AND conrelid = 'catalog.categories'::regclass) = 1 AND (SELECT count(*) FROM pg_indexes WHERE schemaname = 'audit' AND tablename = 'business_events' AND indexname = 'ux_business_events_pin_recovery_success_operation' AND indexdef ILIKE '%UNIQUE%' AND indexdef ILIKE '%WHERE%') = 1 AND (SELECT count(*) FROM pg_indexes WHERE schemaname = 'audit' AND tablename = 'business_events' AND indexname = 'ux_business_events_pin_recovery_consumed_nonce' AND indexdef ILIKE '%UNIQUE%' AND indexdef ILIKE '%WHERE%') = 1 THEN 1 ELSE 0 END;"
    $schemaVerified = & $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $restoredDb -tA -c $schemaSql
    if ($LASTEXITCODE -ne 0 -or [int]$schemaVerified -ne 1) { throw "Legacy category schema/data verification failed for $($case.Label)." }
    $migrationList = & dotnet ef migrations list --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release --connection $restoredConnection --no-build
    if ($LASTEXITCODE -ne 0 -or ($migrationList | Out-String) -match '\(Pending\)') { throw "Legacy upgrade has pending migrations for $($case.Label)." }
    Write-Output "PHASE3_PG_LEGACY_CATEGORY_UPGRADE_PASS=$($case.Label)"
}

# NEW_COVERAGE: a one-category database can pass the immutable migration
# without staging; the appended migration must replace its legacy '' symbol.
$singleDb = 'edge_retails_phase3_single_category'
$singleConnection = "Host=127.0.0.1;Port=$Port;Database=$singleDb;Username=$AdminUser;Password=$AdminPassword"
& (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $AdminUser $singleDb
if ($LASTEXITCODE -ne 0) { throw 'Single-category createdb failed.' }
& dotnet ef database update 20260923125420_Phase5PurchaseHistoryOrderingIndex --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release --connection $singleConnection
if ($LASTEXITCODE -ne 0) { throw "Single-category baseline migration failed, exit $LASTEXITCODE." }
& $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $singleDb -c "INSERT INTO catalog.categories (id, name, is_active) VALUES ('00000000-0000-0000-0000-000000000003', 'Fan', true);" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Single-category seed failed.' }
& dotnet ef database update 20260930065058_Phase3COwnerPinAuthorizationConsumption --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release --connection $singleConnection
if ($LASTEXITCODE -ne 0) { throw "Single-category forward upgrade failed, exit $LASTEXITCODE." }
$singleSql = "SELECT CASE WHEN (SELECT count(*) FROM system.__ef_migrations_history) = 20 AND (SELECT count(*) FROM catalog.categories) = 1 AND (SELECT count(*) FROM catalog.categories WHERE id = '00000000-0000-0000-0000-000000000003' AND name = 'Fan' AND is_active AND identity_symbol = 'F000') = 1 AND NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'system' AND table_name = 'phase3_legacy_category_hold') AND (SELECT column_default FROM information_schema.columns WHERE table_schema = 'catalog' AND table_name = 'categories' AND column_name = 'identity_symbol') IS NULL AND (SELECT count(*) FROM pg_indexes WHERE schemaname = 'audit' AND tablename = 'business_events' AND indexname = 'ux_business_events_pin_recovery_success_operation' AND indexdef ILIKE '%UNIQUE%' AND indexdef ILIKE '%WHERE%') = 1 AND (SELECT count(*) FROM pg_indexes WHERE schemaname = 'audit' AND tablename = 'business_events' AND indexname = 'ux_business_events_pin_recovery_consumed_nonce' AND indexdef ILIKE '%UNIQUE%' AND indexdef ILIKE '%WHERE%') = 1 THEN 1 ELSE 0 END;"
$singleVerified = & $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $Port -U $AdminUser -d $singleDb -tA -c $singleSql
if ($LASTEXITCODE -ne 0 -or [int]$singleVerified -ne 1) { throw 'Single-category identity and migration verification failed.' }
Write-Output 'PHASE3_PG_SINGLE_CATEGORY_UPGRADE_PASS'
