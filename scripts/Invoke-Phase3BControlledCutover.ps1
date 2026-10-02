[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$result = [ordered]@{ Status='RUNNING'; Stage='Preflight'; StartedUtc=[DateTime]::UtcNow.ToString('o'); Provider='PostgreSQL 18 / Npgsql' }
$maintenance = $false
$evidenceCreated = $false
function Save-State { $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'phase3b-result.json') -Encoding UTF8 }
function Set-Stage([string]$stage) { $result.Stage=$stage; Save-State; Write-Output "PHASE3B_STAGE=$stage" }
function Read-Database([string]$sql) {
    $config = Get-Content -LiteralPath 'C:\ProgramData\EdgeRetails\config.json' -Raw | ConvertFrom-Json
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connection.set_ConnectionString([string]$config.ConnectionStrings.DefaultConnection)
    if ($connection['Host'] -ne '127.0.0.1' -or $connection['Database'] -ne 'edge_retails_prod') { throw 'Unexpected database authority.' }
    if ($connection.ContainsKey('Port') -and [int]$connection['Port'] -ne 5432) { throw 'Unexpected database port.' }
    $savedPassword=$env:PGPASSWORD; $savedTimeout=$env:PGCONNECT_TIMEOUT
    try {
        $env:PGPASSWORD=[string]$connection['Password']; $env:PGCONNECT_TIMEOUT='10'
        $query='BEGIN READ ONLY; '+$sql+'; COMMIT;'
        $output=$query | & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -w -q -tA -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 5432 -U ([string]$connection['Username']) -d edge_retails_prod -f - 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'Read-only database query failed; sensitive output withheld.' }
        return (($output | Where-Object { [string]$_ -match '^\s*\{' } | Select-Object -Last 1) | ConvertFrom-Json)
    } finally { $env:PGPASSWORD=$savedPassword; $env:PGCONNECT_TIMEOUT=$savedTimeout }
}
function Read-Outbox {
    Read-Database "SELECT json_build_object('total',count(*),'pending',count(*) FILTER(WHERE status=1),'processing',count(*) FILTER(WHERE status=2),'failed',count(*) FILTER(WHERE status=4),'action_required',count(*) FILTER(WHERE status=5),'completed',count(*) FILTER(WHERE status=3)) FROM system.outbox_messages"
}
function Assert-Recovery([string]$name) {
    $text=(& sc.exe qfailure $name 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or ([regex]::Matches($text,'RESTART')).Count -lt 3 -or $text -notmatch '60000') { throw 'Service recovery policy mismatch.' }
}
function Get-Ready {
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    do {
        try {
            $response=Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:7150/api/system/ready' -TimeoutSec 3
            $value=$response.Content | ConvertFrom-Json
            if ([int]$response.StatusCode -eq 200 -and $value.status -eq 'Ready' -and $value.canConnect -and -not $value.hasPendingMigrations -and $value.maintenanceState -eq 'Normal') { return $value }
        } catch { }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Server readiness did not pass within 60 seconds.'
}
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Elevated Administrator context required.' }
    if (Test-Path -LiteralPath $evidence) { throw 'Use a new evidence directory for each controlled attempt.' }
    [IO.Directory]::CreateDirectory($evidence) | Out-Null
    $evidenceCreated=$true
    Save-State
    $backup=Join-Path $env:LOCALAPPDATA 'EdgeRetails\Production\backups\post_phase3a_migration_20260929_20260929_184442.dump'
    if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne 'FF2682D091C324EE41F241E4315136B30F4DA6C66E0EC39EFF66864C9834A606') { throw 'Certified backup hash mismatch.' }
    if (@(Get-Process EdgeRetails.Desktop -ErrorAction SilentlyContinue).Count) { throw 'Close the installed Desktop before binary cutover.' }
    if ($env:EDGE_RETAILS_DB) { throw 'Unexpected database environment override.' }
    $release=Join-Path $root 'artifacts\release-1.0.5'
    $manifest=Get-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.version -ne '1.0.5') { throw 'Release version mismatch.' }
    $paths=@{'EdgeRetails.Desktop.exe'='publish\EdgeRetails.Desktop.exe';'EdgeRetails.Server.exe'='publish\server\EdgeRetails.Server.exe';'EdgeRetails.Worker.exe'='publish\worker\EdgeRetails.Worker.exe';'EdgeRetailsSetup.exe'='setup\EdgeRetailsSetup.exe';'EdgeRetailsSetup.msi'='msi\EdgeRetailsSetup.msi'}
    foreach ($name in $paths.Keys) {
        $entries=@($manifest.artifacts | Where-Object File -CEQ $name)
        if ($entries.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $release $paths[$name]) -Algorithm SHA256).Hash -ne $entries[0].Sha256) { throw 'Release artifact hash mismatch.' }
    }
    foreach ($name in @('EdgeRetailsServer','EdgeRetailsWorker')) {
        $service=Get-CimInstance Win32_Service -Filter "Name='$name'"
        $expected=if($name -eq 'EdgeRetailsServer'){'C:\Program Files\Edge Retails\server\EdgeRetails.Server.exe'}else{'C:\Program Files\Edge Retails\worker\EdgeRetails.Worker.exe'}
        if ($null -eq $service -or $service.PathName.Trim('"') -ne $expected -or $service.StartName -ne 'LocalSystem') { throw 'Installed service authority mismatch.' }
        Assert-Recovery $name
    }
    $result.OutboxBefore=Read-Outbox
    if ($result.OutboxBefore.pending -or $result.OutboxBefore.processing -or $result.OutboxBefore.failed -or $result.OutboxBefore.action_required) { throw 'Outbox requires review before cutover.' }
    Set-Stage 'Maintenance'
    $maintenance=$true
    foreach($name in @('EdgeRetailsWorker','EdgeRetailsServer')) {
        Set-Service -Name $name -StartupType Disabled
        if ((Get-Service $name).Status -ne 'Stopped') { Stop-Service -Name $name -ErrorAction Stop }
        (Get-Service $name).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(30))
    }
    if (Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue) { throw 'Listener remains during maintenance.' }
    Set-Stage 'CredentialRotation'
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-Phase3CredentialRotation.ps1') -Mode Operational -BackupPath $backup -EvidencePath (Join-Path $evidence 'credential-rotation.json') -Apply
    if ($LASTEXITCODE -ne 0) { throw 'Credential rotation failed; services remain in maintenance.' }
    Set-Stage 'InstallRelease105'
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-Phase3InstallRelease104.ps1') -ReleaseVersion 1.0.5 -EvidencePath (Join-Path $evidence 'release-1.0.5-install.json')
    if ($LASTEXITCODE -ne 0) { throw 'Release install failed; inspect installer evidence.' }
    Set-Stage 'Server'
    Set-Service EdgeRetailsServer -StartupType Automatic
    Start-Service EdgeRetailsServer
    $result.Readiness=Get-Ready
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-EdgeRetailsServices.ps1') | Tee-Object -FilePath (Join-Path $evidence 'server-health.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Server health script failed.' }
    $server=Get-CimInstance Win32_Service -Filter "Name='EdgeRetailsServer'"
    $listeners=@(Get-NetTCPConnection -State Listen -LocalPort 7150)
    if (-not $listeners.Count -or @($listeners | Where-Object { $_.LocalAddress -ne '127.0.0.1' -or $_.OwningProcess -ne $server.ProcessId }).Count) { throw 'Server port owner/binding mismatch.' }
    $result.ServerPid=$server.ProcessId
    $result.OutboxBeforeWorker=Read-Outbox
    if ($result.OutboxBeforeWorker.pending -or $result.OutboxBeforeWorker.processing -or $result.OutboxBeforeWorker.failed -or $result.OutboxBeforeWorker.action_required) { throw 'Outbox changed before Worker startup.' }
    Set-Stage 'WorkerObservation'
    $workerStart=[DateTime]::Now
    Set-Service EdgeRetailsWorker -StartupType Automatic
    Start-Service EdgeRetailsWorker
    (Get-Service EdgeRetailsWorker).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(30))
    $worker=Get-CimInstance Win32_Service -Filter "Name='EdgeRetailsWorker'"
    $workerProcessId=$worker.ProcessId
    $result.WorkerPid=$workerProcessId
    Save-State
    for($i=0;$i -lt 30;$i++) {
        Start-Sleep -Seconds 3
        $worker=Get-CimInstance Win32_Service -Filter "Name='EdgeRetailsWorker'"
        if ($worker.State -ne 'Running' -or $worker.ProcessId -ne $workerProcessId -or $worker.StartMode -ne 'Auto') { throw 'Worker terminated/restarted during 90-second observation.' }
    }
    $connections=@(Get-NetTCPConnection -State Established -OwningProcess $workerProcessId -RemotePort 5432 -ErrorAction SilentlyContinue)
    if (-not $connections.Count) { throw 'Worker PostgreSQL connection not observed.' }
    $ports=(@($connections.LocalPort | Sort-Object -Unique) -join ',')
    $result.WorkerDatabase=Read-Database "SELECT json_build_object('authenticated_clients',count(*),'outbox_query_observed',coalesce(bool_or(query ILIKE '%outbox_messages%'),false)) FROM pg_stat_activity WHERE datname=current_database() AND usename=current_user AND client_port IN ($ports)"
    if ($result.WorkerDatabase.authenticated_clients -lt 1 -or -not $result.WorkerDatabase.outbox_query_observed) { throw 'Worker authenticated outbox query not observed.' }
    $heartbeat='C:\Windows\System32\config\systemprofile\AppData\Local\EdgeRetails\Production\worker\worker.heartbeat'
    if (-not (Test-Path -LiteralPath $heartbeat) -or (Get-Item -LiteralPath $heartbeat).LastWriteTime -lt $workerStart) { throw 'Worker heartbeat missing/stale.' }
    $result.WorkerHeartbeatUtc=(Get-Item -LiteralPath $heartbeat).LastWriteTimeUtc.ToString('o')
    $errors=@(Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=$workerStart;Level=1,2} -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -match 'EdgeRetails' -or $_.Message -match 'EdgeRetails.Worker' })
    if ($errors.Count) { throw 'Worker/application error event observed; inspect redacted event details.' }
    foreach($name in @('EdgeRetailsServer','EdgeRetailsWorker')) { Assert-Recovery $name }
    $result.WorkerObservationSeconds=90
    $result.RuntimeErrorEvents=$errors.Count
    $result.Readiness=Get-Ready
    $result.OutboxAfter=Read-Outbox
    $result.Status='PASS'; $result.Stage='Complete'; $result.CompletedUtc=[DateTime]::UtcNow.ToString('o'); Save-State
    Write-Output 'PHASE3B_CONTROLLED_CUTOVER_PASS'
    exit 0
} catch {
    if ($maintenance) {
        foreach($name in @('EdgeRetailsWorker','EdgeRetailsServer')) {
            try { Set-Service $name -StartupType Disabled; Stop-Service $name -ErrorAction Stop } catch { }
        }
    }
    $result.Status='FAIL'; $result.FailureType=$_.Exception.GetType().Name; $result.FailureLine=$_.InvocationInfo.ScriptLineNumber; $result.CompletedUtc=[DateTime]::UtcNow.ToString('o')
    if ($evidenceCreated) { Save-State }
    Write-Output "PHASE3B_CONTROLLED_CUTOVER_FAIL Stage=$($result.Stage); sensitive exception details withheld."
    exit 1
}
