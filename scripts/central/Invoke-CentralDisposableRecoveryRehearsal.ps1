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
$script:recoveryHostProcesses=[Collections.Generic.List[System.Diagnostics.Process]]::new()
$script:evidence=$null
$savedPg=@{}
foreach($name in @('PGPASSWORD','PGDATABASE','PGUSER','PGHOST','PGPORT','PGSSLMODE','PGOPTIONS','PGSERVICE','PGHOSTADDR','PGPASSFILE','PGCLIENTENCODING')) { $savedPg[$name]=[Environment]::GetEnvironmentVariable($name) }
$hostEnvironmentNames=@('CENTRAL_DATABASE_CONNECTION_STRING','CENTRAL_SIGNER_KEY_STORE_ROOT','CENTRAL_OBJECT_STORAGE_ROOT','CENTRAL_PAYMENT_STORAGE_ROOT','CENTRAL_PG_PORT','CENTRAL_PG_DATABASE','CENTRAL_PG_ROLE','CENTRAL_PG_MIGRATION_ROLE','CENTRAL_PG_PID','CENTRAL_PG_BINARY','CENTRAL_PG_OWNERSHIP_MARKER','CENTRAL_PG_DATA_DIR','CENTRAL_OUTBOX_ENABLED','CENTRAL_OUTBOX_BATCH_SIZE','CENTRAL_OUTBOX_MAX_ATTEMPTS','CENTRAL_OUTBOX_POLL_MS','CENTRAL_OUTBOX_LEASE_SECONDS','CENTRAL_OUTBOX_RETRY_BASE_MS','CENTRAL_OUTBOX_RETRY_MAX_MS','CENTRAL_STORAGE_RECONCILIATION_ENABLED','CENTRAL_ACTIVATION_DELIVERY_CLEANUP_ENABLED','ASPNETCORE_URLS','ASPNETCORE_ENVIRONMENT')
$savedHostEnvironment=@{}
foreach($name in $hostEnvironmentNames){$savedHostEnvironment[$name]=[Environment]::GetEnvironmentVariable($name)}
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
function Identity($Credential,[switch]$Admin,[switch]$MigrationOwner) {
    $j=(Query $Credential "SELECT jsonb_build_object('version',current_setting('server_version_num')::int,'database',current_database(),'role',current_user,'sessionRole',session_user,'superuser',(SELECT rolsuper FROM pg_roles WHERE rolname=current_user),'createRole',(SELECT rolcreaterole FROM pg_roles WHERE rolname=current_user),'createDb',(SELECT rolcreatedb FROM pg_roles WHERE rolname=current_user),'replication',(SELECT rolreplication FROM pg_roles WHERE rolname=current_user),'bypassRls',(SELECT rolbypassrls FROM pg_roles WHERE rolname=current_user),'address',host(inet_server_addr()),'port',inet_server_port())::text;").Output|ConvertFrom-Json
    # The separately attested recovery administrator alone may create the
    # disposable target database. Runtime and migration-owner identities stay
    # non-superuser and cannot create roles/databases or bypass RLS.
    $roleMismatch=if($Admin){$j.superuser -or $j.createRole -or -not $j.createDb -or $j.replication -or $j.bypassRls}else{$j.superuser -or $j.createRole -or $j.createDb -or $j.replication -or $j.bypassRls}
    if([int]$j.version -lt 180000 -or [int]$j.version -ge 190000 -or $j.database -ne $Credential.Database -or $j.role -ne $Credential.Role -or $j.sessionRole -ne $Credential.Role -or $j.address -notin @('127.0.0.1','::1') -or [int]$j.port -ne $script:port -or $roleMismatch){throw ('Live recovery identity mismatch. Observed: '+(JsonText $j))}
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
function AddDpapiRecoveryBridge {
    if('CentralRecoveryDpapiBridge029x' -as [type]){return}
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public static class CentralRecoveryDpapiBridge029x
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr LocalFree(IntPtr memory);

    public sealed class Result { public byte[] ProtectedBytes { get; set; } }
    public static void Zero(byte[] bytes) { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }

    public static Result Reprotect(byte[] protectedBytes, string sourceRoot, string targetRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI recovery is Windows-only.");
        byte[] plain = null, target = null, check = null;
        try
        {
            plain = Transform(protectedBytes, Entropy(sourceRoot), false);
            target = Transform(plain, Entropy(targetRoot), true);
            check = Transform(target, Entropy(targetRoot), false);
            if (!CryptographicOperations.FixedTimeEquals(plain, check)) throw new CryptographicException("Reprotected DPAPI content differs from its source.");
            return new Result { ProtectedBytes = target };
        }
        finally
        {
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            if (check != null) CryptographicOperations.ZeroMemory(check);
        }
    }

    private static byte[] Entropy(string root) => Encoding.UTF8.GetBytes("EdgeRetails.Central.DevelopmentPrivateKeys.v1|" + PathRoot(root));
    private static string PathRoot(string root) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));

    private static byte[] Transform(byte[] bytes, byte[] entropy, bool protect)
    {
        var input = MakeBlob(bytes); var optional = MakeBlob(entropy); Blob output = default;
        try
        {
            bool ok = protect
                ? CryptProtectData(ref input, "EdgeRetails Central disposable recovery", ref optional, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref optional, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI operation failed.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result;
        }
        finally
        {
            ClearAndFree(ref input, false); ClearAndFree(ref optional, false); ClearAndFree(ref output, true);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static Blob MakeBlob(byte[] bytes)
    {
        var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1)) };
        if (bytes.Length > 0) Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static void ClearAndFree(ref Blob blob, bool local)
    {
        if (blob.Data == IntPtr.Zero) return;
        if (blob.Size > 0)
        {
            var zero = new byte[blob.Size]; Marshal.Copy(zero, 0, blob.Data, blob.Size);
            CryptographicOperations.ZeroMemory(zero);
        }
        if (local) LocalFree(blob.Data); else Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }
}
'@
}
function GetSignerRootProof([string]$AssemblyPath,[string]$Root,[string]$ExpectedPublicKey) {
    $assembly= [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $storeType=$assembly.GetType('EdgeRetails.ProtectedSigner.Keys.PersistentDevelopmentPrivateKeyStore',$true)
    $store=[Activator]::CreateInstance($storeType,[object[]]@($Root))
    try {
        $rootId='central-root-authority-v1'
        $publicKey=[string]$storeType.GetMethod('GetPublicKeyPem').Invoke($store,[object[]]@($rootId))
        if([string]::IsNullOrWhiteSpace($publicKey)){throw 'Recovered protected signer store has no root public key.'}
        if($ExpectedPublicKey -and $publicKey -cne $ExpectedPublicKey){throw 'Recovered signer root fingerprint differs from the captured source key.'}
        $key=$storeType.GetMethod('GetKey').Invoke($store,[object[]]@($rootId))
        if($null -eq $key){throw 'Recovered protected signer root key could not be reopened.'}
        $payload=[Text.Encoding]::UTF8.GetBytes('CENTRAL_DISPOSABLE_RECOVERY_SIGNER_PROOF_V1')
        $signature=$key.SignData($payload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $verifier=[Security.Cryptography.RSA]::Create()
        try {
            $verifier.ImportFromPem($publicKey)
            if(-not $verifier.VerifyData($payload,$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)){throw 'Recovered signer root failed its synthetic RS256 signing proof.'}
        } finally { $verifier.Dispose(); [CentralRecoveryDpapiBridge029x]::Zero($payload); [CentralRecoveryDpapiBridge029x]::Zero($signature) }
        return [ordered]@{rootPublicKeyPem=$publicKey;rootPublicKeySha256=(TextHash $publicKey);rootKeyReopened=$true;syntheticSignatureVerified=$true;profile='RS256'}
    } finally { if($store -is [IDisposable]){$store.Dispose()} }
}
function OutboxMessageState($Credential,[string]$Id) {
    $sql='SELECT jsonb_build_object(''status'',m."Status",''retryCount'',m."RetryCount",''effectCount'',(SELECT count(*) FROM central.outbox_consumer_effects e WHERE e."MessageId"=m."Id"),''notificationCount'',(SELECT count(*) FROM central.admin_notifications n WHERE n."Id"=m."Id"))::text FROM central.outbox_messages m WHERE m."Id"='+(Literal $Id)+'::uuid;'
    $raw=(Query $Credential $sql).Output
    if(-not $raw){throw 'Recovery replay message disappeared from the disposable target.'}
    return ($raw|ConvertFrom-Json)
}
function PendingOutboxIds($Credential) {
    $raw=(Query $Credential 'SELECT COALESCE(jsonb_agg("Id"::text ORDER BY "OccurredOnUtc","Id"),''[]''::jsonb)::text FROM central.outbox_messages WHERE "Status"=''PENDING'';').Output
    return @($raw|ConvertFrom-Json)
}
function DeadLetterSnapshot($Credential) {
    $raw=(Query $Credential 'SELECT COALESCE(jsonb_agg(jsonb_build_array("Id"::text,"RetryCount","ProcessedAtUtc") ORDER BY "Id"),''[]''::jsonb)::text FROM central.outbox_messages WHERE "Status"=''DEAD_LETTER'';').Output
    return (TextHash $raw)
}
function VerifyPersistedTrustSet($Credential,[string]$RootPublicKey,[string]$ExpectedRootKeyId) {
    $sql='SELECT jsonb_build_object(''version'',"TrustSetVersion",''revision'',"EmergencyRolloverRevision",''rootKeyId'',"SignedByTrustedRootKeyId",''signature'',"Signature",''keysJson'',"KeysJson",''revokedJson'',"RevokedKeyIdsJson")::text FROM central.trust_sets ORDER BY "TrustSetVersion" DESC LIMIT 1;'
    $raw=(Query $Credential $sql).Output
    if(-not $raw){throw 'Restored database has no persisted signed trust-set record.'}
    $record=$raw|ConvertFrom-Json
    if([string]$record.rootKeyId -cne $ExpectedRootKeyId){throw 'Restored trust set is not signed by the captured development root authority.'}
    $envelope=[string]$record.version+':'+[string]$record.revision+':'+[string]$record.keysJson+':'+[string]$record.revokedJson
    $verifier=[Security.Cryptography.RSA]::Create()
    try {
        $verifier.ImportFromPem($RootPublicKey)
        $sig=[Convert]::FromBase64String([string]$record.signature)
        if(-not $verifier.VerifyData([Text.Encoding]::UTF8.GetBytes($envelope),$sig,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)){throw 'Restored trust-set signature failed against the recovered protected root.'}
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($sig)
    } finally { $verifier.Dispose() }
    $keys=@(ConvertFrom-Json -InputObject ([string]$record.keysJson))
    $revoked=@(ConvertFrom-Json -InputObject ([string]$record.revokedJson))
    $revokedFromKeys=@($keys|Where-Object { $_.Status -eq 'REVOKED' }|ForEach-Object { [string]$_.KeyId }|Sort-Object -Unique)
    $revokedFromSet=@($revoked|ForEach-Object { [string]$_ }|Sort-Object -Unique)
    if((JsonText $revokedFromKeys) -cne (JsonText $revokedFromSet)){throw 'Restored trust-set revocation list differs from its signed key descriptors.'}
    return [ordered]@{trustSetVersion=[int]$record.version;emergencyRolloverRevision=[int]$record.revision;rootKeyId=[string]$record.rootKeyId;signatureVerified=$true;revokedKeyCount=$revokedFromSet.Count;revocationsMatchSignedDescriptors=$true;envelopeSha256=(TextHash $envelope)}
}
function SetRecoveryHostEnvironment($Credential,[string]$SignerRoot,[string]$ObjectRoot,[string]$PaymentRoot,[string]$MarkerPath,[string]$DataPath,[string]$BinaryPath) {
    $builder=[Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host']='127.0.0.1'; $builder['Port']=$script:port; $builder['Database']=$Credential.Database; $builder['Username']=$Credential.Role; $builder['Password']=$Credential.Password; $builder['Pooling']='false'; $builder['Timeout']='5'; $builder['Command Timeout']='10'
    $env:CENTRAL_DATABASE_CONNECTION_STRING=$builder.ConnectionString
    $env:CENTRAL_SIGNER_KEY_STORE_ROOT=$SignerRoot; $env:CENTRAL_OBJECT_STORAGE_ROOT=$ObjectRoot; $env:CENTRAL_PAYMENT_STORAGE_ROOT=$PaymentRoot
    $env:CENTRAL_PG_PORT=[string]$script:port; $env:CENTRAL_PG_DATABASE=$Credential.Database; $env:CENTRAL_PG_ROLE=$Credential.Role; $env:CENTRAL_PG_MIGRATION_ROLE=$ownerRole
    $env:CENTRAL_PG_PID=[string]$processId; $env:CENTRAL_PG_BINARY=$BinaryPath; $env:CENTRAL_PG_OWNERSHIP_MARKER=$MarkerPath; $env:CENTRAL_PG_DATA_DIR=$DataPath
    $env:CENTRAL_OUTBOX_ENABLED='true'; $env:CENTRAL_OUTBOX_BATCH_SIZE='100'; $env:CENTRAL_OUTBOX_MAX_ATTEMPTS='5'; $env:CENTRAL_OUTBOX_POLL_MS='100'; $env:CENTRAL_OUTBOX_LEASE_SECONDS='30'; $env:CENTRAL_OUTBOX_RETRY_BASE_MS='100'; $env:CENTRAL_OUTBOX_RETRY_MAX_MS='1000'
    $env:CENTRAL_STORAGE_RECONCILIATION_ENABLED='false'; $env:CENTRAL_ACTIVATION_DELIVERY_CLEANUP_ENABLED='false'
    $env:ASPNETCORE_URLS='http://127.0.0.1:0'; $env:ASPNETCORE_ENVIRONMENT='CentralDisposableRecovery'
}
function StartRecoveryHost([string]$AssemblyPath,[int]$Pass) {
    $stdout=Join-Path $script:evidence "recovery-host-$Pass.stdout.log"; $stderr=Join-Path $script:evidence "recovery-host-$Pass.stderr.log"
    $quotedAssembly='"'+$AssemblyPath+'"'
    $process=Start-Process -FilePath (Get-Command dotnet.exe -ErrorAction Stop).Source -ArgumentList $quotedAssembly -WorkingDirectory (Split-Path -Parent $AssemblyPath) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $script:recoveryHostProcesses.Add($process)
    return $process
}
function StopRecoveryHost($Process) {
    if($Process -and -not $Process.HasExited){$Process.Kill($true); $Process.WaitForExit(10000)|Out-Null}
    if($Process -and -not $Process.HasExited){throw 'Disposable recovery host could not be stopped.'}
}
function WaitForReplay($Credential,[string]$SyntheticId,[string[]]$ExpectedPending,$Process) {
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        if($Process.HasExited){throw 'Disposable recovery host exited before outbox replay finished.'}
        $synthetic=OutboxMessageState $Credential $SyntheticId
        $pending=PendingOutboxIds $Credential
        $expectedDone=$true
        foreach($id in $ExpectedPending){$state=OutboxMessageState $Credential $id; if($state.status -ne 'DISPATCHED' -or [int]$state.effectCount -ne 1){$expectedDone=$false;break}}
        if($synthetic.status -eq 'DISPATCHED' -and [int]$synthetic.effectCount -eq 1 -and $pending.Count -eq 0 -and $expectedDone){return $synthetic}
        Start-Sleep -Milliseconds 250
    } while([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'Disposable hosted outbox replay did not reach the expected durable effect state within 60 seconds.'
}
function ExerciseOutboxReplay($Credential,[string]$AssemblyPath,[string]$SignerRoot,[string]$ObjectRoot,[string]$PaymentRoot,[string]$MarkerPath,[string]$DataPath,[string]$BinaryPath) {
    $beforeDeadLetters=DeadLetterSnapshot $Credential
    $originalPending=@(PendingOutboxIds $Credential)
    $syntheticId=[Guid]::NewGuid().ToString('D')
    $payload='{"synthetic":true}'
    $insert='INSERT INTO central.outbox_messages ("Id","OccurredOnUtc","EventType","AggregateType","AggregateId","PayloadJson","Status","RetryCount") VALUES ('+(Literal $syntheticId)+'::uuid,CURRENT_TIMESTAMP,''central.recovery.synthetic-replay'',''CentralRecoveryRehearsal'',''synthetic'','+(Literal $payload)+',''PENDING'',0);'
    $null=Query $Credential $insert
    SetRecoveryHostEnvironment $Credential $SignerRoot $ObjectRoot $PaymentRoot $MarkerPath $DataPath $BinaryPath
    $first=StartRecoveryHost $AssemblyPath 1
    try { $firstResult=WaitForReplay $Credential $syntheticId $originalPending $first }
    finally { StopRecoveryHost $first }
    if([int](Query $Credential ('UPDATE central.outbox_messages SET "Status"=''PENDING'',"RetryCount"=0,"LastError"=NULL,"ProcessedAtUtc"=NULL,"ClaimToken"=NULL,"ClaimExpiresAtUtc"=NULL,"NextAttemptAtUtc"=NULL WHERE "Id"='+(Literal $syntheticId)+'::uuid AND "Status"=''DISPATCHED'' RETURNING 1;')).Output -ne 1){throw 'Synthetic replay receipt could not be reset on disposable target.'}
    $second=StartRecoveryHost $AssemblyPath 2
    try { $secondResult=WaitForReplay $Credential $syntheticId @() $second }
    finally { StopRecoveryHost $second }
    if([int]$secondResult.effectCount -ne 1 -or [int]$secondResult.notificationCount -ne 0){throw 'Repeated hosted replay produced a duplicate or unexpected synthetic business effect.'}
    if((DeadLetterSnapshot $Credential) -ne $beforeDeadLetters){throw 'Hosted replay changed a preserved dead-letter record.'}
    return [ordered]@{hostedWorkerRestartedTwice=$true;syntheticReplayMessageId=$syntheticId;firstPassEffectCount=[int]$firstResult.effectCount;secondPassEffectCount=[int]$secondResult.effectCount;duplicateEffects=0;initialPendingMessageCount=$originalPending.Count;initialPendingResolved=$true;deadLetterSnapshotSha256=$beforeDeadLetters;deadLettersUnchanged=$true;externalDelivery='NOT_CONFIGURED_OR_INVOKED';proof='Actual CentralOutboxHostedService with DurableOutboxConsumerSink on disposable restored PostgreSQL; second pass requeues the same synthetic receipt and proves consumer idempotence.'}
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
 SELECT jsonb_build_object('table','encrypted_objects','id',"Id",'shopId',"ShopId",'path',"StoragePath",'kind','objects','bytes',"ContentLength",'declaredSha256',"CiphertextSha256",'finalized',true,'required',NOT ("IsTombstoned" AND NOT "IsUnderLegalHold" AND "RetentionUntilUtc" IS NOT NULL AND "RetentionUntilUtc"<=CURRENT_TIMESTAMP),'retentionDeletionEligible',("IsTombstoned" AND NOT "IsUnderLegalHold" AND "RetentionUntilUtc" IS NOT NULL AND "RetentionUntilUtc"<=CURRENT_TIMESTAMP)) x FROM central.encrypted_objects
 UNION ALL SELECT jsonb_build_object('table','payment_evidences','id',p."Id",'shopId',p."ShopId",'path',p."StoragePath",'kind',CASE WHEN EXISTS (SELECT 1 FROM central.encrypted_objects o WHERE o."ShopId"=p."ShopId" AND o."StoragePath"=p."StoragePath") THEN 'objects' ELSE 'payments' END,'bytes',p."ByteLength",'declaredSha256',p."Sha256Digest",'finalized',true,'required',true) FROM central.payment_evidences p
 UNION ALL SELECT jsonb_build_object('table','upload_sessions','id',u."Id",'shopId',u."ShopId",'targetEntityId',u."TargetEntityId",'path',u."StoragePath",'kind',CASE WHEN u."Purpose" IN ('ENCRYPTED_BACKUP','SUPPORT_ATTACHMENT') THEN 'objects' ELSE 'payments' END,'bytes',u."UploadedBytes",'expectedBytes',u."ExpectedBytes",'expectedSha256',u."ExpectedSha256",'declaredSha256',CASE WHEN u."Status"='FINALIZED' THEN u."ActualSha256" ELSE NULL END,'finalized',u."Status"='FINALIZED','status',u."Status",'legalHold',u."IsUnderLegalHold",'retentionUntilUtc',u."RetentionUntilUtc",'retentionDeletionEligible',((u."Status" IN ('EXPIRED','QUARANTINED') AND NOT u."IsUnderLegalHold" AND u."RetentionUntilUtc" IS NOT NULL AND u."RetentionUntilUtc"<=CURRENT_TIMESTAMP) OR EXISTS (SELECT 1 FROM central.encrypted_objects o WHERE o."Id"=u."TargetEntityId" AND o."ShopId"=u."ShopId" AND o."IsTombstoned" AND NOT o."IsUnderLegalHold" AND o."RetentionUntilUtc" IS NOT NULL AND o."RetentionUntilUtc"<=CURRENT_TIMESTAMP)),'required',(u."UploadedBytes">0 AND NOT ((u."Status" IN ('EXPIRED','QUARANTINED') AND NOT u."IsUnderLegalHold" AND u."RetentionUntilUtc" IS NOT NULL AND u."RetentionUntilUtc"<=CURRENT_TIMESTAMP) OR (u."Status"='FINALIZED' AND EXISTS (SELECT 1 FROM central.encrypted_objects o WHERE o."Id"=u."TargetEntityId" AND o."ShopId"=u."ShopId" AND o."IsTombstoned" AND NOT o."IsUnderLegalHold" AND o."RetentionUntilUtc" IS NOT NULL AND o."RetentionUntilUtc"<=CURRENT_TIMESTAMP))))) FROM central.upload_sessions u
) q;
'@).Output|ConvertFrom-Json)
    if([int](Query $Credential "SELECT count(*) FROM information_schema.columns WHERE table_schema='central' AND column_name='StoragePath' AND table_name NOT IN ('encrypted_objects','payment_evidences','upload_sessions');").Output -ne 0){throw 'Unknown storage-path table needs an explicit recovery mapping.'}
    foreach($ref in $references) {
        $id=[Guid]::Parse($ref.id); $sourceCandidates=@()
        if($ref.finalized -and $ref.declaredSha256 -notmatch '\A[a-fA-F0-9]{64}\z'){throw 'Finalized storage metadata lacks its required digest.'}
        # Upload rows can persist either an absolute provider path or a
        # relative path that reflects the adapter actually used. Resolve
        # them against every explicitly configured storage root and require
        # exactly one contained byte source; the purpose label alone is not
        # authoritative for the physical provider location.
        $referenceRoots=if($ref.table -eq 'upload_sessions'){@($roots)}else{@($roots|Where-Object kind -EQ $ref.kind)}
        foreach($root in $referenceRoots) {
                $candidatePaths=@()
            if($ref.table -eq 'upload_sessions' -and -not $ref.finalized) {
                # Chunked uploads use a staging file while other uploads may
                # publish directly to StoragePath. Resolve all actual provider
                # locations and require exactly one match below.
                $shop=[Guid]::Parse($ref.shopId)
                $relativeStage=Join-Path '.uploads' (Join-Path $shop.ToString('N') ($id.ToString('N')+'.part'))
                $candidatePaths+=@{kind='uploadStage';path=(CanonicalPath ([IO.Path]::GetFullPath($relativeStage,$root.root)))}
                $target=[Guid]::Parse($ref.targetEntityId)
                $relativePublished=Join-Path $shop.ToString('N') ($target.ToString('N')+'.bin')
                $candidatePaths+=@{kind='publishedObject';path=(CanonicalPath ([IO.Path]::GetFullPath($relativePublished,$root.root)))}
                $storedPath=if([IO.Path]::IsPathRooted($ref.path)){CanonicalPath $ref.path}else{CanonicalPath ([IO.Path]::GetFullPath($ref.path,$root.root))}
                $candidatePaths+=@{kind='storagePath';path=$storedPath}
            } else {
                $storedPath=if([IO.Path]::IsPathRooted($ref.path)){CanonicalPath $ref.path}else{CanonicalPath ([IO.Path]::GetFullPath($ref.path,$root.root))}
                $candidatePaths+=@{kind='storagePath';path=$storedPath}
            }
            foreach($candidate in $candidatePaths){if((Contained $root.root $candidate.path) -and (Test-Path -LiteralPath $candidate.path -PathType Leaf)){$sourceCandidates+=@{kind=$candidate.kind;path=$candidate.path;root=$root.root;rootKind=$root.kind}}}
        }
        $sourceMatches=@($sourceCandidates.path|Select-Object -Unique)
        if($sourceMatches.Count -gt 1 -or ($ref.required -and $sourceMatches.Count -ne 1)){throw "Missing/ambiguous source storage: $($ref.table)/$($ref.id)"}
        $source=$null
        if($sourceMatches.Count -eq 1){$source=@($sourceCandidates|Where-Object path -EQ $sourceMatches[0]|Sort-Object @{Expression={switch($_.kind){'publishedObject'{0};'uploadStage'{1};default{2}}}})[0]}
        if($sourceMatches.Count -eq 1){$ref.kind=$source.rootKind}
        $relative=$ref.kind+'/'+$ref.table+'/'+$id.ToString('N')+'.bin'
        $ref|Add-Member -NotePropertyName bundlePath -NotePropertyValue ('files/'+$relative)
        $ref|Add-Member -NotePropertyName targetPath -NotePropertyValue ($ref.table+'/'+$id.ToString('N')+'.bin')
        $ref|Add-Member -NotePropertyName present -NotePropertyValue ($sourceMatches.Count -eq 1)
        if($sourceMatches.Count -eq 1) {
            $sourcePath=$source.path; $actualLength=(Get-Item -LiteralPath $sourcePath).Length
            if($ref.table -eq 'upload_sessions' -and -not $ref.finalized) {
                if($source.kind -eq 'publishedObject') {
                    if($actualLength -ne [long]$ref.expectedBytes -or (Sha $sourcePath) -ne $ref.expectedSha256.ToLowerInvariant()){throw 'Unpublished object bytes do not match the upload session expected integrity metadata.'}
                    $ref|Add-Member -NotePropertyName acceptedBytes -NotePropertyValue ([long]$ref.bytes)
                    $ref|Add-Member -NotePropertyName preservedUnpublishedObjectBytes -NotePropertyValue $actualLength
                    $ref.bytes=$actualLength
                } elseif($ref.status -eq 'QUARANTINED') {
                    # Rejected bytes are forensic custody, not accepted upload
                    # progress. Preserve both independently without editing DB.
                    $ref|Add-Member -NotePropertyName acceptedBytes -NotePropertyValue ([long]$ref.bytes)
                    $ref|Add-Member -NotePropertyName quarantineBytes -NotePropertyValue $actualLength
                    $ref.bytes=$actualLength
                }
            }
            if($ref.table -eq 'upload_sessions' -and $source.kind -eq 'uploadStage'){$ref.targetPath='.uploads/'+$ref.shopId.Replace('-','')+'/'+$id.ToString('N')+'.part'}
            elseif($ref.table -eq 'upload_sessions' -and $source.kind -eq 'publishedObject'){$target=[Guid]::Parse($ref.targetEntityId);$ref.targetPath=$ref.shopId.Replace('-','')+'/'+$target.ToString('N')+'.bin'}
            elseif($source.kind -eq 'storagePath'){$relativeSource=[IO.Path]::GetRelativePath($source.root,$sourcePath);if(-not (Contained $source.root $sourcePath)){throw 'Captured storage path escapes its configured root.'};$ref.targetPath=$relativeSource.Replace('\','/')}
            if($actualLength -ne [long]$ref.bytes){throw "Storage length mismatch: $($ref.table)/$($ref.id), metadata=$($ref.bytes), file=$actualLength. Recovery cannot certify inconsistent source bytes."}
            $hash=Sha $sourcePath; if($ref.declaredSha256 -and $hash -ne $ref.declaredSha256.ToLowerInvariant()){throw 'Finalized storage content differs from its persisted digest.'}
            $dest=Owned ([IO.Path]::GetFullPath($ref.bundlePath,$Bundle)); $null=New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force; $null=CanonicalPath $dest
            Copy-Item -LiteralPath $sourcePath -Destination $dest
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
    $signerAssembly=Owned (Required 'CENTRAL_RECOVERY_PROTECTED_SIGNER_ASSEMBLY')
    $apiAssembly=Owned (Required 'CENTRAL_RECOVERY_API_ASSEMBLY')
    foreach($assemblyPath in @($signerAssembly,$apiAssembly)){if(-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf) -or [IO.Path]::GetExtension($assemblyPath) -ne '.dll'){throw 'Recovery requires existing owned Central .NET assemblies for protected-key and hosted-worker proof.'}}
    if($RestoreBundlePath) {
        if(-not $ExpectedBundleManifestSha256){throw 'Restore-only requires an externally recorded bundle-manifest SHA-256.'}
        $bundle=Owned $RestoreBundlePath; $bundleManifestPath=CanonicalPath (Join-Path $bundle 'bundle-manifest.json')
        if((Sha $bundleManifestPath) -ne $ExpectedBundleManifestSha256.ToLowerInvariant()){throw 'Backup manifest integrity failed before target creation.'}
        $manifest=Get-Content -LiteralPath $bundleManifestPath -Raw|ConvertFrom-Json
    } else {
        if(-not $QuiescenceProofPath){throw 'Coordinator must provide actual owned-writer quiescence proof before capture.'}
        $proofPath=Owned $QuiescenceProofPath; $proof=ValidateQuiescenceProof $proofPath $sourceDatabase $script:port $processId $binary $data
        $sourceIdentity=Identity $owner -MigrationOwner; $null=Identity $runtime; $privileges=Privileges $runtime; $p=$privileges|ConvertFrom-Json
        if(-not $p.usage -or $p.ownsRelations -or $p.auditMutation){throw 'Runtime source role violates existing schema/audit least privilege.'}
        AssertIdleSource $owner
        $bundle=Join-Path $script:evidence 'backup-bundle'; $null=New-Item -ItemType Directory -Path $bundle
        $tables=@(TableHashes $owner); $tablesWithoutPaths=@(TableHashes $owner -WithoutPaths); $catalog=Catalog $owner; $outbox=Outbox $owner
        $null=Native 'pg_dump.exe' @('--format=custom','--no-owner','--schema=central','--file',(Join-Path $bundle 'central.dump')) $owner
        $references=@(CaptureFiles $owner $bundle)
        $sourceSignerRoot=Owned (Required 'CENTRAL_SIGNER_KEY_STORE_ROOT')
        if(-not (Test-Path -LiteralPath $sourceSignerRoot -PathType Container)){throw 'Configured protected signer key-store root is absent.'}
        $sourceSignerPath=CanonicalPath (Join-Path $sourceSignerRoot 'central-private-keys.dpapi')
        if(-not (Contained $sourceSignerRoot $sourceSignerPath) -or -not (Test-Path -LiteralPath $sourceSignerPath -PathType Leaf)){throw 'Persistent protected signer key file is absent or outside its explicit owned root.'}
        $sourceSignerLock=CanonicalPath (Join-Path $sourceSignerRoot '.central-private-keys.lock')
        if(-not (Test-Path -LiteralPath $sourceSignerLock -PathType Leaf)){throw 'Persistent signer lock file is absent; source-key reopen proof must not create source-root state during capture.'}
        $sourceSignerBytes=[IO.File]::ReadAllBytes($sourceSignerPath)
        if($sourceSignerBytes.Length -lt 32 -or $sourceSignerBytes.Length -gt (1MB+4096)){[CentralRecoveryDpapiBridge029x]::Zero($sourceSignerBytes);throw 'Protected signer ciphertext size is outside the adapter bounds.'}
        $sourceSignerHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($sourceSignerBytes))
        AddDpapiRecoveryBridge
        $sourceSignerProof=GetSignerRootProof $signerAssembly $sourceSignerRoot $null
        $signerBundlePath=Join-Path $bundle 'protected-signer\central-private-keys.dpapi'
        $null=New-Item -ItemType Directory -Path (Split-Path -Parent $signerBundlePath)
        try { [IO.File]::WriteAllBytes($signerBundlePath,$sourceSignerBytes) }
        finally { [CentralRecoveryDpapiBridge029x]::Zero($sourceSignerBytes) }
        if((Sha $sourceSignerPath) -ne $sourceSignerHash -or (Sha $signerBundlePath) -ne $sourceSignerHash){throw 'Protected signer ciphertext changed during recovery capture.'}
        $after=@(TableHashes $owner)
        if((JsonText $tables) -ne (JsonText $after) -or (Catalog $owner) -ne $catalog -or (Outbox $owner) -ne $outbox){throw 'Owned source changed during capture; no consistent recovery proof can be issued.'}
        AssertIdleSource $owner
        $manifest=[ordered]@{format='CENTRAL_DEVELOPMENT_RECOVERY_V3';sourceDatabase=$sourceDatabase;sourceIdentity=$sourceIdentity;sourceMarkerSha256=(Sha $markerPath);quiescenceProofSha256=(Sha $proofPath);processObservations=@($script:processObservations);tables=$tables;tablesWithoutPaths=$tablesWithoutPaths;catalog=$catalog;catalogSha256=(TextHash $catalog);runtimePrivileges=$privileges;outbox=$outbox;fileReferences=$references;protectedSigner=[ordered]@{format='DPAPI_CURRENTUSER_ROOT_BOUND_V1';sourceRoot=$sourceSignerRoot;rootKeyId='central-root-authority-v1';currentUserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;bundlePath='protected-signer/central-private-keys.dpapi';bytes=(Get-Item -LiteralPath $signerBundlePath).Length;sha256=$sourceSignerHash;rootPublicKeyPem=$sourceSignerProof.rootPublicKeyPem;rootPublicKeySha256=$sourceSignerProof.rootPublicKeySha256;plaintextPrivateKeys='NEVER_EXPORTED'};files=@(Get-ChildItem -LiteralPath $bundle -Recurse -File|ForEach-Object {@{path=([IO.Path]::GetRelativePath($bundle,$_.FullName).Replace('\','/'));bytes=$_.Length;sha256=(Sha $_.FullName)}});createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');privateSigningKeys='DPAPI_PROTECTED_CIPHERTEXT_ONLY'}
        $bundleManifestPath=Join-Path $bundle 'bundle-manifest.json'; SaveJson $bundleManifestPath $manifest
    }
    if($manifest.format -ne 'CENTRAL_DEVELOPMENT_RECOVERY_V3' -or $manifest.sourceDatabase -ne $sourceDatabase){throw 'Unsupported backup format or source identity; V2 bundles cannot claim protected signer recovery.'}
    if($RestoreBundlePath) {
        $restoreSourceIdentity=Identity $owner -MigrationOwner; $null=Identity $runtime
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
    # The recovery administrator is intentionally CREATEDB-only and is not a
    # member of the migration role. Create the database as that administrator;
    # pg_restore assigns every schema object to the migration role below.
    $null=Native 'createdb.exe' @('--template','template0',$TargetDatabase) $admin
    $null=Query $admin ('GRANT CONNECT, CREATE, TEMP ON DATABASE '+(Identifier $TargetDatabase)+' TO '+(Identifier $ownerRole)+';')
    $targetOwner=@{Database=$TargetDatabase;Role=$ownerRole;Password=$owner.Password}; $targetRuntime=@{Database=$TargetDatabase;Role=$runtimeRole;Password=$runtime.Password}
    # ACLs are deliberately retained; restored migration history must not conceal missing grants.
    $null=Native 'pg_restore.exe' @('--exit-on-error','--single-transaction','--no-owner','--role',$ownerRole,'--dbname',$TargetDatabase,$archive) $targetOwner
    $targetIdentity=Identity $targetOwner -MigrationOwner; $null=Identity $targetRuntime
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
    if(-not $manifest.protectedSigner -or $manifest.protectedSigner.format -ne 'DPAPI_CURRENTUSER_ROOT_BOUND_V1' -or $manifest.protectedSigner.rootKeyId -ne 'central-root-authority-v1' -or [string]$manifest.protectedSigner.currentUserSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw 'Backup does not contain protected signer material bound to this exact Windows DPAPI CurrentUser identity; legacy or cross-account bundles cannot claim T19 signer recovery.' }
    $signerCipherPath=Owned ([IO.Path]::GetFullPath([string]$manifest.protectedSigner.bundlePath,$bundle))
    if(-not (Contained $bundle $signerCipherPath) -or -not (Test-Path -LiteralPath $signerCipherPath -PathType Leaf) -or (Sha $signerCipherPath) -ne [string]$manifest.protectedSigner.sha256 -or (Get-Item -LiteralPath $signerCipherPath).Length -ne [long]$manifest.protectedSigner.bytes){throw 'Protected signer ciphertext does not match its signed recovery bundle manifest.'}
    $targetSignerRoot=Join-Path $script:evidence 'restored-protected-signer'
    $null=New-Item -ItemType Directory -Path $targetSignerRoot
    $targetSignerRoot=Owned $targetSignerRoot
    $targetSignerStore=Join-Path $targetSignerRoot 'central-private-keys.dpapi'
    AddDpapiRecoveryBridge
    $sourceSignerBytes=[IO.File]::ReadAllBytes($signerCipherPath)
    try {
        $reprotected=[CentralRecoveryDpapiBridge029x]::Reprotect($sourceSignerBytes,[string]$manifest.protectedSigner.sourceRoot,$targetSignerRoot)
        $targetProtectedBytes=$reprotected.ProtectedBytes
        try {
            $stream=[IO.File]::Open($targetSignerStore,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $stream.Write($targetProtectedBytes,0,$targetProtectedBytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        } finally { [CentralRecoveryDpapiBridge029x]::Zero($targetProtectedBytes) }
    } finally { [CentralRecoveryDpapiBridge029x]::Zero($sourceSignerBytes) }
    if((Get-Item -LiteralPath $targetSignerStore).Length -lt 32 -or (Sha $targetSignerStore) -eq [string]$manifest.protectedSigner.sha256){throw 'Protected signer re-protection did not create a distinct target-root DPAPI envelope.'}
    $signerRecoveryProof=GetSignerRootProof $signerAssembly $targetSignerRoot ([string]$manifest.protectedSigner.rootPublicKeyPem)
    if($signerRecoveryProof.rootPublicKeySha256 -cne [string]$manifest.protectedSigner.rootPublicKeySha256){throw 'Reopened signer root fingerprint differs from backup metadata.'}
    $trustRecoveryProof=VerifyPersistedTrustSet $targetRuntime ([string]$manifest.protectedSigner.rootPublicKeyPem) ([string]$manifest.protectedSigner.rootKeyId)
    $probe=[Guid]::NewGuid().ToString('D'); $sql='BEGIN; INSERT INTO central.outbox_messages ("Id","OccurredOnUtc","EventType","AggregateType","AggregateId","PayloadJson","Status","RetryCount") VALUES ('+(Literal $probe)+'::uuid,CURRENT_TIMESTAMP,''central.recovery.probe'',''CentralRecoveryRehearsal'',''synthetic'',''{"synthetic":true}'',''PENDING'',0); SELECT count(*) FROM central.outbox_messages WHERE "Id"='+(Literal $probe)+'::uuid; ROLLBACK;'
    if((Query $targetRuntime $sql).Output -ne '1'){throw 'Restored runtime transactional write/rollback failed.'}
    foreach($sql in @('UPDATE central.audit_records SET "Reason"="Reason" WHERE false;','DELETE FROM central.audit_records WHERE false;','TRUNCATE central.audit_records;')) {
        $denial=Query $targetRuntime ('BEGIN; '+$sql+' ROLLBACK;') -AllowFailure
        if($denial.ExitCode -eq 0 -or $denial.Error -notmatch '42501'){throw 'Restored runtime audit operation was not denied by PostgreSQL privileges.'}
    }
    $outboxReplayProof=ExerciseOutboxReplay $targetRuntime $apiAssembly $targetSignerRoot $objectRoot $paymentRoot (Join-Path $script:evidence 'RESTORED_TARGET_MARKER.json') $data $binary
    $invariants=(Outbox $targetRuntime)|ConvertFrom-Json
    $eligible=$invariants.pending -eq 0 -and $invariants.dispatchedWithoutEffect -eq 0 -and $invariants.effectWithoutMessage -eq 0 -and $invariants.effectPayloadMismatch -eq 0 -and $invariants.invalidTimestamps -eq 0
    $result=[ordered]@{format='CENTRAL_DISPOSABLE_RECOVERY_V3';result=if($eligible){'PASS'}else{'RESTORE_VERIFIED_WITH_INVARIANT_FAILURES'};recoveryAcceptanceEligible=$eligible;sourceDatabase=$sourceDatabase;restoredDatabase=$TargetDatabase;targetIdentity=$targetIdentity;port=$script:port;backupBundle=$bundle;backupBundleManifestSha256=(Sha $bundleManifestPath);backupSha256=(Sha $archive);centralTables=$restored;catalogSha256=(TextHash $restoredCatalog);runtimePrivilegesPreserved=$true;runtimeTransactionalWriteRollback=$true;auditMutationDenied=$true;protectedSigner=[ordered]@{recovered=$true;sourceRootPublicKeySha256=$manifest.protectedSigner.rootPublicKeySha256;targetRootPublicKeySha256=$signerRecoveryProof.rootPublicKeySha256;targetStoreRoot=$targetSignerRoot;reopened=$signerRecoveryProof.rootKeyReopened;syntheticRs256SignatureVerified=$signerRecoveryProof.syntheticSignatureVerified;dpapiPlaintextIntegrityVerified=$true;currentUserSid=$manifest.protectedSigner.currentUserSid};trustSetRecovery=$trustRecoveryProof;outbox=$invariants;outboxWorkerReplay=$outboxReplayProof;restoredObjectStorageRoot=$objectRoot;restoredPaymentStorageRoot=$paymentRoot;storageReferences=$readbacks;completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');scope='Owned Central schema/data/ACL/files restored; protected development signer DPAPI ciphertext reprotected under this Windows user and target root, reopened and used to sign; restored trust-set signature and revocation descriptors verified; actual hosted durable outbox worker restarted twice on the disposable target. External delivery, production keys/providers, upgrades and production cutover remain outside this rehearsal.'}
    SaveJson (Join-Path $script:evidence 'recovery-manifest.json') $result; $result|ConvertTo-Json -Depth 10
    if(-not $eligible){throw 'Restore completed but outbox invariants fail; recovery acceptance is withheld.'}
} catch {
    if($script:evidence -and (Test-Path -LiteralPath $script:evidence -PathType Container)){SaveJson (Join-Path $script:evidence 'failure.json') @{result='FAIL';error=$_.Exception.Message;utc=[DateTimeOffset]::UtcNow.ToString('O')}}
    throw
} finally {
    foreach($hostProcess in $script:recoveryHostProcesses){try{StopRecoveryHost $hostProcess}catch{}}
    if($script:evidence -and (Test-Path -LiteralPath $script:evidence -PathType Container)){SaveJson (Join-Path $script:evidence 'commands.json') @($script:commands)}
    foreach($name in $savedHostEnvironment.Keys) {
        if($null -eq $savedHostEnvironment[$name]) { Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name,$savedHostEnvironment[$name]) }
    }
    foreach($name in $savedPg.Keys) {
        if($null -eq $savedPg[$name]) { Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name,$savedPg[$name]) }
    }
}
