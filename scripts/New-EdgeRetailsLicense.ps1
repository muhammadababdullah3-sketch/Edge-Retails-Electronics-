# New-EdgeRetailsLicense.ps1
# Authoritative script for issuing Edge Retails RSA-2048 machine-bound license

[CmdletBinding()]
param(
    [string]$OutputLicensePath = "license.erlic"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot | Split-Path -Parent
$crashHost = Join-Path $root "tests\EdgeRetails.CrashTestHost\EdgeRetails.CrashTestHost.csproj"

& dotnet run --project $crashHost -c Release --scenario generate-license --output $OutputLicensePath
if ($LASTEXITCODE -ne 0) {
    throw "Failed to generate authoritative license."
}
