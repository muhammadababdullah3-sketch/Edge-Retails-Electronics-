[CmdletBinding()]
param(
    [ValidateSet('Operational','Rehearsal')][string]$Mode = 'Operational',
    [string]$BackupPath,
    [string]$EvidencePath,
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Never emit native stderr: authentication errors and SQL can contain sensitive text.
function New-Secret {
    $bytes = New-Object byte[] 48
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes); return [Convert]::ToBase64String($bytes) } finally { $rng.Dispose() }
}
function Protect-Path([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point rejected.' }
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    # Construct only a DACL update; do not ask Set-Acl to rewrite SACL/owner.
    if ($item.PSIsContainer) { $acl = [Security.AccessControl.DirectorySecurity]::new() }
    else { $acl = [Security.AccessControl.FileSecurity]::new() }
    $current = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $allowed = @($owner.Value,$current.Value,'S-1-5-18','S-1-5-32-544') | Select-Object -Unique
    if ($owner.Value -in @('S-1-1-0','S-1-5-32-545','S-1-5-11','S-1-5-32-546')) { throw 'Broad owner rejected.' }
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($rule in @($acl.Access)) { $null = $acl.RemoveAccessRuleSpecific($rule) }
    foreach ($sid in $allowed) {
        $identity = [Security.Principal.SecurityIdentifier]::new($sid)
        if ($item.PSIsContainer) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        } else { $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','Allow') }
        $acl.AddAccessRule($rule)
    }
    if ($item.PSIsContainer) { [IO.Directory]::SetAccessControl($Path,$acl) }
    else { [IO.File]::SetAccessControl($Path,$acl) }
    $actual = Get-Acl -LiteralPath $Path
    if (-not $actual.AreAccessRulesProtected -or -not $actual.AreAccessRulesCanonical) { throw 'ACL protection failed.' }
    foreach ($rule in $actual.Access) {
        if ($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowed -or $rule.AccessControlType -ne 'Allow') { throw 'Unexpected ACL principal.' }
    }
}
function Native([string]$Tool,[string]$Arguments,[string]$InputText='', [string]$Password='') {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = Join-Path $PgBin "$Tool.exe"
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    foreach ($key in @($psi.EnvironmentVariables.Keys)) { if ([string]$key -like 'PG*') { $psi.EnvironmentVariables.Remove([string]$key) } }
    $psi.EnvironmentVariables['PGPASSWORD'] = $Password
    $psi.EnvironmentVariables['PGCONNECT_TIMEOUT'] = '10'
    $psi.EnvironmentVariables['PGSSLMODE'] = 'disable'
    $psi.EnvironmentVariables['PGCLIENTENCODING'] = 'UTF8'
    $p = [Diagnostics.Process]::new(); $p.StartInfo = $psi
    try {
        $null = $p.Start()
        $stdout = $p.StandardOutput.ReadToEndAsync(); $stderr = $p.StandardError.ReadToEndAsync()
        $p.StandardInput.Write($InputText); $p.StandardInput.Close()
        if (-not $p.WaitForExit(60000)) { $p.Kill(); throw 'Native operation timed out.' }
        return @{ Code=$p.ExitCode; Out=$stdout.Result; Err=$stderr.Result }
    } finally { $p.Dispose() }
}
function Sql([string]$Password,[string]$Query) {
    return Native 'psql' "-X -w -q -tA -v ON_ERROR_STOP=1 -v VERBOSITY=verbose -h 127.0.0.1 -p $script:port -U $script:role -d $script:database -f -" $Query $Password
}
function GoodSql([string]$Password,[string]$Query) {
    $r = Sql $Password $Query
    if ($r.Code -ne 0) { throw 'Database operation failed; details suppressed.' }
    return $r.Out.Trim()
}
function RejectPassword([string]$Password) {
    $r = Sql $Password 'SELECT 1;'
    if ($r.Code -eq 0 -or $r.Err -notmatch 'password authentication failed') { throw 'Expected password-authentication rejection was not demonstrated.' }
}
function Scram([string]$Password) {
    $salt = New-Object byte[] 16
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($salt) } finally { $rng.Dispose() }
    $derive = [Security.Cryptography.Rfc2898DeriveBytes]::new($Password,$salt,4096,[Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $salted = $derive.GetBytes(32) } finally { $derive.Dispose() }
    $hmac = [Security.Cryptography.HMACSHA256]::new($salted)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $stored = $sha.ComputeHash($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes('Client Key')))
        $server = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes('Server Key'))
        return 'SCRAM-SHA-256$4096:'+[Convert]::ToBase64String($salt)+'$'+[Convert]::ToBase64String($stored)+':'+[Convert]::ToBase64String($server)
    } finally { $hmac.Dispose(); $sha.Dispose(); [Array]::Clear($salted,0,$salted.Length) }
}
function Parse([string]$Value) {
    try { $b = [Data.Common.DbConnectionStringBuilder]::new(); $b.set_ConnectionString($Value); return ,$b }
    catch { throw 'Invalid configuration connection string; details suppressed.' }
}
function Rotate([string]$ConfigPath,[string]$ExpectedData='') {
    $directory = Split-Path -Parent $ConfigPath
    Protect-Path $directory; Protect-Path $ConfigPath
    $pending = Join-Path $directory 'credential-rotation.pending.json'
    $previous = Join-Path $directory 'credential-rotation.previous.json'
    if (Test-Path -LiteralPath $pending) { throw 'Protected pending configuration exists: manual recovery required before retry.' }
    if (Test-Path -LiteralPath $previous) { throw 'Protected previous configuration exists: manual recovery required before retry.' }
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    $b = Parse $config.ConnectionStrings.DefaultConnection
    $alias = Parse $config.EDGE_RETAILS_DB
    if ($b.get_ConnectionString() -cne $alias.get_ConnectionString()) { throw 'Configuration aliases differ.' }
    $script:port = 5432; if ($b.ContainsKey('Port')) { $script:port = [int]$b['Port'] }; $script:role = [string]$b['Username']; $script:database = [string]$b['Database']
    if ($b['Host'] -notin @('127.0.0.1','localhost') -or $role -notmatch '^[A-Za-z_][A-Za-z0-9_]*$' -or $database -notmatch '^[a-z_][a-z0-9_]*$') { throw 'Target identity rejected.' }
    if ($Mode -eq 'Operational' -and ($port -ne 5432 -or $database -ne 'edge_retails_prod')) { throw 'Production identity mismatch.' }
    if ($Mode -eq 'Rehearsal' -and ($port -ne 56943 -or $database -ne 'phase3b_rotation' -or $role -ne 'rotation_fixture')) { throw 'Rehearsal isolation mismatch.' }
    $old = [string]$b['Password']
    if ([string]::IsNullOrEmpty($old)) { throw 'Missing password.' }
    $identity = GoodSql $old "BEGIN READ ONLY; SELECT json_build_object('db',current_database(),'role',current_user,'port',inet_server_port(),'version',current_setting('server_version_num'),'primary',NOT pg_is_in_recovery(),'data',current_setting('data_directory')); COMMIT;" | ConvertFrom-Json
    if ($identity.db -ne $database -or $identity.role -ne $role -or $identity.port -ne $port -or $identity.version -notmatch '^18\d{4}$' -or -not $identity.primary) { throw 'Server identity mismatch.' }
    if ($ExpectedData -and [IO.Path]::GetFullPath($identity.data) -ne $ExpectedData) { throw 'Disposable data directory mismatch.' }
    if ($Mode -eq 'Operational') {
        $history = GoodSql $old 'BEGIN READ ONLY; SELECT count(*)::text || ''|'' || max("MigrationId") FROM system.__ef_migrations_history; COMMIT;'
        if ($history -ne '18|20260929100000_Phase3LegacyCategoryUpgradeRecovery') { throw 'Certified migration state mismatch.' }
    }
    RejectPassword (New-Secret)
    $attributesSql = "BEGIN READ ONLY; SELECT row_to_json(r)::text FROM (SELECT rolname,rolsuper,rolinherit,rolcreaterole,rolcreatedb,rolcanlogin,rolreplication,rolconnlimit,rolvaliduntil,rolbypassrls,rolconfig FROM pg_roles WHERE rolname=current_user) r; SELECT coalesce(json_agg(m ORDER BY m.roleid,m.member)::text,'[]') FROM (SELECT roleid,member,grantor,admin_option,inherit_option,set_option FROM pg_auth_members WHERE member=(SELECT oid FROM pg_roles WHERE rolname=current_user)) m; COMMIT;"
    $before = GoodSql $old $attributesSql
    $new = New-Secret; $verifier = Scram $new
    $b['Password'] = $new; $alias['Password'] = $new
    $config.ConnectionStrings.DefaultConnection = $b.get_ConnectionString(); $config.EDGE_RETAILS_DB = $alias.get_ConnectionString()
    # Parent ACL is already protected. Persist and flush the recovery copy BEFORE ALTER ROLE.
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($config | ConvertTo-Json -Depth 100))
    $stream = [IO.FileStream]::new($pending,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    Protect-Path $pending
    $null = GoodSql $old ('ALTER ROLE "'+$role+'" PASSWORD '''+$verifier+''';')
    $after = GoodSql $new $attributesSql
    if ($before -cne $after) { throw 'Role attributes/memberships changed unexpectedly.' }
    RejectPassword $old
    # Atomic same-volume replacement; pending survives every pre-replacement failure.
    [IO.File]::Replace($pending,$ConfigPath,$previous)
    Protect-Path $ConfigPath
    $saved = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if ($saved.ConnectionStrings.DefaultConnection -cne $saved.EDGE_RETAILS_DB) { throw 'Saved aliases differ.' }
    $savedBuilder = Parse $saved.ConnectionStrings.DefaultConnection
    $null = GoodSql ([string]$savedBuilder['Password']) 'SELECT 1;'
    Remove-Item -LiteralPath $previous -Force
    Write-Output 'PASS: PG18 identity; old-auth preflight; wrong-password authentication rejection; SCRAM rotation; new-auth success; old-password authentication rejection; role attributes/memberships preserved; protected atomic config; matching aliases.'
}
if ($Mode -eq 'Operational') {
    if (-not $Apply) { throw 'Operational rotation requires -Apply.' }
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Elevated administrator required.' }
    foreach ($name in @('EdgeRetailsServer','EdgeRetailsWorker')) {
        $svc = Get-CimInstance Win32_Service -Filter "Name='$name'"
        if (-not $svc -or $svc.State -ne 'Stopped' -or $svc.StartMode -ne 'Disabled') { throw 'Both runtime services must be Stopped/Disabled.' }
        $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$name"
        $environment = (Get-ItemProperty -LiteralPath $serviceKey).PSObject.Properties['Environment']
        if ($environment -and @($environment.Value | Where-Object { $_ -match '^(EDGE_RETAILS_DB|ConnectionStrings__DefaultConnection)=' }).Count) { throw 'Service connection override rejected.' }
    }
    if (Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue) { throw 'Port 7150 must be inactive.' }
    if (Test-Path -LiteralPath 'C:\Windows\System32\config\systemprofile\AppData\Local\EdgeRetails\config.json') { throw 'LocalSystem local configuration requires authority review before rotation.' }
    foreach ($scope in @('Process','User','Machine')) {
        foreach ($key in @('EDGE_RETAILS_DB','ConnectionStrings__DefaultConnection')) {
            if ([Environment]::GetEnvironmentVariable($key,$scope)) { throw 'Environment connection override rejected.' }
        }
    }
    foreach ($key in @('Registry::HKEY_USERS\S-1-5-18\Environment','Registry::HKEY_USERS\.DEFAULT\Environment')) {
        if (Test-Path $key) { $props=Get-ItemProperty $key; foreach ($name in @('EDGE_RETAILS_DB','ConnectionStrings__DefaultConnection')) { if ($props.PSObject.Properties[$name]) { throw 'LocalSystem connection override rejected.' } } }
    }
    if (-not $BackupPath -or (Get-FileHash -LiteralPath $BackupPath -Algorithm SHA256).Hash -ne 'FF2682D091C324EE41F241E4315136B30F4DA6C66E0EC39EFF66864C9834A606') { throw 'Certified Phase3A backup hash mismatch.' }
    Rotate (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'EdgeRetails\config.json')
} else {
    $temp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
    $root = Join-Path $temp ('EdgeRetailsRotation_'+[guid]::NewGuid().ToString('N'))
    $data = Join-Path $root 'data'; $fixturePort = 56943
    if (Get-NetTCPConnection -State Listen -LocalPort $fixturePort -ErrorAction SilentlyContinue) { throw 'Fixture port occupied.' }
    $null = New-Item -ItemType Directory -Path $root
    Protect-Path $root
    try {
        $password = New-Secret
        $pwfile = Join-Path $root 'init.pw'; [IO.File]::WriteAllText($pwfile,$password); Protect-Path $pwfile
        $r = Native 'initdb' "-D `"$data`" --username=rotation_fixture --pwfile=`"$pwfile`" --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale"
        if ($r.Code -ne 0) { throw 'Fixture initdb failed.' }
        Add-Content -LiteralPath "$data\postgresql.conf" -Value @("port = $fixturePort","listen_addresses = '127.0.0.1'",'max_connections = 20')
        # pg_ctl's Windows launcher can leave redirected anonymous pipes open.
        # Use native invocation for this secret-free lifecycle command.
        $launcher = Start-Process -FilePath (Join-Path $PgBin 'pg_ctl.exe') -ArgumentList "-D `"$data`" -l `"$root\postgres.log`" -w start" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$root\start.out" -RedirectStandardError "$root\start.err"
        $null = $launcher.Handle
        if (-not $launcher.WaitForExit(60000) -or $launcher.ExitCode -ne 0) { throw 'Fixture start failed.' }
        $script:port=$fixturePort; $script:role='rotation_fixture'; $script:database='postgres'
        $null = GoodSql $password 'CREATE DATABASE phase3b_rotation;'
        $connection="Host=127.0.0.1;Port=$fixturePort;Database=phase3b_rotation;Username=rotation_fixture;Password=$password"
        @{ConnectionStrings=@{DefaultConnection=$connection};EDGE_RETAILS_DB=$connection;PreservedSetting='fixture'} | ConvertTo-Json | Set-Content -LiteralPath "$root\config.json"
        Rotate "$root\config.json" $data
        if ((Get-Content "$root\config.json" -Raw | ConvertFrom-Json).PreservedSetting -ne 'fixture') { throw 'Unrelated setting changed.' }
        Write-Output 'NEW_COVERAGE: isolated PostgreSQL18 rotation rehearsal PASS; unrelated config preserved.'
    } finally {
        if (Test-Path "$data\PG_VERSION") {
            $r = Native 'pg_ctl' "-D `"$data`" status"
            if ($r.Code -eq 0) { $r = Native 'pg_ctl' "-D `"$data`" -m fast -w stop"; if ($r.Code -ne 0) { throw 'Fixture stop failed; preserved for recovery.' } }
            $r = Native 'pg_ctl' "-D `"$data`" status"
            if ($r.Code -ne 3) { throw 'Fixture stopped status not proven.' }
        }
        if (Get-NetTCPConnection -State Listen -LocalPort $fixturePort -ErrorAction SilentlyContinue) { throw 'Fixture listener remains.' }
        if (-not [IO.Path]::GetFullPath($root).StartsWith($temp+'\EdgeRetailsRotation_',[StringComparison]::OrdinalIgnoreCase) -or (Get-Item $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe cleanup path.' }
        Remove-Item -LiteralPath $root -Recurse -Force
        if (Test-Path $root) { throw 'Fixture residue remains.' }
        Write-Output 'CLEANUP PASS: pg_ctl status=3; loopback listener absent; unique fixture directory removed.'
    }
}
if ($EvidencePath) {
    [ordered]@{ mode=$Mode; result='PASS'; postgresMajor=18; oldAuthenticationBefore=$true; wrongPasswordReason='password authentication failed'; oldPasswordAfterReason='password authentication failed'; newAuthentication=$true; roleAttributesAndMembershipsPreserved=$true; configAliasesConsistent=$true; canonicalAcl=$true; secretInEvidence=$false; utc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
}
