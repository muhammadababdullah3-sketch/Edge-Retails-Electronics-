[CmdletBinding()]
param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

Write-Host "POS-82 gate: provision owned Master profile via IntegrationTests harness, then run FAILURE_REGISTER union."
Write-Host "Requires: PostgresFixtureProfile Master attestation (loopback 55xxx, edge_retails_* DB under EdgeRetailsMasterPg_* temp root)."

& $dotnet test (Join-Path $repoRoot 'tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj') `
    -c $Configuration `
    --filter "FullyQualifiedName~MasterCashierCreationPostgresTests" `
    --logger "trx;LogFileName=pos82-master-cashier-sample.trx"

Write-Host "Expand this script to run the full 82-row FQNs from governance/10-execution-runs/ER-RUN-POS-82-REMEDIATION-20261024-001/FAILURE_REGISTER.csv after E1/E2 profile routing is green."
