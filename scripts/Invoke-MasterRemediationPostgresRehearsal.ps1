[CmdletBinding()]
param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [ValidateRange(55000, 55999)][int]$Port = 55640,
    [string]$TestFilter = 'FullyQualifiedName~MasterSupplierProductSequencePostgresTests|FullyQualifiedName~MasterLabelSourcePostgresTests',
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$masterRepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $masterRepoRoot ('artifacts\master-remediation-20261002\postgres-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
}
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$masterCommandLog = Join-Path $EvidenceDirectory 'commands.log'
$masterRecords = [Collections.Generic.List[object]]::new()
$masterStarted = $false
$masterStartAttempted = $false
$masterProviderVerified = $false
$masterFailure = $false
$masterCleanupPass = $false
$masterTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$masterRunRoot = [IO.Path]::GetFullPath((Join-Path $masterTempRoot ('EdgeRetailsMasterPg_' + [Guid]::NewGuid().ToString('N'))))
$masterDataRoot = Join-Path $masterRunRoot 'data'
$masterPasswordFile = Join-Path $masterRunRoot 'admin.pw'
$masterUser = 'er_master_admin'
$masterDatabase = 'edge_retails_master_test'
$masterEnvironmentNames = @(
    'EDGE_RETAILS_DB', 'EDGE_RETAILS_TEST_DB', 'EDGE_RETAILS_TEST_DB_HOST', 'EDGE_RETAILS_TEST_DB_PORT',
    'EDGE_RETAILS_TEST_DB_NAME', 'EDGE_RETAILS_TEST_DB_USER', 'EDGE_RETAILS_TEST_DB_PASSWORD',
    'EDGE_RETAILS_TEST_DB_MAINT_USER', 'EDGE_RETAILS_TEST_DB_MAINT_PASSWORD', 'EDGE_RETAILS_TEST_DB_MAINT_DATABASE',
    'EDGE_RETAILS_HIGHWATER_PATH', 'EDGE_RETAILS_PRODUCTION_STATE_DIR', 'EDGE_RETAILS_BACKUP_KEYRING_PATH',
    'EDGE_RETAILS_MASTER_PG_RUN_ROOT',
    'EDGE_RETAILS_BACKUP_KEY', 'EDGE_RETAILS_BACKUP_DIR', 'EDGE_RETAILS_PG_BIN', 'PGPASSWORD'
)
$masterPreviousEnvironment = @{}
foreach ($masterEnvironmentName in $masterEnvironmentNames) {
    $masterPreviousEnvironment[$masterEnvironmentName] = [Environment]::GetEnvironmentVariable($masterEnvironmentName, 'Process')
}

function Add-MasterCommandRecord {
    param([string]$Command, [int]$ExitCode, $Passed = $null, $Failed = $null, $Skipped = 0,
        [string]$Provider = 'PostgreSQL 18 / Npgsql', [int]$ExpectedExitCode = 0)
    if ($null -eq $Passed) { $Passed = [int]($ExitCode -eq $ExpectedExitCode) }
    if ($null -eq $Failed) { $Failed = [int]($ExitCode -ne $ExpectedExitCode) }
    $masterCommandCompletion = if ($ExitCode -eq $ExpectedExitCode -and $Passed -gt 0 -and $Failed -eq 0 -and $Skipped -eq 0) { 'PASS' } else { 'FAIL' }
    $masterRecords.Add([pscustomobject]@{
        Command = $Command; ExitCode = $ExitCode; Passed = $Passed; Failed = $Failed; Skipped = $Skipped
        DatabaseProvider = $Provider; CompletionStatus = $masterCommandCompletion
    })
    "Command=$Command ExitCode=$ExitCode Passed=$Passed Failed=$Failed Skipped=$Skipped Provider=$Provider CompletionStatus=$masterCommandCompletion" | Add-Content -LiteralPath $masterCommandLog
}

function Assert-MasterOwnedRoot {
    $masterSafePrefix = $masterTempRoot + '\EdgeRetailsMasterPg_'
    if (-not $masterRunRoot.StartsWith($masterSafePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFullPath($masterDataRoot) -ne (Join-Path $masterRunRoot 'data')) {
        throw 'Disposable PostgreSQL root escaped its explicitly owned temporary directory.'
    }
}

function New-MasterRandomHex {
    $masterRandomBytes = [byte[]]::new(32)
    $masterRng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $masterRng.GetBytes($masterRandomBytes)
        return ([BitConverter]::ToString($masterRandomBytes)).Replace('-', '')
    }
    finally { $masterRng.Dispose() }
}

try {
    Assert-MasterOwnedRoot
    foreach ($masterTool in @('initdb.exe', 'pg_ctl.exe', 'pg_isready.exe', 'psql.exe', 'createdb.exe', 'pg_dump.exe', 'pg_restore.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PgBin $masterTool))) { throw "Required PostgreSQL tool is missing: $masterTool" }
    }
    if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
        throw "Requested isolated PostgreSQL port $Port already has a listener; no existing instance will be reused."
    }
    New-Item -ItemType Directory -Path $masterRunRoot | Out-Null
    $masterOwnerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $masterAcl = Get-Acl -LiteralPath $masterRunRoot
    $masterAcl.SetAccessRuleProtection($true, $false)
    foreach ($masterSid in @($masterOwnerSid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $masterAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $masterSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $masterRunRoot -AclObject $masterAcl
    $masterPassword = New-MasterRandomHex
    [IO.File]::WriteAllText($masterPasswordFile, $masterPassword, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PgBin 'initdb.exe') -D $masterDataRoot --username=$masterUser --pwfile=$masterPasswordFile --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8 --no-locale *>> $masterCommandLog
    $masterInitExit = $LASTEXITCODE
    Add-MasterCommandRecord 'initdb owned isolated cluster (password file withheld)' $masterInitExit
    if ($masterInitExit -ne 0) { throw 'Owned isolated initdb failed.' }
    Add-Content -LiteralPath (Join-Path $masterDataRoot 'postgresql.conf') -Value @('', "port = $Port", "listen_addresses = '127.0.0.1'", 'max_connections = 80')
    $masterStartAttempted = $true
    # Windows PowerShell native-pipeline capture can wait on handles inherited by the
    # long-lived postgres child after pg_ctl itself exits. Give pg_ctl file handles
    # directly and wait only for that process; the cluster remains owned and isolated.
    $masterStartOut = Join-Path $masterRunRoot 'pg-start.stdout.log'
    $masterStartErr = Join-Path $masterRunRoot 'pg-start.stderr.log'
    $masterStartArguments = '-D "' + $masterDataRoot + '" -l "' + (Join-Path $masterRunRoot 'postgres.log') + '" -w start'
    $masterStartProcess = Start-Process -FilePath (Join-Path $PgBin 'pg_ctl.exe') -ArgumentList $masterStartArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $masterStartOut -RedirectStandardError $masterStartErr
    $null = $masterStartProcess.Handle
    $masterStartProcess.WaitForExit()
    $masterStartProcess.Refresh()
    $masterStartExit = $masterStartProcess.ExitCode
    if ($null -eq $masterStartExit) { throw 'pg_ctl terminal exit code could not be read.' }
    Get-Content -LiteralPath $masterStartOut, $masterStartErr | Add-Content -LiteralPath $masterCommandLog
    Add-MasterCommandRecord 'pg_ctl start owned isolated cluster' $masterStartExit
    if ($masterStartExit -ne 0) { throw 'Owned PostgreSQL start failed.' }
    $masterStarted = $true
    $env:PGPASSWORD = $masterPassword
    & (Join-Path $PgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $masterUser $masterDatabase *>> $masterCommandLog
    $masterCreateExit = $LASTEXITCODE
    Add-MasterCommandRecord 'createdb edge_retails_master_test in owned cluster' $masterCreateExit
    if ($masterCreateExit -ne 0) { throw 'Owned test database creation failed.' }
    $masterVersion = & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p $Port -U $masterUser -d $masterDatabase -tA -c 'SHOW server_version_num'
    if ($LASTEXITCODE -ne 0 -or [int]$masterVersion -lt 180000 -or [int]$masterVersion -ge 190000) { throw 'PostgreSQL 18 provider could not be established.' }
    Write-Output 'Provider = PostgreSQL 18 / Npgsql'
    $masterProviderVerified = $true
    "Provider = PostgreSQL 18 / Npgsql; server_version_num=$masterVersion; Host=127.0.0.1; Port=$Port; Database=$masterDatabase; OperationalDatabase=UNTOUCHED" | Add-Content -LiteralPath $masterCommandLog
    Add-MasterCommandRecord 'SHOW server_version_num; require PostgreSQL 18' 0

    $masterConnection = "Host=127.0.0.1;Port=$Port;Database=$masterDatabase;Username=$masterUser;Password=$masterPassword"
    $env:EDGE_RETAILS_DB = $masterConnection
    $env:EDGE_RETAILS_TEST_DB = $masterConnection
    $env:EDGE_RETAILS_TEST_DB_HOST = '127.0.0.1'
    $env:EDGE_RETAILS_TEST_DB_PORT = $Port.ToString()
    $env:EDGE_RETAILS_TEST_DB_NAME = $masterDatabase
    $env:EDGE_RETAILS_TEST_DB_USER = $masterUser
    $env:EDGE_RETAILS_TEST_DB_PASSWORD = $masterPassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_USER = $masterUser
    $env:EDGE_RETAILS_TEST_DB_MAINT_PASSWORD = $masterPassword
    $env:EDGE_RETAILS_TEST_DB_MAINT_DATABASE = 'postgres'
    $env:EDGE_RETAILS_HIGHWATER_PATH = Join-Path $masterRunRoot 'highwater\highwater.manifest'
    $env:EDGE_RETAILS_MASTER_PG_RUN_ROOT = $masterRunRoot
    $env:EDGE_RETAILS_PRODUCTION_STATE_DIR = Join-Path $masterRunRoot 'state'
    $env:EDGE_RETAILS_BACKUP_KEYRING_PATH = Join-Path $masterRunRoot 'backup-protection\keys.dpapi'
    $env:EDGE_RETAILS_BACKUP_KEY = New-MasterRandomHex
    $env:EDGE_RETAILS_BACKUP_DIR = Join-Path $masterRunRoot 'backups'
    $env:EDGE_RETAILS_PG_BIN = $PgBin
    if ([string]::IsNullOrWhiteSpace($env:EDGE_RETAILS_DB) -or
        -not [string]::Equals($env:EDGE_RETAILS_DB, $masterConnection, [StringComparison]::Ordinal)) {
        throw 'Canonical design-time factory authority did not select the owned isolated database.'
    }
    Add-MasterCommandRecord 'verify canonical DesignTimeEdgeRetailsDbContextFactory EDGE_RETAILS_DB nonempty exact owned authority; ProgramData fallback not selected' 0
    Push-Location -LiteralPath $masterRepoRoot
    try {
        & dotnet ef database update --project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release *>> $masterCommandLog
        $masterMigrationExit = $LASTEXITCODE
        Add-MasterCommandRecord 'dotnet ef database update --project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release (owned environment authority)' $masterMigrationExit
        if ($masterMigrationExit -ne 0) { throw 'Owned PostgreSQL forward migrations failed.' }
        & dotnet ef migrations has-pending-model-changes --project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release *>> $masterCommandLog
        $masterModelExit = $LASTEXITCODE
        Add-MasterCommandRecord 'dotnet ef migrations has-pending-model-changes --project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --configuration Release' $masterModelExit
        if ($masterModelExit -ne 0) { throw 'Current model alignment failed.' }
        $masterResults = Join-Path $EvidenceDirectory 'test-results'
        New-Item -ItemType Directory -Path $masterResults -Force | Out-Null
        $masterTestErrorPreference = $ErrorActionPreference
        try {
            # Windows PowerShell wraps native stderr (including an expected xUnit RED)
            # as ErrorRecord. Keep consuming output until dotnet has a terminal exit,
            # then evaluate exit code and TRX; never shut down a still-running test DB.
            $ErrorActionPreference = 'Continue'
            & dotnet test tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj --configuration Release --filter $TestFilter --logger trx --results-directory $masterResults *>> $masterCommandLog
            $masterTestExit = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $masterTestErrorPreference }
        $masterTrx = Get-ChildItem -LiteralPath $masterResults -Filter '*.trx' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -eq $masterTrx) {
            Add-MasterCommandRecord "dotnet test IntegrationTests --configuration Release --filter $TestFilter; terminal command has no TRX test execution evidence" $masterTestExit 0 0 0
            throw 'Test command has no terminal TRX evidence.'
        }
        [xml]$masterTrxXml = Get-Content -LiteralPath $masterTrx.FullName -Raw
        $masterCounters = $masterTrxXml.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -eq $masterCounters) { throw 'Test TRX has no terminal counters.' }
        $masterPassed = [int]$masterCounters.GetAttribute('passed')
        $masterFailed = [int]$masterCounters.GetAttribute('failed')
        $masterSkipped = [int]$masterCounters.GetAttribute('notExecuted')
        Add-MasterCommandRecord "dotnet test tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj --configuration Release --filter '$TestFilter' --logger trx --results-directory '$masterResults'" $masterTestExit $masterPassed $masterFailed $masterSkipped
        if ($masterTestExit -ne 0 -or $masterFailed -ne 0 -or $masterSkipped -ne 0 -or $masterPassed -eq 0) { throw 'Required PostgreSQL tests did not all pass with zero skipped.' }
    }
    finally { Pop-Location }
}
catch {
    $masterFailure = $true
    # Type and source line identify harness defects without logging exception text,
    # which may contain authority-bearing process arguments or connections.
    ('SafeFailureType=' + $_.Exception.GetType().FullName + '; ScriptLine=' + $_.InvocationInfo.ScriptLineNumber) | Add-Content -LiteralPath $masterCommandLog
    # Do not emit exceptions containing connections, process arguments or keys.
    Write-Output 'MASTER_POSTGRES_REHEARSAL_FAILED; inspect safe command/TRX evidence.'
}
finally {
    try {
        Assert-MasterOwnedRoot
        if ($masterStartAttempted) {
            & (Join-Path $PgBin 'pg_ctl.exe') -D $masterDataRoot status *>> $masterCommandLog
            $masterStatusExit = $LASTEXITCODE
            if ($masterStatusExit -eq 0) {
                Add-MasterCommandRecord 'pg_ctl status owned start-attempt cluster observed running' $masterStatusExit
                & (Join-Path $PgBin 'pg_ctl.exe') -D $masterDataRoot -w -m fast stop *>> $masterCommandLog
                $masterStopExit = $LASTEXITCODE
                Add-MasterCommandRecord 'pg_ctl -w -m fast stop owned isolated cluster' $masterStopExit
                if ($masterStopExit -ne 0) { throw 'Owned PostgreSQL stop failed; root retained.' }
                & (Join-Path $PgBin 'pg_ctl.exe') -D $masterDataRoot status *>> $masterCommandLog
                $masterStatusExit = $LASTEXITCODE
            }
            Add-MasterCommandRecord 'pg_ctl status require owned cluster no longer running' $masterStatusExit -ExpectedExitCode 3
            if ($masterStatusExit -ne 3 -or (Test-Path -LiteralPath (Join-Path $masterDataRoot 'postmaster.pid'))) { throw 'Owned PostgreSQL terminal shutdown unverified; root retained.' }
        }
        if (Test-Path -LiteralPath $masterRunRoot) { Remove-Item -LiteralPath $masterRunRoot -Recurse -Force }
        $masterCleanupPass = -not (Test-Path -LiteralPath $masterRunRoot)
        if (-not $masterCleanupPass) { throw 'Owned disposable cleanup failed.' }
        Add-MasterCommandRecord 'verify owned root within temp boundary; remove owned temporary fixture after verified shutdown' 0
    }
    catch {
        $masterFailure = $true
        Add-MasterCommandRecord 'owned isolated cluster shutdown and guarded cleanup verification' 1
        Write-Output 'MASTER_POSTGRES_CLEANUP_FAILED; owned root retained where shutdown could not be verified.'
    }
    foreach ($masterEnvironmentName in $masterEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($masterEnvironmentName, $masterPreviousEnvironment[$masterEnvironmentName], 'Process')
    }
    [pscustomobject]@{
        Provider = 'PostgreSQL 18 / Npgsql'; ProviderVerified = $masterProviderVerified
        CompletionStatus = $(if ($masterFailure) { 'FAIL' } else { 'PASS' }); ExitCode = $(if ($masterFailure) { 1 } else { 0 })
        CleanupPass = $masterCleanupPass; Commands = $masterRecords.ToArray()
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'terminal-result.json')
}
if ($masterFailure) { exit 1 }
Write-Output 'MASTER_POSTGRES_REHEARSAL_PASS; Cleanup=PASS'
exit 0
