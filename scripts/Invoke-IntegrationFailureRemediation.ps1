[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$EvidenceDirectory,
 [string]$PgBin='C:\Program Files\PostgreSQL\18\bin',
 [string]$Dotnet="$env:USERPROFILE\.dotnet\dotnet.exe",
 [ValidateSet(55437,55438)][int]$Phase11Port=55437,
 [ValidateRange(55000,55999)][int]$MasterPort=55640,
 [ValidateRange(55000,55999)][int]$ModelPort=55641,
 [string]$TestFilter,
 [string]$GovernanceRoot='C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body',
 [long]$MaximumScratchBytes=335544320
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=Split-Path -Parent $PSScriptRoot
$EvidenceDirectory=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $EvidenceDirectory){throw 'Evidence directory must be new; preserve previous attempts.'}
if(@(@($Phase11Port,$MasterPort,$ModelPort)|Select-Object -Unique).Count -ne 3){throw 'Profiles require distinct ports.'}
foreach($port in @($Phase11Port,$MasterPort,$ModelPort)){
 if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){throw 'A required port is occupied; its owner was not touched.'}
}
$runId=[Guid]::NewGuid().ToString('N')
$runRoot=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('EdgeRetailsMasterPg_'+$runId)))
$safePrefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\EdgeRetailsMasterPg_'
if(-not $runRoot.StartsWith($safePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Owned root escaped temporary boundary.'}
& python -B (Join-Path $GovernanceRoot 'tools/temp_storage_guard.py') --reserve-bytes $MaximumScratchBytes --extra-temp-root $runRoot
if($LASTEXITCODE -ne 0){throw 'Disposable resource budget was not reserved.'}
New-Item -ItemType Directory -Path $EvidenceDirectory|Out-Null
$log=Join-Path $EvidenceDirectory 'commands.log'
$records=[Collections.Generic.List[object]]::new()
$started=[Collections.Generic.List[object]]::new()
$profiles=[Collections.Generic.List[object]]::new()
$oldEnvironment=@{}
$failed=$false
$cleanup=$true
$peakBytes=0L
function Set-RunEnvironment([string]$Name,[string]$Value){
 if(-not $oldEnvironment.ContainsKey($Name)){$oldEnvironment[$Name]=[Environment]::GetEnvironmentVariable($Name,'Process')}
 [Environment]::SetEnvironmentVariable($Name,$Value,'Process')
}
function Record-Step([string]$Name,[int]$Code){
 $records.Add([pscustomobject]@{step=$Name;exit_code=$Code;utc=[DateTime]::UtcNow.ToString('o')})
 Add-Content -LiteralPath $log -Value ($Name+' ExitCode='+$Code)
 if($Code -ne 0){throw ('Step failed: '+$Name)}
}
function Random-Secret{[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))}
function Psql([object]$Profile,[string]$Database,[string]$SqlFile,[string]$Name){
 Set-RunEnvironment 'PGPASSWORD' $Profile.AdminPassword
 $destination=if([IO.Path]::GetFileName($SqlFile) -eq 'roles.sql'){Join-Path $Profile.Root 'private-role-provision.log'}else{$log}
 & (Join-Path $PgBin 'psql.exe') -X -h 127.0.0.1 -p $Profile.Port -U er_master_admin -d $Database -v ON_ERROR_STOP=1 -f $SqlFile *>>$destination
 Record-Step $Name $LASTEXITCODE
}
function Assert-Scratch{
 $bytes=(Get-ChildItem -LiteralPath $runRoot -File -Recurse|Measure-Object Length -Sum).Sum
 $script:peakBytes=[Math]::Max($script:peakBytes,[long]$bytes)
 if($bytes -gt $MaximumScratchBytes){throw 'Owned runtime exceeded reserved maximum; owned clusters will be stopped.'}
 & python -B (Join-Path $GovernanceRoot 'tools/temp_storage_guard.py') --extra-temp-root $runRoot
 if($LASTEXITCODE -ne 0){throw 'Aggregate scratch threshold reached.'}
}
function Capture-Profile([object]$Profile){
 $names=@(@('er_provisioner',$Profile.WritePassword),@('er_master_admin',$Profile.AdminPassword),@('er_master_runtime',$Profile.BackupPassword))
 $query=@'
SELECT json_build_object('database',current_database(),'connected_role',current_user,'port',inet_server_port(),
 'server_version_num',current_setting('server_version_num'),
 'role_attributes',(SELECT row_to_json(r) FROM (SELECT rolsuper,rolinherit,rolcreaterole,rolcreatedb,rolreplication,rolbypassrls FROM pg_roles WHERE rolname=current_user) r),
 'memberships',(SELECT coalesce(json_agg(rolname),'[]'::json) FROM pg_roles WHERE oid<>(SELECT oid FROM pg_roles WHERE rolname=current_user) AND pg_has_role(current_user,oid,'MEMBER')),
 'migration_history_exists',to_regclass('system.__ef_migrations_history') IS NOT NULL);
'@
 foreach($identity in $names){
  Set-RunEnvironment 'PGPASSWORD' $identity[1]
  $raw=& (Join-Path $PgBin 'psql.exe') -X -h 127.0.0.1 -p $Profile.Port -U $identity[0] -d $Profile.Database -tA -v ON_ERROR_STOP=1 -c $query
  if($LASTEXITCODE -ne 0){throw 'Profile identity observation failed.'}
  $fact=$raw|ConvertFrom-Json
  $liveData=$null;$historyCount=$null
  if($identity[0] -eq 'er_master_admin'){
   $liveData=& (Join-Path $PgBin 'psql.exe') -X -h 127.0.0.1 -p $Profile.Port -U $identity[0] -d $Profile.Database -tA -v ON_ERROR_STOP=1 -c 'SHOW data_directory'
   if($LASTEXITCODE -ne 0){throw 'Live data directory observation failed.'}
   if($Profile.Name -eq 'MODEL'){$historyCount=0}else{
    $historyCount=& (Join-Path $PgBin 'psql.exe') -X -h 127.0.0.1 -p $Profile.Port -U $identity[0] -d $Profile.Database -tA -v ON_ERROR_STOP=1 -c 'SELECT count(*) FROM system.__ef_migrations_history'
    if($LASTEXITCODE -ne 0){throw 'Migration history observation failed.'}
   }
  }
  [pscustomobject]@{profile=$Profile.Name;owned_root=$Profile.Root;expected_data_directory=(Join-Path $Profile.Root 'data');observed_data_directory=$liveData;migration_history_count=$historyCount;postmaster_pid=(Get-Content (Join-Path $Profile.Root 'data/postmaster.pid') -First 1);schema_mode=$(if($Profile.Name -eq 'MODEL'){'MODEL_ONLY'}else{'MIGRATED'});observation=$fact}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $EvidenceDirectory ('attestation-'+$Profile.Name+'-'+$identity[0]+'.json')) -Encoding utf8
 }
}
try{
 if(-not(Test-Path -LiteralPath $Dotnet)){throw 'Explicit SDK executable missing.'}
 $Dotnet=[IO.Path]::GetFullPath($Dotnet)
 Push-Location $repo
 try{
  $sdk=(& $Dotnet --version).Trim()
  $expectedSdk=(Get-Content (Join-Path $repo 'global.json') -Raw|ConvertFrom-Json).sdk.version
  if($sdk -ne $expectedSdk){throw 'SDK does not match global.json.'}
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DOTNET' $Dotnet
  Set-RunEnvironment 'PATH' ((Split-Path $Dotnet -Parent)+';'+$env:PATH)
  & $Dotnet build tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj -c Release --no-restore *>>$log
  Record-Step 'Release integration build' $LASTEXITCODE
  & $Dotnet build tests/EdgeRetails.CrashTestHost/EdgeRetails.CrashTestHost.csproj -c Release --no-restore *>>$log
  Record-Step 'Release crash host build' $LASTEXITCODE
  $inputs=[Collections.Generic.List[object]]::new()
  foreach($sourceRoot in @('src','tests/EdgeRetails.CrashTestHost','tests/EdgeRetails.IntegrationTests','.config')){
   $absolute=Join-Path $repo $sourceRoot
   if(Test-Path $absolute){Get-ChildItem -LiteralPath $absolute -File -Recurse|Where-Object{$_.FullName -notmatch '[\\/](bin|obj|\.git|artifacts|TestResults)[\\/]'}|ForEach-Object{
    $inputs.Add([pscustomobject]@{path=[IO.Path]::GetRelativePath($repo,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()})
   }}
  }
  foreach($name in @('global.json','Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.config')){
   $absolute=Join-Path $repo $name
   if(Test-Path $absolute){$inputs.Add([pscustomobject]@{path=$name;sha256=(Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLower()})}
  }
  $hostRoot=Join-Path $repo 'tests/EdgeRetails.CrashTestHost/bin/Release/net10.0-windows'
  $outputs=@(Get-ChildItem -LiteralPath $hostRoot -File -Recurse|ForEach-Object{[pscustomobject]@{path=[IO.Path]::GetRelativePath($repo,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()}})
  $manifest=Join-Path $EvidenceDirectory 'child-host-manifest.json'
  [pscustomobject]@{candidate_root=$repo;configuration='Release';dotnet_sdk_version=$sdk;built_utc=[DateTime]::UtcNow.ToString('o');source_inputs=$inputs.ToArray();output_files=$outputs}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $manifest -Encoding utf8
  Set-RunEnvironment 'EDGE_RETAILS_CRASH_HOST_MANIFEST' $manifest
  New-Item -ItemType Directory -Path $runRoot|Out-Null
  $acl=Get-Acl -LiteralPath $runRoot
  $acl.SetAccessRuleProtection($true,$false)
  foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){
   $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
  }
  Set-Acl -LiteralPath $runRoot -AclObject $acl
  $scratch=Join-Path $runRoot 'scratch'
  New-Item -ItemType Directory -Path $scratch|Out-Null
  Set-RunEnvironment 'TEMP' $scratch
  Set-RunEnvironment 'TMP' $scratch
  foreach($spec in @(@('PHASE11',$Phase11Port,('edgeretails_test_r82_'+$runId)),@('MASTER',$MasterPort,'edge_retails_master_test'),@('MODEL',$ModelPort,'edge_retails_master_test'))){
   $name=[string]$spec[0];$port=[int]$spec[1];$database=[string]$spec[2]
   $root=Join-Path $scratch ('EdgeRetailsMasterPg_'+$runId+'_'+$name)
   New-Item -ItemType Directory -Path $root|Out-Null
   $profile=[pscustomobject]@{Name=$name;Port=$port;Database=$database;Root=$root;AdminPassword=(Random-Secret);WritePassword=(Random-Secret);BackupPassword=(Random-Secret)}
   $profiles.Add($profile)
   $pwfile=Join-Path $root 'admin.pw'
   [IO.File]::WriteAllText($pwfile,$profile.AdminPassword,[Text.UTF8Encoding]::new($false))
   & (Join-Path $PgBin 'initdb.exe') -D (Join-Path $root 'data') --wal-segsize=1 --username=er_master_admin --pwfile=$pwfile --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale *>>$log
   Record-Step ('initdb '+$name) $LASTEXITCODE
   Add-Content (Join-Path $root 'data/postgresql.conf') @("port=$port","listen_addresses='127.0.0.1'","max_connections=60","min_wal_size='2MB'","max_wal_size='16MB'","checkpoint_timeout='30s'")
   $out=Join-Path $root 'start.out';$err=Join-Path $root 'start.err'
   $startArgs='-D "'+(Join-Path $root 'data')+'" -l "'+(Join-Path $root 'postgres.log')+'" -w start'
   $process=Start-Process -FilePath (Join-Path $PgBin 'pg_ctl.exe') -ArgumentList $startArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
   $started.Add($profile)
   $null=$process.Handle
   $process.WaitForExit();$process.Refresh();Record-Step ('start '+$name) $process.ExitCode
   [pscustomobject]@{marker='ER_DISPOSABLE_CLUSTER_OWNERSHIP_MARKER';run_id=$runId+'_'+$name;port=$port;isolation_mode='owned_disposable_local_cluster'}|ConvertTo-Json|Set-Content (Join-Path $root 'OWNERSHIP_MARKER.json') -Encoding utf8
   $sql=Join-Path $root 'roles.sql'
   [IO.File]::WriteAllText($sql,"CREATE ROLE er_provisioner LOGIN PASSWORD '$($profile.WritePassword)' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;`nCREATE ROLE er_master_runtime LOGIN PASSWORD '$($profile.BackupPassword)' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;`nGRANT pg_read_all_settings TO er_provisioner WITH INHERIT TRUE;`nCREATE DATABASE $database;",[Text.UTF8Encoding]::new($false))
   Psql $profile 'postgres' $sql ('create identities/database '+$name)
   $admin="Host=127.0.0.1;Port=$port;Database=$database;Username=er_master_admin;Password=$($profile.AdminPassword);Pooling=false"
   $write="Host=127.0.0.1;Port=$port;Database=$database;Username=er_provisioner;Password=$($profile.WritePassword)"
   Set-RunEnvironment 'EDGE_RETAILS_DB' $admin
   if($name -eq 'MODEL'){
    & $Dotnet ef dbcontext script --project src/EdgeRetails.Infrastructure --startup-project src/EdgeRetails.Infrastructure --context EdgeRetailsDbContext --configuration Release --no-build --output (Join-Path $root 'model.sql') *>>$log
    Record-Step 'generate model-only schema' $LASTEXITCODE
    Psql $profile $database (Join-Path $root 'model.sql') 'initialize model-only profile before tests'
   }else{
    & $Dotnet ef database update --project src/EdgeRetails.Infrastructure --startup-project src/EdgeRetails.Infrastructure --context EdgeRetailsDbContext --configuration Release --no-build *>>$log
    Record-Step ('migrate '+$name) $LASTEXITCODE
   }
   $grant=Join-Path $root 'grants.sql'
   @'
DO $$ DECLARE s record; BEGIN
 FOR s IN SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg_%' AND nspname <> 'information_schema' LOOP
  EXECUTE format('GRANT USAGE ON SCHEMA %I TO er_provisioner, er_master_runtime',s.nspname);
  EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER ON ALL TABLES IN SCHEMA %I TO er_provisioner',s.nspname);
  EXECUTE format('GRANT USAGE,SELECT,UPDATE ON ALL SEQUENCES IN SCHEMA %I TO er_provisioner',s.nspname);
  EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO er_master_runtime',s.nspname);
  EXECUTE format('GRANT SELECT ON ALL SEQUENCES IN SCHEMA %I TO er_master_runtime',s.nspname);
 END LOOP;
END $$;
'@|Set-Content $grant -Encoding utf8
   Psql $profile $database $grant ('grant write and separate backup read '+$name)
   $prefix='EDGE_RETAILS_FIXTURE_'+$name
   Set-RunEnvironment ($prefix+'_DB') $write
   Set-RunEnvironment ($prefix+'_MAINT') "Host=127.0.0.1;Port=$port;Database=postgres;Username=er_master_admin;Password=$($profile.AdminPassword);Pooling=false"
   Set-RunEnvironment ($prefix+'_BACKUP') "Host=127.0.0.1;Port=$port;Database=$database;Username=er_master_runtime;Password=$($profile.BackupPassword)"
   Set-RunEnvironment ($prefix+'_ROOT') $root
   Capture-Profile $profile
   Assert-Scratch
  }
  $default=$profiles[0]
  Set-RunEnvironment 'EDGE_RETAILS_DB' $env:EDGE_RETAILS_FIXTURE_PHASE11_DB
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DB' $env:EDGE_RETAILS_FIXTURE_PHASE11_DB
  Set-RunEnvironment 'EDGE_RETAILS_MASTER_PG_RUN_ROOT' $default.Root
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DB_HOST' '127.0.0.1'
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DB_PORT' $Phase11Port.ToString()
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DB_NAME' $default.Database
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DB_USER' 'er_master_runtime'
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DB_PASSWORD' $default.BackupPassword
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DB_MAINT_USER' 'er_master_admin'
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DB_MAINT_PASSWORD' $default.AdminPassword
  Set-RunEnvironment 'EDGE_RETAILS_TEST_DB_MAINT_DATABASE' 'postgres'
  Set-RunEnvironment 'EDGE_RETAILS_SPRINT8_ALLOW_DESTRUCTIVE_CUTOVER_TEST' 'YES_DISPOSABLE_ONLY'
  Set-RunEnvironment 'EDGE_RETAILS_PG_BIN' $PgBin
  Set-RunEnvironment 'EDGE_RETAILS_HIGHWATER_PATH' (Join-Path $default.Root 'state/highwater.manifest')
  Set-RunEnvironment 'EDGE_RETAILS_PRODUCTION_STATE_DIR' (Join-Path $default.Root 'state')
  Set-RunEnvironment 'EDGE_RETAILS_BACKUP_KEYRING_PATH' (Join-Path $default.Root 'keys.dpapi')
  Set-RunEnvironment 'EDGE_RETAILS_BACKUP_KEY' (Random-Secret)
  Set-RunEnvironment 'EDGE_RETAILS_BACKUP_DIR' (Join-Path $default.Root 'backups')
  $testArgs=@('test','tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj','-c','Release','--no-build','--no-restore','--logger','trx;LogFileName=integration-release.trx','--results-directory',$EvidenceDirectory)
  if($TestFilter){$testArgs+=@('--filter',$TestFilter)}
  $testOutput=Join-Path $repo 'tests/EdgeRetails.IntegrationTests/bin/Release/net10.0'
  $binaryInputs=@(Get-ChildItem -LiteralPath $testOutput -File -Recurse|Where-Object{$_.Name -match '\.(dll|exe|pdb)$|\.(deps|runtimeconfig)\.json$'}|ForEach-Object{[pscustomobject]@{path=[IO.Path]::GetRelativePath($repo,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()}})
  [pscustomobject]@{source_inputs=$inputs.ToArray();integration_binaries=$binaryInputs;crash_host_outputs=$outputs;captured_utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $EvidenceDirectory 'candidate-test-manifest.json') -Encoding utf8
  Assert-Scratch
  $testInfo=[Diagnostics.ProcessStartInfo]::new($Dotnet)
  $testInfo.WorkingDirectory=$repo;$testInfo.UseShellExecute=$false;$testInfo.CreateNoWindow=$true
  $testInfo.RedirectStandardOutput=$true;$testInfo.RedirectStandardError=$true
  foreach($argument in $testArgs){$testInfo.ArgumentList.Add($argument)}
  $testProcess=[Diagnostics.Process]::Start($testInfo)
  $stdout=$testProcess.StandardOutput.ReadToEndAsync();$stderr=$testProcess.StandardError.ReadToEndAsync()
  $timer=[Diagnostics.Stopwatch]::StartNew();$lastAggregate=0L
  try{
   while(-not $testProcess.WaitForExit(500)){
    $bytes=[long](Get-ChildItem -LiteralPath $runRoot -File -Recurse|Measure-Object Length -Sum).Sum
    $peakBytes=[Math]::Max($peakBytes,$bytes)
    if($bytes -ge ($MaximumScratchBytes-16777216)){throw 'Owned runtime reached the reserved budget safety margin.'}
    if($timer.Elapsed.TotalMinutes -gt 60){throw 'Owned test process exceeded its bounded runtime.'}
    if($timer.ElapsedMilliseconds-$lastAggregate -ge 10000){Assert-Scratch *>>$log;$lastAggregate=$timer.ElapsedMilliseconds}
   }
  }finally{
   if(-not $testProcess.HasExited){$testProcess.Kill($true);$testProcess.WaitForExit()}
   $stdout.GetAwaiter().GetResult()|Add-Content -LiteralPath $log
   $stderr.GetAwaiter().GetResult()|Add-Content -LiteralPath $log
   $testExit=$testProcess.ExitCode
   $records.Add([pscustomobject]@{step='Owned test process';pid=$testProcess.Id;elapsed_seconds=$timer.Elapsed.TotalSeconds;peak_scratch_bytes=$peakBytes;reserved_bytes=$MaximumScratchBytes;exit_code=$testExit})
   $testProcess.Dispose()
  }
  foreach($bound in @($inputs.ToArray())+@($binaryInputs)+@($outputs)){
   if((Get-FileHash -LiteralPath (Join-Path $repo $bound.path) -Algorithm SHA256).Hash.ToLower() -ne $bound.sha256){throw 'Candidate input or test binary changed during execution.'}
  }
  $custody=@(Get-ChildItem -LiteralPath $runRoot -File -Recurse|Where-Object{$_.Name -match '^cluster_binding\.json$|^highwater\.manifest(\.checkpoint|\.lock)?$'}|ForEach-Object{[pscustomobject]@{path=[IO.Path]::GetRelativePath($runRoot,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()}})
  $custody|ConvertTo-Json -Depth 4|Set-Content (Join-Path $EvidenceDirectory 'custody-bindings.json') -Encoding utf8
  $trx=Join-Path $EvidenceDirectory 'integration-release.trx'
  if(-not(Test-Path $trx)){throw 'No terminal TRX result produced.'}
  [xml]$xml=Get-Content $trx -Raw
  $counter=$xml.SelectSingleNode("//*[local-name()='Counters']")
  $records.Add([pscustomobject]@{step='IntegrationTests Release';exit_code=$testExit;total=$counter.GetAttribute('total');passed=$counter.GetAttribute('passed');failed=$counter.GetAttribute('failed');not_executed=$counter.GetAttribute('notExecuted');filter=$TestFilter})
  Assert-Scratch
  if($testExit -ne 0 -or [int]$counter.GetAttribute('failed') -ne 0 -or [int]$counter.GetAttribute('notExecuted') -ne 0 -or [int]$counter.GetAttribute('passed') -eq 0){throw 'Required integration results were not all passing.'}
 }finally{Pop-Location}
}catch{
 $failed=$true
 $records.Add([pscustomobject]@{step='Failure';type=$_.Exception.GetType().FullName;line=$_.InvocationInfo.ScriptLineNumber})
 Write-Output 'R82_REHEARSAL_FAILED; inspect sanitized command/TRX records.'
}finally{
 if(Test-Path -LiteralPath $runRoot){$peakBytes=[Math]::Max($peakBytes,[long](Get-ChildItem -LiteralPath $runRoot -File -Recurse|Measure-Object Length -Sum).Sum)}
 for($i=$started.Count-1;$i -ge 0;$i--){
  $profile=$started[$i]
  try{
   & (Join-Path $PgBin 'pg_ctl.exe') -D (Join-Path $profile.Root 'data') status *>>$log
   if($LASTEXITCODE -eq 0){
    & (Join-Path $PgBin 'pg_ctl.exe') -D (Join-Path $profile.Root 'data') -w -m fast stop *>>$log
    if($LASTEXITCODE -ne 0){throw 'Owned shutdown failed.'}
   }
   & (Join-Path $PgBin 'pg_ctl.exe') -D (Join-Path $profile.Root 'data') status *>>$log
   if($LASTEXITCODE -ne 3 -or (Test-Path (Join-Path $profile.Root 'data/postmaster.pid'))){throw 'Owned shutdown unverified.'}
  }catch{$cleanup=$false;$failed=$true}
 }
 if($cleanup -and (Test-Path -LiteralPath $runRoot)){
  $resolved=[IO.Path]::GetFullPath($runRoot)
  if(-not $resolved.StartsWith($safePrefix,[StringComparison]::OrdinalIgnoreCase)){$cleanup=$false;$failed=$true}
  else{Remove-Item -LiteralPath $resolved -Recurse -Force}
 }
 foreach($name in $oldEnvironment.Keys){[Environment]::SetEnvironmentVariable($name,$oldEnvironment[$name],'Process')}
 [pscustomobject]@{status=$(if($failed){'FAIL'}else{'PASS'});cleanup_pass=$cleanup;owned_root=$runRoot;peak_scratch_bytes=$peakBytes;reserved_scratch_bytes=$MaximumScratchBytes;records=$records.ToArray()}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $EvidenceDirectory 'terminal-result.json') -Encoding utf8
}
if($failed){exit 1}
