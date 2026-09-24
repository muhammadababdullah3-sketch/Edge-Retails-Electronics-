# Unregister-EdgeRetailsServices.ps1
# Authoritative script for safely stopping and unregistering Edge Retails Windows services

[CmdletBinding()]
param(
    [switch]$Worker = $true,
    [switch]$Server = $true
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Unregister-EdgeRetailsServices.ps1 requires elevated administrative privileges. Please run from an elevated PowerShell window."
}

Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host " EDGE RETAILS - SERVICE UNREGISTRATION" -ForegroundColor Cyan
Write-Host "=======================================================================" -ForegroundColor Cyan

if ($Worker) {
    $existing = Get-Service -Name "EdgeRetailsWorker" -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host ">>> Stopping and removing EdgeRetailsWorker..." -ForegroundColor Yellow
        Stop-Service -Name "EdgeRetailsWorker" -Force -ErrorAction SilentlyContinue
        & sc.exe delete EdgeRetailsWorker | Out-Null
        Write-Host "    EdgeRetailsWorker removed." -ForegroundColor Green
    } else {
        Write-Host ">>> EdgeRetailsWorker is not installed." -ForegroundColor Gray
    }
}

if ($Server) {
    $existing = Get-Service -Name "EdgeRetailsServer" -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host ">>> Stopping and removing EdgeRetailsServer..." -ForegroundColor Yellow
        Stop-Service -Name "EdgeRetailsServer" -Force -ErrorAction SilentlyContinue
        & sc.exe delete EdgeRetailsServer | Out-Null
        Write-Host "    EdgeRetailsServer removed." -ForegroundColor Green
    } else {
        Write-Host ">>> EdgeRetailsServer is not installed." -ForegroundColor Gray
    }
}

Write-Host "`nService unregistration completed." -ForegroundColor Cyan
