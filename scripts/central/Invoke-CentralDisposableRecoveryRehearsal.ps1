param(
    [Parameter(Mandatory=$true)][ValidatePattern('\Acentral_restore_[a-z0-9_]{1,40}\z')][string]$TargetDatabase,
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [string]$QuiescenceProofPath,
    [string]$RestoreBundlePath,
    [ValidatePattern('\A[a-fA-F0-9]{64}\z')][string]$ExpectedBundleManifestSha256
)

$ErrorActionPreference='Stop'
$script:commands=[Collections.Generic.List[object]]::new()
$script:processObservations=[Collections.Generic.List[object]]::new()
$script:evidence=$null
$savedPg=@{}
foreach($name in @('PGPASSWORD','PGDATABASE','PGUSER','PGHOST','PGPORT','PGSSLMODE','PGOPTIONS','PGSERVICE','PGHOSTADDR','PGPASSFILE','PGCLIENTENCODING')) { $savedPg[$name]=[Environment]::GetEnvironmentVariable($name) }
function Required([string]$Name) { $v=[Environment]::GetEnvironmentVariable($Name); if([string]::IsNullOrWhiteSpace($v)){throw "Required owned-development value is absent: $Name"}; return $v }
function Sha([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function TextHash([string]$Value) { return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))) }
function SaveJson([string]$Path,$Value) { $Value|ConvertTo-Json -Depth 40|Set-Content -LiteralPath $Path -Encoding utf8 }
function Literal([string]$Value) { return "'"+$Value.Replace("'","''")+"'" }
function Identifier([string]$Value) { return '"'+$Value.Replace('"','""')+'"' }
function JsonText($Value) { return ConvertTo-Json -InputObject $Value -Depth 40 -Compress }
function CanonicalPath([string]$Path) {
    if(-not [IO.Path]::IsPathFullyQualified($Path)){throw 'Recovery paths must be fully qualified.'}
    $full=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path)); $root=[IO.Path]::GetPathRoot($full); $current=$root
    $parts=$full.Substring($root.Length).Split(@([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar),[StringSplitOptions]::RemoveEmptyEntries)
    foreach($part in @('')+$parts) {
        if($part) { if($part.Contains(':') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?\z'){throw 'Windows path aliases are forbidden.'}; $current=Join-Path $current $part }
        try { if(([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Recovery storage cannot traverse links or reparse components.'} }
        catch [IO.FileNotFoundException] { break }
        catch [IO.DirectoryNotFoundException] { break }
    }
    return $full
}
function Contained([string]$Root,[string]$Path) { $r=[IO.Path]::GetRelativePath($Root,$Path); return $r -ne '.' -and $r -ne '..' -and -not $r.StartsWith("..$([IO.Path]::DirectorySeparatorChar)") -and -not [IO.Path]::IsPathRooted($r) }
function Owned([string]$Path) {
    $p=CanonicalPath $Path
    if(-not @($script:ownedRoots|Where-Object { Contained $_ $p }).Count){throw 'Recovery path escapes the explicitly owned Central development roots.'}
    return $p
}
function Credential([string]$Variable,[string]$Database,[string]$Role) {
    $b=[Data.Common.DbConnectionStringBuilder]::new(); $b.set_ConnectionString((Required $Variable))
    $hostName=if($b.ContainsKey('Host')){[string]$b['Host']}elseif($b.ContainsKey('Server')){[string]$b['Server']}else{''}
    $userName=if($b.ContainsKey('Username')){[string]$b['Username']}elseif($b.ContainsKey('User Id')){[string]$b['User Id']}else{''}
    if($hostName -notin @('127.0.0.1','::1','localhost') -or -not $b.ContainsKey('Port') -or [int]$b['Port'] -ne $script:port -or [string]$b['Database'] -ne $Database -or $userName -ne $Role -or -not $b.ContainsKey('Password') -or [string]::IsNullOrEmpty([string]$b['Password'])){throw "Credential target disagrees with owned identity: $Variable"}
    return @{Database=$Database;Role=$Role;Password=[string]$b['Password']}
}
function UseCredential($Credential) { $env:PGDATABASE=$Credential.Database; $env:PGUSER=$Credential.Role; $env:PGPASSWORD=$Credential.Password }
function Native([string]$Tool,[string[]]$Arguments,$Credential,[switch]$AllowFailure) {
    $PSNativeCommandUseErrorActionPreference=$false
    UseCredential $Credential
    $stderr=Join-Path $script:evidence ('stderr-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    try {
        $output=@(& (Join-Path $script:pgBin $Tool) @Arguments 2> $stderr)
        $exit=$LASTEXITCODE; $errorText=if(Test-Path -LiteralPath $stderr){Get-Content -LiteralPath $stderr -Raw}else{''}
        $script:commands.Add([ordered]@{tool=$Tool;database=$Credential.Database;role=$Credential.Role;exitCode=$exit;error=$errorText;utc=[DateTimeOffset]::UtcNow.ToString('O')})
        if($exit -ne 0 -and -not $AllowFailure){throw "Recovery command failed ($Tool, exit $exit): $errorText"}
        return @{Output=($output -join "`n").Trim();ExitCode=$exit;Error=$errorText}
    } finally { if(Test-Path -LiteralPath $stderr){Remove-Item -LiteralPath $stderr} }
}
function Query($Credential,[string]$Sql,[switch]$AllowFailure) { return Native 'psql.exe' @('--no-psqlrc','--quiet','--tuples-only','--no-align','--set','ON_ERROR_STOP=1','--set','VERBOSITY=verbose','--command',("SET TIME ZONE 'UTC'; SET DateStyle='ISO, YMD'; SET extra_float_digits=3; SET search_path=pg_catalog; "+$Sql)) $Credential -AllowFailure:$AllowFailure }
function Identity($Credential,[switch]$Admin) {
    $j=(Query $Credential "SELECT jsonb_build_object('version',current_setting('server_version_num')::int,'database',current_database(),'role',current_user,'sessionRole',session_user,'superuser',(SELECT rolsuper FROM pg_roles WHERE rolname=current_user),'createRole',(SELECT rolcreaterole FROM pg_roles WHERE rolname=current_user),'address',host(inet_server_addr()),'port',inet_server_port())::text;").Output|ConvertFrom-Json
    if([int]$j.version -lt 180000 -or [int]$j.version -ge 190000 -or $j.database -ne $Credential.Database -or $j.role -ne $Credential.Role -or $j.sessionRole -ne $Credential.Role -or $j.address -notin @('127.0.0.1','::1') -or [int]$j.port -ne $script:port -or (-not $Admin -and ($j.superuser -or $j.createRole))){throw ('Live recovery identity mismatch. Observed: '+(JsonText $j))}
    return $j
}
function TableHashes($Credential,[switch]$WithoutPaths) {
    $tables=@((Query $Credential "SELECT COALESCE(jsonb_agg(c.relname ORDER BY c.relname),'[]'::jsonb)::text FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='central' AND c.relkind IN ('r','p');").Output|ConvertFrom-Json)
    if($tables.Count -eq 0){throw 'The source/restore has no Central tables.'}
    $rows=@()
    foreach($table in $tables) {
        $name='central.'+(Identifier $table); $row=if($WithoutPaths -and $table -in @('encrypted_objects','payment_evidences','upload_sessions')){"to_jsonb(r)-'StoragePath'"}else{'to_jsonb(r)'}
        $count=[long](Query $Credential "SELECT count(*) FROM $name;").Output
        $sql='COPY (SELECT ('+$row+')::text FROM '+$name+' r ORDER BY (('+$row+')::text) COLLATE "C") TO STDOUT WITH (FORMAT text);'
        # Raw rows remain in memory only; evidence exports their canonical SHA-256, not admin/session data.
        $content=(Query $Credential $sql).Output
        $rows+=[ordered]@{table=$table;rows=$count;sha256=(TextHash $content)}
    }
    return $rows
}
function Catalog($Credential) {
    $catalog=(Query $Credential @'
SELECT jsonb_build_object(
 'columns',(SELECT COALESCE(jsonb_agg(jsonb_build_array(c.relname,a.attnum,a.attname,format_type(a.atttypid,a.atttypmod),a.attnotnull,a.attidentity,a.attgenerated,pg_get_expr(d.adbin,d.adrelid)) ORDER BY c.relname,a.attnum),'[]'::jsonb) FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum WHERE n.nspname='central' AND c.relkind IN ('r','p') AND a.attnum>0 AND NOT a.attisdropped),
 'constraints',(SELECT COALESCE(jsonb_agg(jsonb_build_array(c.relname,x.conname,pg_get_constraintdef(x.oid,true)) ORDER BY c.relname,x.conname),'[]'::jsonb) FROM pg_constraint x JOIN pg_class c ON c.oid=x.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='central'),
 'indexes',(SELECT COALESCE(jsonb_agg(jsonb_build_array(tablename,indexname,indexdef) ORDER BY tablename,indexname),'[]'::jsonb) FROM pg_indexes WHERE schemaname='central'),
 'triggers',(SELECT COALESCE(jsonb_agg(jsonb_build_array(c.relname,t.tgname,pg_get_triggerdef(t.oid,true)) ORDER BY c.relname,t.tgname),'[]'::jsonb) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='central' AND NOT t.tgisinternal),
 'routines',(SELECT COALESCE(jsonb_agg(pg_get_functiondef(p.oid) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)),'[]'::jsonb) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='central' AND p.prokind IN ('f','p')),
 'sequences',(SELECT COALESCE(jsonb_agg(to_jsonb(s)-'sequenceowner' ORDER BY sequencename),'[]'::jsonb) FROM pg_sequences s WHERE schemaname='central')
)::text;
'@).Output
    # pg_dump/restore can distribute this text[] cast onto the two varchar
    # literals. Canonicalize only this exact equivalent deparse, retaining all
    # index names, columns, operators, predicates, types and values otherwise.
    return $catalog.Replace("(ARRAY['PENDING'::character varying, 'FAILED'::character varying])::text[]", "ARRAY[('PENDING'::character varying)::text, ('FAILED'::character varying)::text]")
}
function Privileges($Credential) {
    return (Query $Credential @'
SELECT jsonb_build_object('usage',has_schema_privilege(current_user,'central','USAGE'),
 'ownsRelations',EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='central' AND pg_get_userbyid(c.relowner)=current_user),
 'auditMutation',has_table_privilege(current_user,'central.audit_records','UPDATE,DELETE,TRUNCATE'),
 'tables',(SELECT jsonb_agg(jsonb_build_array(c.relname,has_table_privilege(current_user,c.oid,'SELECT'),has_table_privilege(current_user,c.oid,'INSERT'),has_table_privilege(current_user,c.oid,'UPDATE'),has_table_privilege(current_user,c.oid,'DELETE'),has_table_privilege(current_user,c.oid,'TRUNCATE')) ORDER BY c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='central' AND c.relkind IN ('r','p')))::text;
'@).Output
}
function Outbox($Credential) {
    return (Query $Credential @'
SELECT jsonb_build_object('pending',(SELECT count(*) FROM central.outbox_messages WHERE "Status"='PENDING'),'failed',(SELECT count(*) FROM central.outbox_messages WHERE "Status"='FAILED'),'deadLetter',(SELECT count(*) FROM central.outbox_messages WHERE "Status"='DEAD_LETTER'),
 'dispatchedWithoutEffect',(SELECT count(*) FROM central.outbox_messages o LEFT JOIN central.outbox_consumer_effects e ON e."MessageId"=o."Id" WHERE o."Status"='DISPATCHED' AND e."MessageId" IS NULL),
 'effectWithoutMessage',(SELECT count(*) FROM central.outbox_consumer_effects e LEFT JOIN central.outbox_messages o ON o."Id"=e."MessageId" WHERE o."Id" IS NULL),
 'effectPayloadMismatch',(SELECT count(*) FROM central.outbox_consumer_effects e JOIN central.outbox_messages o ON o."Id"=e."MessageId" WHERE e."EventType" IS DISTINCT FROM o."EventType" OR e."AggregateId" IS DISTINCT FROM o."AggregateId" OR e."PayloadJson" IS DISTINCT FROM o."PayloadJson"),
 'invalidTimestamps',(SELECT count(*) FROM central.outbox_messages WHERE ("Status"='DISPATCHED')<>("ProcessedAtUtc" IS NOT NULL)))::text;
'@).Output
}
function CaptureFiles($Credential,[string]$Bundle) {
    $roots=@()
    foreach($spec in @(@{kind='objects';name='CENTRAL_OBJECT_STORAGE_ROOTS'},@{kind='payments';name='CENTRAL_PAYMENT_STORAGE_ROOTS'})) {
        $raw=[Environment]::GetEnvironmentVariable($spec.name)
        if(-not $raw -and $spec.kind -eq 'payments'){$raw=[Environment]::GetEnvironmentVariable('CENTRAL_PAYMENT_STORAGE_ROOT')}
        if($raw){foreach($root in ($raw -split ';')){$full=Owned $root.Trim(); if(-not (Test-Path -LiteralPath $full -PathType Container)){throw 'Explicit source storage root is absent.'}; $roots+=@{kind=$spec.kind;root=$full}}}
    }
    $references=@((Query $Credential @'
SELECT COALESCE(jsonb_agg(x ORDER BY x->>'table',x->>'id'),'[]'::jsonb)::text FROM (
 SELECT jsonb_build_object('table','encrypted_objects','id',"Id",'path',"StoragePath",'kind','objects','bytes',"ContentLength",'declaredSha256',"CiphertextSha256",'finalized',true,'required',true) x FROM central.encrypted_objects WHERE NOT "IsTombstoned"
 UNION ALL SELECT jsonb_build_object('table','payment_evidences','id',"Id",'path',"StoragePath",'kind','payments','bytes',"ByteLength",'declaredSha256',"Sha256Digest",'finalized',true,'required',true) FROM central.payment_evidences
 UNION ALL SELECT jsonb_build_object('table','upload_sessions','id',"Id",'path',"StoragePath",'kind',CASE WHEN "Purpose" IN ('ENCRYPTED_BACKUP','SUPPORT_ATTACHMENT') OR "RetentionPolicyId" IS NOT NULL THEN 'objects' ELSE 'payments' END,'bytes',"UploadedBytes",'declaredSha256',CASE WHEN "Status"='FINALIZED' THEN "ActualSha256" ELSE NULL END,'finalized',"Status"='FINALIZED','status',"Status",'legalHold',"IsUnderLegalHold",'retentionUntilUtc',"RetentionUntilUtc",'retentionDeletionEligible',("Status" IN ('EXPIRED','QUARANTINED') AND NOT "IsUnderLegalHold" AND "RetentionUntilUtc" IS NOT NULL AND "RetentionUntilUtc"<=CURRENT_TIMESTAMP),'required',("UploadedBytes">0 AND NOT ("Status" IN ('EXPIRED','QUARANTINED') AND NOT "IsUnderLegalHold" AND "RetentionUntilUtc" IS NOT NULL AND "RetentionUntilUtc"<=CURRENT_TIMESTAMP))) FROM central.upload_sessions
) q;
'@).Output|ConvertFrom-Json)
    if([int](Query $Credential "SELECT count(*) FROM information_schema.columns WHERE table_schema='central' AND column_name='StoragePath' AND table_name NOT IN ('encrypted_objects','payment_evidences','upload_sessions');").Output -ne 0){throw 'Unknown storage-path table needs an explicit recovery mapping.'}
    foreach($ref in $references) {
        $id=[Guid]::Parse($ref.id); $sourceMatches=@()
        if($ref.finalized -and $ref.declaredSha256 -notmatch '\A[a-fA-F0-9]{64}\z'){throw 'Finalized storage metadata lacks its required digest.'}
        foreach($root in @($roots|Where-Object kind -EQ $ref.kind)) {
            $path=if([IO.Path]::IsPathRooted($ref.path)){CanonicalPath $ref.path}else{CanonicalPath ([IO.Path]::GetFullPath($ref.path,$root.root))}
            if((Contained $root.root $path) -and (Test-Path -LiteralPath $path -PathType Leaf)){$sourceMatches+=$path}
        }
        $sourceMatches=@($sourceMatches|Select-Object -Unique)
        if($sourceMatches.Count -gt 1 -or ($ref.required -and $sourceMatches.Count -ne 1)){throw "Missing/ambiguous source storage: $($ref.table)/$($ref.id)"}
        $relative=$ref.kind+'/'+$ref.table+'/'+$id.ToString('N')+'.bin'
        $ref|Add-Member -NotePropertyName bundlePath -NotePropertyValue ('files/'+$relative)
        $ref|Add-Member -NotePropertyName targetPath -NotePropertyValue ($ref.table+'/'+$id.ToString('N')+'.bin')
        $ref|Add-Member -NotePropertyName present -NotePropertyValue ($sourceMatches.Count -eq 1)
        if($sourceMatches.Count -eq 1) {
            $source=$sourceMatches[0]; $actualLength=(Get-Item -LiteralPath $source).Length
            if($ref.table -eq 'upload_sessions' -and -not $ref.finalized) {
                $state=(Query $Credential ('SELECT "Status" FROM central.upload_sessions WHERE "Id"='+(Literal $ref.id)+'::uuid;')).Output
                if($state -eq 'QUARANTINED') {
                    # Rejected bytes are forensic custody, not accepted upload
                    # progress. Preserve both independently without editing DB.
                    $ref|Add-Member -NotePropertyName acceptedBytes -NotePropertyValue ([long]$ref.bytes)
                    $ref|Add-Member -NotePropertyName quarantineBytes -NotePropertyValue $actualLength
                    $ref.bytes=$actualLength
                }
            }
            if($actualLength -ne [long]$ref.bytes){throw "Storage length mismatch: $($ref.table)/$($ref.id), metadata=$($ref.bytes), file=$actualLength. Recovery cannot certify inconsistent source bytes."}
            $hash=Sha $source; if($ref.declaredSha256 -and $hash -ne $ref.declaredSha256.ToLowerInvariant()){throw 'Finalized storage content differs from its persisted digest.'}
            $dest=Owned ([IO.Path]::GetFullPath($ref.bundlePath,$Bundle)); $null=New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force; $null=CanonicalPath $dest
            Copy-Item -LiteralPath $source -Destination $dest
            if((Sha $dest) -ne $hash){throw 'Backup file copy integrity failed.'}
            $ref|Add-Member -NotePropertyName capturedSha256 -NotePropertyValue $hash
        } elseif([long]$ref.bytes -ne 0 -and $ref.required){throw 'A required byte-bearing storage reference cannot be omitted.'} elseif([long]$ref.bytes -ne 0){$ref|Add-Member -NotePropertyName omittedByRetention -NotePropertyValue $true}
    }
    return $references
}
function AssertIdleSource($Credential) {
    $others=[long](Query $Credential 'SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid();').Output
    if($others -ne 0){throw 'Other clients remain connected to the source database; the coordinator must quiesce only its owned writers before capture.'}
}
function GetAllowedPostgresProcessIds([int]$PostgresPid,[string]$Binary,[string]$DataDirectory,[int]$Port) {
    $postgres=Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$PostgresPid" -ErrorAction Stop
    $postgresCommand=([string]$postgres.CommandLine).Replace('/','\')
    if(-not $postgres -or [string]$postgres.ExecutablePath -ne $Binary -or $postgresCommand -notmatch [regex]::Escape($DataDirectory) -or $postgresCommand -notmatch "(?i)(?:^|\s)-p\s+$Port(?:\s|$)"){throw 'The attested PostgreSQL process no longer matches its binary, data directory and port.'}
    $ids=@($PostgresPid)
    $parent=Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$([int]$postgres.ParentProcessId)" -ErrorAction SilentlyContinue
    if($parent) {
        $parentCommand=([string]$parent.CommandLine).Replace('/','\')
        if($parent.Name -ne 'cmd.exe' -or $parentCommand.IndexOf($Binary,[StringComparison]::OrdinalIgnoreCase) -lt 0 -or $parentCommand.IndexOf($DataDirectory,[StringComparison]::OrdinalIgnoreCase) -lt 0 -or $parentCommand -notmatch "(?i)(?:^|\s)-p\s+$Port(?:\s|$)"){throw 'PostgreSQL launcher is not an attested non-writer parent process.'}
        $ids+=@([int]$parent.ProcessId)
    }
    return @($ids)
}
function GetOwnedWriterProcesses([int]$CoordinatorPid,[int]$PostgresPid,[string]$Binary,[string]$DataDirectory,[int]$Port) {
    $rows=@(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
    $allowedInfrastructure=@(GetAllowedPostgresProcessIds $PostgresPid $Binary $DataDirectory $Port)
    $writers=@()
    foreach($row in $rows) {
        $processId=[int]$row.ProcessId
        if($processId -in (@($CoordinatorPid)+$allowedInfrastructure)){continue}
        $cmd=[string]$row.CommandLine; $exe=[string]$row.ExecutablePath
        if([string]::IsNullOrWhiteSpace($cmd) -and [string]::IsNullOrWhiteSpace($exe)){continue}
        $matchesRoot=$false
        foreach($root in $script:ownedRoots) {
            $needle=$root.TrimEnd('\','/')
            if(($cmd -and $cmd.IndexOf($needle,[StringComparison]::OrdinalIgnoreCase) -ge 0) -or ($exe -and $exe.StartsWith($needle+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase))){$matchesRoot=$true;break}
        }
        $centralMarker=($cmd -match '(?i)EdgeRetails\.Central\.(Api|Worker|IntegrationTests|UnitTests)|EdgeRetails\.ProtectedSigner|Invoke-CentralDisposableRecoveryRehearsal')
        if($matchesRoot -or $centralMarker) {
            $writers+=@{processId=$processId;parentProcessId=[int]$row.ParentProcessId;name=[string]$row.Name;executablePath=$exe;commandLineSha256=(TextHash $cmd);matchedOwnedRoot=$matchesRoot;matchedCentralMarker=$centralMarker}
        }
    }
    return @($writers|Sort-Object processId)
}
function ValidateQuiescenceProof([string]$Path,[string]$Database,[int]$Port,[int]$PostgresPid,[string]$Binary,[string]$DataDirectory) {
    $proof=Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json -DateKind String
    $now=[DateTimeOffset]::UtcNow
    try{$observed=[DateTimeOffset]::ParseExact([string]$proof.observedAtUtc,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None)}catch{throw 'Quiescence proof timestamp is invalid.'}
    $age=$now-$observed
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    $coordinatorPid=$PID
    if($proof.sourceDatabase -ne $Database -or [int]$proof.port -ne $Port -or [int]$proof.postgresPid -ne $PostgresPid -or $proof.fileWritersQuiesced -ne $true -or $proof.databaseWritersQuiesced -ne $true -or [string]$proof.coordinator -ne $identity -or [int]$proof.coordinatorPid -ne $coordinatorPid -or @($proof.activeOwnedWriterProcessIds).Count -ne 0 -or $age.TotalSeconds -lt 0 -or $age.TotalSeconds -gt 120){throw 'Quiescence proof is stale or does not bind this coordinator and the attested idle source.'}
    $refs=@($proof.observationEvidence)
    if($refs.Count -eq 0){throw 'Quiescence proof must reference native process-inventory evidence.'}
    $processRef=@($refs|Where-Object { $_.kind -eq 'windows-process-inventory' })
    if($processRef.Count -ne 1 -or $processRef[0].path -isnot [string] -or [string]$processRef[0].sha256 -notmatch '\A[a-fA-F0-9]{64}\z'){throw 'Quiescence proof must reference exactly one hashed Windows process inventory.'}
    $observationPath=Owned ([string]$processRef[0].path)
    if((Sha $observationPath) -ne ([string]$processRef[0].sha256).ToLowerInvariant()){throw 'Quiescence process evidence hash does not match its raw observation.'}
    $inventory=Get-Content -LiteralPath $observationPath -Raw|ConvertFrom-Json -DateKind String
    try{$inventoryAt=[DateTimeOffset]::ParseExact([string]$inventory.capturedAtUtc,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None)}catch{throw 'Raw process inventory timestamp is invalid.'}
    if([Math]::Abs(($now-$inventoryAt).TotalSeconds) -gt 120 -or $inventory.coordinator -ne $identity -or [int]$inventory.coordinatorPid -ne $coordinatorPid -or [int]$inventory.postgresPid -ne $PostgresPid -or @($inventory.processes).Count -eq 0){throw 'Raw process inventory is stale or bound to a different coordinator/source.'}
    $allowedInfrastructure=@(GetAllowedPostgresProcessIds $PostgresPid $Binary $DataDirectory $Port)
    if([int]$proof.postgresLauncherPid -ne $(if($allowedInfrastructure.Count -gt 1){$allowedInfrastructure[1]}else{0})){throw 'Quiescence proof launcher identity does not match the attested PostgreSQL process tree.'}
    $observedWriters=@($inventory.processes|Where-Object { [int]$_.processId -notin (@([int]$proof.coordinatorPid)+$allowedInfrastructure) -and ($_.matchedOwnedRoot -eq $true -or $_.matchedCentralMarker -eq $true) })
    if($observedWriters.Count -ne 0){throw 'Raw native process inventory contains an owned Central writer.'}
    $currentWriters=@(GetOwnedWriterProcesses $coordinatorPid $PostgresPid $Binary $DataDirectory $Port)
    if($currentWriters.Count -ne 0){throw 'A Central process referencing an explicitly owned root is currently active.'}
    $script:processObservations.Add([ordered]@{kind='windows-process-inventory';path=$observationPath;sha256=(Sha $observationPath);capturedAtUtc=$inventoryAt.ToUniversalTime().ToString('O');coordinator=$identity;coordinatorPid=$coordinatorPid;rawProcessCount=@($inventory.processes).Count;currentOwnedWriterCount=$currentWriters.Count})
    return $proof
}

try {
    $rootsValue=[Environment]::GetEnvironmentVariable('CENTRAL_RECOVERY_OWNED_ROOTS')
    if([string]::IsNullOrWhiteSpace($rootsValue)){$rootsValue=Required 'CENTRAL_RECOVERY_OWNED_ROOT'}
    $script:ownedRoots=@($rootsValue -split ';'|ForEach-Object { CanonicalPath $_.Trim() }|Sort-Object -Unique)
    if($script:ownedRoots.Count -eq 0){throw 'At least one explicitly owned Central development root is required.'}
    foreach($root in $script:ownedRoots) {
        if(-not (Test-Path -LiteralPath $root -PathType Container)){throw 'Every explicitly owned recovery root must already exist as a directory.'}
        if(@($script:ownedRoots|Where-Object { $_ -ne $root -and (Contained $root $_ -or Contained $_ $root) }).Count){throw 'Explicit recovery roots must be disjoint; broad ancestor roots are forbidden.'}
    }
    $script:port=[int](Required 'CENTRAL_PG_PORT')
    $sourceDatabase=Required 'CENTRAL_PG_DATABASE'; $runtimeRole=Required 'CENTRAL_PG_ROLE'; $ownerRole=Required 'CENTRAL_PG_MIGRATION_ROLE'; $adminRole=Required 'CENTRAL_PG_DATABASE_ADMIN_ROLE'
    foreach($name in @($sourceDatabase,$runtimeRole,$ownerRole,$adminRole)){if($name -notmatch '\A[a-z_][a-z0-9_]{0,62}\z'){throw 'Invalid PostgreSQL identity.'}}
    if($sourceDatabase -notmatch '\Acentral[a-z0-9_]+\z' -or $TargetDatabase -eq $sourceDatabase -or $TargetDatabase -match 'edgeretails|shop|pos|postgres|template' -or $script:port -in @(5432,7150) -or $script:port -lt 1024 -or $script:port -gt 65535){throw 'Recovery requires a separate disposable Central database on the reserved nonstandard port.'}
    $binary=CanonicalPath (Required 'CENTRAL_PG_BINARY'); $data=Owned (Required 'CENTRAL_PG_DATA_DIR'); $markerPath=Owned (Required 'CENTRAL_PG_OWNERSHIP_MARKER'); $marker=Get-Content -LiteralPath $markerPath -Raw|ConvertFrom-Json
    $processId=[int](Required 'CENTRAL_PG_PID'); $process=Get-Process -Id $processId -ErrorAction Stop; $nativeProcess=Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$processId" -ErrorAction Stop; $postmaster=Get-Content -LiteralPath (Join-Path $data 'postmaster.pid')
    $nativeCommand=([string]$nativeProcess.CommandLine).Replace('/','\'); $dataArg='(?i)(?:^|\s)-D\s+"?'+[regex]::Escape($data)+'"?(?:\s|$)'; $portArg="(?i)(?:^|\s)-p\s+$script:port(?:\s|$)"
    if($process.HasExited -or $process.Path -ne $binary -or [string]$nativeProcess.ExecutablePath -ne $binary -or $nativeCommand -notmatch $dataArg -or $nativeCommand -notmatch $portArg -or [Diagnostics.FileVersionInfo]::GetVersionInfo($binary).FileMajorPart -ne 18 -or [int]$marker.pid -ne $processId -or $marker.database -ne $sourceDatabase -or [int]$marker.port -ne $script:port -or $marker.runtimeRole -ne $runtimeRole -or $marker.migrationRole -ne $ownerRole -or (CanonicalPath $marker.dataDirectory) -ne $data -or $postmaster.Count -lt 4 -or [int]$postmaster[0] -ne $processId -or (CanonicalPath $postmaster[1]) -ne $data -or [int]$postmaster[3] -ne $script:port){throw 'PostgreSQL process, binary, command-line data directory/port, version, marker and postmaster identity disagree.'}
    if(-not (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)){throw 'Listener ownership attestation is unavailable.'}
    $listeners=@(Get-NetTCPConnection -LocalPort $script:port -State Listen -ErrorAction Stop)
    if($listeners.Count -eq 0 -or @($listeners|Where-Object {$_.OwningProcess -ne $processId -or $_.LocalAddress -notin @('127.0.0.1','::1')}).Count -gt 0){throw 'The reserved listener is not owned exclusively by the attested loopback PostgreSQL process.'}
    $script:pgBin=Split-Path -Parent $binary
    foreach($tool in @('pg_dump.exe','pg_restore.exe','createdb.exe','psql.exe')){if(-not (Test-Path -LiteralPath (Join-Path $script:pgBin $tool) -PathType Leaf)){throw "Required PostgreSQL recovery tool missing: $tool"}}
    $owner=Credential 'CENTRAL_MIGRATION_CONNECTION_STRING' $sourceDatabase $ownerRole; $runtime=Credential 'CENTRAL_DATABASE_CONNECTION_STRING' $sourceDatabase $runtimeRole
    $admin=Credential 'CENTRAL_PG_DATABASE_ADMIN_CONNECTION_STRING' 'postgres' $adminRole
    foreach($name in @('PGOPTIONS','PGSERVICE','PGHOSTADDR','PGPASSFILE')){Remove-Item -LiteralPath ('Env:'+ $name) -ErrorAction SilentlyContinue}
    $env:PGHOST='127.0.0.1'; $env:PGPORT=[string]$script:port; $env:PGSSLMODE='disable'; $env:PGCLIENTENCODING='UTF8'
    $script:evidence=Owned $EvidenceRoot; if(Test-Path -LiteralPath $script:evidence){throw 'Evidence root must be absent; preserve previous attempts.'}; $null=New-Item -ItemType Directory -Path $script:evidence
    $null=CanonicalPath $script:evidence; $null=Identity $admin -Admin
    if($RestoreBundlePath) {
        if(-not $ExpectedBundleManifestSha256){throw 'Restore-only requires an externally recorded bundle-manifest SHA-256.'}
        $bundle=Owned $RestoreBundlePath; $bundleManifestPath=CanonicalPath (Join-Path $bundle 'bundle-manifest.json')
        if((Sha $bundleManifestPath) -ne $ExpectedBundleManifestSha256.ToLowerInvariant()){throw 'Backup manifest integrity failed before target creation.'}
        $manifest=Get-Content -LiteralPath $bundleManifestPath -Raw|ConvertFrom-Json
    } else {
        if(-not $QuiescenceProofPath){throw 'Coordinator must provide actual owned-writer quiescence proof before capture.'}
        $proofPath=Owned $QuiescenceProofPath; $proof=ValidateQuiescenceProof $proofPath $sourceDatabase $script:port $processId $binary $data
        $sourceIdentity=Identity $owner; $null=Identity $runtime; $privileges=Privileges $runtime; $p=$privileges|ConvertFrom-Json
        if(-not $p.usage -or $p.ownsRelations -or $p.auditMutation){throw 'Runtime source role violates existing schema/audit least privilege.'}
        AssertIdleSource $owner
        $bundle=Join-Path $script:evidence 'backup-bundle'; $null=New-Item -ItemType Directory -Path $bundle
        $tables=@(TableHashes $owner); $tablesWithoutPaths=@(TableHashes $owner -WithoutPaths); $catalog=Catalog $owner; $outbox=Outbox $owner
        $null=Native 'pg_dump.exe' @('--format=custom','--no-owner','--schema=central','--file',(Join-Path $bundle 'central.dump')) $owner
        $references=@(CaptureFiles $owner $bundle)
        $after=@(TableHashes $owner)
        if((JsonText $tables) -ne (JsonText $after) -or (Catalog $owner) -ne $catalog -or (Outbox $owner) -ne $outbox){throw 'Owned source changed during capture; no consistent recovery proof can be issued.'}
        AssertIdleSource $owner
        $manifest=[ordered]@{format='CENTRAL_DEVELOPMENT_RECOVERY_V2';sourceDatabase=$sourceDatabase;sourceIdentity=$sourceIdentity;sourceMarkerSha256=(Sha $markerPath);quiescenceProofSha256=(Sha $proofPath);processObservations=@($script:processObservations);tables=$tables;tablesWithoutPaths=$tablesWithoutPaths;catalog=$catalog;catalogSha256=(TextHash $catalog);runtimePrivileges=$privileges;outbox=$outbox;fileReferences=$references;files=@(Get-ChildItem -LiteralPath $bundle -Recurse -File|ForEach-Object {@{path=([IO.Path]::GetRelativePath($bundle,$_.FullName).Replace('\','/'));bytes=$_.Length;sha256=(Sha $_.FullName)}});createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');privateSigningKeys='NOT_EXPORTED_OR_RECOVERED_BY_THIS_SCRIPT'}
        $bundleManifestPath=Join-Path $bundle 'bundle-manifest.json'; SaveJson $bundleManifestPath $manifest
    }
    if($manifest.format -ne 'CENTRAL_DEVELOPMENT_RECOVERY_V2' -or $manifest.sourceDatabase -ne $sourceDatabase){throw 'Unsupported backup format or source identity.'}
    if($RestoreBundlePath) {
        $restoreSourceIdentity=Identity $owner; $null=Identity $runtime
        if((JsonText $restoreSourceIdentity) -ne (JsonText $manifest.sourceIdentity)){throw 'Restore source identity differs from the identity bound into the backup manifest.'}
        $restoreSourcePrivileges=Privileges $runtime; $restorePrivilegeFacts=$restoreSourcePrivileges|ConvertFrom-Json
        if(-not $restorePrivilegeFacts.usage -or $restorePrivilegeFacts.ownsRelations -or $restorePrivilegeFacts.auditMutation -or $restoreSourcePrivileges -ne $manifest.runtimePrivileges){throw 'Restore source runtime credentials or least-privilege grants do not match the captured source.'}
    }
    foreach($entry in $manifest.files) {
        if([IO.Path]::IsPathRooted($entry.path)){throw 'Backup entries must be relative.'}; $path=Owned ([IO.Path]::GetFullPath($entry.path,$bundle))
        if(-not (Contained $bundle $path) -or -not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne [long]$entry.bytes -or (Sha $path) -ne $entry.sha256){throw 'Backup archive/blob integrity failed before target creation.'}
    }
    if(@($manifest.files|Where-Object path -EQ 'central.dump').Count -ne 1){throw 'Backup manifest does not bind exactly one PostgreSQL archive.'}
    $archive=CanonicalPath (Join-Path $bundle 'central.dump'); $null=Native 'pg_restore.exe' @('--list',$archive) $admin
    $null=Native 'createdb.exe' @('--owner',$ownerRole,'--template','template0',$TargetDatabase) $admin
    $targetOwner=@{Database=$TargetDatabase;Role=$ownerRole;Password=$owner.Password}; $targetRuntime=@{Database=$TargetDatabase;Role=$runtimeRole;Password=$runtime.Password}
    # ACLs are deliberately retained; restored migration history must not conceal missing grants.
    $null=Native 'pg_restore.exe' @('--exit-on-error','--single-transaction','--no-owner','--role',$ownerRole,'--dbname',$TargetDatabase,$archive) $targetOwner
    $targetIdentity=Identity $targetOwner; $null=Identity $targetRuntime
    SaveJson (Join-Path $script:evidence 'RESTORED_TARGET_MARKER.json') @{pid=$processId;database=$TargetDatabase;port=$script:port;binary=$binary;dataDirectory=$data;runtimeRole=$runtimeRole;migrationRole=$ownerRole;bindAddress='127.0.0.1';purpose='owned-disposable-Central-restored-target';sourceMarkerSha256=(Sha $markerPath)}
    $restored=@(TableHashes $targetOwner); $restoredCatalog=Catalog $targetOwner; $restoredOutbox=Outbox $targetOwner
    SaveJson (Join-Path $script:evidence 'restored-content-comparison.json') @{
        expectedTables=@($manifest.tables);actualTables=$restored
        expectedCatalogSha256=$manifest.catalogSha256;actualCatalogSha256=(TextHash $restoredCatalog)
        expectedCatalog=$manifest.catalog;actualCatalog=$restoredCatalog
        expectedOutbox=$manifest.outbox;actualOutbox=$restoredOutbox
    }
    if((JsonText $restored) -ne (JsonText @($manifest.tables)) -or (TextHash $restoredCatalog) -ne $manifest.catalogSha256 -or $restoredOutbox -ne $manifest.outbox){throw 'Full Central table contents, schema/sequence state or outbox state differ from the captured backup.'}
    if((Privileges $targetRuntime) -ne $manifest.runtimePrivileges){throw 'Runtime privileges were not preserved in the restored database.'}
    $objectRoot=Join-Path $script:evidence 'restored-object-store'; $paymentRoot=Join-Path $script:evidence 'restored-payment-store'; $null=New-Item -ItemType Directory -Path $objectRoot; $null=New-Item -ItemType Directory -Path $paymentRoot
    $readbacks=@()
    foreach($ref in $manifest.fileReferences) {
        if($ref.table -notin @('encrypted_objects','payment_evidences','upload_sessions') -or $ref.kind -notin @('objects','payments')){throw 'Unknown storage mapping in backup.'}; $null=[Guid]::Parse($ref.id)
        $root=if($ref.kind -eq 'objects'){$objectRoot}else{$paymentRoot}; $dest=Owned ([IO.Path]::GetFullPath($ref.targetPath,$root))
        if(-not (Contained $root $dest)){throw 'Restored metadata path escapes its storage root.'}
        if($ref.present) {
            $source=Owned ([IO.Path]::GetFullPath($ref.bundlePath,$bundle)); if(-not (Contained $bundle $source) -or (Sha $source) -ne $ref.capturedSha256){throw 'Backup file reference is missing or altered.'}
            $null=New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force; $null=CanonicalPath $dest; Copy-Item -LiteralPath $source -Destination $dest
            if((Sha $dest) -ne $ref.capturedSha256 -or (Get-Item -LiteralPath $dest).Length -ne [long]$ref.bytes){throw 'Restored file integrity differs from its backup.'}
        }
        $newPath=$ref.targetPath.Replace('\','/'); $sql='UPDATE central.'+(Identifier $ref.table)+' SET "StoragePath"='+(Literal $newPath)+' WHERE "Id"='+(Literal $ref.id)+'::uuid AND "StoragePath"='+(Literal $ref.path)+' RETURNING "Id"::text;'
        if((Query $targetOwner $sql).Output -ne $ref.id){throw 'Storage rebase did not update exactly the expected original row.'}
        $sql='SELECT "StoragePath" FROM central.'+(Identifier $ref.table)+' WHERE "Id"='+(Literal $ref.id)+'::uuid;'
        if((Query $targetOwner $sql).Output -ne $newPath){throw 'Restored storage metadata readback failed.'}
        $readbacks+=@{table=$ref.table;id=$ref.id;root=$ref.kind;path=$newPath;bytes=$ref.bytes;present=$ref.present;retentionDeletionEligible=($ref.retentionDeletionEligible -eq $true);omittedByRetention=($ref.omittedByRetention -eq $true);sha256=$ref.capturedSha256}
    }
    if((JsonText @(TableHashes $targetOwner -WithoutPaths)) -ne (JsonText @($manifest.tablesWithoutPaths))){throw 'Path rebasing changed other persisted data.'}
    $probe=[Guid]::NewGuid().ToString('D'); $sql='BEGIN; INSERT INTO central.outbox_messages ("Id","OccurredOnUtc","EventType","AggregateType","AggregateId","PayloadJson","Status","RetryCount") VALUES ('+(Literal $probe)+'::uuid,CURRENT_TIMESTAMP,''central.recovery.probe'',''CentralRecoveryRehearsal'',''synthetic'',''{"synthetic":true}'',''PENDING'',0); SELECT count(*) FROM central.outbox_messages WHERE "Id"='+(Literal $probe)+'::uuid; ROLLBACK;'
    if((Query $targetRuntime $sql).Output -ne '1'){throw 'Restored runtime transactional write/rollback failed.'}
    foreach($sql in @('UPDATE central.audit_records SET "Reason"="Reason" WHERE false;','DELETE FROM central.audit_records WHERE false;','TRUNCATE central.audit_records;')) {
        $denial=Query $targetRuntime ('BEGIN; '+$sql+' ROLLBACK;') -AllowFailure
        if($denial.ExitCode -eq 0 -or $denial.Error -notmatch '42501'){throw 'Restored runtime audit operation was not denied by PostgreSQL privileges.'}
    }
    $invariants=$restoredOutbox|ConvertFrom-Json; $eligible=$invariants.dispatchedWithoutEffect -eq 0 -and $invariants.effectWithoutMessage -eq 0 -and $invariants.effectPayloadMismatch -eq 0 -and $invariants.invalidTimestamps -eq 0
    $result=[ordered]@{result=if($eligible){'PASS'}else{'RESTORE_VERIFIED_WITH_INVARIANT_FAILURES'};recoveryAcceptanceEligible=$eligible;sourceDatabase=$sourceDatabase;restoredDatabase=$TargetDatabase;targetIdentity=$targetIdentity;port=$script:port;backupBundle=$bundle;backupBundleManifestSha256=(Sha $bundleManifestPath);backupSha256=(Sha $archive);centralTables=$restored;catalogSha256=(TextHash $restoredCatalog);runtimePrivilegesPreserved=$true;runtimeTransactionalWriteRollback=$true;auditMutationDenied=$true;outbox=$invariants;outboxWorkerReplay='NOT_EXERCISED_BY_THIS_SCRIPT';restoredObjectStorageRoot=$objectRoot;restoredPaymentStorageRoot=$paymentRoot;storageReferences=$readbacks;completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');scope='Owned Central schema/data/ACL/files restored. Hosted business effects, protected-key recovery, application restart, upgrade and production cutover need separate proof.'}
    SaveJson (Join-Path $script:evidence 'recovery-manifest.json') $result; $result|ConvertTo-Json -Depth 10
    if(-not $eligible){throw 'Restore completed but outbox invariants fail; recovery acceptance is withheld.'}
} catch {
    if($script:evidence -and (Test-Path -LiteralPath $script:evidence -PathType Container)){SaveJson (Join-Path $script:evidence 'failure.json') @{result='FAIL';error=$_.Exception.Message;utc=[DateTimeOffset]::UtcNow.ToString('O')}}
    throw
} finally {
    if($script:evidence -and (Test-Path -LiteralPath $script:evidence -PathType Container)){SaveJson (Join-Path $script:evidence 'commands.json') @($script:commands)}
    foreach($name in $savedPg.Keys) {
        if($null -eq $savedPg[$name]) { Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name,$savedPg[$name]) }
    }
}
