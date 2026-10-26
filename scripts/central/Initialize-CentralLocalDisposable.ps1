# Owned disposable PostgreSQL 18 cluster for local Central development only.
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$artifactRoot = Join-Path $repoRoot 'tests\artifacts\central-dev-pg18'
$dataDir = Join-Path $artifactRoot 'data'
$logDir = Join-Path $artifactRoot 'logs'
$markerPath = Join-Path $artifactRoot 'ownership-marker.json'
$attestationPath = Join-Path $artifactRoot 'data-dir.marker'
$runtimeRoot = Join-Path $repoRoot 'tests\artifacts\central-dev-runtime'
$envFile = Join-Path $repoRoot 'local\central-dev.env'

$pgBin = 'C:\Program Files\PostgreSQL\18\bin'
if (-not (Test-Path (Join-Path $pgBin 'postgres.exe'))) {
    throw "PostgreSQL 18 not found at $pgBin"
}

$port = 55448
$db = 'central_local_dev'
$runtimeRole = 'central_local_runtime'
$migrationRole = 'central_local_migration'
$bootstrapSuper = 'central_local_bootstrap'
$runtimePassword = 'CentralLocalRuntime!2026'
$migrationPassword = 'CentralLocalMigrate!2026'

foreach ($path in @($artifactRoot, $logDir, $runtimeRoot, (Join-Path $repoRoot 'local'))) {
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path | Out-Null }
}
foreach ($sub in @('signer-keys', 'objects', 'payments')) {
    $p = Join-Path $runtimeRoot $sub
    if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p | Out-Null }
}

if (-not (Test-Path (Join-Path $dataDir 'PG_VERSION'))) {
    Write-Host "Initializing disposable PostgreSQL cluster at $dataDir"
    & (Join-Path $pgBin 'initdb.exe') -D $dataDir -U $bootstrapSuper -A trust -E UTF8 --locale=C | Out-Null
    $conf = Join-Path $dataDir 'postgresql.conf'
    Add-Content $conf "`nlisten_addresses = 'localhost'`nport = $port"
}

$pm = Join-Path $dataDir 'postmaster.pid'
$runningPid = $null
if (Test-Path $pm) {
    $runningPid = [int](Get-Content $pm -TotalCount 1)
    if (-not (Get-Process -Id $runningPid -ErrorAction SilentlyContinue)) { $runningPid = $null }
}
if (-not $runningPid) {
    Write-Host "Starting PostgreSQL on port $port"
    & (Join-Path $pgBin 'pg_ctl.exe') -D $dataDir -l (Join-Path $logDir 'postgresql.log') -o "-p $port" start | Out-Null
    Start-Sleep -Seconds 3
    $runningPid = [int](Get-Content $pm -TotalCount 1)
}

$env:PGHOST = '127.0.0.1'
$env:PGPORT = [string]$port
$psql = Join-Path $pgBin 'psql.exe'
& $psql -U $bootstrapSuper -d postgres -c "CREATE ROLE $migrationRole LOGIN PASSWORD '$migrationPassword';" 2>$null
& $psql -U $bootstrapSuper -d postgres -c "CREATE ROLE $runtimeRole LOGIN PASSWORD '$runtimePassword';" 2>$null
$dbExists = (& $psql -U $bootstrapSuper -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$db';").Trim()
if ([string]::IsNullOrWhiteSpace($dbExists)) {
    & $psql -U $bootstrapSuper -d postgres -c "CREATE DATABASE $db OWNER $migrationRole;" | Out-Null
}

$marker = [ordered]@{
    pid = $runningPid
    dataDirectory = (Resolve-Path $dataDir).Path
    port = $port
    database = $db
    runtimeRole = $runtimeRole
    migrationRole = $migrationRole
    bindAddress = '127.0.0.1'
    purpose = 'local_disposable_development'
}
$marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
@"
owner=central_test_admin
cluster_type=disposable_pg18
port=$port
database=$db
schema=central
"@ | Set-Content -LiteralPath $attestationPath -Encoding UTF8

$runtimeCs = "Host=127.0.0.1;Port=$port;Database=$db;Username=$runtimeRole;Password=$runtimePassword"
$migrationCs = "Host=127.0.0.1;Port=$port;Database=$db;Username=$migrationRole;Password=$migrationPassword"

$envLines = @(
    "CENTRAL_DATABASE_CONNECTION_STRING=$runtimeCs"
    "CENTRAL_PG_PORT=$port"
    "CENTRAL_PG_DATABASE=$db"
    "CENTRAL_PG_ROLE=$runtimeRole"
    "CENTRAL_PG_MIGRATION_ROLE=$migrationRole"
    "CENTRAL_PG_PID=$runningPid"
    "CENTRAL_PG_BINARY=$((Join-Path $pgBin 'postgres.exe'))"
    "CENTRAL_PG_OWNERSHIP_MARKER=$((Resolve-Path $markerPath).Path)"
    "CENTRAL_PG_DATA_DIR=$((Resolve-Path $dataDir).Path)"
    "CENTRAL_SIGNER_KEY_STORE_ROOT=$((Join-Path $runtimeRoot 'signer-keys'))"
    "CENTRAL_OBJECT_STORAGE_ROOT=$((Join-Path $runtimeRoot 'objects'))"
    "CENTRAL_PAYMENT_STORAGE_ROOT=$((Join-Path $runtimeRoot 'payments'))"
    "CENTRAL_ADMIN_AUTH_CHALLENGE_TTL_SECONDS=300"
    "CENTRAL_ADMIN_AUTH_SESSION_IDLE_TIMEOUT_SECONDS=900"
    "CENTRAL_ADMIN_AUTH_SESSION_ABSOLUTE_LIFETIME_SECONDS=28800"
    "CENTRAL_ADMIN_STEP_UP_TTL_SECONDS=300"
    "CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS=5"
    "CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS=60"
    "CENTRAL_SENSITIVE_APPROVAL_TTL_SECONDS=3600"
    "CENTRAL_DUAL_APPROVAL_ACTIONS=NONE"
    "CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS=120"
    "CENTRAL_BOOTSTRAP_CHALLENGE_TTL_SECONDS=300"
    "CENTRAL_INTERNAL_SIGNER_TOKEN=local-central-dev-signer-token"
    "ASPNETCORE_URLS=https://localhost:5001"
    "ASPNETCORE_ENVIRONMENT=Development"
    "CENTRAL_DEV_PORTAL_USERNAME=central-dev@edgeretails.local"
    "CENTRAL_DEV_PORTAL_PASSWORD=CentralDevPortal!2026"
)
$envLines | Set-Content -LiteralPath $envFile -Encoding UTF8

Write-Host "Wrote $envFile"
Write-Host "Applying EF migrations as $migrationRole ..."
$env:CENTRAL_DATABASE_CONNECTION_STRING = $migrationCs
$env:CENTRAL_PG_PORT = [string]$port
$env:CENTRAL_PG_DATABASE = $db
$env:CENTRAL_PG_ROLE = $runtimeRole
$env:CENTRAL_PG_MIGRATION_ROLE = $migrationRole
$env:CENTRAL_PG_PID = [string]$runningPid
$env:CENTRAL_PG_BINARY = (Join-Path $pgBin 'postgres.exe')
$env:CENTRAL_PG_OWNERSHIP_MARKER = (Resolve-Path $markerPath).Path
$env:CENTRAL_PG_DATA_DIR = (Resolve-Path $dataDir).Path

Set-Location $repoRoot
dotnet ef database update --project 'src\EdgeRetails.Central.Infrastructure\EdgeRetails.Central.Infrastructure.csproj' --startup-project 'src\EdgeRetails.Central.Api\EdgeRetails.Central.Api.csproj' --context CentralDbContext
if ($LASTEXITCODE -ne 0) { throw 'EF migrations failed.' }

$env:CENTRAL_DEV_PORTAL_USERNAME = 'central-dev@edgeretails.local'
$env:CENTRAL_DEV_PORTAL_PASSWORD = 'CentralDevPortal!2026'
$devTools = Join-Path $repoRoot 'src\EdgeRetails.Central.DevTools\EdgeRetails.Central.DevTools.csproj'
Write-Host 'Synchronizing local portal admin (password + MFA record)...'
dotnet run --project $devTools -- sync-portal-admin
if ($LASTEXITCODE -ne 0) { throw 'sync-portal-admin failed.' }

Write-Host 'Disposable Central database is ready. Start the API with scripts/central/Start-CentralLocalDev.ps1'
