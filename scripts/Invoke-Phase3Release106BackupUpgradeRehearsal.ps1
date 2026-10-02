[CmdletBinding()]
param(
    [Parameter()]
    [string]$BackupDirectory = (Join-Path $env:LOCALAPPDATA 'EdgeRetails\Production\backups')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Phase3ConnectionString.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Phase3PostgresAddress.psm1') -Force

$script:RunId = [Guid]::NewGuid().ToString('D')
$script:StartedUtc = [DateTime]::UtcNow
$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:ConfigPath = 'C:\ProgramData\EdgeRetails\config.json'
$script:SourceMigrationIds = @()
$script:Production = $null
$script:BackupPath = $null
$script:EvidencePath = $null
$script:BackupSha256 = $null
$script:ClusterRoot = $null
$script:ClusterData = $null
$script:ClusterPort = $null
$script:ClusterDatabase = $null
$script:ClusterStarted = $false
$script:ClusterOwned = $false
$script:PgBin = $null
$script:FailureStep = $null
$script:CleanupSucceeded = $null
$script:Evidence = [ordered]@{
    SchemaVersion = 1
    RunId = $script:RunId
    StartedUtc = $script:StartedUtc.ToString('o')
    FinishedUtc = $null
    Provider = 'PostgreSQL 18 / Npgsql'
    CompletionStatus = 'IN_PROGRESS'
    FailureStep = $null
    EvidenceWriteFailureType = $null
    ProductionTarget = $null
    Backup = $null
    MigrationIds = [ordered]@{
        ProductionBaseline = @()
        RehearsedForward = @()
        Final = @()
    }
    Rehearsal = $null
    Stages = [ordered]@{}
}

function Set-Stage {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'NOT_RUN')][string]$Status,
        [string]$Command,
        [Nullable[int]]$ExitCode,
        [hashtable]$Details
    )

    $stage = [ordered]@{ Status = $Status }
    if ($Command) { $stage.Command = $Command }
    if ($null -ne $ExitCode) { $stage.ExitCode = [int]$ExitCode }
    if ($Details) { $stage.Details = $Details }
    $script:Evidence.Stages[$Name] = $stage
    Save-Evidence
}

function Save-Evidence {
    if (-not $script:EvidencePath) { return }
    $tempPath = Join-Path (Split-Path -Parent $script:EvidencePath) ('.' + [IO.Path]::GetFileName($script:EvidencePath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $json = $script:Evidence | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($tempPath, $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
    try {
        if ([IO.File]::Exists($script:EvidencePath)) {
            $previousPath = $tempPath + '.previous'
            [IO.File]::Replace($tempPath, $script:EvidencePath, $previousPath)
            if ([IO.File]::Exists($previousPath)) { [IO.File]::Delete($previousPath) }
        }
        else {
            [IO.File]::Move($tempPath, $script:EvidencePath)
        }
    }
    finally {
        if ([IO.File]::Exists($tempPath)) { [IO.File]::Delete($tempPath) }
    }
}

function Assert-Condition {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$FailureCode)
    if (-not $Condition) { throw $FailureCode }
}

function ConvertTo-ProductionTarget {
    param([Parameter(Mandatory)][string]$ConnectionString)

    $builder = ConvertFrom-EdgeRetailsNpgsqlConnectionString $ConnectionString
    $hostName = Get-EdgeRetailsConnectionStringValue $builder @('Host', 'Server', 'Data Source') $null
    $database = Get-EdgeRetailsConnectionStringValue $builder @('Database', 'Initial Catalog') $null
    $username = Get-EdgeRetailsConnectionStringValue $builder @('Username', 'User ID', 'User') $null
    $password = Get-EdgeRetailsConnectionStringValue $builder @('Password', 'Pwd') ''
    $portText = Get-EdgeRetailsConnectionStringValue $builder @('Port') '5432'
    $sslMode = Get-EdgeRetailsConnectionStringValue $builder @('SSL Mode', 'SslMode') 'Prefer'
    $trustCert = Get-EdgeRetailsConnectionStringValue $builder @('Trust Server Certificate', 'TrustServerCertificate') 'false'
    $integrated = Get-EdgeRetailsConnectionStringValue $builder @('Integrated Security', 'SSPI') 'false'
    $targetSession = Get-EdgeRetailsConnectionStringValue $builder @('Target Session Attributes', 'TargetSessionAttributes') 'Primary'

    Assert-Condition (-not [string]::IsNullOrWhiteSpace($hostName)) 'CONFIG_HOST_MISSING'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($database)) 'CONFIG_DATABASE_MISSING'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($username)) 'CONFIG_USERNAME_MISSING'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($password)) 'CONFIG_PASSWORD_MISSING'
    Assert-Condition ($hostName -notmatch ',|;|\s') 'CONFIG_MULTIPLE_OR_INVALID_HOST'
    Assert-Condition ($hostName -ceq '127.0.0.1') 'CONFIG_HOST_NOT_127001'
    Assert-Condition ($integrated -notmatch '^(?i:true|yes|1)$') 'CONFIG_INTEGRATED_AUTH_UNSUPPORTED'
    Assert-Condition ($targetSession -match '^(?i:Primary|PrimaryOnly)$') 'CONFIG_TARGET_SESSION_NOT_PRIMARY'
    Assert-Condition ($trustCert -notmatch '^(?i:true|yes|1)$') 'CONFIG_UNVERIFIABLE_CERTIFICATE_MODE'

    $port = 0
    Assert-Condition ([int]::TryParse($portText, [ref]$port) -and $port -ge 1 -and $port -le 65535) 'CONFIG_PORT_INVALID'
    $sslMap = @{
        Disable = 'disable'; Allow = 'allow'; Prefer = 'prefer'; Require = 'require'
        VerifyCA = 'verify-ca'; VerifyFull = 'verify-full'
    }
    $sslKey = $sslMode.Replace('-', '').Replace('_', '')
    $sslMatch = @($sslMap.Keys | Where-Object { $_ -ieq $sslKey })
    Assert-Condition ($sslMatch.Count -eq 1) 'CONFIG_SSL_MODE_UNSUPPORTED'

    return [pscustomobject]@{
        Host = $hostName
        Port = $port
        Database = $database
        Username = $username
        Password = $password
        SslMode = $sslMap[$sslMatch[0]]
    }
}

function Get-ProtectedRuntimeTarget {
    Assert-Condition ([string]::IsNullOrWhiteSpace($env:EDGE_RETAILS_DB)) 'ENVIRONMENT_DATABASE_OVERRIDE_PRESENT'
    Assert-Condition ([IO.File]::Exists($script:ConfigPath)) 'PROGRAMDATA_CONFIG_MISSING'
    $item = Get-Item -LiteralPath $script:ConfigPath -Force
    Assert-Condition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'PROGRAMDATA_CONFIG_IS_REPARSE_POINT'

    $acl = Get-Acl -LiteralPath $script:ConfigPath
    Assert-Condition ($acl.AreAccessRulesProtected) 'PROGRAMDATA_CONFIG_ACL_INHERITED'
    $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    Assert-Condition ($ownerSid -in @('S-1-5-18', 'S-1-5-32-544')) 'PROGRAMDATA_CONFIG_OWNER_UNTRUSTED'
    $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
    $broadSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -in $broadSids) {
            throw 'PROGRAMDATA_CONFIG_BROAD_READ_OR_WRITE_ACCESS'
        }
    }
    $adminSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $systemSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $adminFull = @($rules | Where-Object { $_.IdentityReference.Value -eq $adminSid.Value -and ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -eq [System.Security.AccessControl.FileSystemRights]::FullControl -and $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow }).Count -gt 0
    $systemFull = @($rules | Where-Object { $_.IdentityReference.Value -eq $systemSid.Value -and ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -eq [System.Security.AccessControl.FileSystemRights]::FullControl -and $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow }).Count -gt 0
    Assert-Condition ($adminFull -and $systemFull) 'PROGRAMDATA_CONFIG_REQUIRED_ACL_MISSING'

    $configText = [IO.File]::ReadAllText($script:ConfigPath)
    $config = ConvertFrom-Json -InputObject $configText
    $candidates = @()
    if ($config.PSObject.Properties['EDGE_RETAILS_DB'] -and $config.EDGE_RETAILS_DB -is [string] -and -not [string]::IsNullOrWhiteSpace($config.EDGE_RETAILS_DB)) {
        $candidates += [pscustomobject]@{ Name = 'EDGE_RETAILS_DB'; Value = [string]$config.EDGE_RETAILS_DB }
    }
    if ($config.PSObject.Properties['DatabaseConnectionString'] -and $config.DatabaseConnectionString -is [string] -and -not [string]::IsNullOrWhiteSpace($config.DatabaseConnectionString)) {
        $candidates += [pscustomobject]@{ Name = 'DatabaseConnectionString'; Value = [string]$config.DatabaseConnectionString }
    }
    if ($config.PSObject.Properties['ConnectionStrings'] -and $null -ne $config.ConnectionStrings) {
        foreach ($property in $config.ConnectionStrings.PSObject.Properties) {
            if ($property.Value -is [string] -and -not [string]::IsNullOrWhiteSpace($property.Value)) {
                $candidates += [pscustomobject]@{ Name = ('ConnectionStrings.' + $property.Name); Value = [string]$property.Value }
            }
        }
    }
    Assert-Condition ($candidates.Count -ge 1) 'PROGRAMDATA_CONNECTION_STRING_MISSING'

    $targets = @()
    foreach ($candidate in $candidates) {
        $targets += [pscustomobject]@{ Name = $candidate.Name; ConnectionString = $candidate.Value; Target = (ConvertTo-ProductionTarget $candidate.Value) }
    }
    $first = $targets[0].Target
    foreach ($entry in $targets | Select-Object -Skip 1) {
        $same = ($entry.Target.Host -ieq $first.Host) -and ($entry.Target.Port -eq $first.Port) -and
            ($entry.Target.Database -ceq $first.Database) -and ($entry.Target.Username -ceq $first.Username) -and
            ($entry.Target.Password -ceq $first.Password) -and ($entry.Target.SslMode -ceq $first.SslMode)
        Assert-Condition $same 'PROGRAMDATA_CONNECTION_AUTHORITIES_DISAGREE'
    }
    return [pscustomobject]@{
        Host = $first.Host; Port = $first.Port; Database = $first.Database; Username = $first.Username
        ConnectionString = $targets[0].ConnectionString
        Password = $first.Password; SslMode = $first.SslMode; ConfigKeys = @($targets.Name)
    }
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

function Invoke-External {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [string[]]$RemoveEnvironmentPrefixes = @('PG', 'EDGE_RETAILS_DB'),
        [int[]]$AllowedExitCodes = @(0),
        [switch]$NoOutputCapture
    )

    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $FilePath
    $start.Arguments = (($Arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument ([string]$_) }) -join ' ')
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = -not $NoOutputCapture
    $start.RedirectStandardError = -not $NoOutputCapture
    $envKeys = @($start.EnvironmentVariables.Keys)
    foreach ($key in $envKeys) {
        foreach ($prefix in $RemoveEnvironmentPrefixes) {
            if ([string]$key -like ($prefix + '*')) { [void]$start.EnvironmentVariables.Remove([string]$key); break }
        }
    }
    foreach ($key in $Environment.Keys) { $start.EnvironmentVariables[[string]$key] = [string]$Environment[$key] }

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'PROCESS_START_FAILED' }
    if (-not $NoOutputCapture) {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
    }
    $process.WaitForExit()
    if ($NoOutputCapture) {
        $stdout = ''
        $stderr = ''
    }
    else {
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
    }
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -notin $AllowedExitCodes) {
        return [pscustomobject]@{ ExitCode = $exitCode; Stdout = $stdout; Stderr = $stderr; Command = (($FilePath) + ' ' + ($Arguments -join ' ')) }
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Stdout = $stdout; Stderr = $stderr; Command = (($FilePath) + ' ' + ($Arguments -join ' ')) }
}

function Assert-ExternalSuccess {
    param([Parameter(Mandatory)]$Result, [Parameter(Mandatory)][string]$FailureCode)
    if ($Result.ExitCode -ne 0) { throw ($FailureCode + '_EXIT_' + $Result.ExitCode) }
}

function Get-PgEnvironment {
    param([Parameter(Mandatory)]$Target, [string]$Password = $Target.Password)
    return @{
        PGPASSWORD = [string]$Password
        PGSSLMODE = [string]$Target.SslMode
        PGCONNECT_TIMEOUT = '15'
        PGAPPNAME = 'EdgeRetails-Phase3-Release106-Rehearsal'
    }
}

function Invoke-ProductionPsql {
    param([Parameter(Mandatory)][string]$Sql)
    $args = @('--no-password', '--no-psqlrc', '--quiet', '--tuples-only', '--no-align', '--set=ON_ERROR_STOP=1', '--host', $script:Production.Host, '--port', [string]$script:Production.Port, '--username', $script:Production.Username, '--dbname', $script:Production.Database, '--command', $Sql)
    $result = Invoke-External (Join-Path $script:PgBin 'psql.exe') $args $script:RepoRoot (Get-PgEnvironment $script:Production)
    Assert-ExternalSuccess $result 'PRODUCTION_PSQL_FAILED'
    return $result.Stdout.TrimEnd("`r", "`n")
}

function Invoke-ClonePsql {
    param([Parameter(Mandatory)][string]$Sql)
    $args = @('--no-password', '--no-psqlrc', '--quiet', '--tuples-only', '--no-align', '--set=ON_ERROR_STOP=1', '--host', '127.0.0.1', '--port', [string]$script:ClusterPort, '--username', 'postgres', '--dbname', $script:ClusterDatabase, '--command', $Sql)
    $result = Invoke-External (Join-Path $script:PgBin 'psql.exe') $args $script:RepoRoot (Get-PgEnvironment ([pscustomobject]@{ SslMode = 'disable'; Password = '' }) '')
    Assert-ExternalSuccess $result 'CLONE_PSQL_FAILED'
    return $result.Stdout.TrimEnd("`r", "`n")
}

function Invoke-ClusterAdminPsql {
    param([Parameter(Mandatory)][string]$Sql)
    $args = @('--no-password', '--no-psqlrc', '--quiet', '--tuples-only', '--no-align', '--set=ON_ERROR_STOP=1', '--host', '127.0.0.1', '--port', [string]$script:ClusterPort, '--username', 'postgres', '--dbname', 'postgres', '--command', $Sql)
    $result = Invoke-External (Join-Path $script:PgBin 'psql.exe') $args $script:RepoRoot (Get-PgEnvironment ([pscustomobject]@{ SslMode = 'disable'; Password = '' }) '')
    Assert-ExternalSuccess $result 'CLUSTER_ADMIN_PSQL_FAILED'
    return $result.Stdout.TrimEnd("`r", "`n")
}

function Get-HistoryIds {
    param([ValidateSet('Production', 'Clone')][string]$Target = 'Clone')
    $sql = 'SELECT "MigrationId" FROM system.__ef_migrations_history ORDER BY "MigrationId";'
    if ($Target -eq 'Production') { $text = Invoke-ProductionPsql $sql } else { $text = Invoke-ClonePsql $sql }
    return @($text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })
}

function Get-CategorySnapshot {
    param([ValidateSet('Production', 'Clone')][string]$Target = 'Clone')
    $countSql = 'SELECT count(*)::text FROM catalog.categories;'
    $rowsSql = 'SELECT to_jsonb(c)::text FROM catalog.categories AS c ORDER BY to_jsonb(c)::text COLLATE "C";'
    if ($Target -eq 'Production') {
        $countText = Invoke-ProductionPsql $countSql
        $rowsText = Invoke-ProductionPsql $rowsSql
    }
    else {
        $countText = Invoke-ClonePsql $countSql
        $rowsText = Invoke-ClonePsql $rowsSql
    }
    $rows = @()
    if (-not [string]::IsNullOrWhiteSpace($rowsText)) { $rows = @($rowsText -split "`r?`n") }
    $canonical = [string]::Join("`n", $rows)
    $bytes = [Text.Encoding]::UTF8.GetBytes($canonical)
    $sha = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    return [pscustomobject]@{ Count = [int]$countText.Trim(); RowsSha256 = $sha }
}

function Get-StructuralSnapshot {
    param([ValidateSet('Production', 'Clone')][string]$Target = 'Clone')
    $sql = @'
SELECT
  (SELECT count(*) FROM pg_namespace WHERE nspname IN ('system','audit','catalog'))::text || E'\t' ||
  (SELECT count(*) FROM information_schema.tables WHERE table_schema='system' AND lower(table_name)='__ef_migrations_history')::text || E'\t' ||
  (SELECT count(*) FROM information_schema.tables WHERE table_schema='catalog' AND table_name='categories')::text || E'\t' ||
  (SELECT count(*) FROM information_schema.tables WHERE table_schema='audit' AND table_name='business_events')::text || E'\t' ||
  (SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema') AND tablename ILIKE '%hold%')::text || E'\t' ||
  (SELECT count(*) FROM pg_class r JOIN pg_namespace n ON n.oid=r.relnamespace WHERE r.relkind IN ('r','p') AND ((n.nspname='system' AND lower(r.relname)='__ef_migrations_history') OR (n.nspname='catalog' AND r.relname='categories') OR (n.nspname='audit' AND r.relname='business_events')) AND EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid=r.oid AND i.indisvalid AND i.indisready))::text;
'@
    if ($Target -eq 'Production') { $line = Invoke-ProductionPsql $sql } else { $line = Invoke-ClonePsql $sql }
    $parts = $line.Trim() -split "`t"
    Assert-Condition ($parts.Count -eq 6) 'STRUCTURE_QUERY_SHAPE_INVALID'
    return [pscustomobject]@{
        RequiredSchemas = [int]$parts[0]
        HistoryTable = [int]$parts[1]
        CategoriesTable = [int]$parts[2]
        BusinessEventsTable = [int]$parts[3]
        HoldTables = [int]$parts[4]
        IndexedRequiredTables = [int]$parts[5]
    }
}

function Assert-RequiredStructure {
    param([Parameter(Mandatory)]$Snapshot)
    Assert-Condition ($Snapshot.RequiredSchemas -eq 3) 'REQUIRED_SCHEMA_MISSING'
    Assert-Condition ($Snapshot.HistoryTable -eq 1) 'MIGRATION_HISTORY_TABLE_MISSING'
    Assert-Condition ($Snapshot.CategoriesTable -eq 1) 'CATEGORIES_TABLE_MISSING'
    Assert-Condition ($Snapshot.BusinessEventsTable -eq 1) 'BUSINESS_EVENTS_TABLE_MISSING'
    Assert-Condition ($Snapshot.HoldTables -eq 0) 'UNEXPECTED_HOLD_TABLE_PRESENT'
    Assert-Condition ($Snapshot.IndexedRequiredTables -eq 3) 'REQUIRED_TABLE_INDEX_MISSING'
}

function Get-IndexSnapshot {
    param([ValidateSet('Production', 'Clone')][string]$Target = 'Clone')
    $sql = @'
SELECT i.indisunique::text || E'\t' || i.indisvalid::text || E'\t' || i.indisready::text || E'\t' ||
       pg_get_indexdef(i.indexrelid) || E'\t' || coalesce(pg_get_expr(i.indpred, i.indrelid), '')
FROM pg_index i
JOIN pg_class ix ON ix.oid=i.indexrelid
JOIN pg_namespace ns ON ns.oid=ix.relnamespace
JOIN pg_class tbl ON tbl.oid=i.indrelid
JOIN pg_namespace tn ON tn.oid=tbl.relnamespace
WHERE ns.nspname='audit' AND tn.nspname='audit' AND tbl.relname='business_events'
  AND ix.relname IN ('ux_business_events_pin_recovery_success_operation','ux_business_events_pin_recovery_consumed_nonce')
ORDER BY ix.relname;
'@
    if ($Target -eq 'Production') { $text = Invoke-ProductionPsql $sql } else { $text = Invoke-ClonePsql $sql }
    $entries = @()
    foreach ($line in @($text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $parts = $line -split "`t", 5
        if ($parts.Count -eq 5) {
            $entries += [pscustomobject]@{ Unique = $parts[0] -eq 'true'; Valid = $parts[1] -eq 'true'; Ready = $parts[2] -eq 'true'; Definition = $parts[3]; Predicate = $parts[4] }
        }
    }
    return ,$entries
}

function Assert-RecoveryIndexes {
    param([Parameter(Mandatory)]$Indexes, [Parameter(Mandatory)][bool]$RequireBoth)
    $expected = @(
        [pscustomobject]@{ Name = 'ux_business_events_pin_recovery_success_operation'; Action = 'USER_PIN_RECOVERY_SUCCEEDED' },
        [pscustomobject]@{ Name = 'ux_business_events_pin_recovery_consumed_nonce'; Action = 'USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED' }
    )
    $requiredCount = if ($RequireBoth) { 2 } else { 0 }
    Assert-Condition ($Indexes.Count -eq $requiredCount) 'RECOVERY_INDEX_BASELINE_UNEXPECTED'
    if (-not $RequireBoth) { return }
    foreach ($item in $expected) {
        $matches = @($Indexes | Where-Object { $_.Definition -match ('(?i)\b' + [Regex]::Escape($item.Name) + '\b') })
        Assert-Condition ($matches.Count -eq 1) 'RECOVERY_INDEX_MISSING_OR_DUPLICATE'
        $idx = $matches[0]
        Assert-Condition ($idx.Unique -and $idx.Valid -and $idx.Ready) 'RECOVERY_INDEX_NOT_VALID_UNIQUE'
        Assert-Condition ($idx.Definition -match '(?i)ON\s+audit\.business_events.*\(correlation_id,\s*action\)') 'RECOVERY_INDEX_WRONG_TABLE_OR_COLUMNS'
        Assert-Condition ($idx.Predicate -match [Regex]::Escape($item.Action)) 'RECOVERY_INDEX_WRONG_PREDICATE'
    }
}

function Get-SourceMigrationIds {
    $migrationDirectory = Join-Path $script:RepoRoot 'src\EdgeRetails.Infrastructure\Persistence\Migrations'
    Assert-Condition ([IO.Directory]::Exists($migrationDirectory)) 'SOURCE_MIGRATIONS_DIRECTORY_MISSING'
    $ids = @(
        Get-ChildItem -LiteralPath $migrationDirectory -File -Filter '*.cs' |
            Where-Object { $_.Name -notlike '*.Designer.cs' -and $_.BaseName -notlike '*ModelSnapshot' } |
            ForEach-Object { $_.BaseName } |
            Where-Object { $_ -match '^\d{14}_.+$' } |
            Sort-Object -CaseSensitive
    )
    $expected19 = '20260930053030_Phase3COwnerPinRecoveryReplaySafety'
    $expected20 = '20260930065058_Phase3COwnerPinAuthorizationConsumption'
    Assert-Condition ($ids.Count -eq 20) 'SOURCE_MIGRATION_COUNT_NOT_20'
    Assert-Condition ($ids[18] -ceq $expected19 -and $ids[19] -ceq $expected20) 'SOURCE_FORWARD_MIGRATION_CHAIN_UNEXPECTED'
    return ,$ids
}

function Get-PostgresTools {
    $bin = Join-Path $env:ProgramFiles 'PostgreSQL\18\bin'
    Assert-Condition ([IO.Directory]::Exists($bin)) 'POSTGRESQL_18_BIN_MISSING'
    foreach ($exe in @('pg_dump.exe', 'pg_restore.exe', 'psql.exe', 'initdb.exe', 'pg_ctl.exe', 'createdb.exe')) {
        Assert-Condition ([IO.File]::Exists((Join-Path $bin $exe))) 'POSTGRESQL_18_TOOL_MISSING'
    }
    foreach ($exe in @('pg_dump.exe', 'pg_restore.exe', 'psql.exe', 'initdb.exe', 'pg_ctl.exe', 'createdb.exe')) {
        $r = Invoke-External (Join-Path $bin $exe) @('--version') $script:RepoRoot @{}
        Assert-ExternalSuccess $r 'POSTGRESQL_TOOL_VERSION_FAILED'
        Assert-Condition ($r.Stdout -match '\b18\.\d+\b') 'POSTGRESQL_TOOL_VERSION_NOT_18'
    }
    return $bin
}

function Get-BackupPaths {
    $directory = [IO.Path]::GetFullPath($BackupDirectory)
    $approvedDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'EdgeRetails\Production\backups'))
    Assert-Condition ($directory.Equals($approvedDirectory, [StringComparison]::OrdinalIgnoreCase)) 'BACKUP_DIRECTORY_NOT_APPROVED_ROOT'
    Assert-Condition ([IO.Directory]::Exists($directory)) 'BACKUP_DIRECTORY_MISSING'
    $dirItem = Get-Item -LiteralPath $directory -Force
    Assert-Condition (($dirItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'BACKUP_DIRECTORY_IS_REPARSE_POINT'
    $backupAcl = Get-Acl -LiteralPath $directory
    $backupOwner = $backupAcl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Assert-Condition ($backupOwner -in @($currentSid, 'S-1-5-18', 'S-1-5-32-544')) 'BACKUP_DIRECTORY_OWNER_UNTRUSTED'
    $backupRules = $backupAcl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
    foreach ($rule in $backupRules) {
        if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -in @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')) {
            throw 'BACKUP_DIRECTORY_BROAD_ACCESS'
        }
    }
    $currentFull = @($backupRules | Where-Object { $_.IdentityReference.Value -eq $currentSid -and ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -eq [System.Security.AccessControl.FileSystemRights]::FullControl -and $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow }).Count -gt 0
    Assert-Condition $currentFull 'BACKUP_DIRECTORY_CURRENT_USER_ACCESS_MISSING'
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fffZ')
    do {
        $suffix = [Guid]::NewGuid().ToString('N')
        $script:BackupPath = Join-Path $directory ("phase3_release106_pre_upgrade_{0}_{1}.dump" -f $stamp, $suffix)
        $script:EvidencePath = Join-Path $directory ("phase3_release106_pre_upgrade_{0}_{1}.evidence.json" -f $stamp, $suffix)
    } while ([IO.File]::Exists($script:BackupPath) -or [IO.Directory]::Exists($script:BackupPath) -or [IO.File]::Exists($script:EvidencePath) -or [IO.Directory]::Exists($script:EvidencePath))
    Assert-Condition (-not [IO.File]::Exists($script:BackupPath) -and -not [IO.Directory]::Exists($script:BackupPath)) 'BACKUP_DESTINATION_ALREADY_EXISTS'
    Assert-Condition (-not [IO.File]::Exists($script:EvidencePath) -and -not [IO.Directory]::Exists($script:EvidencePath)) 'EVIDENCE_DESTINATION_ALREADY_EXISTS'
    $script:EvidencePath = [IO.Path]::GetFullPath($script:EvidencePath)
    $script:BackupPath = [IO.Path]::GetFullPath($script:BackupPath)
}

function Get-PsqlConnectionEnvironment {
    param([Parameter(Mandatory)]$Target)
    return (Get-PgEnvironment $Target)
}

function Get-ServerPort {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return [int]$listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
}

function Set-PrivateClusterAcl {
    param([Parameter(Mandatory)][string]$Path)
    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($currentSid)
    foreach ($sidText in @($currentSid.Value, 'S-1-5-18')) {
        $sid = New-Object System.Security.Principal.SecurityIdentifier($sidText)
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($sid, [System.Security.AccessControl.FileSystemRights]::FullControl, [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit', [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
    $verified = Get-Acl -LiteralPath $Path
    Assert-Condition ($verified.AreAccessRulesProtected) 'TEMP_CLUSTER_ACL_NOT_PROTECTED'
    Assert-Condition ($verified.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -eq $currentSid.Value) 'TEMP_CLUSTER_OWNER_MISMATCH'
}

function New-OwnedClusterRoot {
    $base = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Temp\EdgeRetails\Phase3Release106BackupUpgradeRehearsal'))
    if (-not [IO.Directory]::Exists($base)) { [void][IO.Directory]::CreateDirectory($base) }
    $baseItem = Get-Item -LiteralPath $base -Force
    Assert-Condition (($baseItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'TEMP_CLUSTER_BASE_IS_REPARSE_POINT'
    $script:ClusterRoot = [IO.Path]::GetFullPath((Join-Path $base $script:RunId))
    Assert-Condition ($script:ClusterRoot.StartsWith($base.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'TEMP_CLUSTER_PATH_OUTSIDE_BASE'
    Assert-Condition (-not [IO.Directory]::Exists($script:ClusterRoot) -and -not [IO.File]::Exists($script:ClusterRoot)) 'TEMP_CLUSTER_ROOT_ALREADY_EXISTS'
    [void][IO.Directory]::CreateDirectory($script:ClusterRoot)
    Set-PrivateClusterAcl $script:ClusterRoot
    $script:ClusterOwned = $true
    $script:ClusterData = [IO.Path]::GetFullPath((Join-Path $script:ClusterRoot 'data'))
    Assert-Condition ($script:ClusterData.StartsWith($script:ClusterRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'TEMP_CLUSTER_DATA_OUTSIDE_ROOT'
    $marker = [ordered]@{ RunId = $script:RunId; Root = $script:ClusterRoot; DataPath = $script:ClusterData; CreatedUtc = [DateTime]::UtcNow.ToString('o') }
    [IO.File]::WriteAllText((Join-Path $script:ClusterRoot 'ownership.json'), ($marker | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
}

function Assert-ClusterOwnership {
    Assert-Condition ($script:ClusterOwned -and $script:ClusterRoot -and $script:ClusterData) 'TEMP_CLUSTER_NOT_OWNED'
    $base = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Temp\EdgeRetails\Phase3Release106BackupUpgradeRehearsal'))
    $root = [IO.Path]::GetFullPath($script:ClusterRoot)
    $data = [IO.Path]::GetFullPath($script:ClusterData)
    Assert-Condition ($root.StartsWith($base.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'TEMP_CLEANUP_ROOT_OUTSIDE_BASE'
    Assert-Condition ($data.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'TEMP_CLEANUP_DATA_OUTSIDE_ROOT'
    Assert-Condition ([IO.File]::Exists((Join-Path $root 'ownership.json'))) 'TEMP_CLEANUP_OWNERSHIP_MARKER_MISSING'
    $marker = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $root 'ownership.json')))
    Assert-Condition ($marker.RunId -eq $script:RunId -and [IO.Path]::GetFullPath($marker.Root) -eq $root -and [IO.Path]::GetFullPath($marker.DataPath) -eq $data) 'TEMP_CLEANUP_MARKER_MISMATCH'
    $item = Get-Item -LiteralPath $root -Force
    Assert-Condition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'TEMP_CLEANUP_ROOT_REPARSE_POINT'
    $reparse = @(Get-ChildItem -LiteralPath $root -Force -Recurse | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    Assert-Condition ($reparse.Count -eq 0) 'TEMP_CLEANUP_CONTAINS_REPARSE_POINT'
}

function Stop-And-RemoveOwnedCluster {
    if (-not $script:ClusterOwned) { $script:CleanupSucceeded = $true; return }
    Assert-ClusterOwnership
    $pgCtl = Join-Path $script:PgBin 'pg_ctl.exe'
    if ([IO.Directory]::Exists($script:ClusterData)) {
        $status = Invoke-External $pgCtl @('-D', $script:ClusterData, 'status') $script:RepoRoot @{} @(0, 3)
        if ($status.ExitCode -eq 0) {
            $stop = Invoke-External $pgCtl @('-D', $script:ClusterData, '-m', 'fast', '-w', '-t', '60', 'stop') $script:RepoRoot @{}
            Assert-ExternalSuccess $stop 'OWNED_CLUSTER_STOP_FAILED'
        }
        $after = Invoke-External $pgCtl @('-D', $script:ClusterData, 'status') $script:RepoRoot @{} @(0, 3)
        Assert-Condition ($after.ExitCode -eq 3) 'OWNED_CLUSTER_STILL_RUNNING'
    }
    Assert-ClusterOwnership
    Remove-Item -LiteralPath $script:ClusterRoot -Recurse -Force
    Assert-Condition (-not [IO.Directory]::Exists($script:ClusterRoot) -and -not [IO.File]::Exists($script:ClusterRoot)) 'OWNED_CLUSTER_CLEANUP_NOT_CONFIRMED'
    $script:CleanupSucceeded = $true
}

function Assert-HistoryEquals {
    param([Parameter(Mandatory)][string[]]$Actual, [Parameter(Mandatory)][string[]]$Expected, [Parameter(Mandatory)][string]$FailureCode)
    Assert-Condition ($Actual.Count -eq $Expected.Count) ($FailureCode + '_COUNT')
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-Condition ($Actual[$index] -ceq $Expected[$index]) ($FailureCode + '_ID_' + $index)
    }
}

function Invoke-ProductionReadOnlyCensus {
    $infra = Join-Path $script:RepoRoot 'src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj'
    $efArgs = @('ef', 'migrations', 'list', '--project', $infra, '--startup-project', $infra, '--context', 'EdgeRetailsDbContext', '--configuration', 'Release', '--verbose')
    $efEnvironment = @{ EDGE_RETAILS_DB = $script:Production.ConnectionString }
    $ef = Invoke-External 'dotnet.exe' $efArgs $script:RepoRoot $efEnvironment @('PG', 'EDGE_RETAILS_DB', 'ConnectionStrings__')
    Assert-ExternalSuccess $ef 'PRODUCTION_NPGSQL_MIGRATION_CENSUS_FAILED'
    Assert-Condition ($ef.Stdout -match '(?i)Npgsql\.EntityFrameworkCore\.PostgreSQL' -or $ef.Stderr -match '(?i)Npgsql\.EntityFrameworkCore\.PostgreSQL') 'PRODUCTION_NPGSQL_PROVIDER_NOT_CONFIRMED'
    foreach ($id in $script:SourceMigrationIds) {
        Assert-Condition (($ef.Stdout + "`n" + $ef.Stderr).Contains($id)) 'PRODUCTION_EF_MIGRATION_CENSUS_INCOMPLETE'
    }
    return [pscustomobject]@{ Command = 'dotnet ef migrations list --project EdgeRetails.Infrastructure --startup-project EdgeRetails.Infrastructure --context EdgeRetailsDbContext --configuration Release --verbose'; ExitCode = $ef.ExitCode }
}

try {
    Get-BackupPaths
    Save-Evidence
    $script:Production = Get-ProtectedRuntimeTarget
    $script:Evidence.ProductionTarget = [ordered]@{
        Authority = $script:ConfigPath
        ConfigKeys = @($script:Production.ConfigKeys)
        Host = $script:Production.Host
        Port = $script:Production.Port
        Database = $script:Production.Database
        Role = $script:Production.Username
        ServerVersion = $null
    }
    $script:SourceMigrationIds = Get-SourceMigrationIds
    Assert-Condition ($script:SourceMigrationIds.Count -eq 20) 'SOURCE_MIGRATION_INVENTORY_INVALID'
    $script:Evidence.MigrationIds.ProductionBaseline = @($script:SourceMigrationIds[0..17])
    $script:Evidence.MigrationIds.RehearsedForward = @($script:SourceMigrationIds[18..19])
    Save-Evidence

    Set-Stage 'ProtectedProgramDataAuthority' 'PASS' $null 0 @{ ConfigKeys = @($script:Production.ConfigKeys); CredentialsRecorded = $false }
    Set-Stage 'CurrentSourceMigrationInventory' 'PASS' 'Enumerate infrastructure migration source files' 0 @{ Count = $script:SourceMigrationIds.Count; ForwardIds = @($script:SourceMigrationIds[18..19]) }

    $script:PgBin = Get-PostgresTools
    Set-Stage 'PostgreSql18ClientTools' 'PASS' 'Verify pg_dump, pg_restore, psql, initdb, pg_ctl, createdb --version' 0 @{ Bin = $script:PgBin; MajorVersion = 18 }

    $efCensus = Invoke-ProductionReadOnlyCensus
    Set-Stage 'ProductionNpgsqlReadOnlyMigrationCensus' 'PASS' $efCensus.Command $efCensus.ExitCode @{ Provider = 'Npgsql'; SchemaMutation = $false }

    $identitySql = "SELECT current_database() || E'\t' || current_user || E'\t' || coalesce(inet_server_addr()::text,'') || E'\t' || inet_server_port()::text || E'\t' || current_setting('server_version_num');"
    $identityText = Invoke-ProductionPsql $identitySql
    $identity = $identityText.Trim() -split "`t"
    Assert-Condition ($identity.Count -eq 5) 'PRODUCTION_IDENTITY_SHAPE_INVALID'
    $script:Evidence.ProductionTarget.ConnectedDatabase = $identity[0]
    $script:Evidence.ProductionTarget.ConnectedRole = $identity[1]
    $script:Evidence.ProductionTarget.ServerAddress = $identity[2]
    $script:Evidence.ProductionTarget.ConnectedPort = $identity[3]
    $script:Evidence.ProductionTarget.ServerVersionNum = $identity[4]
    Assert-Condition ($identity[0] -ceq $script:Production.Database -and $identity[1] -ceq $script:Production.Username) 'PRODUCTION_IDENTITY_MISMATCH'
    Assert-Condition ([int]$identity[3] -eq $script:Production.Port) 'PRODUCTION_PORT_MISMATCH'
    $parsedServerAddress = ConvertFrom-Phase3PostgresServerAddress $identity[2]
    Assert-Condition ($parsedServerAddress.HasHostPrefix -and $parsedServerAddress.IsLoopback) 'PRODUCTION_SERVER_NOT_LOOPBACK'
    $script:Evidence.ProductionTarget.NormalizedServerAddress = $parsedServerAddress.NormalizedAddress
    $script:Evidence.ProductionTarget.ServerAddressPrefixLength = $parsedServerAddress.PrefixLength
    $serverVersionNum = [int]$identity[4]
    Assert-Condition ($serverVersionNum -ge 180000 -and $serverVersionNum -lt 190000) 'PRODUCTION_SERVER_MAJOR_NOT_18'
    $script:Evidence.ProductionTarget.ServerVersion = [string][int]([math]::Floor($serverVersionNum / 10000)) + '.' + [string][int]($serverVersionNum % 10000)
    Set-Stage 'ProductionTargetPostgreSql18Identity' 'PASS' 'psql read-only identity/version query from protected ProgramData authority; loopback host prefix validated' 0 @{ Database = $identity[0]; Role = $identity[1]; Address = $identity[2]; NormalizedAddress = $parsedServerAddress.NormalizedAddress; PrefixLength = $parsedServerAddress.PrefixLength; Port = [int]$identity[3]; ServerVersionNum = $serverVersionNum }

    $productionHistory = Get-HistoryIds 'Production'
    $expectedBaseline = @($script:SourceMigrationIds[0..17])
    $productionAtFinal20 = $false
    if ($productionHistory.Count -eq $expectedBaseline.Count) {
        Assert-HistoryEquals $productionHistory $expectedBaseline 'PRODUCTION_HISTORY_NOT_APPROVED_18_BASELINE'
    }
    elseif ($productionHistory.Count -eq $script:SourceMigrationIds.Count) {
        Assert-HistoryEquals $productionHistory $script:SourceMigrationIds 'PRODUCTION_HISTORY_NOT_FINAL_SOURCE_20'
        $productionAtFinal20 = $true
    }
    else {
        throw 'PRODUCTION_HISTORY_NOT_APPROVED_18_BASELINE_OR_FINAL_SOURCE_20'
    }
    $script:Evidence.MigrationIds.ProductionBaseline = @($productionHistory)
    if ($productionAtFinal20) { $script:Evidence.MigrationIds.RehearsedForward = @() }
    $prodCategories = Get-CategorySnapshot 'Production'
    Assert-Condition ($prodCategories.Count -eq 2) 'PRODUCTION_CATEGORY_COUNT_NOT_2'
    $prodStructure = Get-StructuralSnapshot 'Production'
    Assert-RequiredStructure $prodStructure
    $prodIndexes = Get-IndexSnapshot 'Production'
    Assert-RecoveryIndexes $prodIndexes $productionAtFinal20
    Set-Stage 'ProductionReadOnlyBaselineAndInvariants' 'PASS' 'psql read-only history, category hash, required schema/index and hold-table checks' 0 @{
        HistoryCount = $productionHistory.Count
        CategoryCount = $prodCategories.Count
        CategoryRowsSha256 = $prodCategories.RowsSha256
        RequiredSchemas = $prodStructure.RequiredSchemas
        RequiredIndexedTables = $prodStructure.IndexedRequiredTables
        HoldTables = $prodStructure.HoldTables
        RecoveryIndexesCurrentCount = $prodIndexes.Count
        ProductionAlreadyAtFinalMigration20 = $productionAtFinal20
    }

    $serverService = Get-Service -Name 'EdgeRetailsServer' -ErrorAction Stop
    $workerService = Get-Service -Name 'EdgeRetailsWorker' -ErrorAction Stop
    Assert-Condition ($serverService.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) 'SERVER_NOT_STOPPED_FOR_CUTOVER_BACKUP'
    Assert-Condition ($workerService.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) 'WORKER_NOT_STOPPED_FOR_CUTOVER_BACKUP'
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue)
    Assert-Condition ($listeners.Count -eq 0) 'SHOP_SERVER_LISTENER_PRESENT_DURING_CUTOVER_BACKUP'
    Set-Stage 'ProductionServicesQuiescedForCutoverBackup' 'PASS' 'Server and Worker stopped; no listener on port 7150' 0 @{ Server = 'Stopped'; Worker = 'Stopped'; Listener7150 = 0 }

    Assert-Condition (-not [IO.File]::Exists($script:BackupPath) -and -not [IO.Directory]::Exists($script:BackupPath)) 'BACKUP_DESTINATION_ALREADY_EXISTS'
    $dumpArgs = @('--format=custom', '--no-password', '--verbose', '--host', $script:Production.Host, '--port', [string]$script:Production.Port, '--username', $script:Production.Username, '--file', $script:BackupPath, '--dbname', $script:Production.Database)
    $dump = Invoke-External (Join-Path $script:PgBin 'pg_dump.exe') $dumpArgs $script:RepoRoot (Get-PsqlConnectionEnvironment $script:Production)
    Assert-ExternalSuccess $dump 'PRODUCTION_BACKUP_FAILED'
    Assert-Condition ([IO.File]::Exists($script:BackupPath)) 'PRODUCTION_BACKUP_FILE_MISSING'
    $backupInfo = Get-Item -LiteralPath $script:BackupPath -Force
    Assert-Condition ($backupInfo.Length -gt 0 -and $backupInfo.LastWriteTimeUtc -ge $script:StartedUtc) 'PRODUCTION_BACKUP_FILE_INVALID_OR_STALE'
    $script:BackupSha256 = (Get-FileHash -LiteralPath $script:BackupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $script:Evidence.Backup = [ordered]@{ Path = $script:BackupPath; SizeBytes = $backupInfo.Length; Sha256 = $script:BackupSha256; CreatedUtc = $backupInfo.LastWriteTimeUtc.ToString('o'); ArchiveTocPass = $false; FullArchiveReadPass = $false; RestoredExactArchivePass = $false }
    Set-Stage 'FreshProductionCustomBackup' 'PASS' 'pg_dump --format=custom --no-password --verbose' $dump.ExitCode @{ Path = $script:BackupPath; SizeBytes = $backupInfo.Length; Sha256 = $script:BackupSha256 }

    $list = Invoke-External (Join-Path $script:PgBin 'pg_restore.exe') @('--list', $script:BackupPath) $script:RepoRoot @{}
    Assert-ExternalSuccess $list 'BACKUP_TOC_READ_FAILED'
    Assert-Condition ($list.Stdout -match '(?im)^.*TABLE DATA.*system.*__ef_migrations_history.*$') 'BACKUP_TOC_MIGRATION_HISTORY_DATA_MISSING'
    Assert-Condition ($list.Stdout -match '(?im)^.*TABLE DATA.*catalog.*categories.*$') 'BACKUP_TOC_CATEGORIES_DATA_MISSING'
    $script:Evidence.Backup.ArchiveTocPass = $true
    Set-Stage 'BackupTocInspection' 'PASS' 'pg_restore --list <fresh archive>' $list.ExitCode @{ MigrationHistoryTableData = $true; CategoriesTableData = $true }

    $fullRead = Invoke-External (Join-Path $script:PgBin 'pg_restore.exe') @('--file', 'NUL', $script:BackupPath) $script:RepoRoot @{}
    Assert-ExternalSuccess $fullRead 'BACKUP_FULL_ARCHIVE_READ_FAILED'
    $script:Evidence.Backup.FullArchiveReadPass = $true
    Set-Stage 'BackupFullArchiveRead' 'PASS' 'pg_restore --file NUL <fresh archive>' $fullRead.ExitCode @{ ArchiveFullyRead = $true }

    $afterDumpSha = (Get-FileHash -LiteralPath $script:BackupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Condition ($afterDumpSha -ceq $script:BackupSha256) 'BACKUP_HASH_CHANGED_BEFORE_RESTORE'
    New-OwnedClusterRoot
    $script:ClusterPort = Get-ServerPort
    $script:ClusterDatabase = 'er_phase3_106_' + $script:RunId.Replace('-', '').Substring(0, 12)
    $initdbArgs = @('--pgdata', $script:ClusterData, '--username', 'postgres', '--auth-local=trust', '--auth-host=trust', '--encoding=UTF8', '--no-locale')
    $init = Invoke-External (Join-Path $script:PgBin 'initdb.exe') $initdbArgs $script:RepoRoot @{}
    Assert-ExternalSuccess $init 'DISPOSABLE_CLUSTER_INITDB_FAILED'
    $logPath = Join-Path $script:ClusterRoot 'postgres.log'
    $serverOptions = '-h 127.0.0.1 -p ' + [string]$script:ClusterPort + ' -c listen_addresses=127.0.0.1'
    $start = Invoke-External (Join-Path $script:PgBin 'pg_ctl.exe') @('-D', $script:ClusterData, '-l', $logPath, '-o', $serverOptions, '-w', '-t', '60', 'start') $script:RepoRoot @{} @('PG', 'EDGE_RETAILS_DB') @(0) -NoOutputCapture
    Assert-ExternalSuccess $start 'DISPOSABLE_CLUSTER_START_FAILED'
    $script:ClusterStarted = $true
    $clusterProbe = Invoke-ClusterAdminPsql "SELECT current_setting('server_version_num') || E'\t' || current_setting('data_directory') || E'\t' || current_user;"
    $clusterIdentity = $clusterProbe.Trim() -split "`t"
    Assert-Condition ($clusterIdentity.Count -eq 3) 'DISPOSABLE_CLUSTER_IDENTITY_SHAPE_INVALID'
    $cloneServerVersionNum = [int]$clusterIdentity[0]
    Assert-Condition ($cloneServerVersionNum -ge 180000 -and $cloneServerVersionNum -lt 190000) 'DISPOSABLE_CLUSTER_SERVER_MAJOR_NOT_18'
    Assert-Condition ([IO.Path]::GetFullPath($clusterIdentity[1]) -eq [IO.Path]::GetFullPath($script:ClusterData)) 'DISPOSABLE_CLUSTER_DATA_DIRECTORY_MISMATCH'
    Assert-Condition ($clusterIdentity[2] -ceq 'postgres') 'DISPOSABLE_CLUSTER_ADMIN_ROLE_MISMATCH'
    $createDb = Invoke-External (Join-Path $script:PgBin 'createdb.exe') @('--no-password', '--host', '127.0.0.1', '--port', [string]$script:ClusterPort, '--username', 'postgres', $script:ClusterDatabase) $script:RepoRoot (Get-PgEnvironment ([pscustomobject]@{ SslMode = 'disable'; Password = '' }) '')
    Assert-ExternalSuccess $createDb 'DISPOSABLE_DATABASE_CREATE_FAILED'
    $restore = Invoke-External (Join-Path $script:PgBin 'pg_restore.exe') @('--exit-on-error', '--single-transaction', '--no-owner', '--no-privileges', '--host', '127.0.0.1', '--port', [string]$script:ClusterPort, '--username', 'postgres', '--dbname', $script:ClusterDatabase, $script:BackupPath) $script:RepoRoot (Get-PgEnvironment ([pscustomobject]@{ SslMode = 'disable'; Password = '' }) '')
    Assert-ExternalSuccess $restore 'ACTUAL_BACKUP_RESTORE_TO_CLONE_FAILED'
    $restoredSha = (Get-FileHash -LiteralPath $script:BackupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Condition ($restoredSha -ceq $script:BackupSha256) 'BACKUP_HASH_CHANGED_DURING_RESTORE'
    $cloneRestoredHistory = Get-HistoryIds 'Clone'
    $expectedRestoredHistory = if ($productionAtFinal20) { $script:SourceMigrationIds } else { $expectedBaseline }
    $restoreHistoryFailure = if ($productionAtFinal20) { 'RESTORED_FINAL_SOURCE_20_HISTORY_MISMATCH' } else { 'RESTORED_BASELINE_HISTORY_MISMATCH' }
    Assert-HistoryEquals $cloneRestoredHistory $expectedRestoredHistory $restoreHistoryFailure
    $cloneRestoredCategories = Get-CategorySnapshot 'Clone'
    Assert-Condition ($cloneRestoredCategories.Count -eq $prodCategories.Count -and $cloneRestoredCategories.RowsSha256 -ceq $prodCategories.RowsSha256) 'RESTORED_CATEGORY_DATA_MISMATCH'
    $cloneRestoredStructure = Get-StructuralSnapshot 'Clone'
    Assert-RequiredStructure $cloneRestoredStructure
    Assert-Condition ($cloneRestoredStructure.HoldTables -eq $prodStructure.HoldTables) 'RESTORED_HOLD_TABLE_STATE_MISMATCH'
    $cloneRestoredIndexes = Get-IndexSnapshot 'Clone'
    Assert-RecoveryIndexes $cloneRestoredIndexes $productionAtFinal20
    $script:Evidence.Backup.RestoredExactArchivePass = $true
    $script:Evidence.Rehearsal = [ordered]@{
        ClusterRoot = $script:ClusterRoot
        DataPath = $script:ClusterData
        Port = $script:ClusterPort
        Database = $script:ClusterDatabase
        Provider = 'PostgreSQL 18 / Npgsql'
        ServerVersionNum = $cloneServerVersionNum
        RestoredHistoryCount = $cloneRestoredHistory.Count
        RestoredCategoryCount = $cloneRestoredCategories.Count
        RestoredCategoryRowsSha256 = $cloneRestoredCategories.RowsSha256
        ProductionAlreadyAtFinalMigration20 = $productionAtFinal20
        FinalHistoryCount = $null
        FinalCategoryRowsSha256 = $null
        CleanupStatus = 'PENDING'
    }
    Set-Stage 'ActualProductionArchiveRestoredToOwnedPostgreSql18Clone' 'PASS' 'initdb; pg_ctl start; createdb; pg_restore --exit-on-error --single-transaction --no-owner --no-privileges' $restore.ExitCode @{
        Provider = 'PostgreSQL 18 / Npgsql'; ServerVersionNum = $cloneServerVersionNum; ClusterDataPathVerified = $true
        ExactBackupSha256 = $restoredSha; HistoryCount = $cloneRestoredHistory.Count
        CategoryCount = $cloneRestoredCategories.Count; CategoryRowsSha256 = $cloneRestoredCategories.RowsSha256
        HoldTables = $cloneRestoredStructure.HoldTables; RecoveryIndexCount = $cloneRestoredIndexes.Count
        MatchesFinalSourceHistory = $productionAtFinal20
    }

    $infra = Join-Path $script:RepoRoot 'src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj'
    $cloneConnection = 'Host=127.0.0.1;Port=' + [string]$script:ClusterPort + ';Database=' + $script:ClusterDatabase + ';Username=postgres;Password=;SSL Mode=Disable;Timeout=15;Command Timeout=60'
    $cloneEnvironment = @{ EDGE_RETAILS_DB = $cloneConnection; PGHOST = '127.0.0.1'; PGPORT = [string]$script:ClusterPort; PGUSER = 'postgres'; PGDATABASE = $script:ClusterDatabase; PGPASSWORD = ''; PGSSLMODE = 'disable'; PGCONNECT_TIMEOUT = '15'; PGAPPNAME = 'EdgeRetails-Phase3-Release106-Clone' }
    $migration19 = $script:SourceMigrationIds[18]
    $migration20 = $script:SourceMigrationIds[19]
    if ($productionAtFinal20) {
        $cloneHistory20 = $cloneRestoredHistory
        Assert-HistoryEquals $cloneHistory20 $script:SourceMigrationIds 'RESTORED_FINAL_SOURCE_20_HISTORY_MISMATCH'
        $cloneCategories20 = $cloneRestoredCategories
        Assert-Condition ($cloneCategories20.Count -eq $prodCategories.Count -and $cloneCategories20.RowsSha256 -ceq $prodCategories.RowsSha256) 'RESTORED_FINAL_CATEGORY_DATA_MISMATCH'
        $cloneStructure20 = $cloneRestoredStructure
        Assert-RequiredStructure $cloneStructure20
        Assert-Condition ($cloneStructure20.HoldTables -eq 0) 'RESTORED_FINAL_HOLD_TABLE_PRESENT'
        $indexes20 = $cloneRestoredIndexes
        Assert-RecoveryIndexes $indexes20 $true
        Set-Stage 'CurrentProductionArchiveRestoredAndVerifiedAtFinalMigration20' 'PASS' 'Restore the exact fresh production archive to owned PostgreSQL 18 clone; compare ordered history, category hash, required schema, recovery indexes, and hold-table absence' $restore.ExitCode @{
            FinalHistoryCount = $cloneHistory20.Count; ExpectedHistoryCount = $script:SourceMigrationIds.Count
            CategoryCount = $cloneCategories20.Count; CategoryRowsSha256 = $cloneCategories20.RowsSha256
            HoldTables = $cloneStructure20.HoldTables; RecoveryIndexes = $indexes20.Count
            Provider = 'PostgreSQL 18 / Npgsql'
        }
    }
    else {
        $ef19Args = @('ef', 'database', 'update', $migration19, '--project', $infra, '--startup-project', $infra, '--context', 'EdgeRetailsDbContext', '--configuration', 'Release')
        $apply19 = Invoke-External 'dotnet.exe' $ef19Args $script:RepoRoot $cloneEnvironment @('PG', 'EDGE_RETAILS_DB', 'ConnectionStrings__')
        Assert-ExternalSuccess $apply19 'CLONE_MIGRATION_19_FAILED'
        $cloneHistory19 = Get-HistoryIds 'Clone'
        Assert-HistoryEquals $cloneHistory19 @($expectedBaseline + $migration19) 'CLONE_HISTORY_AFTER_MIGRATION_19_MISMATCH'
        $indexes19 = Get-IndexSnapshot 'Clone'
        Assert-Condition ($indexes19.Count -eq 1) 'CLONE_MIGRATION_19_INDEX_COUNT_MISMATCH'
        Assert-Condition ($indexes19[0].Definition -match '(?i)ux_business_events_pin_recovery_success_operation') 'CLONE_MIGRATION_19_INDEX_MISSING'
        Assert-Condition ($indexes19[0].Unique -and $indexes19[0].Valid -and $indexes19[0].Ready -and $indexes19[0].Predicate -match 'USER_PIN_RECOVERY_SUCCEEDED') 'CLONE_MIGRATION_19_INDEX_INVALID'
        Set-Stage 'CloneForwardMigration19' 'PASS' ('dotnet ef database update ' + $migration19 + ' --project EdgeRetails.Infrastructure --startup-project EdgeRetails.Infrastructure --context EdgeRetailsDbContext --configuration Release') $apply19.ExitCode @{ AppliedMigration = $migration19; HistoryCount = $cloneHistory19.Count; IndexCount = $indexes19.Count; Provider = 'Npgsql' }

        $ef20Args = @('ef', 'database', 'update', $migration20, '--project', $infra, '--startup-project', $infra, '--context', 'EdgeRetailsDbContext', '--configuration', 'Release')
        $apply20 = Invoke-External 'dotnet.exe' $ef20Args $script:RepoRoot $cloneEnvironment @('PG', 'EDGE_RETAILS_DB', 'ConnectionStrings__')
        Assert-ExternalSuccess $apply20 'CLONE_MIGRATION_20_FAILED'
        $cloneHistory20 = Get-HistoryIds 'Clone'
        Assert-HistoryEquals $cloneHistory20 $script:SourceMigrationIds 'CLONE_FINAL_HISTORY_MISMATCH'
        $cloneCategories20 = Get-CategorySnapshot 'Clone'
        Assert-Condition ($cloneCategories20.Count -eq $prodCategories.Count -and $cloneCategories20.RowsSha256 -ceq $prodCategories.RowsSha256) 'CLONE_CATEGORY_DATA_CHANGED_BY_MIGRATION'
        $cloneStructure20 = Get-StructuralSnapshot 'Clone'
        Assert-RequiredStructure $cloneStructure20
        Assert-Condition ($cloneStructure20.HoldTables -eq 0) 'CLONE_FINAL_HOLD_TABLE_PRESENT'
        $indexes20 = Get-IndexSnapshot 'Clone'
        Assert-RecoveryIndexes $indexes20 $true
        $script:Evidence.MigrationIds.RehearsedForward = @($migration19, $migration20)
        Set-Stage 'CloneForwardMigration20AndFinalInvariants' 'PASS' ('dotnet ef database update ' + $migration20 + ' --project EdgeRetails.Infrastructure --startup-project EdgeRetails.Infrastructure --context EdgeRetailsDbContext --configuration Release') $apply20.ExitCode @{
            FinalHistoryCount = $cloneHistory20.Count; ExpectedHistoryCount = 20; CategoryCount = $cloneCategories20.Count
            CategoryRowsSha256 = $cloneCategories20.RowsSha256; HoldTables = $cloneStructure20.HoldTables
            ValidUniqueRecoveryIndexes = @($indexes20 | ForEach-Object { $_.Definition.Split(' ')[3] }); Provider = 'Npgsql'
        }
    }
    $script:Evidence.MigrationIds.Final = @($cloneHistory20)
    if ($productionAtFinal20) { $script:Evidence.MigrationIds.RehearsedForward = @() }
    $script:Evidence.Rehearsal.FinalHistoryCount = $cloneHistory20.Count
    $script:Evidence.Rehearsal.FinalCategoryRowsSha256 = $cloneCategories20.RowsSha256

    $script:Evidence.CompletionStatus = 'PASS'
}
catch {
    $candidateFailureCode = [string]$_.Exception.Message
    if ($candidateFailureCode -match '^[A-Z][A-Z0-9_]*(?:_\d+)?$') { $script:FailureStep = $candidateFailureCode }
    else { $script:FailureStep = 'UNCLASSIFIED_FAILURE' }
    $script:Evidence.CompletionStatus = 'FAIL'
    $script:Evidence.FailureStep = $script:FailureStep
}
finally {
    try {
        if ($script:ClusterOwned) {
            Stop-And-RemoveOwnedCluster
            if ($null -ne $script:Evidence.Rehearsal) { $script:Evidence.Rehearsal.CleanupStatus = 'PASS' }
            Set-Stage 'OwnedDisposableClusterCleanup' 'PASS' 'pg_ctl status/stop; verify run marker and path containment; remove owned temp root' 0 @{ CleanupConfirmed = $true }
        }
        else {
            $script:CleanupSucceeded = $true
            Set-Stage 'OwnedDisposableClusterCleanup' 'NOT_RUN' $null $null @{ Reason = 'No disposable cluster was created.' }
        }
    }
    catch {
        $script:CleanupSucceeded = $false
        if ($null -ne $script:Evidence.Rehearsal) { $script:Evidence.Rehearsal.CleanupStatus = 'FAIL' }
        $script:Evidence.CompletionStatus = 'FAIL'
        $script:Evidence.FailureStep = 'OWNED_CLUSTER_CLEANUP_FAILED'
        $script:FailureStep = 'OWNED_CLUSTER_CLEANUP_FAILED'
        try { Set-Stage 'OwnedDisposableClusterCleanup' 'FAIL' 'pg_ctl status/stop; verified cleanup guards' $null @{ CleanupConfirmed = $false } } catch { }
    }
    $script:Evidence.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    if ($script:Evidence.CompletionStatus -eq 'PASS' -and $script:CleanupSucceeded -ne $true) {
        $script:Evidence.CompletionStatus = 'FAIL'
        $script:Evidence.FailureStep = 'OWNED_CLUSTER_CLEANUP_NOT_CONFIRMED'
    }
    try { Save-Evidence }
    catch {
        $script:Evidence.CompletionStatus = 'FAIL'
        if (-not $script:Evidence.FailureStep) { $script:Evidence.FailureStep = 'EVIDENCE_WRITE_FAILED' }
        $script:Evidence.EvidenceWriteFailureType = $_.Exception.GetType().FullName
    }
}

if ($script:Evidence.CompletionStatus -eq 'PASS') {
    Write-Output ('PHASE3_RELEASE106_BACKUP_UPGRADE_REHEARSAL_PASS Evidence=' + $script:EvidencePath + ' Backup=' + $script:BackupPath + ' SHA256=' + $script:BackupSha256)
    exit 0
}

Write-Error ('PHASE3_RELEASE106_BACKUP_UPGRADE_REHEARSAL_FAIL Step=' + [string]$script:Evidence.FailureStep + ' EvidenceWriteFailureType=' + [string]$script:Evidence.EvidenceWriteFailureType + ' Evidence=' + [string]$script:EvidencePath)
exit 1
