# Test-EdgeRetailsServices.ps1
# Diagnostics script to verify presence and status of Edge Retails services and published executables

[CmdletBinding()]
param(
    [string]$InstallPath = "C:\Program Files\Edge Retails"
)

$ErrorActionPreference = "Continue"

Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host " EDGE RETAILS - SERVICE \u0026 BINARY DIAGNOSTICS" -ForegroundColor Cyan
Write-Host "=======================================================================" -ForegroundColor Cyan

$desktopExe = Join-Path $InstallPath "EdgeRetails.Desktop.exe"
$workerExe = Join-Path $InstallPath "worker\EdgeRetails.Worker.exe"
$serverExe = Join-Path $InstallPath "server\EdgeRetails.Server.exe"

Write-Host "`n1. Executable Presence:" -ForegroundColor Yellow
Write-Host ("   {0,-40} : {1}" -f "Desktop (Root)", (Test-Path $desktopExe))
Write-Host ("   {0,-40} : {1}" -f "Worker  (worker\)", (Test-Path $workerExe))
Write-Host ("   {0,-40} : {1}" -f "Server  (server\)", (Test-Path $serverExe))

Write-Host "`n2. Windows Service Registrations:" -ForegroundColor Yellow
$workerSvc = Get-Service -Name "EdgeRetailsWorker" -ErrorAction SilentlyContinue
if ($workerSvc) {
    Write-Host ("   {0,-40} : {1} ({2})" -f "EdgeRetailsWorker", $workerSvc.Status, $workerSvc.StartType) -ForegroundColor Green
} else {
    Write-Host ("   {0,-40} : NOT REGISTERED" -f "EdgeRetailsWorker") -ForegroundColor Gray
}

$serverSvc = Get-Service -Name "EdgeRetailsServer" -ErrorAction SilentlyContinue
if ($serverSvc) {
    Write-Host ("   {0,-40} : {1} ({2})" -f "EdgeRetailsServer", $serverSvc.Status, $serverSvc.StartType) -ForegroundColor Green
} else {
    Write-Host ("   {0,-40} : NOT REGISTERED" -f "EdgeRetailsServer") -ForegroundColor Gray
}
