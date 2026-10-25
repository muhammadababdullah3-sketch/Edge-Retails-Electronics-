[CmdletBinding()]
param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [ValidateRange(55000, 55999)][int]$Port = 55437,
    [string]$EvidenceDirectory,
    [switch]$RunIntegrationTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = 'C:\Users\muham\OneDrive\Desktop\Point of Sale'
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$runRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('EdgeRetailsCatalogPg_ER-RUN-' + [Guid]::NewGuid().ToString('N'))))
$dataRoot = Join-Path $runRoot 'data'
$passwordFile = Join-Path $runRoot 'admin.pw'
$logDir = if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) { Join-Path $repoRoot ('governance\10-execution-runs\ER-RUN-CATALOG-CORRECTION-20261021-107\test-results\pg18-' + $timestamp) } else { [IO.Path]::GetFullPath($EvidenceDirectory) }
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$commandLog = Join-Path $logDir 'catalog-postgres-rehearsal.log'
$records = [Collections.Generic.List[object]]::new()
$started = $false
$failure = $false
$cleanupPass = $false
$providerVerified = $false
$envNames = @('EDGE_RETAILS_DB','EDGE_RETAILS_TEST_DB','EDGE_RETAILS_TEST_DB_HOST','EDGE_RETAILS_TEST_DB_PORT','EDGE_RETAILS_TEST_DB_NAME','EDGE_RETAILS_TEST_DB_USER','EDGE_RETAILS_TEST_DB_PASSWORD','EDGE_RETAILS_TEST_DB_MAINT_USER','EDGE_RETAILS_TEST_DB_MAINT_PASSWORD','EDGE_RETAILS_TEST_DB_MAINT_DATABASE','EDGE_RETAILS_MASTER_PG_RUN_ROOT','EDGE_RETAILS_PG_BIN','PGPASSWORD')
$oldEnv = @{}
foreach($name in $envNames){ $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }

function Add-RunRecord([string]$Name,[int]$ExitCode,[string]$Status) {
    $records.Add([pscustomobject]@{Step=$Name;ExitCode=$ExitCode;Status=$Status;Utc=[DateTime]::UtcNow.ToString('o')})
    "$($records[-1].Utc) Step=$Name ExitCode=$ExitCode Status=$Status" | Add-Content -LiteralPath $commandLog
}
function Assert-OwnedRoot {
    $prefix = $tempRoot + '\EdgeRetailsCatalogPg_'
    if (-not $runRoot.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFullPath($dataRoot) -ne (Join-Path $runRoot 'data')) { throw 'CAT_PG_ROOT_ESCAPE' }
}
function New-Secret {
    $bytes = [byte[]]::new(32)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes); return ([Convert]::ToBase64String($bytes)).TrimEnd('=').Replace('+','A').Replace('/','B') }
    finally { $rng.Dispose() }
}
function Invoke-CheckedNative([string]$Name,[string]$Exe,[string[]]$Arguments,[string]$OutputPath=$commandLog) {
    $stdoutTemp = [IO.Path]::GetTempFileName()
    $stderrTemp = [IO.Path]::GetTempFileName()
    try {
        $quotedArgs = $Arguments | ForEach-Object {
            if ($_ -match '\s' -and -not ($_.StartsWith('"') -and $_.EndsWith('"'))) {
                "`"$_`""
            } else {
                $_
            }
        }
        $p = Start-Process -FilePath $Exe -ArgumentList $quotedArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdoutTemp -RedirectStandardError $stderrTemp
        $null = $p.Handle
        $p.WaitForExit()
        $p.Refresh()
        $code = $p.ExitCode
        Get-Content -LiteralPath $stdoutTemp, $stderrTemp -ErrorAction SilentlyContinue | Add-Content -LiteralPath $OutputPath
        Add-RunRecord $Name $code $(if($code -eq 0){'PASS'}else{'FAIL'})
        if($code -ne 0){ throw "CAT_PG_STEP_FAILED:${Name}:${code}" }
    }
    finally {
        Remove-Item -LiteralPath $stdoutTemp, $stderrTemp -Force -ErrorAction SilentlyContinue
    }
}

try {
    Assert-OwnedRoot
    foreach($tool in @('initdb.exe','pg_ctl.exe','pg_isready.exe','psql.exe','createdb.exe')) {
        if(-not (Test-Path -LiteralPath (Join-Path $PgBin $tool))) { throw "PG18_TOOL_MISSING:$tool" }
    }
    $initdbVersion = & (Join-Path $PgBin 'initdb.exe') --version
    if($LASTEXITCODE -ne 0 -or $initdbVersion -notmatch '\b18\.') { throw 'PG18_BINARY_VERSION_NOT_18' }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
    if($listeners.Count -gt 0) { throw "CAT_PG_PORT_ALREADY_LISTENING:$Port" }
    foreach($protected in @(5432,7150)) {
        $found = @(Get-NetTCPConnection -State Listen -LocalPort $protected -ErrorAction SilentlyContinue)
        "ProtectedPort=$protected ListenerCount=$($found.Count); no connection attempted" | Add-Content -LiteralPath $commandLog
    }
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = Get-Acl -LiteralPath $runRoot
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @($ownerSid,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $runRoot -AclObject $acl
    $adminUser = 'er_cat_admin'
    $testUser = 'er_provisioner'
    $database = 'edgeretails_test_cat107'
    $adminPassword = New-Secret
    $testPassword = New-Secret
    [IO.File]::WriteAllText($passwordFile,$adminPassword,[Text.UTF8Encoding]::new($false))
    # Default PostgreSQL 18 cluster configuration is preserved deliberately (data page checksums,
    # wal_log_hints and autovacuum all left at their defaults). The dry-run no-write property is
    # asserted by persistent-state comparison, not by suppressing WAL generation, so the rehearsal
    # must not doctor the engine to make a weaker oracle pass.
    Invoke-CheckedNative 'initdb-owned-cluster' (Join-Path $PgBin 'initdb.exe') @('-D',$dataRoot,"--username=$adminUser","--pwfile=$passwordFile",'--auth-host=scram-sha-256','--auth-local=scram-sha-256','--encoding=UTF8','--no-locale')
    Add-Content -LiteralPath (Join-Path $dataRoot 'postgresql.conf') -Value @('',"port = $Port","listen_addresses = '127.0.0.1'",'max_connections = 50')
    $startLog = Join-Path $runRoot 'postgres.log'
    $startOut = Join-Path $runRoot 'pg-start.stdout.log'
    $startErr = Join-Path $runRoot 'pg-start.stderr.log'
    $startArgs = '-D "' + $dataRoot + '" -l "' + $startLog + '" -w start'
    $startProc = Start-Process -FilePath (Join-Path $PgBin 'pg_ctl.exe') -ArgumentList $startArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput $startOut -RedirectStandardError $startErr
    $null = $startProc.Handle
    $startProc.WaitForExit()
    $startProc.Refresh()
    $startExit = $startProc.ExitCode
    Get-Content -LiteralPath $startOut, $startErr -ErrorAction SilentlyContinue | Add-Content -LiteralPath $commandLog
    Add-RunRecord 'pgctl-start-owned-cluster' $startExit $(if($startExit -eq 0){'PASS'}else{'FAIL'})
    if($startExit -ne 0){ throw 'CAT_PG_START_FAILED' }
    $started = $true
    $env:PGPASSWORD = $adminPassword
    Invoke-CheckedNative 'create-owned-database' (Join-Path $PgBin 'createdb.exe') @('-h','127.0.0.1','-p',"$Port",'-U',$adminUser,$database)
    $version = & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $database -tA -c 'SHOW server_version_num'
    if($LASTEXITCODE -ne 0 -or [int]$version -lt 180000 -or [int]$version -ge 190000) { throw 'CAT_PG_SERVER_VERSION_NOT_18' }
    $dataDirectory = & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $adminUser -d $database -tA -c 'SHOW data_directory'
    if($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($dataDirectory).TrimEnd('\') -ne [IO.Path]::GetFullPath($dataRoot).TrimEnd('\')) { throw 'CAT_PG_DATA_DIRECTORY_MISMATCH' }
    $ownerFile = Join-Path $runRoot 'OWNERSHIP_MARKER.json'
    [pscustomobject]@{marker='ER_DISPOSABLE_CLUSTER_OWNERSHIP_MARKER';port=$Port;run_id='ER-RUN-CATALOG-CORRECTION-20261021-107';isolation_mode='owned_disposable_local_cluster';server_version_num=[int]$version;data_directory=$dataDirectory;database=$database;runtime_role=$testUser} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ownerFile -Encoding utf8
    $roleSql = "CREATE ROLE $testUser LOGIN PASSWORD '$testPassword' NOSUPERUSER NOCREATEDB NOCREATEROLE INHERIT NOREPLICATION NOBYPASSRLS; GRANT ALL PRIVILEGES ON DATABASE $database TO $testUser; ALTER DATABASE $database OWNER TO $testUser; GRANT ALL ON SCHEMA public TO $testUser; GRANT pg_read_all_settings TO $testUser; GRANT pg_checkpoint TO $testUser;"
    $roleFile = Join-Path $runRoot 'create-test-role.sql'
    [IO.File]::WriteAllText($roleFile,$roleSql,[Text.UTF8Encoding]::new($false))
    Invoke-CheckedNative 'create-test-role' (Join-Path $PgBin 'psql.exe') @('-h','127.0.0.1','-p',"$Port",'-U',$adminUser,'-d',$database,'-v','ON_ERROR_STOP=1','-f',$roleFile)
    Remove-Item -LiteralPath $roleFile -Force

    $adminConn = "Host=127.0.0.1;Port=$Port;Database=$database;Username=$adminUser;Password=$adminPassword"
    $testConn = "Host=127.0.0.1;Port=$Port;Database=$database;Username=$testUser;Password=$testPassword"
    $env:EDGE_RETAILS_DB = $adminConn
    $env:EDGE_RETAILS_TEST_DB = $testConn
    $env:EDGE_RETAILS_TEST_DB_HOST = '127.0.0.1'
    $env:EDGE_RETAILS_TEST_DB_PORT = "$Port"
    $env:EDGE_RETAILS_TEST_DB_NAME = $database
    $env:EDGE_RETAILS_TEST_DB_USER = $testUser
    $env:EDGE_RETAILS_TEST_DB_PASSWORD = $testPassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_USER = $adminUser
    $env:EDGE_RETAILS_TEST_DB_MAINT_PASSWORD = $adminPassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_DATABASE = $database
    $env:EDGE_RETAILS_MASTER_PG_RUN_ROOT = $runRoot
    $env:EDGE_RETAILS_PG_BIN = $PgBin

    # Migrations run only against this just-created owned database; never use operational endpoints.
    Push-Location -LiteralPath $repoRoot
    try {
        Invoke-CheckedNative 'apply-ef-migrations-owned-db' 'dotnet' @('ef','database','update','--project','src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj','--startup-project','src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj','--context','EdgeRetailsDbContext','--connection',$adminConn)
        Invoke-CheckedNative 'verify-ef-model-sync' 'dotnet' @('ef','migrations','has-pending-model-changes','--project','src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj','--startup-project','src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj','--context','EdgeRetailsDbContext')

        # Grant test role full privileges on all migrated schemas and objects, and assign object ownership
        $grantSql = @'
DO $$ DECLARE s record; t record; seq record; BEGIN
  FOR s IN SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg_toast%' AND nspname NOT LIKE 'pg_temp%' AND nspname <> 'information_schema' LOOP
    EXECUTE format('GRANT USAGE, CREATE ON SCHEMA %I TO __TEST_USER__', s.nspname);
    EXECUTE format('GRANT ALL ON ALL TABLES IN SCHEMA %I TO __TEST_USER__', s.nspname);
    EXECUTE format('GRANT ALL ON ALL SEQUENCES IN SCHEMA %I TO __TEST_USER__', s.nspname);
    EXECUTE format('GRANT ALL ON ALL ROUTINES IN SCHEMA %I TO __TEST_USER__', s.nspname);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE __ADMIN_USER__ IN SCHEMA %I GRANT ALL ON TABLES TO __TEST_USER__', s.nspname);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE __ADMIN_USER__ IN SCHEMA %I GRANT ALL ON SEQUENCES TO __TEST_USER__', s.nspname);
  END LOOP;
  FOR t IN SELECT schemaname, tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema') LOOP
    EXECUTE format('ALTER TABLE %I.%I OWNER TO __TEST_USER__', t.schemaname, t.tablename);
  END LOOP;
  FOR seq IN SELECT sequence_schema, sequence_name FROM information_schema.sequences WHERE sequence_schema NOT IN ('pg_catalog', 'information_schema') LOOP
    EXECUTE format('ALTER SEQUENCE %I.%I OWNER TO __TEST_USER__', seq.sequence_schema, seq.sequence_name);
  END LOOP;
  FOR s IN SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg_toast%' AND nspname NOT LIKE 'pg_temp%' AND nspname NOT IN ('information_schema', 'pg_catalog') LOOP
    EXECUTE format('ALTER SCHEMA %I OWNER TO __TEST_USER__', s.nspname);
  END LOOP;
END $$;
GRANT __ADMIN_USER__ TO __TEST_USER__;
'@.Replace('__TEST_USER__', $testUser).Replace('__ADMIN_USER__', $adminUser)
        $grantFile = Join-Path $runRoot 'grant-test-role.sql'
        [IO.File]::WriteAllText($grantFile,$grantSql,[Text.UTF8Encoding]::new($false))
        Invoke-CheckedNative 'grant-test-role-migrated-schemas' (Join-Path $PgBin 'psql.exe') @('-h','127.0.0.1','-p',"$Port",'-U',$adminUser,'-d',$database,'-v','ON_ERROR_STOP=1','-f',$grantFile)
        Remove-Item -LiteralPath $grantFile -Force

        # No artificial VACUUM/CHECKPOINT quiescence is performed. That step previously existed only
        # to stabilise a cluster-wide LSN equality assertion, which is diagnostic and not proof.
        # The preview's no-write property is asserted by persistent-state comparison inside the
        # product code, so the rehearsal must exercise an ordinary freshly migrated database.

        # The CAT integration tests themselves call the repository's live target guard
        # before opening a connection; no placeholder or nonexistent guard-test class.
        if($RunIntegrationTests){
            Invoke-CheckedNative 'run-catalog-postgres-integration' 'dotnet' @('test','tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj','--configuration','Release','--filter','FullyQualifiedName~GoldenLifecycleTracePostgresTests|FullyQualifiedName~CatalogV3SeedLifecyclePostgresTests|FullyQualifiedName~CatalogV3ImportAtomicityPostgresTests|FullyQualifiedName~CatalogReferenceUpgradeApiContractTests|FullyQualifiedName~CatalogMutationRouteAuthorizationMatrixTests|FullyQualifiedName~Phase11DurablePostingIntegrationTests|FullyQualifiedName~ThakaPostgresTransactionIntegrationTests|FullyQualifiedName~PackConcurrencyPostgresTests|FullyQualifiedName~ShopTimezoneBoundaryPostgresTests','--logger','trx','--results-directory',$logDir)
        }
    }
    finally { Pop-Location }
    $providerVerified = $true
}
catch {
    $failure = $true
    ('FailureType=' + $_.Exception.GetType().FullName + '; Message=' + $_.Exception.Message) | Add-Content -LiteralPath $commandLog
    Write-Output 'CAT_PG_REHEARSAL_FAILED; inspect safe logs and retain blocker.'
}
finally {
    if($started){
        $stopOut = Join-Path $runRoot 'pg-stop.stdout.log'
        $stopErr = Join-Path $runRoot 'pg-stop.stderr.log'
        $stopArgs = '-D "' + $dataRoot + '" -w -m fast stop'
        $stopProc = Start-Process -FilePath (Join-Path $PgBin 'pg_ctl.exe') -ArgumentList $stopArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput $stopOut -RedirectStandardError $stopErr
        $null = $stopProc.Handle
        $stopProc.WaitForExit()
        $stopProc.Refresh()
        $stopExit = $stopProc.ExitCode
        Get-Content -LiteralPath $stopOut, $stopErr -ErrorAction SilentlyContinue | Add-Content -LiteralPath $commandLog
        Add-RunRecord 'pgctl-stop-owned-cluster' $stopExit $(if($stopExit -eq 0){'PASS'}else{'FAIL'})
        $statusProc = Start-Process -FilePath (Join-Path $PgBin 'pg_ctl.exe') -ArgumentList @('-D', $dataRoot, 'status') -WindowStyle Hidden -PassThru
        $null = $statusProc.Handle
        $statusProc.WaitForExit()
        $statusProc.Refresh()
        $statusExit = $statusProc.ExitCode
        if($statusExit -ne 3 -or (Test-Path (Join-Path $dataRoot 'postmaster.pid'))){$failure=$true; Add-RunRecord 'verify-cluster-stopped' $statusExit 'FAIL'} else { Add-RunRecord 'verify-cluster-stopped' 0 'PASS' }
    }
    if(-not $failure -and (Test-Path $runRoot)){ Remove-Item -LiteralPath $runRoot -Recurse -Force; $cleanupPass = -not (Test-Path $runRoot) }
    foreach($name in $envNames){[Environment]::SetEnvironmentVariable($name,$oldEnv[$name],'Process')}
    [pscustomobject]@{provider='PostgreSQL 18 / Npgsql';provider_verified=$providerVerified;status=$(if($failure){'FAIL'}else{'PASS'});cleanup_pass=$cleanupPass;port=$Port;database=$database;role=$testUser;records=$records.ToArray()} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $logDir 'terminal-result.json') -Encoding utf8
}
if($failure){exit 1}else{Write-Output 'CAT_PG_REHEARSAL_TERMINAL';exit 0}

