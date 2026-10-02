# Test-EdgeRetailsServices.ps1
# Verify installed binaries, automatic Server service startup, loopback-only binding,
# and the same readiness endpoint used by the Desktop HTTP runtime.

[CmdletBinding()]
param(
    [string]$InstallPath = "C:\Program Files\Edge Retails"
)

$ErrorActionPreference = "Stop"
$failures = [System.Collections.Generic.List[string]]::new()

function Write-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $state = if ($Passed) { "PASS" } else { "FAIL" }
    $color = if ($Passed) { "Green" } else { "Red" }
    Write-Host ("{0,-7} {1}: {2}" -f $state, $Name, $Detail) -ForegroundColor $color
    if (-not $Passed) { $script:failures.Add($Name) }
}

Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host " EDGE RETAILS - LOOPBACK SERVER DEPLOYMENT VERIFICATION" -ForegroundColor Cyan
Write-Host "=======================================================================" -ForegroundColor Cyan

$desktopExe = Join-Path $InstallPath "EdgeRetails.Desktop.exe"
$workerExe = Join-Path $InstallPath "worker\EdgeRetails.Worker.exe"
$serverExe = Join-Path $InstallPath "server\EdgeRetails.Server.exe"

Write-Check "Desktop binary" (Test-Path -LiteralPath $desktopExe -PathType Leaf) $desktopExe
Write-Check "Worker binary" (Test-Path -LiteralPath $workerExe -PathType Leaf) $workerExe
Write-Check "Server binary" (Test-Path -LiteralPath $serverExe -PathType Leaf) $serverExe

$service = Get-CimInstance Win32_Service -Filter "Name='EdgeRetailsServer'" -ErrorAction SilentlyContinue
$serviceExists = $null -ne $service
if ($serviceExists) {
    $autoStart = $service.StartMode -eq "Auto"
    $running = $service.State -eq "Running"
    $expectedPath = [IO.Path]::GetFullPath($serverExe)
    $actualPath = [string]$service.PathName
    $pathMatches = $actualPath.IndexOf($expectedPath, [StringComparison]::OrdinalIgnoreCase) -ge 0
    Write-Check "Server service" $running "state=$($service.State)"
    Write-Check "Automatic startup" $autoStart "startMode=$($service.StartMode)"
    Write-Check "Installed service path" $pathMatches "path matches installed Server binary=$pathMatches"
} else {
    Write-Check "Server service" $false "EdgeRetailsServer is not registered"
    Write-Check "Automatic startup" $false "service registration is unavailable"
    Write-Check "Installed service path" $false "service registration is unavailable"
}

$recoveryOutput = & sc.exe qfailure EdgeRetailsServer 2>&1
$recoveryText = $recoveryOutput -join "`n"
$recoveryConfigured = $LASTEXITCODE -eq 0 -and
    [regex]::Matches($recoveryText, "RESTART", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase).Count -ge 3 -and
    $recoveryText -match "60000"
Write-Check "Failure recovery" $recoveryConfigured "three restart actions with 60-second delay configured=$recoveryConfigured"

$listeners = @()
try {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction Stop)
    $loopbackOnly = $listeners.Count -gt 0
    foreach ($listener in $listeners) {
        $address = $null
        if (-not [Net.IPAddress]::TryParse([string]$listener.LocalAddress, [ref]$address) -or
            -not [Net.IPAddress]::IsLoopback($address)) {
            $loopbackOnly = $false
        }
    }
    $addresses = if ($listeners.Count -gt 0) { ($listeners.LocalAddress | Sort-Object -Unique) -join "," } else { "none" }
    Write-Check "Loopback binding" $loopbackOnly "port=7150 addresses=$addresses"
} catch {
    Write-Check "Loopback binding" $false "listener state could not be verified"
}

$apiReachable = $false
try {
    $versionResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:7150/api/system/version" -Method Get -TimeoutSec 15 -ErrorAction Stop
    $version = $versionResponse.Content | ConvertFrom-Json -ErrorAction Stop
    $apiReachable = [int]$versionResponse.StatusCode -eq 200 -and
        -not [string]::IsNullOrWhiteSpace([string]$version.protocolVersion)
    Write-Check "Desktop API endpoint" $apiReachable "HTTP $([int]$versionResponse.StatusCode) from 127.0.0.1:7150/api/system/version"
} catch {
    Write-Check "Desktop API endpoint" $false "version request failed ($($_.Exception.GetType().Name))"
}

$ready = $false
try {
    $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:7150/api/system/ready" -Method Get -TimeoutSec 15 -ErrorAction Stop
    $ready = [int]$response.StatusCode -eq 200
    Write-Check "Server readiness" $ready "HTTP $([int]$response.StatusCode) from 127.0.0.1:7150/api/system/ready"
    if ($ready) {
        $readiness = $response.Content | ConvertFrom-Json -ErrorAction Stop
        $databaseReady = $readiness.status -eq "Ready" -and
            $readiness.canConnect -eq $true -and
            $readiness.hasPendingMigrations -eq $false -and
            $readiness.maintenanceState -eq "Normal"
        Write-Check "Database readiness" $databaseReady "connected=$($readiness.canConnect); pendingMigrations=$($readiness.hasPendingMigrations); maintenance=$($readiness.maintenanceState)"
    } else {
        Write-Check "Database readiness" $false "readiness endpoint did not return HTTP 200"
    }
} catch {
    $statusCode = $null
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
        $statusCode = [int]$_.Exception.Response.StatusCode
    }
    $detail = if ($null -ne $statusCode) { "HTTP $statusCode" } else { "request failed ($($_.Exception.GetType().Name))" }
    Write-Check "Server readiness" $false $detail
}

if ($failures.Count -gt 0) {
    Write-Host "Deployment verification failed: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "Deployment verification passed. The Shop Server is ready on loopback." -ForegroundColor Green
exit 0
