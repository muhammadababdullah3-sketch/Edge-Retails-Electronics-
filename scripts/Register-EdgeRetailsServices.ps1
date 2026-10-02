# Register-EdgeRetailsServices.ps1
# Authoritative script for registering Edge Retails Windows background services
# Ensures correct executable paths within the release directory layout

[CmdletBinding()]
param(
    [string]$InstallPath = "C:\Program Files\Edge Retails",
    [switch]$RegisterWorker,
    [switch]$RegisterServer,
    [int]$ServerPort = 7150
)

$ErrorActionPreference = "Stop"

if (-not $RegisterWorker -and -not $RegisterServer) {
    throw "Select at least one explicit role: -RegisterWorker and/or -RegisterServer."
}

if ($RegisterServer -and $ServerPort -ne 7150) {
    throw "The single-machine Shop Server is restricted to loopback port 7150."
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Register-EdgeRetailsServices.ps1 requires elevated administrative privileges. Please run from an elevated PowerShell window."
}

Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host " EDGE RETAILS - SERVICE REGISTRATION" -ForegroundColor Cyan
Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host "Install Path: $InstallPath"

if ($RegisterWorker) {
    $workerExe = Join-Path $InstallPath "worker\EdgeRetails.Worker.exe"
    Write-Host "`n>>> Configuring Background Worker Service..." -ForegroundColor Yellow
    if (-not (Test-Path $workerExe -PathType Leaf)) {
        throw "Worker executable not found at expected path: $workerExe"
    }

    $existing = Get-Service -Name "EdgeRetailsWorker" -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "    Service 'EdgeRetailsWorker' already exists. Stopping and removing old registration..." -ForegroundColor Gray
        Stop-Service -Name "EdgeRetailsWorker" -Force -ErrorAction SilentlyContinue
        & sc.exe delete EdgeRetailsWorker | Out-Null
        Start-Sleep -Seconds 1
    }

    Write-Host "    Creating service 'EdgeRetailsWorker' with binary path '$workerExe'..." -ForegroundColor Gray
    & sc.exe create EdgeRetailsWorker binPath= "`"$workerExe`"" start= auto DisplayName= "Edge Retails Background Worker"
    if ($LASTEXITCODE -ne 0) { throw "Failed to create EdgeRetailsWorker service." }

    Write-Host "    Configuring crash recovery policy (restart after 60s)..." -ForegroundColor Gray
    & sc.exe failure EdgeRetailsWorker reset= 86400 actions= restart/60000/restart/60000/restart/60000
    if ($LASTEXITCODE -ne 0) { throw "Failed to configure failure recovery on EdgeRetailsWorker." }

    Write-Host "    Starting EdgeRetailsWorker..." -ForegroundColor Gray
    Start-Service -Name "EdgeRetailsWorker" -ErrorAction Stop
    $workerService = Get-Service -Name "EdgeRetailsWorker"
    $workerService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
    if ($workerService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        throw "EdgeRetailsWorker did not reach the Running state."
    }
    Write-Host "    EdgeRetailsWorker registered successfully." -ForegroundColor Green
}

if ($RegisterServer) {
    $serverExe = Join-Path $InstallPath "server\EdgeRetails.Server.exe"
    Write-Host "`n>>> Configuring loopback Shop Server Service..." -ForegroundColor Yellow
    if (-not (Test-Path $serverExe -PathType Leaf)) {
        throw "Server executable not found at expected path: $serverExe"
    }

    $existing = Get-Service -Name "EdgeRetailsServer" -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "    Service 'EdgeRetailsServer' already exists. Stopping and removing old registration..." -ForegroundColor Gray
        Stop-Service -Name "EdgeRetailsServer" -Force -ErrorAction SilentlyContinue
        & sc.exe delete EdgeRetailsServer | Out-Null
        Start-Sleep -Seconds 1
    }

    Write-Host "    Creating service 'EdgeRetailsServer' with binary path '$serverExe'..." -ForegroundColor Gray
    & sc.exe create EdgeRetailsServer binPath= "`"$serverExe`"" start= auto DisplayName= "Edge Retails Shop Server"
    if ($LASTEXITCODE -ne 0) { throw "Failed to create EdgeRetailsServer service." }

    Write-Host "    Configuring crash recovery policy..." -ForegroundColor Gray
    & sc.exe failure EdgeRetailsServer reset= 86400 actions= restart/60000/restart/60000/restart/60000
    if ($LASTEXITCODE -ne 0) { throw "Failed to configure failure recovery on EdgeRetailsServer." }

    Write-Host "    Starting EdgeRetailsServer..." -ForegroundColor Gray
    Start-Service -Name "EdgeRetailsServer" -ErrorAction Stop
    $serverService = Get-Service -Name "EdgeRetailsServer"
    $serverService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
    if ($serverService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        throw "EdgeRetailsServer did not reach the Running state."
    }
    Write-Host "    Loopback-only binding required; no inbound firewall rule is created." -ForegroundColor Gray
    Write-Host "    EdgeRetailsServer registered successfully." -ForegroundColor Green
}

Write-Host "`nService configuration completed." -ForegroundColor Cyan
