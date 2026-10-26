# Provisions machine-local client pairing (terminal credential) for the shop install.
# Does NOT install license signing keys — only a DPAPI-protected terminal secret.
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$installDir = Join-Path $env:ProgramData "EdgeRetails\installation"
$credentialPath = Join-Path $installDir "terminal-credential.dpapi"
$manifestPath = Join-Path $installDir "installation.json"

if ((Test-Path $credentialPath) -and -not $Force) {
    Write-Host "Client pairing credential already exists at $credentialPath (use -Force to replace)." -ForegroundColor Green
    exit 0
}

if ($Force -and (Test-Path $credentialPath)) {
    Remove-Item -LiteralPath $credentialPath -Force
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null

Add-Type -AssemblyName System.Security
$entropy = [Text.Encoding]::UTF8.GetBytes("EdgeRetails.Client.TerminalCredential.V1")
$secretBytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Fill($secretBytes)
$secret = [Convert]::ToBase64String($secretBytes)
$plain = [Text.Encoding]::UTF8.GetBytes($secret)
$protected = [Security.Cryptography.ProtectedData]::Protect($plain, $entropy, [Security.Cryptography.DataProtectionScope]::LocalMachine)

$temp = "$credentialPath.$([guid]::NewGuid().ToString('N')).tmp"
[System.IO.File]::WriteAllBytes($temp, $protected)
Move-Item -LiteralPath $temp -Destination $credentialPath -Force

if (-not (Test-Path $manifestPath)) {
    $manifest = @{
        terminalCode = "LOCAL"
        provisionedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json
    Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding UTF8
}

Write-Host "Client pairing credential provisioned (DPAPI LocalMachine)." -ForegroundColor Green
Write-Host "  Credential: $credentialPath" -ForegroundColor Gray
Write-Host "  Do not copy this file off the shop PC. It is not a cloud admin or license signing key." -ForegroundColor Gray
