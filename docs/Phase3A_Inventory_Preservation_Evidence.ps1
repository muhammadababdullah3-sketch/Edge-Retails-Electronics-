$ErrorActionPreference = 'Stop'
$pg = 'C:\Program Files\PostgreSQL\18\bin'
$backups = Join-Path $env:LOCALAPPDATA 'EdgeRetails\Production\backups'
$archives = @(
 @('pre_migration_20260929_163913.dump','675D0248EF1583C73637AD87A558245F2168B9E3B6C5B9488FC91C65996C907D','inventory_pre'),
 @('post_phase3a_migration_20260929_20260929_184442.dump','FF2682D091C324EE41F241E4315136B30F4DA6C66E0EC39EFF66864C9834A606','inventory_post'))
$ledger = [Collections.Generic.List[string]]::new()
function Run([string]$tool,[string[]]$arguments) {
 $out = & (Join-Path $pg "$tool.exe") @arguments 2>&1
 $code = $LASTEXITCODE
 $ledger.Add("$tool $($arguments -join ' ') => exit $code")
 if($code -ne 0){throw "$tool failed exit $code"}
 return $out
}
$sql = @'
BEGIN READ ONLY;
SELECT json_build_object('database',current_database(),'port',inet_server_port(),'version',current_setting('server_version_num'),'read_only',current_setting('transaction_read_only'),'table_exists',to_regclass('inventory.units') IS NOT NULL,'unit_count',(SELECT count(*) FROM inventory.units));
SELECT jsonb_agg(x ORDER BY name,source) FROM (
SELECT c.conname name,c.conrelid::regclass::text source,c.confrelid::regclass::text target,pg_get_constraintdef(c.oid) definition,c.convalidated valid,
ARRAY(SELECT a.attname FROM unnest(c.conkey) WITH ORDINALITY k(n,i) JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.n ORDER BY i) source_columns,
ARRAY(SELECT a.attname FROM unnest(c.confkey) WITH ORDINALITY k(n,i) JOIN pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.n ORDER BY i) target_columns
FROM pg_constraint c WHERE c.contype='f' AND (c.conrelid='inventory.units'::regclass OR c.confrelid='inventory.units'::regclass)) x;
SELECT format('SELECT json_build_object(''constraint'',%L,''orphans'',count(*)) FROM %s s WHERE %s AND NOT EXISTS (SELECT 1 FROM %s t WHERE %s);', c.conname,c.conrelid::regclass,
string_agg(format('s.%I IS NOT NULL',a.attname),' AND ' ORDER BY k.i),c.confrelid::regclass,
string_agg(format('s.%I=t.%I',a.attname,b.attname),' AND ' ORDER BY k.i))
FROM pg_constraint c CROSS JOIN LATERAL unnest(c.conkey,c.confkey) WITH ORDINALITY k(sn,tn,i)
JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.sn
JOIN pg_attribute b ON b.attrelid=c.confrelid AND b.attnum=k.tn
WHERE c.contype='f' AND (c.conrelid='inventory.units'::regclass OR c.confrelid='inventory.units'::regclass)
GROUP BY c.oid ORDER BY c.conname
\gexec
COMMIT;
'@
function Query([string]$db,[string]$server,[int]$targetPort,[string]$user,[string]$query=$sql){
 $out = $query | & "$pg\psql.exe" -X -w -q -tA -v ON_ERROR_STOP=1 -h $server -p $targetPort -U $user -d $db -f - 2>&1
 $code=$LASTEXITCODE; $ledger.Add("psql -X -w -q -tA -v ON_ERROR_STOP=1 -h $server -p $targetPort -U [redacted] -d $db -f - (BEGIN READ ONLY) => exit $code")
 if($code -ne 0){throw "Read-only SQL failed exit $code"}
 return ,@($out | Where-Object {$_ -match '^\s*[\{\[]'})
}
foreach($a in $archives){
 $path=Join-Path $backups $a[0]
 if((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Archive reparse point'}
 if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $a[1]){throw 'Archive hash mismatch'}
 $ledger.Add("Get-FileHash $path SHA256=$($a[1]) => PASS")
 $null=Run 'pg_restore' @('--list',$path)
 $null=Run 'pg_restore' @('--file','NUL',$path)
}
$temp=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
$root=[IO.Path]::GetFullPath((Join-Path $temp ('EdgeRetailsInventoryClosure_'+[guid]::NewGuid().ToString('N'))))
$prefix=$temp+'\EdgeRetailsInventoryClosure_'
if(-not $root.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe temp root'}
$data=Join-Path $root 'data'; $port=56941
if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){throw 'Port occupied'}
$oldPassword=$env:PGPASSWORD; $oldSsl=$env:PGSSLMODE; $oldTimeout=$env:PGCONNECT_TIMEOUT
$admin='inventory_closure_admin'; $password=[guid]::NewGuid().ToString('N')+[guid]::NewGuid().ToString('N')
$results=@{}
try {
 $null=New-Item -ItemType Directory -Path $root
 [IO.File]::WriteAllText((Join-Path $root 'admin.pw'),$password)
 $null=Run 'initdb' @('-D',$data,"--username=$admin","--pwfile=$root\admin.pw",'--auth-host=scram-sha-256','--auth-local=scram-sha-256','--encoding=UTF8','--no-locale')
 Add-Content -LiteralPath "$data\postgresql.conf" -Value @("port = $port","listen_addresses = '127.0.0.1'",'max_connections = 20')
 & "$pg\pg_ctl.exe" -D $data -l "$root\postgres.log" -w start
 $ledger.Add("pg_ctl -D $data -l $root\postgres.log -w start => exit $LASTEXITCODE")
 if($LASTEXITCODE -ne 0){throw 'Start failed'}
 $env:PGPASSWORD=$password; $env:PGSSLMODE='disable'; $env:PGCONNECT_TIMEOUT='10'
 $identity=Query 'postgres' '127.0.0.1' $port $admin "BEGIN READ ONLY; SELECT json_build_object('data',current_setting('data_directory'),'version',current_setting('server_version_num')); COMMIT;"
 $id=$identity[0]|ConvertFrom-Json
 if([IO.Path]::GetFullPath($id.data) -ne $data -or $id.version -notmatch '^18\d{4}$'){throw 'Isolation identity mismatch'}
 foreach($a in $archives){
  $null=Run 'createdb' @('-h','127.0.0.1','-p',"$port",'-U',$admin,'--template=template0',$a[2])
  $null=Run 'pg_restore' @('--exit-on-error','--single-transaction','--no-owner','--no-privileges','-h','127.0.0.1','-p',"$port",'-U',$admin,'-d',$a[2],(Join-Path $backups $a[0]))
  $results[$a[2]]=Query $a[2] '127.0.0.1' $port $admin
 }
 $config=Get-Content -LiteralPath "$env:ProgramData\EdgeRetails\config.json" -Raw|ConvertFrom-Json
 $runtime=$config.ConnectionStrings.DefaultConnection
 if([string]::IsNullOrWhiteSpace($runtime)){$runtime=$config.EDGE_RETAILS_DB}
 $builder=[System.Data.Common.DbConnectionStringBuilder]::new(); $builder.set_ConnectionString($runtime)
 if($env:EDGE_RETAILS_DB){throw 'Unexpected runtime override'}
 if($builder['Database'] -ne 'edge_retails_prod' -or $builder['Host'] -ne '127.0.0.1'){throw 'Production authority mismatch'}
 $productionPort=5432; if($builder.ContainsKey('Port')){$productionPort=[int]$builder['Port']}
 if($productionPort -eq $port){throw 'Port collision'}
 $env:PGPASSWORD=[string]$builder['Password']
 $results['production']=Query ([string]$builder['Database']) ([string]$builder['Host']) $productionPort ([string]$builder['Username'])
 if(($results.inventory_pre[0]|ConvertFrom-Json).unit_count -gt 0 -and ($results.production[0]|ConvertFrom-Json).unit_count -eq 0){throw 'CRITICAL: pre inventory units lost'}
 if($results.inventory_pre[1] -cne $results.production[1] -or $results.inventory_post[1] -cne $results.production[1]){throw 'FK structure mismatch'}
 foreach($key in $results.Keys){
  $state=$results[$key][0]|ConvertFrom-Json
  if(-not $state.table_exists -or $state.unit_count -ne 0 -or $state.read_only -ne 'on'){throw 'Unexpected inventory state'}
  foreach($fk in ($results[$key][1]|ConvertFrom-Json)){if(-not $fk.valid){throw 'Invalid FK'}}
  foreach($line in $results[$key]|Select-Object -Skip 2){if(($line|ConvertFrom-Json).orphans -ne 0){throw 'Orphans found'}}
 }
 $results|ConvertTo-Json -Depth 4|Set-Content -LiteralPath "$PSScriptRoot\Phase3A_Inventory_Preservation_Raw_2026-09-29.json"
 Write-Output 'INVENTORY STRUCTURE PASS: A=0 B=0 C=0; identical valid FKs; zero orphans'
} finally {
 $env:PGPASSWORD=$oldPassword; $env:PGSSLMODE=$oldSsl; $env:PGCONNECT_TIMEOUT=$oldTimeout
 if(Test-Path -LiteralPath "$data\PG_VERSION"){
  & "$pg\pg_ctl.exe" -D $data status 2>&1|Out-Null
  if($LASTEXITCODE -eq 0){$null=Run 'pg_ctl' @('-D',$data,'-m','fast','-w','stop')}
  & "$pg\pg_ctl.exe" -D $data status 2>&1|Out-Null
  $ledger.Add("pg_ctl -D $data status => exit $LASTEXITCODE (3=stopped)")
  if($LASTEXITCODE -ne 3){throw 'Cluster not confirmed stopped'}
 }
 if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){throw 'Disposable listener remains'}
 if(-not $root.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFullPath($root) -ne $root -or (Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe cleanup path'}
 Remove-Item -LiteralPath $root -Recurse -Force
 $ledger.Add("Cleanup exact root $root => absent=$( -not (Test-Path -LiteralPath $root)); listener $port absent=True")
 $ledger|Set-Content -LiteralPath "$PSScriptRoot\Phase3A_Inventory_Preservation_Commands_2026-09-29.txt"
}
