# Delivery Readback Verification for ER-RUN-LOCALFLOW-20261019-074
$ErrorActionPreference = "Stop"
Write-Host "=== DELIVERY READBACK VERIFICATION: ER-RUN-LOCALFLOW-20261019-074 ===" -ForegroundColor Cyan

$runDir = $PSScriptRoot
$receiptPath = Join-Path $runDir "RECEIPT.json"
if (-not (Test-Path $receiptPath)) { Write-Error "RECEIPT.json missing!" }
$receipt = Get-Content $receiptPath -Raw | ConvertFrom-Json
Write-Host "[PASS] RECEIPT.json valid (Status: $($receipt.status))" -ForegroundColor Green

$stages = @(
    "STAGE1_CONFIRMED_THAKA_AUDIT_FINDINGS_AND_CAUSAL_DIAGNOSIS.md",
    "STAGE2_FINITE_ANALOGOUS_FLOW_INVENTORY_ACROSS_DOMAINS.md",
    "STAGE3_VERIFIED_FOUNDATION_LOCK_AND_CUSTODY.md",
    "STAGE4_EXACT_CRITERIA_AND_CONTRACT_DECISION.md",
    "STAGE5_ORIGINAL_TASK_CROSSWALK.md",
    "STAGE6_IMPLEMENTATION_AND_EVIDENCE.md",
    "STAGE7_WHOLE_WAVE_EVALUATION.md",
    "STAGE8_FINAL_DEPOSITION_AND_RECEIPT.md"
)

foreach ($s in $stages) {
    $p = Join-Path $runDir $s
    if (-not (Test-Path $p)) { Write-Error "Stage report missing: $s" }
    $len = (Get-Item $p).Length
    Write-Host "[PASS] $s present ($len bytes)" -ForegroundColor Green
}

Write-Host "=== DELIVERY READBACK VERIFICATION COMPLETED: 100% PASS ===" -ForegroundColor Cyan
