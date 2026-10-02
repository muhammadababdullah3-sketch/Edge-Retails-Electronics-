[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Disposable', 'Operational')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$BackupPath,

    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$BackupSha256,

    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedTargetFingerprint,

    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',

    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This is a data preparation step for a released, immutable EF migration. The
# later append-only corrective migration restores the held rows. Never use this
# script to apply EF migrations or to restore directly into production.
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

function Get-ApprovedConnectionString {
    param([switch]$Production)

    if (-not $Production) {
        if ([string]::IsNullOrWhiteSpace($env:EDGE_RETAILS_DB)) {
            throw 'Disposable mode requires EDGE_RETAILS_DB for its isolated PostgreSQL database.'
        }
        return $env:EDGE_RETAILS_DB
    }

    $configPath = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'EdgeRetails\config.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw 'Approved ProgramData database configuration is unavailable.'
    }
    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $value = $null
        if ($null -ne $config.PSObject.Properties['ConnectionStrings'] -and
            $null -ne $config.ConnectionStrings -and
            $null -ne $config.ConnectionStrings.PSObject.Properties['DefaultConnection']) {
            $value = $config.ConnectionStrings.DefaultConnection
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            if ($null -ne $config.PSObject.Properties['EDGE_RETAILS_DB']) {
                $value = $config.EDGE_RETAILS_DB
            }
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw 'missing'
        }
        return [string]$value
    }
    catch {
        throw 'Approved ProgramData database configuration is invalid or unavailable.'
    }
}

function Parse-Connection {
    param([string]$Value)

    try {
        $parts = [System.Data.Common.DbConnectionStringBuilder]::new()
        # PowerShell's property assignment can be intercepted by IDictionary's
        # member adapter; call the .NET setter directly for the connection string.
        $parts.set_ConnectionString($Value)

        function Read-Part {
            param($Builder, [string[]]$Keys)
            foreach ($key in $Keys) {
                if ($Builder.ContainsKey($key)) { return [string]$Builder[$key] }
            }
            return $null
        }

        $hostName = Read-Part $parts @('Host', 'Server')
        $database = Read-Part $parts @('Database', 'Initial Catalog')
        $username = Read-Part $parts @('Username', 'User ID', 'User Id', 'UserID')
        $password = Read-Part $parts @('Password', 'Pwd')
        $portText = Read-Part $parts @('Port')
        $sslMode = Read-Part $parts @('SSL Mode', 'Ssl Mode', 'SSLMode')
        if ([string]::IsNullOrWhiteSpace($portText)) { $portText = '5432' }
        $portNumber = 0
        if (-not [int]::TryParse($portText, [ref]$portNumber) -or $portNumber -lt 1 -or $portNumber -gt 65535 -or
            [string]::IsNullOrWhiteSpace($hostName) -or $hostName.Contains(',') -or
            [string]::IsNullOrWhiteSpace($database) -or [string]::IsNullOrWhiteSpace($username) -or
            [string]::IsNullOrWhiteSpace($password)) {
            throw 'invalid'
        }
        return [pscustomobject]@{
            Host = $hostName.Trim()
            Port = $portNumber
            Database = $database.Trim()
            Username = $username.Trim()
            Password = $password
            SslMode = $sslMode
        }
    }
    catch {
        throw 'Approved database connection settings cannot be parsed safely.'
    }
}

function Get-TargetFingerprint {
    param($Connection)
    $identity = '{0}|{1}|{2}|{3}' -f $Connection.Host.ToLowerInvariant(), $Connection.Port,
        $Connection.Database.ToLowerInvariant(), $Connection.Username.ToLowerInvariant()
    $bytes = [Text.Encoding]::UTF8.GetBytes($identity)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Assert-OperationalServicesStopped {
    foreach ($name in @('EdgeRetailsServer', 'EdgeRetailsWorker')) {
        $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
        if ($null -eq $service -or $service.State -ne 'Stopped' -or $service.StartMode -ne 'Disabled') {
            throw "$name must be Stopped/Disabled throughout the migration maintenance window."
        }
    }
    if (Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue) {
        throw 'Port 7150 still has a listener; operational writes are not safely drained.'
    }
}

$psql = Join-Path $PgBin 'psql.exe'
$pgDump = Join-Path $PgBin 'pg_dump.exe'
$pgRestore = Join-Path $PgBin 'pg_restore.exe'
foreach ($tool in @($psql, $pgDump, $pgRestore)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "Required PostgreSQL 18 client tool is missing: $tool"
    }
}

if ($Mode -eq 'Operational') {
    if (-not [string]::IsNullOrWhiteSpace($env:EDGE_RETAILS_DB)) {
        throw 'Operational mode requires the installed Server ProgramData authority; remove the EDGE_RETAILS_DB override.'
    }
    $connection = Parse-Connection (Get-ApprovedConnectionString -Production)
    if ($Apply -and [string]::IsNullOrWhiteSpace($ExpectedTargetFingerprint)) {
        throw 'Operational -Apply requires -ExpectedTargetFingerprint from a reviewed read-only preflight.'
    }
    if ($Apply) {
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $isAdmin) { throw 'Operational -Apply requires an elevated administrator shell.' }
    }
    Assert-OperationalServicesStopped
}
else {
    $connection = Parse-Connection (Get-ApprovedConnectionString)
    $production = Parse-Connection (Get-ApprovedConnectionString -Production)
    if ($connection.Port -eq 5432 -or $connection.Port -eq $production.Port -or
        $connection.Database.Equals($production.Database, [StringComparison]::OrdinalIgnoreCase) -or
        $connection.Host -notin @('127.0.0.1', 'localhost', '::1')) {
        throw 'Disposable mode refuses the operational PostgreSQL database or port.'
    }
}

$fingerprint = Get-TargetFingerprint $connection
if ($Mode -eq 'Operational' -and -not [string]::IsNullOrWhiteSpace($ExpectedTargetFingerprint) -and
    $fingerprint -ne $ExpectedTargetFingerprint.ToUpperInvariant()) {
    throw 'Operational target fingerprint does not match the reviewed target.'
}

$backup = [IO.Path]::GetFullPath($BackupPath)
$createOperationalBackup = $Mode -eq 'Operational' -and $Apply
if ($createOperationalBackup) {
    $approvedBackupRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'EdgeRetails\Production\backups')).TrimEnd('\') + '\'
    $backupParent = [IO.Path]::GetFullPath((Split-Path -Parent $backup)).TrimEnd('\') + '\'
    if (-not $backupParent.StartsWith($approvedBackupRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-Path -LiteralPath $backup) -or
        [IO.Path]::GetFileName($backup) -notmatch '^pre_migration_[0-9]{8}_[0-9]{6}\.dump$') {
        throw 'Operational -Apply creates a new timestamped backup only inside the approved production backup directory.'
    }
    if (-not [string]::IsNullOrWhiteSpace($BackupSha256)) {
        throw 'Operational -Apply creates the backup itself; omit BackupSha256.'
    }
}
else {
    if ($BackupSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Read-only or Disposable mode requires the reviewed backup SHA-256.'
    }
    if (-not (Test-Path -LiteralPath $backup -PathType Leaf) -or
        (Get-Item -LiteralPath $backup).Length -le 0 -or
        ((Get-Item -LiteralPath $backup).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The pre-migration custom-format backup is missing, empty, or a reparse point.'
    }
    $actualBackupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backup).Hash
    if ($actualBackupHash -ne $BackupSha256.ToUpperInvariant()) {
        throw 'The pre-migration backup SHA-256 does not match the reviewed value.'
    }
}

$previousPgPassword = $env:PGPASSWORD
$previousPgSslMode = $env:PGSSLMODE
$previousPgConnectTimeout = $env:PGCONNECT_TIMEOUT
$env:PGPASSWORD = $connection.Password
$env:PGCONNECT_TIMEOUT = '10'
if (-not [string]::IsNullOrWhiteSpace($connection.SslMode)) {
    $env:PGSSLMODE = $connection.SslMode.ToLowerInvariant().Replace('verifyfull', 'verify-full').Replace('verifyca', 'verify-ca')
}

function Invoke-PsqlScalar {
    param([string]$Sql)
    # Windows PowerShell 5.1 can strip SQL identifier quotes from a native
    # -c argument. Feed SQL through stdin so quoted EF columns stay intact.
    $result = $Sql | & $psql -X -w -v ON_ERROR_STOP=1 -h $connection.Host -p $connection.Port `
        -U $connection.Username -d $connection.Database -tA -f - 2>&1
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL preflight or mutation failed (psql exit $LASTEXITCODE)." }
    return (($result | Out-String).Trim())
}

$rowHashSql = @'
SELECT encode(sha256(convert_to(COALESCE(string_agg(
    id::text || ':' || encode(convert_to(name, 'UTF8'), 'hex') || ':' || CASE WHEN is_active THEN '1' ELSE '0' END,
    E'\n' ORDER BY id), ''), 'UTF8')), 'hex')
FROM catalog.categories;
'@

try {
    foreach ($tool in @($psql, $pgRestore)) {
        $toolVersion = & $tool --version 2>&1
        if ($LASTEXITCODE -ne 0 -or ($toolVersion | Out-String) -notmatch '\b18\.\d+\b') {
            throw 'PostgreSQL 18 client tools are required.'
        }
    }
    $version = Invoke-PsqlScalar 'SHOW server_version_num;'
    if ($version -notmatch '^18\d{4}$') {
        throw "PostgreSQL 18 is required; server_version_num was '$version'."
    }
    if ((Invoke-PsqlScalar 'SELECT CASE WHEN pg_is_in_recovery() OR current_setting(''transaction_read_only'') = ''on'' THEN 0 ELSE 1 END;') -ne '1') {
        throw 'The selected PostgreSQL database is not a writable primary.'
    }

    if (-not $createOperationalBackup) {
        $toc = & $pgRestore --list $backup 2>&1
        if ($LASTEXITCODE -ne 0 -or
            -not (($toc | Out-String) -match '(?im)^;\s+dbname:\s*(.+)\s*$') -or
            -not (($toc | Out-String) -match '(?im)^\d+;.*TABLE DATA system __ef_migrations_history\b')) {
            throw 'The backup TOC is invalid or lacks the EF migration-history table data.'
        }
        $archiveDatabase = [regex]::Match(($toc | Out-String), '(?im)^;\s+dbname:\s*(.+?)\s*$').Groups[1].Value
        if (-not $archiveDatabase.Equals($connection.Database, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The backup database identity differs from the selected runtime database.'
        }
        $nullDevice = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { 'NUL' } else { '/dev/null' }
        & $pgRestore --file $nullDevice $backup 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Full backup archive read failed.' }
    }

    $migrations = @(Invoke-PsqlScalar 'SELECT "MigrationId" FROM system.__ef_migrations_history ORDER BY "MigrationId";')
    $migrations = @(($migrations | Out-String).Trim() -split "`r?`n")
    if ($migrations.Count -ne $expectedMigrations.Count) {
        throw 'Applied EF migration count differs from the approved 13-migration baseline.'
    }
    for ($i = 0; $i -lt $expectedMigrations.Count; $i++) {
        if ($migrations[$i] -ne $expectedMigrations[$i]) {
            throw 'Applied EF migration history differs from the approved 13-migration baseline.'
        }
    }

    $schemaStateSql = @'
SELECT CASE WHEN
    to_regclass('system.phase3_legacy_category_hold') IS NULL
    AND NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'catalog' AND table_name = 'categories' AND column_name = 'identity_symbol')
    AND (SELECT count(*) FROM catalog.categories) = 2
    AND (SELECT count(*) FROM catalog.products WHERE category_id IS NOT NULL) = 0
    AND (SELECT count(*) FROM inventory.stocktakes WHERE category_id IS NOT NULL) = 0
    AND (SELECT count(*) FROM pg_constraint c
        WHERE c.contype = 'f' AND c.confrelid = 'catalog.categories'::regclass) = 2
    AND (SELECT count(*) FROM pg_constraint c
        WHERE c.contype = 'f' AND c.confrelid = 'catalog.categories'::regclass
          AND ((c.conrelid = 'catalog.products'::regclass AND c.conname = 'fk_products_categories_category_id')
            OR (c.conrelid = 'inventory.stocktakes'::regclass AND c.conname = 'fk_stocktakes_categories_category_id'))) = 2
THEN 1 ELSE 0 END;
'@
    if ((Invoke-PsqlScalar $schemaStateSql) -ne '1') {
        throw 'Legacy category schema, row count, hold state, FK set, or dependent-row preflight failed.'
    }
    $rowHash = Invoke-PsqlScalar $rowHashSql
    if ($rowHash -notmatch '^[0-9a-f]{64}$') { throw 'Legacy category row hash could not be established.' }

    if ($Mode -eq 'Operational' -and $Apply) {
        & $pgDump --format=custom --no-password --host $connection.Host --port $connection.Port `
            --username $connection.Username --file $backup $connection.Database
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $backup -PathType Leaf) -or
            (Get-Item -LiteralPath $backup).Length -le 0 -or
            ((Get-Item -LiteralPath $backup).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Fresh same-target operational pg_dump failed; no production rows were staged.'
        }
        $actualBackupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backup).Hash
        $toc = & $pgRestore --list $backup 2>&1
        if ($LASTEXITCODE -ne 0 -or
            -not (($toc | Out-String) -match '(?im)^;\s+dbname:\s*(.+)\s*$') -or
            -not (($toc | Out-String) -match '(?im)^\d+;.*TABLE DATA system __ef_migrations_history\b')) {
            throw 'Fresh operational backup TOC verification failed; no production rows were staged.'
        }
        $archiveDatabase = [regex]::Match(($toc | Out-String), '(?im)^;\s+dbname:\s*(.+?)\s*$').Groups[1].Value
        if (-not $archiveDatabase.Equals($connection.Database, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Fresh operational backup database identity mismatched; no production rows were staged.'
        }
        $nullDevice = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { 'NUL' } else { '/dev/null' }
        & $pgRestore --file $nullDevice $backup 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Fresh operational backup full archive read failed; no production rows were staged.' }
        $backupItem = Get-Item -LiteralPath $backup
        if ($backupItem.LastWriteTimeUtc -gt [DateTime]::UtcNow.AddMinutes(2) -or
            $backupItem.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddMinutes(-30)) {
            throw 'Operational -Apply requires a newly captured same-target backup no more than 30 minutes old.'
        }
        $restoreVerifier = Join-Path $PSScriptRoot 'Invoke-Phase3OperationalBackupRestoreRehearsal.ps1'
        if (-not (Test-Path -LiteralPath $restoreVerifier -PathType Leaf)) {
            throw 'The isolated operational backup restore verifier is unavailable.'
        }
        & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File $restoreVerifier `
            -BackupPath $backup -BackupSha256 $actualBackupHash -ExpectedCategoryRowsSha256 $rowHash -PgBin $PgBin
        if ($LASTEXITCODE -ne 0) {
            throw 'Fresh operational backup restore verification failed; no production rows were staged.'
        }
        Write-Output 'PHASE3_LEGACY_CATEGORY_OPERATIONAL_BACKUP_RESTORE_VERIFIED'
    }

    if ($Mode -eq 'Operational') { Assert-OperationalServicesStopped }
    Write-Output "PHASE3_LEGACY_CATEGORY_PREFLIGHT_PASS Database=PostgreSQL18 Client=libpq Mode=$Mode Categories=2"
    Write-Output "PHASE3_LEGACY_CATEGORY_TARGET_FINGERPRINT=$fingerprint"
    Write-Output "PHASE3_LEGACY_CATEGORY_BACKUP_SHA256=$actualBackupHash"
    Write-Output "PHASE3_LEGACY_CATEGORY_ROWS_SHA256=$rowHash"

    if (-not $Apply) {
        Write-Output 'PHASE3_LEGACY_CATEGORY_DRY_RUN_PASS'
        return
    }

    if ($Mode -eq 'Operational') { Assert-OperationalServicesStopped }
    $transactionSql = @'
BEGIN;
SET LOCAL lock_timeout = '5s';
LOCK TABLE catalog.categories IN ACCESS EXCLUSIVE MODE;
LOCK TABLE catalog.products, inventory.stocktakes IN SHARE MODE;
LOCK TABLE system.__ef_migrations_history IN ACCESS EXCLUSIVE MODE;
DO $phase3$
DECLARE
    actual_hash text;
    hold_hash text;
BEGIN
    IF to_regclass('system.phase3_legacy_category_hold') IS NOT NULL
       OR EXISTS (SELECT 1 FROM information_schema.columns
           WHERE table_schema = 'catalog' AND table_name = 'categories' AND column_name = 'identity_symbol')
       OR (SELECT array_agg("MigrationId"::text ORDER BY "MigrationId")
           FROM system.__ef_migrations_history) <> ARRAY[
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
           ]::text[]
       OR (SELECT count(*) FROM catalog.categories) <> 2
       OR EXISTS (SELECT 1 FROM catalog.products WHERE category_id IS NOT NULL)
       OR EXISTS (SELECT 1 FROM inventory.stocktakes WHERE category_id IS NOT NULL)
       OR (SELECT count(*) FROM pg_constraint c
           WHERE c.contype = 'f' AND c.confrelid = 'catalog.categories'::regclass) <> 2
       OR (SELECT count(*) FROM pg_constraint c
           WHERE c.contype = 'f' AND c.confrelid = 'catalog.categories'::regclass
             AND ((c.conrelid = 'catalog.products'::regclass AND c.conname = 'fk_products_categories_category_id')
               OR (c.conrelid = 'inventory.stocktakes'::regclass AND c.conname = 'fk_stocktakes_categories_category_id'))) <> 2
    THEN
        RAISE EXCEPTION 'Legacy category pre-migration state changed; no rows were moved.';
    END IF;

    SELECT encode(sha256(convert_to(COALESCE(string_agg(
        id::text || ':' || encode(convert_to(name, 'UTF8'), 'hex') || ':' || CASE WHEN is_active THEN '1' ELSE '0' END,
        E'\n' ORDER BY id), ''), 'UTF8')), 'hex')
    INTO actual_hash FROM catalog.categories;
    IF actual_hash <> '__EXPECTED_ROW_HASH__' THEN
        RAISE EXCEPTION 'Legacy category rows changed since preflight; no rows were moved.';
    END IF;

    CREATE TABLE system.phase3_legacy_category_hold (
        id uuid PRIMARY KEY,
        name character varying(150) NOT NULL,
        is_active boolean NOT NULL
    );
    INSERT INTO system.phase3_legacy_category_hold (id, name, is_active)
        SELECT id, name, is_active FROM catalog.categories ORDER BY id;
    SELECT encode(sha256(convert_to(COALESCE(string_agg(
        id::text || ':' || encode(convert_to(name, 'UTF8'), 'hex') || ':' || CASE WHEN is_active THEN '1' ELSE '0' END,
        E'\n' ORDER BY id), ''), 'UTF8')), 'hex')
    INTO hold_hash FROM system.phase3_legacy_category_hold;
    IF hold_hash <> actual_hash OR (SELECT count(*) FROM system.phase3_legacy_category_hold) <> 2 THEN
        RAISE EXCEPTION 'Legacy category hold verification failed; no rows were moved.';
    END IF;

    DELETE FROM catalog.categories;
    IF EXISTS (SELECT 1 FROM catalog.categories) THEN
        RAISE EXCEPTION 'Legacy category clear failed; transaction rolled back.';
    END IF;
END
$phase3$;
COMMIT;
'@
    $transactionSql = $transactionSql.Replace('__EXPECTED_ROW_HASH__', $rowHash)
    $null = Invoke-PsqlScalar $transactionSql

    $holdStateSql = $rowHashSql.Replace('FROM catalog.categories;', 'FROM system.phase3_legacy_category_hold;')
    if ((Invoke-PsqlScalar 'SELECT count(*) FROM system.phase3_legacy_category_hold;') -ne '2' -or
        (Invoke-PsqlScalar 'SELECT count(*) FROM catalog.categories;') -ne '0' -or
        (Invoke-PsqlScalar $holdStateSql) -ne $rowHash -or
        (Invoke-PsqlScalar 'SELECT count(*) FROM system.__ef_migrations_history;') -ne '13') {
        throw 'Post-commit hold verification failed; keep both application services disabled and investigate.'
    }
    if ($Mode -eq 'Operational') { Assert-OperationalServicesStopped }
    Write-Output "PHASE3_LEGACY_CATEGORY_HOLD_PASS Count=2 SHA256=$rowHash"
}
finally {
    $env:PGPASSWORD = $previousPgPassword
    $env:PGSSLMODE = $previousPgSslMode
    $env:PGCONNECT_TIMEOUT = $previousPgConnectTimeout
}
