param(
    [Parameter(Mandatory)][string]$SourceConnectionString,
    [Parameter(Mandatory)][string]$RestoreConnectionString,
    [Parameter(Mandatory)][string]$PgDumpPath,
    [Parameter(Mandatory)][string]$PgRestorePath,
    [Parameter(Mandatory)][string]$PsqlPath,
    [Parameter(Mandatory)][string]$ExpectedSourceDatabase,
    [Parameter(Mandatory)][string]$ExpectedRestoreDatabase,
    [Parameter(Mandatory)][string]$SourceObjectRoot,
    [Parameter(Mandatory)][string]$RestoreObjectRoot,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
foreach ($exe in @($PgDumpPath, $PgRestorePath, $PsqlPath)) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Required PostgreSQL tool is missing: $exe" }
}
if ($ExpectedSourceDatabase -eq $ExpectedRestoreDatabase) { throw 'Recovery target must be a separately named disposable database.' }
if ($ExpectedSourceDatabase -in @('postgres','template1','edgeretails','edgeretails_pos','shop_pos') -or
    $ExpectedRestoreDatabase -in @('postgres','template1','edgeretails','edgeretails_pos','shop_pos')) {
    throw 'Operational/default database names are prohibited.'
}
if (Test-Path -LiteralPath $EvidenceDirectory) { throw "Evidence directory already exists: $EvidenceDirectory" }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null

function Invoke-PsqlJson([string]$Connection, [string]$Sql) {
    $parsed = ConvertFrom-CentralConnectionString $Connection
    $hostValue = $parsed.Host
    $portValue = if ($parsed.Port) { $parsed.Port } else { '5432' }
    $databaseValue = $parsed.Database
    $usernameValue = $parsed.Username
    $passwordValue = $parsed.Password
    if ([string]::IsNullOrWhiteSpace($hostValue) -or [string]::IsNullOrWhiteSpace($databaseValue) -or [string]::IsNullOrWhiteSpace($usernameValue)) {
        throw 'Connection string must explicitly include Host, Database, and Username.'
    }
    $env:PGPASSWORD = $passwordValue
    $out = & $PsqlPath --host $hostValue --port $portValue --username $usernameValue --dbname $databaseValue --no-psqlrc --set ON_ERROR_STOP=1 --tuples-only --no-align --command $Sql 2>&1
    $exitCode = $LASTEXITCODE
    $env:PGPASSWORD = $null
    if ($exitCode -ne 0) { throw "psql failed: $($out -join "`n")" }
    return ($out -join '').Trim()
}

function ConvertFrom-CentralConnectionString([string]$Connection) {
    $values = @{}
    foreach ($segment in $Connection -split ';') {
        if ([string]::IsNullOrWhiteSpace($segment)) { continue }
        $parts = $segment -split '=', 2
        if ($parts.Count -ne 2) { throw 'Malformed Central PostgreSQL connection string.' }
        $values[$parts[0].Trim().ToLowerInvariant()] = $parts[1].Trim()
    }
    [pscustomobject]@{
        Host = if ($values.ContainsKey('host')) { $values['host'] } elseif ($values.ContainsKey('server')) { $values['server'] } else { '' }
        Port = if ($values.ContainsKey('port')) { $values['port'] } else { '' }
        Database = if ($values.ContainsKey('database')) { $values['database'] } else { '' }
        Username = if ($values.ContainsKey('username')) { $values['username'] } elseif ($values.ContainsKey('user id')) { $values['user id'] } else { '' }
        Password = if ($values.ContainsKey('password')) { $values['password'] } else { '' }
    }
}

$identitySql = "SELECT json_build_object('version',current_setting('server_version_num'),'database',current_database(),'address',host(inet_server_addr()),'port',inet_server_port(),'schema',current_schema())::text"
$source = Invoke-PsqlJson $SourceConnectionString $identitySql | ConvertFrom-Json
$target = Invoke-PsqlJson $RestoreConnectionString $identitySql | ConvertFrom-Json
foreach ($identity in @($source,$target)) {
    if ([int]$identity.version -lt 180000 -or $identity.address -notin @('127.0.0.1','::1')) { throw 'Both recovery databases must be PostgreSQL 18 on loopback.' }
}
if ($source.database -ne $ExpectedSourceDatabase -or $target.database -ne $ExpectedRestoreDatabase) { throw 'Connected database identity differs from explicit expected names.' }
$targetUserObjectsSql = "SELECT json_build_object('schemas',(SELECT count(*) FROM information_schema.schemata WHERE schema_name NOT IN ('pg_catalog','information_schema','pg_toast')), 'tables',(SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema','pg_toast')))::text"
$targetUserObjects = Invoke-PsqlJson $RestoreConnectionString $targetUserObjectsSql | ConvertFrom-Json
if ([int]$targetUserObjects.schemas -ne 1 -or [int]$targetUserObjects.tables -ne 0) {
    throw 'Restore target must be a fresh PostgreSQL template0 database containing only public schema and no user tables.'
}

$archive = Join-Path $EvidenceDirectory 'central.dump'
$sourceParsed = ConvertFrom-CentralConnectionString $SourceConnectionString
$env:PGPASSWORD = $sourceParsed.Password
$dumpLog = & $PgDumpPath --host $sourceParsed.Host --port $(if ($sourceParsed.Port) { $sourceParsed.Port } else { '5432' }) --username $sourceParsed.Username --dbname $sourceParsed.Database --format=custom --no-owner --no-acl --file $archive 2>&1
$dumpExitCode = $LASTEXITCODE
$env:PGPASSWORD = $null
if ($dumpExitCode -ne 0) { throw "pg_dump failed: $($dumpLog -join "`n")" }
$restoreParsed = ConvertFrom-CentralConnectionString $RestoreConnectionString
$env:PGPASSWORD = $restoreParsed.Password
$restoreLog = & $PgRestorePath --host $restoreParsed.Host --port $(if ($restoreParsed.Port) { $restoreParsed.Port } else { '5432' }) --username $restoreParsed.Username --dbname $restoreParsed.Database --exit-on-error --no-owner --no-acl $archive 2>&1
$restoreExitCode = $LASTEXITCODE
$env:PGPASSWORD = $null
if ($restoreExitCode -ne 0) { throw "pg_restore failed: $($restoreLog -join "`n")" }

$invariantsSql = @"
SELECT json_build_object(
 'database',current_database(),
 'migrationCount',(SELECT count(*) FROM central.__ef_migrations_history),
 'outboxCount',(SELECT count(*) FROM central.outbox_messages),
 'unprocessedOutboxCount',(SELECT count(*) FROM central.outbox_messages WHERE "ProcessedAtUtc" IS NULL),
 'encryptedObjectCount',(SELECT count(*) FROM central.encrypted_objects),
 'encryptedObjectBytes',(SELECT COALESCE(sum("ContentLength"),0) FROM central.encrypted_objects),
 'consumerEffectCount',(SELECT count(*) FROM central.outbox_consumer_effects),
 'auditCount',(SELECT count(*) FROM central.audit_records),
 'adminCount',(SELECT count(*) FROM central.admin_users),
 'outboxFingerprint',(SELECT md5(COALESCE(string_agg(to_jsonb(x)::text, E'\n' ORDER BY x."Id"),'')) FROM central.outbox_messages x),
 'encryptedObjectFingerprint',(SELECT md5(COALESCE(string_agg(to_jsonb(x)::text, E'\n' ORDER BY x."Id"),'')) FROM central.encrypted_objects x),
 'consumerEffectFingerprint',(SELECT md5(COALESCE(string_agg(to_jsonb(x)::text, E'\n' ORDER BY x."MessageId"),'')) FROM central.outbox_consumer_effects x))::text
"@
$sourceFacts = Invoke-PsqlJson $SourceConnectionString $invariantsSql | ConvertFrom-Json
$restoreFacts = Invoke-PsqlJson $RestoreConnectionString $invariantsSql | ConvertFrom-Json
foreach ($key in @('migrationCount','outboxCount','unprocessedOutboxCount','encryptedObjectCount','encryptedObjectBytes','consumerEffectCount','auditCount','adminCount','outboxFingerprint','encryptedObjectFingerprint','consumerEffectFingerprint')) {
    if ($sourceFacts.$key.ToString() -cne $restoreFacts.$key.ToString()) { throw "Restored invariant differs: $key" }
}

$sourceRoot = [IO.Path]::GetFullPath($SourceObjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$restoreRoot = [IO.Path]::GetFullPath($RestoreObjectRoot)
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw 'Source object storage root must exist.' }
function Assert-NoReparseComponents([string]$RootPath,[string]$TargetPath) {
    $rootFull = [IO.Path]::GetFullPath($RootPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $targetFull = [IO.Path]::GetFullPath($TargetPath)
    if (-not $targetFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Object path escaped its owned root.' }
    $relative = $targetFull.Substring($rootFull.Length + 1)
    $cursor = $rootFull
    foreach ($part in $relative.Split([IO.Path]::DirectorySeparatorChar,[StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $part
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in object storage path: $cursor" }
        }
    }
}
Assert-NoReparseComponents $sourceRoot $sourceRoot
if (Test-Path -LiteralPath $restoreRoot) { throw 'Restore object storage root must be a fresh path.' }
New-Item -ItemType Directory -Path $restoreRoot -Force | Out-Null
$objectRowsSql = @"
SELECT COALESCE(json_agg(json_build_object('path',"StoragePath",'length',"ContentLength",'sha256',"CiphertextSha256") ORDER BY "Id"),'[]'::json)::text
FROM central.encrypted_objects WHERE NOT "IsTombstoned"
"@
$objectRows = Invoke-PsqlJson $SourceConnectionString $objectRowsSql | ConvertFrom-Json
$objectProof = @()
foreach ($row in $objectRows) {
    $relative = [string]$row.path
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Split([char[]]@('/','\')) -contains '..') { throw 'Object metadata path is not a safe relative path.' }
    $sourceFile = [IO.Path]::GetFullPath((Join-Path $sourceRoot $relative))
    if (-not $sourceFile.StartsWith($sourceRoot,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "Referenced ciphertext is missing or outside source storage: $relative" }
    Assert-NoReparseComponents $sourceRoot $sourceFile
    $destination = [IO.Path]::GetFullPath((Join-Path $restoreRoot $relative))
    if (-not $destination.StartsWith($restoreRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Restored object path escaped restore root.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Assert-NoReparseComponents $restoreRoot $destination
    Copy-Item -LiteralPath $sourceFile -Destination $destination
    $actual = Get-FileHash -LiteralPath $destination -Algorithm SHA256
    if ((Get-Item -LiteralPath $destination).Length -ne [long]$row.length -or $actual.Hash -ine [string]$row.sha256) { throw "Restored ciphertext integrity mismatch: $relative" }
    $objectProof += [ordered]@{path=$relative;length=[long]$row.length;sha256=$actual.Hash.ToLowerInvariant()}
}
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{status='RESTORE_VERIFIED';source=$source;restore=$target;sourceInvariants=$sourceFacts;restoreInvariants=$restoreFacts;ciphertextObjects=$objectProof;archiveSha256=$archiveHash;archiveBytes=(Get-Item -LiteralPath $archive).Length;createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'recovery-attestation.json') -Encoding utf8
Write-Output (Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'recovery-attestation.json') -Raw)


