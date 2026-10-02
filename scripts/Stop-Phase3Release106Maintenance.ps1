#Requires -RunAsAdministrator
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Phase3ConnectionString.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Phase3PostgresAddress.psm1') -Force

function Get-ProductionConnectionTarget {
    $configPath = 'C:\ProgramData\EdgeRetails\config.json'
    if (-not [string]::IsNullOrWhiteSpace($env:EDGE_RETAILS_DB)) { throw 'ENVIRONMENT_DATABASE_OVERRIDE_PRESENT' }
    if (-not [IO.File]::Exists($configPath)) { throw 'PROGRAMDATA_CONFIG_MISSING' }
    $configItem = Get-Item -LiteralPath $configPath -Force
    if (($configItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'PROGRAMDATA_CONFIG_IS_REPARSE_POINT' }
    $acl = Get-Acl -LiteralPath $configPath
    if (-not $acl.AreAccessRulesProtected) { throw 'PROGRAMDATA_CONFIG_ACL_INHERITED' }
    $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    if ($ownerSid -notin @('S-1-5-18', 'S-1-5-32-544')) { throw 'PROGRAMDATA_CONFIG_OWNER_UNTRUSTED' }
    $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
            $rule.IdentityReference.Value -in @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')) {
            throw 'PROGRAMDATA_CONFIG_BROAD_READ_OR_WRITE_ACCESS'
        }
    }
    $adminSid = 'S-1-5-32-544'
    $systemSid = 'S-1-5-18'
    foreach ($sid in @($adminSid, $systemSid)) {
        $fullControl = @($rules | Where-Object {
            $_.IdentityReference.Value -eq $sid -and
            ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -eq [System.Security.AccessControl.FileSystemRights]::FullControl -and
            $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow
        }).Count -gt 0
        if (-not $fullControl) { throw 'PROGRAMDATA_CONFIG_REQUIRED_ACL_MISSING' }
    }
    $config = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($configPath))
    $candidates = @()
    if ($config.PSObject.Properties['EDGE_RETAILS_DB'] -and $config.EDGE_RETAILS_DB -is [string] -and -not [string]::IsNullOrWhiteSpace($config.EDGE_RETAILS_DB)) {
        $candidates += [pscustomobject]@{ Name = 'EDGE_RETAILS_DB'; Value = [string]$config.EDGE_RETAILS_DB }
    }
    if ($config.PSObject.Properties['DatabaseConnectionString'] -and $config.DatabaseConnectionString -is [string] -and -not [string]::IsNullOrWhiteSpace($config.DatabaseConnectionString)) {
        $candidates += [pscustomobject]@{ Name = 'DatabaseConnectionString'; Value = [string]$config.DatabaseConnectionString }
    }
    if ($config.PSObject.Properties['ConnectionStrings'] -and $null -ne $config.ConnectionStrings) {
        foreach ($property in $config.ConnectionStrings.PSObject.Properties) {
            if ($property.Value -is [string] -and -not [string]::IsNullOrWhiteSpace($property.Value)) {
                $candidates += [pscustomobject]@{ Name = ('ConnectionStrings.' + $property.Name); Value = [string]$property.Value }
            }
        }
    }
    if ($candidates.Count -lt 1) { throw 'PROGRAMDATA_CONNECTION_STRING_MISSING' }

    $targets = foreach ($candidate in $candidates) {
        $builder = ConvertFrom-EdgeRetailsNpgsqlConnectionString $candidate.Value
        $hostName = Get-EdgeRetailsConnectionStringValue $builder @('Host', 'Server', 'Data Source') $null
        $database = Get-EdgeRetailsConnectionStringValue $builder @('Database', 'Initial Catalog') $null
        $username = Get-EdgeRetailsConnectionStringValue $builder @('Username', 'User ID', 'User') $null
        $password = Get-EdgeRetailsConnectionStringValue $builder @('Password', 'Pwd') ''
        $port = Get-EdgeRetailsConnectionStringValue $builder @('Port') '5432'
        $sslMode = Get-EdgeRetailsConnectionStringValue $builder @('SSL Mode', 'SslMode') 'Prefer'
        if ($hostName -cne '127.0.0.1' -or $port -cne '5432' -or $database -cne 'edge_retails_prod' -or $username -cne 'er_app_user' -or [string]::IsNullOrWhiteSpace($password)) {
            throw 'PRODUCTION_TARGET_NOT_APPROVED'
        }
        [pscustomobject]@{ Name = $candidate.Name; Host = $hostName; Port = $port; Database = $database; Username = $username; Password = $password; SslMode = $sslMode }
    }

    $first = $targets[0]
    foreach ($target in $targets | Select-Object -Skip 1) {
        if ($target.Host -cne $first.Host -or $target.Port -cne $first.Port -or $target.Database -cne $first.Database -or
            $target.Username -cne $first.Username -or $target.Password -cne $first.Password -or $target.SslMode -cne $first.SslMode) {
            throw 'PROGRAMDATA_CONNECTION_AUTHORITIES_DISAGREE'
        }
    }
    return $first
}

function ConvertTo-LibpqSslMode {
    param([Parameter(Mandatory)][string]$Value)
    $key = $Value.Replace('-', '').Replace('_', '')
    $map = @{ Disable = 'disable'; Allow = 'allow'; Prefer = 'prefer'; Require = 'require'; VerifyCA = 'verify-ca'; VerifyFull = 'verify-full' }
    $matches = @($map.Keys | Where-Object { $_ -ieq $key })
    if ($matches.Count -ne 1) { throw 'CONFIG_SSL_MODE_UNSUPPORTED' }
    return $map[$matches[0]]
}

$postgresService = Get-Service -Name 'postgresql-x64-18' -ErrorAction Stop
$serverService = Get-Service -Name 'EdgeRetailsServer' -ErrorAction Stop
$workerService = Get-Service -Name 'EdgeRetailsWorker' -ErrorAction Stop
if ($postgresService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) { throw 'POSTGRESQL18_SERVICE_NOT_RUNNING' }

$target = Get-ProductionConnectionTarget
$libpqSslMode = ConvertTo-LibpqSslMode $target.SslMode
try {
    $env:PGPASSWORD = $target.Password
    $env:PGSSLMODE = $libpqSslMode
    $env:PGCONNECT_TIMEOUT = '15'
    $env:PGAPPNAME = 'EdgeRetails-Phase3-Upgrade-Drain-Preflight'
    $sql = "SELECT current_database()||'|'||current_user||'|'||coalesce(inet_server_addr()::text,'')||'|'||inet_server_port()||'|'||current_setting('server_version_num')||'|'||(SELECT count(*) FILTER (WHERE status IN (1,2)) FROM system.outbox_messages)||'|'||(SELECT count(*) FILTER (WHERE status=5) FROM system.outbox_messages);"
    $psql = Join-Path $env:ProgramFiles 'PostgreSQL\18\bin\psql.exe'
    if (-not [IO.File]::Exists($psql)) { throw 'POSTGRESQL18_PSQL_MISSING' }
    $result = & $psql -X -A -t -F '|' --no-password --no-psqlrc --set=ON_ERROR_STOP=1 --host $target.Host --port $target.Port --username $target.Username --dbname $target.Database --command $sql
    if ($LASTEXITCODE -ne 0) { throw 'PRODUCTION_OUTBOX_PREFLIGHT_FAILED' }
    $parts = ([string]$result).Trim() -split '\|'
    if ($parts.Count -ne 7) { throw 'PRODUCTION_OUTBOX_PREFLIGHT_SHAPE_INVALID' }
    $serverAddress = ConvertFrom-Phase3PostgresServerAddress $parts[2]
    $serverVersionNum = [int]$parts[4]
    $pending = [int]$parts[5]
    $actionRequired = [int]$parts[6]
    if ($parts[0] -cne $target.Database -or $parts[1] -cne $target.Username -or -not $serverAddress.IsLoopback -or
        -not $serverAddress.HasHostPrefix -or [int]$parts[3] -ne [int]$target.Port -or $serverVersionNum -lt 180000 -or $serverVersionNum -ge 190000) {
        throw 'PRODUCTION_IDENTITY_MISMATCH'
    }
    Write-Output "Provider=PostgreSQL 18 / Npgsql; Production=$($parts[0])/$($parts[1]) at $($serverAddress.NormalizedAddress):$($parts[3]); PendingOutbox=$pending; ActionRequiredOutbox=$actionRequired"
    if ($pending -ne 0 -or $actionRequired -ne 0) { throw 'OUTBOX_NOT_DRAINED' }
}
finally {
    Remove-Item Env:\PGPASSWORD, Env:\PGSSLMODE, Env:\PGCONNECT_TIMEOUT, Env:\PGAPPNAME -ErrorAction SilentlyContinue
    $target.Password = $null
}

if ($workerService.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
    Stop-Service -Name 'EdgeRetailsWorker' -ErrorAction Stop
    $workerService = Get-Service -Name 'EdgeRetailsWorker'
    $workerService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
}
if ((Get-Service -Name 'EdgeRetailsWorker').Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) { throw 'WORKER_DID_NOT_STOP' }

if ((Get-Service -Name 'EdgeRetailsServer').Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
    Stop-Service -Name 'EdgeRetailsServer' -ErrorAction Stop
    $serverService = Get-Service -Name 'EdgeRetailsServer'
    $serverService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
}
if ((Get-Service -Name 'EdgeRetailsServer').Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) { throw 'SERVER_DID_NOT_STOP' }

$listeners = @(Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue)
if ($listeners.Count -ne 0) { throw 'SHOP_SERVER_LOOPBACK_LISTENER_REMAINS' }
if ((Get-Service -Name 'postgresql-x64-18').Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) { throw 'POSTGRESQL18_STOPPED_UNEXPECTEDLY' }
Write-Output 'EDGE_RETAILS_PHASE3_MAINTENANCE_STOP_PASS Server=Stopped Worker=Stopped PostgreSQL18=Running Listener7150=None StartupConfiguration=Unchanged'
