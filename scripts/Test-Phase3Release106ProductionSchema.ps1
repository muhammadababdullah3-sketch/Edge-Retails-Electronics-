[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupEvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Phase3ConnectionString.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Phase3PostgresAddress.psm1') -Force

function Assert-Check {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$FailureCode)
    if (-not $Condition) { throw $FailureCode }
}

function ConvertTo-LibpqSslMode {
    param([Parameter(Mandatory)][string]$Value)
    $key = $Value.Replace('-', '').Replace('_', '')
    $map = @{ Disable = 'disable'; Allow = 'allow'; Prefer = 'prefer'; Require = 'require'; VerifyCA = 'verify-ca'; VerifyFull = 'verify-full' }
    $matches = @($map.Keys | Where-Object { $_ -ieq $key })
    Assert-Check ($matches.Count -eq 1) 'CONFIG_SSL_MODE_UNSUPPORTED'
    return $map[$matches[0]]
}

function ConvertTo-WindowsCommandLineArgument {
    param([AllowEmptyString()][string]$Value)
    if ($null -eq $Value) { $Value = '' }
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            for ($index = 0; $index -lt (2 * $slashes + 1); $index++) { [void]$builder.Append('\') }
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) {
            for ($index = 0; $index -lt $slashes; $index++) { [void]$builder.Append('\') }
            $slashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) {
        for ($index = 0; $index -lt (2 * $slashes); $index++) { [void]$builder.Append('\') }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-ProductionPsql {
    param([Parameter(Mandatory)][string]$Sql)
    $arguments = @('-X', '-A', '-t', '-F', "`t", '--no-password', '--no-psqlrc', '--set=ON_ERROR_STOP=1', '--host', $script:Target.Host,
        '--port', [string]$script:Target.Port, '--username', $script:Target.Username, '--dbname', $script:Target.Database, '--command', $Sql)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $script:PsqlPath
    $start.Arguments = (($arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument ([string]$_) }) -join ' ')
    $start.WorkingDirectory = $script:RepoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'PRODUCTION_PSQL_START_FAILED' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.Result
    $exitCode = $process.ExitCode
    $process.Dispose()
    Assert-Check ($exitCode -eq 0) 'PRODUCTION_PSQL_VERIFICATION_FAILED'
    $trimmed = $stdout.TrimEnd("`r", "`n")
    if ([string]::IsNullOrWhiteSpace($trimmed)) { return }
    return $trimmed -split "`r?`n"
}

Assert-Check ([string]::IsNullOrWhiteSpace($env:EDGE_RETAILS_DB)) 'ENVIRONMENT_DATABASE_OVERRIDE_PRESENT'
Assert-Check ([IO.File]::Exists($BackupEvidencePath)) 'BACKUP_EVIDENCE_MISSING'
$backupEvidence = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText([IO.Path]::GetFullPath($BackupEvidencePath)))
Assert-Check ($backupEvidence.CompletionStatus -ceq 'PASS' -and $backupEvidence.Provider -ceq 'PostgreSQL 18 / Npgsql') 'BACKUP_EVIDENCE_NOT_PASS'
Assert-Check ($backupEvidence.Stages.ProductionServicesQuiescedForCutoverBackup.Status -ceq 'PASS') 'BACKUP_WAS_NOT_QUIESCED'
Assert-Check ($backupEvidence.Backup.RestoredExactArchivePass -eq $true -and $backupEvidence.Rehearsal.CleanupStatus -ceq 'PASS') 'BACKUP_RESTORE_REHEARSAL_NOT_PASS'
Assert-Check ($backupEvidence.Rehearsal.FinalHistoryCount -eq 20) 'BACKUP_CLONE_FINAL_HISTORY_NOT_20'
$productionAlreadyAtFinal20 = $backupEvidence.Rehearsal.ProductionAlreadyAtFinalMigration20 -eq $true
if ($productionAlreadyAtFinal20) {
    Assert-Check ($backupEvidence.MigrationIds.ProductionBaseline.Count -eq 20 -and
        $backupEvidence.MigrationIds.RehearsedForward.Count -eq 0 -and
        $backupEvidence.Stages.CurrentProductionArchiveRestoredAndVerifiedAtFinalMigration20.Status -ceq 'PASS') 'BACKUP_RESTORED_FINAL_HISTORY_INVALID'
}
else {
    Assert-Check ($backupEvidence.MigrationIds.ProductionBaseline.Count -eq 18 -and
        $backupEvidence.MigrationIds.RehearsedForward.Count -eq 2 -and
        $backupEvidence.Stages.CloneForwardMigration19.Status -ceq 'PASS' -and
        $backupEvidence.Stages.CloneForwardMigration20AndFinalInvariants.Status -ceq 'PASS') 'BACKUP_CLONE_FORWARD_CHAIN_INVALID'
}
$backupPath = [IO.Path]::GetFullPath([string]$backupEvidence.Backup.Path)
Assert-Check ([IO.File]::Exists($backupPath)) 'BACKUP_ARCHIVE_MISSING'
$backupSha = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Check ($backupSha -ceq [string]$backupEvidence.Backup.Sha256) 'BACKUP_ARCHIVE_HASH_MISMATCH'
$expectedCategorySha = [string]$backupEvidence.Stages.ProductionReadOnlyBaselineAndInvariants.Details.CategoryRowsSha256
Assert-Check ($expectedCategorySha -match '^[0-9a-f]{64}$') 'BACKUP_CATEGORY_HASH_MISSING'

$configPath = 'C:\ProgramData\EdgeRetails\config.json'
Assert-Check ([IO.File]::Exists($configPath)) 'PROGRAMDATA_CONFIG_MISSING'
$configItem = Get-Item -LiteralPath $configPath -Force
Assert-Check (($configItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'PROGRAMDATA_CONFIG_IS_REPARSE_POINT'
$configAcl = Get-Acl -LiteralPath $configPath
Assert-Check ($configAcl.AreAccessRulesProtected) 'PROGRAMDATA_CONFIG_ACL_INHERITED'
$configOwner = $configAcl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
Assert-Check ($configOwner -in @('S-1-5-18', 'S-1-5-32-544')) 'PROGRAMDATA_CONFIG_OWNER_UNTRUSTED'
$aclRules = $configAcl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
foreach ($rule in $aclRules) {
    Assert-Check (-not ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -in @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545'))) 'PROGRAMDATA_CONFIG_BROAD_READ_OR_WRITE_ACCESS'
}
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $hasFullControl = @($aclRules | Where-Object {
        $_.IdentityReference.Value -eq $sid -and
        ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -eq [System.Security.AccessControl.FileSystemRights]::FullControl -and
        $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow
    }).Count -gt 0
    Assert-Check $hasFullControl 'PROGRAMDATA_CONFIG_REQUIRED_ACL_MISSING'
}

$config = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($configPath))
$connectionStrings = @()
if ($config.PSObject.Properties['EDGE_RETAILS_DB'] -and $config.EDGE_RETAILS_DB -is [string] -and -not [string]::IsNullOrWhiteSpace($config.EDGE_RETAILS_DB)) {
    $connectionStrings += [string]$config.EDGE_RETAILS_DB
}
if ($config.PSObject.Properties['DatabaseConnectionString'] -and $config.DatabaseConnectionString -is [string] -and -not [string]::IsNullOrWhiteSpace($config.DatabaseConnectionString)) {
    $connectionStrings += [string]$config.DatabaseConnectionString
}
if ($config.PSObject.Properties['ConnectionStrings'] -and $null -ne $config.ConnectionStrings) {
    foreach ($property in $config.ConnectionStrings.PSObject.Properties) {
        if ($property.Value -is [string] -and -not [string]::IsNullOrWhiteSpace($property.Value)) { $connectionStrings += [string]$property.Value }
    }
}
Assert-Check ($connectionStrings.Count -gt 0) 'PROGRAMDATA_CONNECTION_STRING_MISSING'
$targets = foreach ($connectionString in $connectionStrings) {
    $builder = ConvertFrom-EdgeRetailsNpgsqlConnectionString $connectionString
    [pscustomobject]@{
        Host = Get-EdgeRetailsConnectionStringValue $builder @('Host', 'Server', 'Data Source') $null
        Port = Get-EdgeRetailsConnectionStringValue $builder @('Port') '5432'
        Database = Get-EdgeRetailsConnectionStringValue $builder @('Database', 'Initial Catalog') $null
        Username = Get-EdgeRetailsConnectionStringValue $builder @('Username', 'User ID', 'User') $null
        Password = Get-EdgeRetailsConnectionStringValue $builder @('Password', 'Pwd') ''
        SslMode = Get-EdgeRetailsConnectionStringValue $builder @('SSL Mode', 'SslMode') 'Prefer'
    }
}
$script:Target = $targets[0]
foreach ($target in $targets | Select-Object -Skip 1) {
    Assert-Check ($target.Host -ceq $script:Target.Host -and $target.Port -ceq $script:Target.Port -and
        $target.Database -ceq $script:Target.Database -and $target.Username -ceq $script:Target.Username -and
        $target.Password -ceq $script:Target.Password -and $target.SslMode -ceq $script:Target.SslMode) 'PROGRAMDATA_CONNECTION_AUTHORITIES_DISAGREE'
}
Assert-Check ($script:Target.Host -ceq '127.0.0.1' -and $script:Target.Port -ceq '5432' -and
    $script:Target.Database -ceq 'edge_retails_prod' -and $script:Target.Username -ceq 'er_app_user') 'PRODUCTION_TARGET_NOT_APPROVED'

$postgresService = Get-Service -Name 'postgresql-x64-18' -ErrorAction Stop
$serverService = Get-Service -Name 'EdgeRetailsServer' -ErrorAction Stop
$workerService = Get-Service -Name 'EdgeRetailsWorker' -ErrorAction Stop
Assert-Check ($postgresService.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) 'POSTGRESQL18_SERVICE_NOT_RUNNING'
Assert-Check ($serverService.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) 'SERVER_NOT_STOPPED_DURING_MIGRATION'
Assert-Check ($workerService.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) 'WORKER_NOT_STOPPED_DURING_MIGRATION'
Assert-Check (@(Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue).Count -eq 0) 'SHOP_SERVER_LISTENER_PRESENT_DURING_MIGRATION'

$migrationDirectory = Join-Path $PSScriptRoot '..\src\EdgeRetails.Infrastructure\Persistence\Migrations'
$sourceMigrations = @(
    Get-ChildItem -LiteralPath $migrationDirectory -File -Filter '*.cs' |
        Where-Object { $_.Name -notlike '*.Designer.cs' -and $_.BaseName -notlike '*ModelSnapshot' -and $_.BaseName -match '^\d{14}_.+$' } |
        ForEach-Object { $_.BaseName } | Sort-Object -CaseSensitive
)
Assert-Check ($sourceMigrations.Count -eq 20) 'SOURCE_MIGRATION_INVENTORY_NOT_20'
Assert-Check ($sourceMigrations[18] -ceq '20260930053030_Phase3COwnerPinRecoveryReplaySafety' -and
    $sourceMigrations[19] -ceq '20260930065058_Phase3COwnerPinAuthorizationConsumption') 'SOURCE_FORWARD_MIGRATION_CHAIN_UNEXPECTED'
Assert-Check ($backupEvidence.MigrationIds.Final.Count -eq $sourceMigrations.Count) 'BACKUP_FINAL_MIGRATION_COUNT_MISMATCH'
for ($i = 0; $i -lt $sourceMigrations.Count; $i++) {
    Assert-Check ($backupEvidence.MigrationIds.Final[$i] -ceq $sourceMigrations[$i]) ('BACKUP_FINAL_MIGRATION_ID_MISMATCH_' + $i)
}
$expectedBackupBaseline = if ($productionAlreadyAtFinal20) { $sourceMigrations } else { @($sourceMigrations[0..17]) }
for ($i = 0; $i -lt $expectedBackupBaseline.Count; $i++) {
    Assert-Check ($backupEvidence.MigrationIds.ProductionBaseline[$i] -ceq $expectedBackupBaseline[$i]) ('BACKUP_BASELINE_MIGRATION_ID_MISMATCH_' + $i)
}
if (-not $productionAlreadyAtFinal20) {
    for ($i = 0; $i -lt 2; $i++) {
        Assert-Check ($backupEvidence.MigrationIds.RehearsedForward[$i] -ceq $sourceMigrations[18 + $i]) ('BACKUP_FORWARD_MIGRATION_ID_MISMATCH_' + $i)
    }
}

$script:PsqlPath = Join-Path $env:ProgramFiles 'PostgreSQL\18\bin\psql.exe'
$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Assert-Check ([IO.File]::Exists($script:PsqlPath)) 'POSTGRESQL18_PSQL_MISSING'
$sslMode = ConvertTo-LibpqSslMode $script:Target.SslMode
$env:PGPASSWORD = $script:Target.Password
$env:PGSSLMODE = $sslMode
$env:PGCONNECT_TIMEOUT = '15'
$env:PGAPPNAME = 'EdgeRetails-Phase3-Production-Schema-Verify'
try {
    $identitySql = "SELECT current_database(), current_user, coalesce(inet_server_addr()::text,''), inet_server_port(), current_setting('server_version_num');"
    $identity = @(Invoke-ProductionPsql $identitySql)
    Assert-Check ($identity.Count -eq 1) 'PRODUCTION_IDENTITY_SHAPE_INVALID'
    $identityParts = $identity[0] -split "`t"
    Assert-Check ($identityParts.Count -eq 5 -and $identityParts[0] -ceq $script:Target.Database -and $identityParts[1] -ceq $script:Target.Username) 'PRODUCTION_IDENTITY_MISMATCH'
    $serverAddress = ConvertFrom-Phase3PostgresServerAddress $identityParts[2]
    $serverVersionNum = [int]$identityParts[4]
    Assert-Check ($serverAddress.IsLoopback -and $serverAddress.HasHostPrefix -and [int]$identityParts[3] -eq [int]$script:Target.Port) 'PRODUCTION_ENDPOINT_NOT_LOOPBACK'
    Assert-Check ($serverVersionNum -ge 180000 -and $serverVersionNum -lt 190000) 'PRODUCTION_SERVER_MAJOR_NOT_18'

    $history = @(Invoke-ProductionPsql 'SELECT "MigrationId" FROM system.__ef_migrations_history ORDER BY "MigrationId";')
    Assert-Check ($history.Count -eq $sourceMigrations.Count) 'PRODUCTION_MIGRATION_COUNT_MISMATCH'
    for ($i = 0; $i -lt $sourceMigrations.Count; $i++) { Assert-Check ($history[$i] -ceq $sourceMigrations[$i]) ('PRODUCTION_MIGRATION_ID_MISMATCH_' + $i) }

    $categoryRows = @(Invoke-ProductionPsql 'SELECT to_jsonb(c)::text FROM catalog.categories AS c ORDER BY to_jsonb(c)::text COLLATE "C";')
    $categoryBytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", [string[]]$categoryRows))
    $categoryHash = [Security.Cryptography.SHA256]::Create()
    try { $categorySha = ([BitConverter]::ToString($categoryHash.ComputeHash($categoryBytes))).Replace('-', '').ToLowerInvariant() }
    finally { $categoryHash.Dispose() }
    Assert-Check ($categoryRows.Count -eq 2 -and $categorySha -ceq $expectedCategorySha) 'PRODUCTION_CATEGORY_DATA_CHANGED'

    $structureSql = @'
SELECT (SELECT count(*) FROM pg_namespace WHERE nspname IN ('system','audit','catalog'))::text || E'\t' ||
       (SELECT count(*) FROM information_schema.tables WHERE table_schema='system' AND lower(table_name)='__ef_migrations_history')::text || E'\t' ||
       (SELECT count(*) FROM information_schema.tables WHERE table_schema='catalog' AND table_name='categories')::text || E'\t' ||
       (SELECT count(*) FROM information_schema.tables WHERE table_schema='audit' AND table_name='business_events')::text || E'\t' ||
       (SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema') AND tablename ILIKE '%hold%')::text || E'\t' ||
       (SELECT count(*) FROM pg_class r JOIN pg_namespace n ON n.oid=r.relnamespace WHERE r.relkind IN ('r','p') AND ((n.nspname='system' AND lower(r.relname)='__ef_migrations_history') OR (n.nspname='catalog' AND r.relname='categories') OR (n.nspname='audit' AND r.relname='business_events')) AND EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid=r.oid AND i.indisvalid AND i.indisready))::text;
'@
    $structure = @(Invoke-ProductionPsql $structureSql)
    $structureParts = $structure[0] -split "`t"
    Assert-Check ($structureParts.Count -eq 6 -and [int]$structureParts[0] -eq 3 -and [int]$structureParts[1] -eq 1 -and
        [int]$structureParts[2] -eq 1 -and [int]$structureParts[3] -eq 1 -and [int]$structureParts[4] -eq 0 -and [int]$structureParts[5] -eq 3) 'PRODUCTION_REQUIRED_STRUCTURE_INVALID'

    $indexSql = @'
SELECT ix.relname || E'\t' || i.indisunique::text || E'\t' || i.indisvalid::text || E'\t' || i.indisready::text || E'\t' ||
       pg_get_indexdef(i.indexrelid) || E'\t' || coalesce(pg_get_expr(i.indpred, i.indrelid), '')
FROM pg_index i JOIN pg_class ix ON ix.oid=i.indexrelid JOIN pg_namespace ns ON ns.oid=ix.relnamespace
WHERE ns.nspname='audit' AND ix.relname IN ('ux_business_events_pin_recovery_success_operation','ux_business_events_pin_recovery_consumed_nonce')
ORDER BY ix.relname;
'@
    $indexes = @(Invoke-ProductionPsql $indexSql)
    Assert-Check ($indexes.Count -eq 2) 'PRODUCTION_RECOVERY_INDEX_COUNT_INVALID'
    foreach ($expected in @(
        [pscustomobject]@{ Name = 'ux_business_events_pin_recovery_success_operation'; Action = 'USER_PIN_RECOVERY_SUCCEEDED' },
        [pscustomobject]@{ Name = 'ux_business_events_pin_recovery_consumed_nonce'; Action = 'USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED' }
    )) {
        $matching = @($indexes | Where-Object { $_ -match ('^' + [Regex]::Escape($expected.Name) + "`t") })
        Assert-Check ($matching.Count -eq 1) 'PRODUCTION_RECOVERY_INDEX_MISSING_OR_DUPLICATE'
        $parts = $matching[0] -split "`t", 6
        Assert-Check ($parts.Count -eq 6 -and $parts[1] -eq 'true' -and $parts[2] -eq 'true' -and $parts[3] -eq 'true') 'PRODUCTION_RECOVERY_INDEX_NOT_VALID_UNIQUE'
        Assert-Check ($parts[4] -match '(?i)ON\s+audit\.business_events.*\(correlation_id,\s*action\)' -and $parts[5] -match [Regex]::Escape($expected.Action)) 'PRODUCTION_RECOVERY_INDEX_DEFINITION_INVALID'
    }

    Write-Output "Provider=PostgreSQL 18 / Npgsql; ServerVersionNum=$serverVersionNum; Database=$($script:Target.Database); Role=$($script:Target.Username); History=$($history.Count); Latest=$($history[-1]); BackupSHA256=$backupSha; Categories=$($categoryRows.Count); CategorySHA256=$categorySha; RecoveryIndexes=$($indexes.Count); HoldTables=$($structureParts[4]); Server=Stopped; Worker=Stopped"
    Write-Output 'PHASE3_RELEASE106_PRODUCTION_SCHEMA_PASS'
}
finally {
    Remove-Item Env:\PGPASSWORD, Env:\PGSSLMODE, Env:\PGCONNECT_TIMEOUT, Env:\PGAPPNAME -ErrorAction SilentlyContinue
    $script:Target.Password = $null
}
