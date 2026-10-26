# Alias for Provision-CentralDevAdmin.ps1 (same idempotent sync; does not rotate MFA).
param(
    [Parameter(Mandatory = $true)]
    [string] $Username,
    [Parameter(Mandatory = $true)]
    [string] $Password
)

& (Join-Path $PSScriptRoot 'Provision-CentralDevAdmin.ps1') -Username $Username -Password $Password
