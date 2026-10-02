[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$BackupPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$BackupSha256,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedCategoryRowsSha256,

    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Restore certification is isolated from the installed Server's database. This
# script never uses a runtime PostgreSQL connection or a production service.
$expectedMigrations = @(
    '20260920094824_InitialProductionBaseline',
    '20260920111318_Sprint7Phase1SetupIdentity',
    '20260920164958_Sprint7ProductionCutover',
    '20260921101001_Sprint8CanonicalReportingSchema',
    '20260921143542_Sprint8FinalProductionAlignment',
    '20260921152602_Sprint8WarrantyAlignment',
    '20260922120000_Phase1CanonicalSchemaAlignment',
    '20260922135055_Phase3ProductionSafetyOutbox',
    '20260923071510_Phase4MultiTerminalSchema',
    '20260923095632_Phase5WarrantyClaimClientOperationId',
    '20260923110943_Phase5WarrantyLifecycleIdempotency',
    '20260923111027_Phase5MovementHistoryOrderingIndex',
    '20260923125420_Phase5PurchaseHistoryOrderingIndex'
)

$requiredTools = @('initdb.exe', 'pg_ctl.exe', 'pg_isready.exe', 'psql.exe', 'createdb.exe', 'pg_restore.exe')
foreach ($toolName in $requiredTools) {
    $toolPath = Join-Path $PgBin $toolName
    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
        throw "Required PostgreSQL 18 tool is missing: $toolPath"
    }
}
$initdb = Join-Path $PgBin 'initdb.exe'
$pgCtl = Join-Path $PgBin 'pg_ctl.exe'
$pgIsReady = Join-Path $PgBin 'pg_isready.exe'
$psql = Join-Path $PgBin 'psql.exe'
$createdb = Join-Path $PgBin 'createdb.exe'
$pgRestore = Join-Path $PgBin 'pg_restore.exe'

foreach ($tool in @($initdb, $pgCtl, $psql, $pgRestore)) {
    $version = & $tool --version 2>&1
    if ($LASTEXITCODE -ne 0 -or ($version | Out-String) -notmatch '\b18\.\d+\b') {
        throw 'PostgreSQL 18 client tools are required for restore certification.'
    }
}

$backup = [IO.Path]::GetFullPath($BackupPath)
if (-not (Test-Path -LiteralPath $backup -PathType Leaf) -or
    (Get-Item -LiteralPath $backup).Length -le 0 -or
    ((Get-Item -LiteralPath $backup).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The approved backup is missing, empty, or a reparse point.'
}
$actualBackupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backup).Hash
if ($actualBackupHash -ne $BackupSha256.ToUpperInvariant()) {
    throw 'The backup SHA-256 does not match the approved value.'
}

$toc = & $pgRestore --list $backup 2>&1
if ($LASTEXITCODE -ne 0 -or
    -not (($toc | Out-String) -match '(?im)^;\s+dbname:\s*(.+)\s*$') -or
    -not (($toc | Out-String) -match '(?im)^\d+;.*TABLE DATA system __ef_migrations_history\b') -or
    -not (($toc | Out-String) -match '(?im)^\d+;.*TABLE DATA catalog categories\b')) {
    throw 'The backup TOC lacks its database identity, EF history, or categories data.'
}
$archiveDatabase = [regex]::Match(($toc | Out-String), '(?im)^;\s+dbname:\s*(.+?)\s*$').Groups[1].Value
$tableDataCount = ([regex]::Matches(($toc | Out-String), '(?im)^\d+;.*\bTABLE DATA\b')).Count
if ($tableDataCount -ne 81) {
    throw "The approved snapshot must contain 81 table-data entries; found $tableDataCount."
}

# Compare only the database name in ProgramData configuration; do not use the
# operational host, port, user, or password for any PostgreSQL command.
$configPath = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'EdgeRetails\config.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw 'Approved ProgramData configuration is unavailable for backup identity validation.'
}
try {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $runtimeString = $null
    if ($null -ne $config.PSObject.Properties['ConnectionStrings'] -and
        $null -ne $config.ConnectionStrings -and
        $null -ne $config.ConnectionStrings.PSObject.Properties['DefaultConnection']) {
        $runtimeString = $config.ConnectionStrings.DefaultConnection
    }
    if ([string]::IsNullOrWhiteSpace($runtimeString) -and
        $null -ne $config.PSObject.Properties['EDGE_RETAILS_DB']) {
        $runtimeString = $config.EDGE_RETAILS_DB
    }
    if ([string]::IsNullOrWhiteSpace($runtimeString)) { throw 'missing' }
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder.set_ConnectionString([string]$runtimeString)
    $operationalDatabase = if ($builder.ContainsKey('Database')) { [string]$builder['Database'] } else { [string]$builder['Initial Catalog'] }
    if ([string]::IsNullOrWhiteSpace($operationalDatabase) -or
        -not $archiveDatabase.Equals($operationalDatabase, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'mismatch'
    }
}
catch {
    throw 'Backup database identity does not match the approved ProgramData configuration.'
}

$nullDevice = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { 'NUL' } else { '/dev/null' }
& $pgRestore --file $nullDevice $backup 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Full backup archive read failed.' }
Write-Output "PHASE3_OPERATIONAL_BACKUP_ARCHIVE_PASS SHA256=$actualBackupHash TableData=$tableDataCount"

$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar)
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('EdgeRetailsPhase3Restore_' + [Guid]::NewGuid().ToString('N'))))
$safePrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar + 'EdgeRetailsPhase3Restore_'
if (-not $runRoot.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Disposable PostgreSQL path escaped the expected temporary root.'
}
$data = Join-Path $runRoot 'data'
$passwordFile = Join-Path $runRoot 'admin.pw'
$log = Join-Path $runRoot 'postgres.log'
$adminUser = 'er_p3_restore_admin'
$verifyDb = 'edge_retails_phase3_restore_verify'
$randomBytes = New-Object byte[] 32
$randomGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $randomGenerator.GetBytes($randomBytes) }
finally { $randomGenerator.Dispose() }
$adminPassword = ([BitConverter]::ToString($randomBytes)).Replace('-', '')
$port = 55900..56999 | Where-Object {
    -not (Get-NetTCPConnection -State Listen -LocalPort $_ -ErrorAction SilentlyContinue)
} | Select-Object -First 1
if ($null -eq $port) { throw 'No isolated PostgreSQL loopback port is available.' }

function Invoke-VerifySql {
    param([string]$Sql, [string]$Database = $verifyDb)
    $output = $Sql | & $psql -X -w -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $port `
        -U $adminUser -d $Database -tA -f - 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Isolated PostgreSQL verification query failed (psql exit $LASTEXITCODE)." }
    return (($output | Out-String).Trim())
}

$previousPgPassword = $env:PGPASSWORD
$previousPgSslMode = $env:PGSSLMODE
$previousPgConnectTimeout = $env:PGCONNECT_TIMEOUT
$createdRoot = $false
try {
    [IO.Directory]::CreateDirectory($runRoot) | Out-Null
    $createdRoot = $true
    [IO.File]::WriteAllText($passwordFile, $adminPassword, [Text.UTF8Encoding]::new($false))

    & $initdb -D $data --username=$adminUser --pwfile=$passwordFile `
        --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Disposable initdb failed (exit $LASTEXITCODE)." }
    Add-Content -LiteralPath (Join-Path $data 'postgresql.conf') -Value @(
        '',
        "port = $port",
        "listen_addresses = '127.0.0.1'",
        'max_connections = 30'
    )

    # Do not pipe pg_ctl start: the child server can inherit the pipeline's
    # output handle and keep PowerShell waiting after pg_ctl has returned.
    & $pgCtl -D $data -l $log start
    if ($LASTEXITCODE -ne 0) { throw "Disposable PostgreSQL start failed (exit $LASTEXITCODE)." }
    $env:PGPASSWORD = $adminPassword
    $env:PGSSLMODE = 'disable'
    $env:PGCONNECT_TIMEOUT = '10'

    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        & $pgIsReady -h 127.0.0.1 -p $port -U $adminUser | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $ready) { throw 'Disposable PostgreSQL did not become ready.' }

    $serverVersion = Invoke-VerifySql 'SHOW server_version_num;' -Database 'postgres'
    if ($serverVersion -notmatch '^18\d{4}$') { throw 'Disposable server is not PostgreSQL 18.' }
    $serverData = Invoke-VerifySql 'SHOW data_directory;' -Database 'postgres'
    if (-not ([IO.Path]::GetFullPath($serverData).TrimEnd('\') -eq [IO.Path]::GetFullPath($data).TrimEnd('\'))) {
        throw 'The selected loopback port is not the disposable PostgreSQL cluster.'
    }

    & $createdb -h 127.0.0.1 -p $port -U $adminUser --template=template0 $verifyDb | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Disposable createdb failed (exit $LASTEXITCODE)." }
    & $pgRestore --exit-on-error --single-transaction --no-owner --no-privileges `
        -h 127.0.0.1 -p $port -U $adminUser -d $verifyDb $backup | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Isolated pg_restore failed (exit $LASTEXITCODE)." }

    $history = @(Invoke-VerifySql 'SELECT "MigrationId" FROM system.__ef_migrations_history ORDER BY "MigrationId";')
    $history = @(($history | Out-String).Trim() -split "`r?`n")
    if ($history.Count -ne $expectedMigrations.Count) {
        throw 'Restored EF migration count differs from the approved 13-migration baseline.'
    }
    for ($i = 0; $i -lt $expectedMigrations.Count; $i++) {
        if ($history[$i] -ne $expectedMigrations[$i]) {
            throw 'Restored EF migration identity differs from the approved 13-migration baseline.'
        }
    }

    $schemaCheck = @'
SELECT CASE WHEN
    (SELECT count(*) FROM catalog.categories) = 2
    AND NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'catalog' AND table_name = 'categories' AND column_name = 'identity_symbol')
    AND to_regclass('system.phase3_legacy_category_hold') IS NULL
    AND to_regclass('catalog.companies') IS NULL
    AND (SELECT count(*) FROM catalog.products) = 0
    AND (SELECT count(*) FROM inventory.stocktakes WHERE category_id IS NOT NULL) = 0
    AND (SELECT count(*) FROM pg_constraint c
        WHERE c.contype = 'f' AND c.confrelid = 'catalog.categories'::regclass) = 2
    AND (SELECT count(*) FROM pg_constraint c
        WHERE c.contype = 'f' AND c.confrelid = 'catalog.categories'::regclass
          AND ((c.conrelid = 'catalog.products'::regclass AND c.conname = 'fk_products_categories_category_id')
            OR (c.conrelid = 'inventory.stocktakes'::regclass AND c.conname = 'fk_stocktakes_categories_category_id'))) = 2
THEN 1 ELSE 0 END;
'@
    if ((Invoke-VerifySql $schemaCheck) -ne '1') {
        throw 'Restored legacy schema, category count, or category dependencies differ from the approved state.'
    }

    $rowHashSql = @'
SELECT encode(sha256(convert_to(COALESCE(string_agg(
    id::text || ':' || encode(convert_to(name, 'UTF8'), 'hex') || ':' || CASE WHEN is_active THEN '1' ELSE '0' END,
    E'\n' ORDER BY id), ''), 'UTF8')), 'hex')
FROM catalog.categories;
'@
    $rowHash = Invoke-VerifySql $rowHashSql
    if ($rowHash -notmatch '^[0-9a-f]{64}$') { throw 'Restored category row hash could not be verified.' }
    if ($rowHash -ne $ExpectedCategoryRowsSha256.ToLowerInvariant()) {
        throw 'Restored category rows differ from the reviewed production row hash.'
    }

    $restoredTableCount = Invoke-VerifySql @'
SELECT count(*) FROM information_schema.tables
WHERE table_type = 'BASE TABLE'
  AND table_schema IN ('audit','finance','catalog','warranty','inventory','parties',
                       'system','purchasing','sales','reporting','identity','thaka');
'@
    if ([int]$restoredTableCount -ne $tableDataCount) {
        throw "Restored base-table count differs from the backup TOC ($restoredTableCount vs $tableDataCount)."
    }

    Write-Output "PHASE3_OPERATIONAL_BACKUP_RESTORE_PASS Provider=PostgreSQL 18 / pg_restore History=13 Categories=2 Tables=$restoredTableCount CategorySHA256=$rowHash"
}
finally {
    $env:PGPASSWORD = $previousPgPassword
    $env:PGSSLMODE = $previousPgSslMode
    $env:PGCONNECT_TIMEOUT = $previousPgConnectTimeout

    if ($createdRoot) {
        $running = $false
        if (Test-Path -LiteralPath $data -PathType Container) {
            & $pgCtl -D $data status 2>&1 | Out-Null
            $running = $LASTEXITCODE -eq 0
        }
        if ($running) {
            & $pgCtl -D $data -m fast stop 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Disposable PostgreSQL could not be stopped. Preserve and inspect $runRoot."
            }
        }
        if (Test-Path -LiteralPath $data -PathType Container) {
            & $pgCtl -D $data status 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                throw "Disposable PostgreSQL is still running. Preserve and inspect $runRoot."
            }
        }
        if (-not $runRoot.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFullPath($runRoot) -ne $runRoot) {
            throw 'Refusing to remove a path outside the owned temporary restore root.'
        }
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
