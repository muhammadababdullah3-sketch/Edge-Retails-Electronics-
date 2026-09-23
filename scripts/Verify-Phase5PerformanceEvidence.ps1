[CmdletBinding()]
param(
    [string]$EvidencePath = "C:\Users\muham\OneDrive\Desktop\Point of Sale\docs\Phase5_Performance_Evidence.json"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $EvidencePath)) {
    throw "Evidence file not found: $EvidencePath"
}

$json = Get-Content $EvidencePath -Raw | ConvertFrom-Json

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
    if ($actual -lt $required[$key]) {
        throw "Required dataset count failed for ${key}: $actual (expected >= $($required[$key]))"
    }
}
Write-Output "VERIFY_DATASET_COUNTS: PASS"

$names = @($json.benchmarks.name)
$requiredBenchmarks = @(
    'P0_ProductSearch','P0_ProductExactSku','P0_SalesHistory','P0_CustomerLookup','P0_SupplierLookup',
    'P1_InventoryMovementHistory','P1_SupplierLedgerPage','P1_WarrantyQueueRead','P1_PurchaseHistory',
    'P1_ThakaProjectPage','P1_CashHistory','P1_OutboxHistory','P2_AuditHistoryPage',
    'P2_ReportDailySlice','P2_ReportAggregate'
)

foreach ($rb in $requiredBenchmarks) {
    if ($names -notcontains $rb) {
        throw "Missing mandatory benchmark: $rb"
    }
}
Write-Output "VERIFY_15_BENCHMARKS: PASS"

foreach ($benchmark in $json.benchmarks) {
    if ([int]$benchmark.sampleCount -lt 120) {
        throw "Insufficient samples for $($benchmark.name): $($benchmark.sampleCount)"
    }
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
Write-Output "VERIFY_STATISTICAL_MONOTONICITY: PASS"

if (-not $json.indexEvidence -or -not $json.indexEvidence.measuredBenefit) {
    throw "Evidence-based purchase-history index benefit was not proven."
}
if (@($json.indexEvidence.beforePlan.nodeTypes) -notcontains 'Seq Scan') {
    throw "Purchase-history before-index plan did not demonstrate the measured baseline scan."
}
if (@($json.indexEvidence.afterPlan.nodeTypes) -notcontains 'Index Scan') {
    throw "Purchase-history after-index plan did not demonstrate index usage."
}
Write-Output "VERIFY_INDEX_EVIDENCE: PASS"

if ($null -eq $json.growth -or @($json.growth).Count -lt 10) {
    throw "Growth authority evidence is incomplete."
}
Write-Output "VERIFY_GROWTH_EVIDENCE: PASS"

foreach ($plan in $json.queryPlans) {
    if ($null -eq $plan.executionMs) { throw "Missing execution plan timing for $($plan.name)" }
    if ([string]::IsNullOrWhiteSpace([string]$plan.planJson)) { throw "Missing plan JSON for $($plan.name)" }
}
Write-Output "VERIFY_QUERY_PLANS: PASS"

if (-not $json.cancellation.cancellationObserved -or
    -not $json.cancellation.connectionUsableAfterCancel -or
    -not $json.cancellation.databaseQueryReleased -or
    -not $json.cancellation.reportCancellationObserved -or
    -not $json.cancellation.reportDatabaseQueryReleased) {
    throw "PostgreSQL and production-report cancellation/resource-release evidence failed."
}
Write-Output "VERIFY_CANCELLATION_EVIDENCE: PASS"

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
Write-Output "VERIFY_DIAGNOSTICS_11_FAMILIES: PASS"

Write-Output "PHASE5_SCALE_PERFORMANCE_EVIDENCE_VERIFIED: PASS"
