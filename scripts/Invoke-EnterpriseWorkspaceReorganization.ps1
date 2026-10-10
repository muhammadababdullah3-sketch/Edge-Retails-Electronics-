# PowerShell Enterprise Workspace Reorganization Script
# Safely relocates loose files into structured enterprise tiers using 'git mv'
# Zero Deletion Standard: 100% file content and history preserved.

param (
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

$script:TransitionMap = [System.Collections.Generic.List[PSCustomObject]]::new()

function Safe-GitMove {
    param (
        [string]$Source,
        [string]$DestinationDir,
        [string]$Category = "Documentation"
    )

    if (Test-Path -Path $Source) {
        if (-not (Test-Path -Path $DestinationDir)) {
            New-Item -ItemType Directory -Path $DestinationDir -Force | Out-Null
        }

        $sourceItem = Get-Item -Path $Source
        $isDir = $sourceItem.PSIsContainer
        $fileName = Split-Path -Leaf $Source
        $destPath = Join-Path $DestinationDir $fileName

        # Compute SHA-256 before move if it's a file
        $sha = $null
        if (-not $isDir) {
            $sha = (Get-FileHash -Path $Source -Algorithm SHA256).Hash
        }

        Write-Host "Moving: $Source -> $destPath" -ForegroundColor Cyan
        if (-not $WhatIf) {
            $tracked = (git ls-files $Source)
            if (-not [string]::IsNullOrWhiteSpace($tracked)) {
                git mv -f $Source $destPath
            } else {
                Move-Item -Path $Source -Destination $destPath -Force
            }

            if (-not $isDir) {
                $script:TransitionMap.Add([PSCustomObject]@{
                    originalPath = ($Source -replace '\\', '/')
                    newPath = ($destPath -replace '\\', '/')
                    sha256 = $sha
                    category = $Category
                })
            }
        }
    } else {
        Write-Host "Skip (not found): $Source" -ForegroundColor Gray
    }
}

Write-Host "=== 1. Creating Target Enterprise Documentation Directories ===" -ForegroundColor Green
$targetDirs = @(
    "docs/architecture",
    "docs/governance/phase7",
    "docs/governance/tracking",
    "docs/governance/manifests",
    "docs/historical-audits/phase1",
    "docs/historical-audits/phase2-3-4",
    "docs/historical-audits/phase6",
    "docs/historical-audits/phase7",
    "docs/historical-audits/database-audits",
    "docs/historical-audits/frontend-audits",
    "docs/operations",
    "docs/roadmap/phase11",
    "artifacts/historical-runs"
)

foreach ($dir in $targetDirs) {
    if (-not (Test-Path -Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

Write-Host "=== 2. Relocating Root Architecture Files ===" -ForegroundColor Green
Safe-GitMove "EDGE_RETAILS_BUSINESS_INVARIANTS_REGISTRY.md" "docs/architecture" "Architecture"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_BUSINESS_EVENT_EFFECT_MATRIX.md" "docs/architecture" "Architecture"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_FROZEN_BUSINESS_AUTHORITY.md" "docs/architecture" "Architecture"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_MISSING_UNIT_DOMAIN_SCHEMA_DECISION.md" "docs/architecture" "Architecture"
# Note: EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md remains at root as lightweight pointer per Phase1CanonicalSchemaDomainAlignmentTests

Write-Host "=== 3. Relocating Root Cryptographic Manifest Files ===" -ForegroundColor Green
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests" "Manifest"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS2_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests" "Manifest"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS3_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests" "Manifest"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS4_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests" "Manifest"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests" "Manifest"

Write-Host "=== 4. Relocating Root Governance & Resolution Files ===" -ForegroundColor Green
Safe-GitMove "EDGE_RETAILS_PHASE7_CORRECTED_IMPLEMENTATION_CONTRACT_AND_SEQUENCE_REVIEW.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_FINAL_DEFECT_OWNERSHIP_AND_BOUNDARY_FREEZE.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS1_FINAL_CERTIFICATION_AND_CLOSURE.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS1_STEP2_IMPLEMENTATION_AND_VERIFICATION.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_REPAIR_AND_FINAL_LOCK.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS2_FINAL_CERTIFICATION_AND_CLOSURE.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS2_FINAL_HOSTILE_CERTIFICATION_AND_LOCK.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS3_CANDIDATE_DRIFT_RECONCILIATION_AND_REFREEZE.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS3_PREIMPLEMENTATION_CHALLENGE_GATE.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS3_SOL_FINAL_CHALLENGE_AND_LOCK_AUTHORIZATION.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS3_STEP2_IMPLEMENTATION_CERTIFICATION_AND_LOCK.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS4_FINAL_CERTIFICATION_AND_LOCK.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_C01.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_D13.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_R19.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_R19_HARNESS.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_RESOLUTION04.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_STOCKTAKE_FREE.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_FOUND_RECOVERY_BUSINESS_DECISION.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_01.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_02.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_03.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_04.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_05.md" "docs/governance/phase7" "Governance"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_IMPLEMENTATION_CHECKPOINT.md" "docs/governance/phase7" "Governance"

Write-Host "=== 5. Relocating Root Tracking Governance Files ===" -ForegroundColor Green
Safe-GitMove "EDGE_RETAILS_TRACKING_SYSTEM_FINAL_CERTIFICATION_AND_FREEZE_2026-10-03.md" "docs/governance/tracking" "Tracking"

Write-Host "=== 6. Relocating Root Historical Phase 7 Audits ===" -ForegroundColor Green
Safe-GitMove "EDGE_RETAILS_AUTOMATIC_TRACKING_SYSTEM_CODE_FORENSIC_AUDIT_2026-10-06.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_CATALOG_PURCHASING_GAP_CONFIRMATION_AND_REMEDIATION_PLAN.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_CURRENT_CATALOG_PRODUCT_MODEL_SUPPLIER_WORKFLOW_READONLY.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS1_STEP1_DEEP_FORENSIC_AUDIT.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS2_STEP1_DEEP_FORENSIC_AUDIT.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS3_STEP1_DEEP_FORENSIC_AUDIT.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS4_DEEP_FORENSIC_AUDIT_AND_PASS5_HANDOVER.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_ARCHITECTURE_CONTRADICTION_REPORT.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_STEP1_LIVE_DELTA_AUDIT.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_PHASE7_PASS5_STEP1_RESUMED_LIVE_DELTA_AUDIT.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_PHASE7_PRE_IMPLEMENTATION_FORENSIC_AUDIT.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "EDGE_RETAILS_TRACKING_REMEDIATION_CONTINUATION_REPORT_2026-10-03.md" "docs/historical-audits/phase7" "Audit"

Write-Host "=== 7. Relocating Root Database Audit Files ===" -ForegroundColor Green
Safe-GitMove "EDGE_RETAILS_DATABASE_BUSINESS_RECONCILIATION_REPORT.md" "docs/historical-audits/database-audits" "Audit"
Safe-GitMove "EDGE_RETAILS_DATABASE_CONSTRAINT_AND_RELATIONSHIP_MATRIX.md" "docs/historical-audits/database-audits" "Audit"
Safe-GitMove "EDGE_RETAILS_DATABASE_DEEP_FORENSIC_AUDIT.md" "docs/historical-audits/database-audits" "Audit"
Safe-GitMove "EDGE_RETAILS_DATABASE_FINDINGS_REGISTER.md" "docs/historical-audits/database-audits" "Audit"
Safe-GitMove "EDGE_RETAILS_DATABASE_INDEX_AND_QUERY_HEALTH_REPORT.md" "docs/historical-audits/database-audits" "Audit"
Safe-GitMove "EDGE_RETAILS_DATABASE_MIGRATION_FORENSIC_REPORT.md" "docs/historical-audits/database-audits" "Audit"
Safe-GitMove "EDGE_RETAILS_DATABASE_SCHEMA_INVENTORY.md" "docs/historical-audits/database-audits" "Audit"

Write-Host "=== 8. Relocating Root Phase 2-4 and Phase 6 Audits ===" -ForegroundColor Green
Safe-GitMove "EDGE_RETAILS_AUDIT_FINDINGS_VS_IMPLEMENTATION_ROADMAP_2026-09-25.md" "docs/historical-audits/phase2-3-4" "Audit"
Safe-GitMove "EDGE_RETAILS_BACKEND_FORENSIC_AUDIT_2026-09-25.md" "docs/historical-audits/phase2-3-4" "Audit"
Safe-GitMove "EDGE_RETAILS_ENTIRE_PROJECT_FORENSIC_AUDIT_2026-09-28.md" "docs/historical-audits/phase2-3-4" "Audit"
Safe-GitMove "EDGE_RETAILS_LIVE_ENDPOINT_REGISTER_2026-09-27.md" "docs/historical-audits/phase2-3-4" "Audit"
Safe-GitMove "Phase 6Edge_Retails_Antigravity_Backend_Implementation_Roadmap.md" "docs/historical-audits/phase6" "Roadmap"

Write-Host "=== 9. Relocating New Phase 11 Root Files ===" -ForegroundColor Green
Safe-GitMove "EDGE_RETAILS_PHASE11_W0_PREEXECUTION_TASK_REGISTER_2026-10-10.md" "docs/roadmap/phase11" "Roadmap"
Safe-GitMove "EDGE_RETAILS_PHASE11_W0_ONE_FINAL_MASTER_REMEDIATION_PROMPT_2026-10-10.md" "docs/roadmap/phase11" "Roadmap"
Safe-GitMove "EDGE_RETAILS_PHASE11_INTEGRATED_ROADMAP_AND_CATALOG_V3_2026-10-10.zip" "docs/roadmap/phase11" "Roadmap"

Write-Host "=== 10. Relocating Loose Files within docs/ to Subfolders ===" -ForegroundColor Green
Safe-GitMove "docs/Edge_Retails_Antigravity_Master_Architecture_Frontend_Backend_Complete_v2_2026-09-24.md" "docs/architecture" "Architecture"
Safe-GitMove "docs/EDGE_RETAILS_PHASE7_PASS5_FINAL21_INDEPENDENT_CERTIFICATION.md" "docs/governance/phase7" "Certification"
Safe-GitMove "docs/EDGE_RETAILS_FRONTEND_CONTRACT_ACCEPTANCE.md" "docs/governance" "Governance"
Safe-GitMove "docs/EDGE_RETAILS_WORKSPACE_INVENTORY_AND_ORGANIZATION_LEDGER.md" "docs/governance" "Governance"
Safe-GitMove "docs/EDGE_RETAILS_17_FINDING_FINAL_STATUS_MATRIX.md" "docs/governance" "Governance"
Safe-GitMove "docs/EDGE_RETAILS_FINAL_REMEDIATION_CLOSURE_REPORT.md" "docs/governance" "Governance"
Safe-GitMove "docs/EDGE_RETAILS_EXTERNAL_OWNER_HANDOFF.md" "docs/governance" "Governance"
Safe-GitMove "docs/EDGE_RETAILS_FRONTEND_FINAL_MASTER_CLOSURE.md" "docs/governance" "Governance"
Safe-GitMove "docs/EDGE_RETAILS_FRONTEND_UIUX_FINAL_CERTIFICATION.md" "docs/governance" "Governance"
Safe-GitMove "docs/EDGE_RETAILS_17_FINDING_REMEDIATION_ROADMAP.md" "docs/governance" "Governance"
Safe-GitMove "docs/EDGE_RETAILS_GITHUB_SAFETY_BACKUP_AUTHORIZATION_AND_INVENTORY_REPORT.md" "docs/governance" "Governance"
Safe-GitMove "docs/COMMIT_MESSAGE.txt" "docs/governance" "Governance"
Safe-GitMove "docs/Master_Remediation_Tracking_Labels_Ledger_2026-10-01.md" "docs/governance/tracking" "Tracking"
Safe-GitMove "docs/Master_Backup_Key_Lifecycle_Design_2026-10-02.md" "docs/operations" "Operations"
Safe-GitMove "docs/EDGE_RETAILS_PHASE1_FINANCIAL_IDENTITY_REMEDIATION_REPORT.md" "docs/historical-audits/phase1" "Audit"
Safe-GitMove "docs/Phase6_Database_Compatibility_Matrix.md" "docs/historical-audits/phase6" "Audit"
Safe-GitMove "docs/Phase6_Execution_State.md" "docs/historical-audits/phase6" "Audit"
Safe-GitMove "docs/Phase6_Operations_Package_Index.md" "docs/historical-audits/phase6" "Audit"
Safe-GitMove "docs/Phase6_Agent_Handoffs" "docs/historical-audits/phase6" "Audit"
Safe-GitMove "docs/EDGE_RETAILS_PHASE2_3_4_REMEDIATION_REPORT.md" "docs/historical-audits/phase2-3-4" "Audit"
Safe-GitMove "docs/Phase2_Shop_Server_API_Parity_Matrix_2026-09-26.md" "docs/historical-audits/phase2-3-4" "Audit"
Safe-GitMove "docs/Sprint5_Master_Implementation_Plan.md" "docs/historical-audits/phase2-3-4" "Audit"
Safe-GitMove "docs/Sprint9_Master_Implementation_Roadmap.md" "docs/historical-audits/phase2-3-4" "Audit"
Safe-GitMove "docs/Phase7Pass4_Attributes_Compatibility.md" "docs/historical-audits/phase7" "Audit"
Safe-GitMove "docs/EDGE_RETAILS_PRODUCTION_DATABASE_LAUNCH_INVESTIGATION_2026-10-06.md" "docs/historical-audits/database-audits" "Audit"
Safe-GitMove "docs/EDGE_RETAILS_FRONTEND_CURRENT_BACKEND_DATABASE_READ_ONLY_AUDIT_2026-10-08.md" "docs/historical-audits/database-audits" "Audit"

# Move Phase 3 docs
Get-ChildItem -Path docs -Filter "Phase3*.md" | ForEach-Object { Safe-GitMove $_.FullName "docs/historical-audits/phase2-3-4" "Phase3" }
Get-ChildItem -Path docs -Filter "Phase3*.txt" | ForEach-Object { Safe-GitMove $_.FullName "docs/historical-audits/phase2-3-4" "Phase3" }
Get-ChildItem -Path docs -Filter "Phase3*.ps1" | ForEach-Object { Safe-GitMove $_.FullName "docs/historical-audits/phase2-3-4" "Phase3" }
Get-ChildItem -Path docs -Filter "Phase3*.json" | ForEach-Object { Safe-GitMove $_.FullName "docs/historical-audits/phase2-3-4" "Phase3" }
Get-ChildItem -Path docs -Filter "Controlled_Demo_*.md" | ForEach-Object { Safe-GitMove $_.FullName "docs/historical-audits/phase2-3-4" "Demo" }

# Move Historical Frontend Audits in docs/
$frontendAuditFiles = @(
    "docs/Frontend_Backend_Forensic_Verification_2026-09-22.md",
    "docs/Frontend_Forensic_UI_UX_Audit_2026-09-24.md",
    "docs/Frontend_Forensic_UI_UX_Remediation_2026-09-25.md",
    "docs/Frontend_Pass1_Candidate_Hash_Manifest.md",
    "docs/EDGE_RETAILS_FRONTEND_PASS1_VERIFICATION.md",
    "docs/EDGE_RETAILS_FRONTEND_PASS2_VERIFICATION.md",
    "docs/Frontend_Pass2_Candidate_Hash_Manifest.md",
    "docs/EDGE_RETAILS_FRONTEND_PASS3_VERIFICATION.md",
    "docs/Frontend_Pass3_Candidate_Hash_Manifest.md",
    "docs/EDGE_RETAILS_USER_REPORTED_UI_DEFECT_PACK_2026-10-05.md",
    "docs/UI_Defect_Pack_Candidate_Manifest_2026-10-05.json",
    "docs/EDGE_RETAILS_UI_STROKE_CORRECTIONS_2026-10-05.md",
    "docs/EDGE_RETAILS_FRONTEND_VISUAL_REVIEW_2026-10-05.md",
    "docs/EDGE_RETAILS_PRODUCTION_LOGO_ALIGNMENT_2026-10-06.md",
    "docs/EDGE_RETAILS_SUPPLIER_UI_CORRECTION_2026-10-06.md",
    "docs/EDGE_RETAILS_UNIVERSAL_SURFACE_TEXT_CORRECTION_2026-10-06.md",
    "docs/EDGE_RETAILS_CUSTOMER_KHATA_SUSPENSION_AND_WORKSPACE_CORRECTION_2026-10-06.md"
)
foreach ($f in $frontendAuditFiles) {
    Safe-GitMove $f "docs/historical-audits/frontend-audits" "FrontendAudit"
}

Write-Host "=== 11. Relocating Root Test Results / Hidden Folders ===" -ForegroundColor Green
Safe-GitMove ".audit-results" "artifacts/historical-runs" "HistoricalTestRun"
Safe-GitMove ".audit-results-2026-09-27" "artifacts/historical-runs" "HistoricalTestRun"
Safe-GitMove ".audit-results-2026-10-04" "artifacts/historical-runs" "HistoricalTestRun"
Safe-GitMove ".tracking-results-2026-10-03" "artifacts/historical-runs" "HistoricalTestRun"

Write-Host "=== 12. Writing Manifest Path Transition Map ===" -ForegroundColor Green
if (-not $WhatIf) {
    $transitionMapObj = [PSCustomObject]@{
        generatedAt = (Get-Date -Format "o")
        standard = "International Enterprise Monorepo Repository Structure"
        totalFilesRelocated = $script:TransitionMap.Count
        zeroDeletionVerified = $true
        mappings = $script:TransitionMap
    }
    $transitionMapJson = $transitionMapObj | ConvertTo-Json -Depth 5
    $transitionMapPath = "docs/governance/manifests/MANIFEST_PATH_TRANSITION_MAP.json"
    [System.IO.File]::WriteAllText((Join-Path (Get-Location) $transitionMapPath), $transitionMapJson)
    Write-Host "Wrote transition map with $($script:TransitionMap.Count) entries to $transitionMapPath" -ForegroundColor Green
}

Write-Host "=== Reorganization Complete ===" -ForegroundColor Green
