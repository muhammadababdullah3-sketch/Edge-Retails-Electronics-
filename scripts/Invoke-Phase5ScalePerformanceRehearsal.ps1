[CmdletBinding()]
param(
    [int]$Port = 55921,
    [string]$Root = "C:\Temp\EdgeRetailsPhase5Rehearsal"
)

$ErrorActionPreference = 'Stop'
$pg = 'C:\Program Files\PostgreSQL\18\bin'
$repo = 'C:\Users\muham\OneDrive\Desktop\Point of Sale'
$data = Join-Path $Root 'data'
$dbName = 'edge_retails_phase5'
$log = Join-Path $Root 'postgres.log'
$connection = "Host=127.0.0.1;Port=$Port;Database=$dbName;Username=er_p5"
$evidence = Join-Path $repo 'docs\Phase5_Performance_Evidence.json'

function Invoke-Pg([string]$File, [string[]]$Arguments) {
    & (Join-Path $pg $File) @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}

try {
    if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
    New-Item -ItemType Directory -Path $data -Force | Out-Null

    Write-Output "PHASE5_POSTGRES18_START $Port"
    Invoke-Pg 'initdb.exe' @('-D', $data, '--username=er_p5', '--auth-local=trust', '--auth-host=trust', '--encoding=UTF8', '--no-locale')
    Add-Content -Path (Join-Path $data 'postgresql.conf') -Value @(
        "port = $Port",
        "listen_addresses = '127.0.0.1'",
        "max_connections = 80",
        "fsync = on",
        "full_page_writes = on",
        "synchronous_commit = on"
    )
    Invoke-Pg 'pg_ctl.exe' @('-D', $data, '-l', $log, 'start')
    Start-Sleep -Seconds 2
    Invoke-Pg 'createdb.exe' @('-h','127.0.0.1','-p',$Port,'-U','er_p5',$dbName)

    Set-Location $repo
    Write-Output "PHASE5_MIGRATIONS_START"
    dotnet ef database update --project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project .\src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --context EdgeRetailsDbContext --connection $connection
    if ($LASTEXITCODE -ne 0) { throw "EF database update failed with exit code $LASTEXITCODE" }

    Write-Output "PHASE5_DATASET_AND_BENCHMARK_START"
    $perfDll = Join-Path $repo 'tests\EdgeRetails.PerformanceTests\bin\Release\net10.0\EdgeRetails.PerformanceTests.dll'
    if (-not (Test-Path $perfDll)) {
        throw "Performance harness binary not found. Build it first."
    }

    $perfArgs = @(
        $perfDll,
        "--connection=$connection",
        "--load",
        "--samples=120",
        "--warmup=15",
        "--diagnostics",
        "--final",
        "--output=$evidence"
    )
    & dotnet @perfArgs | Tee-Object -FilePath (Join-Path $Root 'benchmark-output.txt')
    if ($LASTEXITCODE -ne 0) { throw "Phase5 performance harness failed with exit code $LASTEXITCODE" }

    if (-not (Test-Path $evidence)) { throw "Performance evidence JSON was not created." }
    $json = Get-Content $evidence -Raw | ConvertFrom-Json

    $required = @{
        'catalog.products' = 10000
        'inventory.units' = 100000
        'sales.sales' = 250000
        'sales.sale_items' = 500000
        'inventory.movements' = 500000
        'finance.supplier_account_entries' = 250000
    }

    foreach ($key in $required.Keys) {
        $actual = [int64]$json.datasetCounts.$key
        if ($actual -lt $required[$key]) { throw "Required dataset count failed for ${key}: $actual" }
    }

    $names = @($json.benchmarks.name)
    foreach ($requiredBenchmark in @(
        'P0_ProductSearch','P0_ProductExactSku','P0_SalesHistory','P0_CustomerLookup','P0_SupplierLookup',
        'P1_InventoryMovementHistory','P1_SupplierLedgerPage','P1_WarrantyQueueRead','P1_PurchaseHistory',
        'P1_ThakaProjectPage','P1_CashHistory','P1_OutboxHistory','P2_AuditHistoryPage',
        'P2_ReportDailySlice','P2_ReportAggregate')) {
        if ($names -notcontains $requiredBenchmark) {
            throw "Missing mandatory benchmark $requiredBenchmark"
        }
    }

    $pgVersion = & (Join-Path $pg 'psql.exe') -h 127.0.0.1 -p $Port -U er_p5 -d $dbName -tAc "SHOW server_version;"
    Write-Output "POSTGRES_VERSION=$($pgVersion.Trim())"

    foreach ($benchmark in $json.benchmarks) {
        if ([int]$benchmark.sampleCount -lt 120) { throw "Insufficient samples for $($benchmark.name)" }
        foreach ($field in @('p50Ms','p95Ms','p99Ms')) {
            if ($null -eq $benchmark.$field -or [double]::IsNaN([double]$benchmark.$field)) {
                throw "Missing percentile evidence $($benchmark.name).$field"
            }
        }
        if ([double]$benchmark.p50Ms -gt [double]$benchmark.p95Ms -or
            [double]$benchmark.p95Ms -gt [double]$benchmark.p99Ms -or
            [double]$benchmark.p99Ms -gt [double]$benchmark.maxMs) {
            throw "Non-monotonic percentile evidence for $($benchmark.name)"
        }
    }

    if (-not $json.indexEvidence -or -not $json.indexEvidence.measuredBenefit) {
        throw "Evidence-based purchase-history index benefit was not proven."
    }
    if (@($json.indexEvidence.beforePlan.nodeTypes) -notcontains 'Seq Scan') {
        throw "Purchase-history before-index plan did not demonstrate the measured baseline scan."
    }
    if (@($json.indexEvidence.afterPlan.nodeTypes) -notcontains 'Index Scan') {
        throw "Purchase-history after-index plan did not demonstrate index usage."
    }

    if ($null -eq $json.growth -or @($json.growth).Count -lt 10) {
        throw "Growth authority evidence is incomplete."
    }

    foreach ($plan in $json.queryPlans) {
        if ($null -eq $plan.executionMs) { throw "Missing execution plan timing for $($plan.name)" }
        if ([string]::IsNullOrWhiteSpace([string]$plan.planJson)) { throw "Missing plan JSON for $($plan.name)" }
    }

    if (-not $json.cancellation.cancellationObserved -or
        -not $json.cancellation.connectionUsableAfterCancel -or
        -not $json.cancellation.databaseQueryReleased -or
        -not $json.cancellation.reportCancellationObserved -or
        -not $json.cancellation.reportDatabaseQueryReleased) {
        throw "PostgreSQL and production-report cancellation/resource-release evidence failed."
    }

    $requiredDiagnosticPrefixes = @(
        'db.latency.', 'db.write_safety.', 'schema.', 'backup.',
        'disk.', 'worker.heartbeat.', 'print.backlog.',
        'outcome_unknown.', 'action_required_backlog.',
        'failed_jobs.', 'reconciliation.'
    )
    if ($null -eq $json.diagnostics) { throw "Diagnostics evidence missing." }
    $diagnosticCodes = @($json.diagnostics.checks.code)
    foreach ($prefix in $requiredDiagnosticPrefixes) {
        if (-not ($diagnosticCodes | Where-Object { $_ -like "$prefix*" })) {
            throw "Required diagnostic code family missing: $prefix"
        }
    }

    Write-Output "PHASE5_SCALE_PERFORMANCE_OBSERVABILITY_PASS"
}
finally {
    try { & (Join-Path $pg 'pg_ctl.exe') -D $data -m fast stop 2>$null } catch { }
}

