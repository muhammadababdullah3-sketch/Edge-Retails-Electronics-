[CmdletBinding()]
param([string]$EvidenceDirectory)

# Read-only diagnostics. No login attempt, PIN, service change or DB mutation.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$loginRepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $loginRepoRoot ('artifacts\master-remediation-20261002\login-probe-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
}
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath (Join-Path $EvidenceDirectory 'result.json')) { throw 'Existing diagnostic evidence must not be overwritten.' }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$loginRecords = [Collections.Generic.List[object]]::new()
foreach ($loginPath in @('/api/system/ready', '/api/system/version', '/api/auth/accounts')) {
    $loginTimer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $loginResponse = Invoke-WebRequest -Uri ('http://127.0.0.1:7150' + $loginPath) -UseBasicParsing -TimeoutSec 10
        $loginTimer.Stop()
        $loginRecord = [ordered]@{ Command = 'GET http://127.0.0.1:7150' + $loginPath; ExitCode = 0; Passed = 1; Failed = 0; Skipped = 0; DatabaseProvider = 'API observation'; CompletionStatus = 'PASS'; HttpStatus = [int]$loginResponse.StatusCode; ElapsedMs = $loginTimer.ElapsedMilliseconds }
        if ($loginPath -eq '/api/system/ready') {
            $loginBody = $loginResponse.Content | ConvertFrom-Json
            $loginRecord.Readiness = @{ Status = $loginBody.status; CanConnect = $loginBody.canConnect; HasPendingMigrations = $loginBody.hasPendingMigrations; MaintenanceState = $loginBody.maintenanceState }
        }
        $loginRecords.Add([pscustomobject]$loginRecord)
    } catch {
        $loginTimer.Stop()
        $loginRecords.Add([pscustomobject]@{ Command = 'GET http://127.0.0.1:7150' + $loginPath; ExitCode = 1; Passed = 0; Failed = 1; Skipped = 0; DatabaseProvider = 'API observation'; CompletionStatus = 'FAIL'; ElapsedMs = $loginTimer.ElapsedMilliseconds; FailureType = $_.Exception.GetType().Name })
    }
}

$loginSavedPassword = $env:PGPASSWORD
$loginSavedOptions = $env:PGOPTIONS
$loginSavedTimeout = $env:PGCONNECT_TIMEOUT
$loginStage = 'Approved configuration parse'
try {
    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'Phase3ConnectionString.psm1') -Force
    $loginConfig = Get-Content -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'EdgeRetails\config.json') -Raw | ConvertFrom-Json
    $loginConnection = $null
    if ($loginConfig.PSObject.Properties['ConnectionStrings'] -and $loginConfig.ConnectionStrings.PSObject.Properties['DefaultConnection']) {
        $loginConnection = $loginConfig.ConnectionStrings.DefaultConnection
    }
    if ([string]::IsNullOrWhiteSpace($loginConnection) -and $loginConfig.PSObject.Properties['EDGE_RETAILS_DB']) { $loginConnection = $loginConfig.EDGE_RETAILS_DB }
    $loginParts = ConvertFrom-EdgeRetailsNpgsqlConnectionString -ConnectionString $loginConnection
    $loginDbHost = Get-EdgeRetailsConnectionStringValue -Builder $loginParts -Names @('Host', 'Server') -DefaultValue '127.0.0.1'
    if ($loginDbHost -notin @('127.0.0.1', 'localhost', '::1')) { throw 'Approved diagnostic target must be loopback.' }
    $loginDbPort = Get-EdgeRetailsConnectionStringValue -Builder $loginParts -Names @('Port') -DefaultValue '5432'
    $loginDbName = Get-EdgeRetailsConnectionStringValue -Builder $loginParts -Names @('Database', 'Initial Catalog')
    $loginDbUser = Get-EdgeRetailsConnectionStringValue -Builder $loginParts -Names @('Username', 'User ID', 'UserId')
    $env:PGPASSWORD = Get-EdgeRetailsConnectionStringValue -Builder $loginParts -Names @('Password')
    $env:PGOPTIONS = '-c default_transaction_read_only=on -c statement_timeout=5000'
    $env:PGCONNECT_TIMEOUT = '5'
    $loginStage = 'Read-only activity and non-secret credential parameters'
    $loginSql = "SELECT json_build_object('serverVersion',current_setting('server_version_num'),'readOnly',current_setting('default_transaction_read_only'),'activity',(SELECT json_agg(x) FROM (SELECT state,wait_event_type,wait_event,count(*) AS sessions FROM pg_stat_activity WHERE datname=current_database() GROUP BY state,wait_event_type,wait_event) x),'blockedSessions',(SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND cardinality(pg_blocking_pids(pid))>0),'pinIterations',(SELECT json_build_object('minimum',min(pin_iterations),'maximum',max(pin_iterations)) FROM identity.users),'recentSessionCount',(SELECT count(*) FROM identity.user_sessions WHERE started_at > now() - interval '15 minutes'));"
    $loginTimer = [Diagnostics.Stopwatch]::StartNew()
    $loginRaw = & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -A -t -v ON_ERROR_STOP=1 -h $loginDbHost -p $loginDbPort -U $loginDbUser -d $loginDbName -c $loginSql 2>&1
    $loginExit = $LASTEXITCODE
    $loginTimer.Stop()
    if ($loginExit -ne 0) { throw 'Read-only diagnostic failed; raw native output suppressed.' }
    $loginData = (($loginRaw | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) | ConvertFrom-Json
    if ([int]$loginData.serverVersion -lt 180000 -or [int]$loginData.serverVersion -ge 190000 -or $loginData.readOnly -ne 'on') { throw 'Diagnostic provider/read-only validation failed.' }
    $loginRecords.Add([pscustomobject]@{ Command = 'psql fixed read-only aggregate: activity, blockers, iteration range, recent session count'; ExitCode = 0; Passed = 1; Failed = 0; Skipped = 0; DatabaseProvider = 'PostgreSQL 18 / libpq (supporting diagnostic)'; CompletionStatus = 'PASS'; ElapsedMs = $loginTimer.ElapsedMilliseconds; Data = $loginData })
} catch {
    $loginRecords.Add([pscustomobject]@{ Command = 'psql fixed read-only aggregate'; ExitCode = 1; Passed = 0; Failed = 1; Skipped = 0; DatabaseProvider = 'PostgreSQL diagnostic (not verified)'; CompletionStatus = 'FAIL'; Stage = $loginStage; FailureType = $_.Exception.GetType().Name })
} finally {
    $env:PGPASSWORD = $loginSavedPassword
    $env:PGOPTIONS = $loginSavedOptions
    $env:PGCONNECT_TIMEOUT = $loginSavedTimeout
}
$loginResult = [pscustomobject]@{ TimestampUtc = [DateTime]::UtcNow.ToString('O'); AuthenticationAttempted = $false; CredentialsExposed = $false; OperationalMutations = $false; Commands = @($loginRecords.ToArray()) }
$loginResult | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'result.json') -Encoding UTF8
$loginResult | ConvertTo-Json -Depth 8
if (@($loginRecords | Where-Object { $_.CompletionStatus -ne 'PASS' }).Count -gt 0) { exit 1 }
exit 0
