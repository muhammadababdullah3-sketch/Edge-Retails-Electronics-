#!/usr/bin/env bash
set -e

echo "=== Creating Target Enterprise Documentation Directories ==="
mkdir -p docs/architecture
mkdir -p docs/governance/phase7
mkdir -p docs/governance/tracking
mkdir -p docs/governance/manifests
mkdir -p docs/historical-audits/phase1
mkdir -p docs/historical-audits/phase2-3-4
mkdir -p docs/historical-audits/phase6
mkdir -p docs/historical-audits/phase7
mkdir -p docs/historical-audits/database-audits
mkdir -p docs/historical-audits/frontend-audits

safe_git_mv() {
    src="$1"
    dst="$2"
    if [ -f "$src" ] || [ -d "$src" ]; then
        echo "Moving: $src -> $dst"
        git mv "$src" "$dst"
    else
        echo "Skip (not found): $src"
    fi
}

echo "=== Relocating Root Architecture Files ==="
safe_git_mv "EDGE_RETAILS_BUSINESS_INVARIANTS_REGISTRY.md" "docs/architecture/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_BUSINESS_EVENT_EFFECT_MATRIX.md" "docs/architecture/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_FROZEN_BUSINESS_AUTHORITY.md" "docs/architecture/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_MISSING_UNIT_DOMAIN_SCHEMA_DECISION.md" "docs/architecture/"

echo "=== Relocating Root Cryptographic Manifest Files ==="
safe_git_mv "EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS2_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS3_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS4_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_FINAL_SOURCE_MANIFEST.sha256" "docs/governance/manifests/"

echo "=== Relocating Root Governance & Resolution Files ==="
safe_git_mv "EDGE_RETAILS_PHASE7_CORRECTED_IMPLEMENTATION_CONTRACT_AND_SEQUENCE_REVIEW.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_FINAL_DEFECT_OWNERSHIP_AND_BOUNDARY_FREEZE.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS1_FINAL_CERTIFICATION_AND_CLOSURE.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS1_STEP2_IMPLEMENTATION_AND_VERIFICATION.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_REPAIR_AND_FINAL_LOCK.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS2_FINAL_CERTIFICATION_AND_CLOSURE.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS2_FINAL_HOSTILE_CERTIFICATION_AND_LOCK.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS3_CANDIDATE_DRIFT_RECONCILIATION_AND_REFREEZE.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS3_PREIMPLEMENTATION_CHALLENGE_GATE.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS3_SOL_FINAL_CHALLENGE_AND_LOCK_AUTHORIZATION.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS3_STEP2_IMPLEMENTATION_CERTIFICATION_AND_LOCK.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS4_FINAL_CERTIFICATION_AND_LOCK.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_C01.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_D13.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_R19.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_R19_HARNESS.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_RESOLUTION04.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_EDIT_WHITELIST_ADDENDUM_STOCKTAKE_FREE.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_FOUND_RECOVERY_BUSINESS_DECISION.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_01.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_02.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_03.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_04.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_GOVERNANCE_RESOLUTION_05.md" "docs/governance/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_IMPLEMENTATION_CHECKPOINT.md" "docs/governance/phase7/"

echo "=== Relocating Root Tracking Files ==="
safe_git_mv "EDGE_RETAILS_TRACKING_SYSTEM_FINAL_CERTIFICATION_AND_FREEZE_2026-10-03.md" "docs/governance/tracking/"

echo "=== Relocating Root Historical Phase 7 Audits ==="
safe_git_mv "EDGE_RETAILS_AUTOMATIC_TRACKING_SYSTEM_CODE_FORENSIC_AUDIT_2026-10-06.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_CATALOG_PURCHASING_GAP_CONFIRMATION_AND_REMEDIATION_PLAN.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_CURRENT_CATALOG_PRODUCT_MODEL_SUPPLIER_WORKFLOW_READONLY.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS1_STEP1_DEEP_FORENSIC_AUDIT.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS2_STEP1_DEEP_FORENSIC_AUDIT.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS3_STEP1_DEEP_FORENSIC_AUDIT.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS4_DEEP_FORENSIC_AUDIT_AND_PASS5_HANDOVER.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_ARCHITECTURE_CONTRADICTION_REPORT.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_STEP1_LIVE_DELTA_AUDIT.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PASS5_STEP1_RESUMED_LIVE_DELTA_AUDIT.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_PHASE7_PRE_IMPLEMENTATION_FORENSIC_AUDIT.md" "docs/historical-audits/phase7/"
safe_git_mv "EDGE_RETAILS_TRACKING_REMEDIATION_CONTINUATION_REPORT_2026-10-03.md" "docs/historical-audits/phase7/"

echo "=== Relocating Root Database Audit Files ==="
safe_git_mv "EDGE_RETAILS_DATABASE_BUSINESS_RECONCILIATION_REPORT.md" "docs/historical-audits/database-audits/"
safe_git_mv "EDGE_RETAILS_DATABASE_CONSTRAINT_AND_RELATIONSHIP_MATRIX.md" "docs/historical-audits/database-audits/"
safe_git_mv "EDGE_RETAILS_DATABASE_DEEP_FORENSIC_AUDIT.md" "docs/historical-audits/database-audits/"
safe_git_mv "EDGE_RETAILS_DATABASE_FINDINGS_REGISTER.md" "docs/historical-audits/database-audits/"
safe_git_mv "EDGE_RETAILS_DATABASE_INDEX_AND_QUERY_HEALTH_REPORT.md" "docs/historical-audits/database-audits/"
safe_git_mv "EDGE_RETAILS_DATABASE_MIGRATION_FORENSIC_REPORT.md" "docs/historical-audits/database-audits/"
safe_git_mv "EDGE_RETAILS_DATABASE_SCHEMA_INVENTORY.md" "docs/historical-audits/database-audits/"

echo "=== Relocating Root Phase 2-4 and Phase 6 Audits ==="
safe_git_mv "EDGE_RETAILS_AUDIT_FINDINGS_VS_IMPLEMENTATION_ROADMAP_2026-09-25.md" "docs/historical-audits/phase2-3-4/"
safe_git_mv "EDGE_RETAILS_BACKEND_FORENSIC_AUDIT_2026-09-25.md" "docs/historical-audits/phase2-3-4/"
safe_git_mv "EDGE_RETAILS_ENTIRE_PROJECT_FORENSIC_AUDIT_2026-09-28.md" "docs/historical-audits/phase2-3-4/"
safe_git_mv "EDGE_RETAILS_LIVE_ENDPOINT_REGISTER_2026-09-27.md" "docs/historical-audits/phase2-3-4/"
safe_git_mv "Phase 6Edge_Retails_Antigravity_Backend_Implementation_Roadmap.md" "docs/historical-audits/phase6/"

echo "=== Relocating Loose Files within docs/ to Subfolders ==="
# Architecture
safe_git_mv "docs/Edge_Retails_Antigravity_Master_Architecture_Frontend_Backend_Complete_v2_2026-09-24.md" "docs/architecture/"

# Governance & Closure
safe_git_mv "docs/EDGE_RETAILS_PHASE7_PASS5_FINAL21_INDEPENDENT_CERTIFICATION.md" "docs/governance/phase7/"
safe_git_mv "docs/EDGE_RETAILS_FRONTEND_CONTRACT_ACCEPTANCE.md" "docs/governance/"
safe_git_mv "docs/EDGE_RETAILS_17_FINDING_FINAL_STATUS_MATRIX.md" "docs/governance/"
safe_git_mv "docs/EDGE_RETAILS_FINAL_REMEDIATION_CLOSURE_REPORT.md" "docs/governance/"
safe_git_mv "docs/EDGE_RETAILS_EXTERNAL_OWNER_HANDOFF.md" "docs/governance/"
safe_git_mv "docs/EDGE_RETAILS_FRONTEND_FINAL_MASTER_CLOSURE.md" "docs/governance/"
safe_git_mv "docs/EDGE_RETAILS_FRONTEND_UIUX_FINAL_CERTIFICATION.md" "docs/governance/"
safe_git_mv "docs/EDGE_RETAILS_17_FINDING_REMEDIATION_ROADMAP.md" "docs/governance/"
safe_git_mv "docs/EDGE_RETAILS_GITHUB_SAFETY_BACKUP_AUTHORIZATION_AND_INVENTORY_REPORT.md" "docs/governance/"
safe_git_mv "docs/COMMIT_MESSAGE.txt" "docs/governance/"

# Tracking
safe_git_mv "docs/Master_Remediation_Tracking_Labels_Ledger_2026-10-01.md" "docs/governance/tracking/"

# Operations
safe_git_mv "docs/Master_Backup_Key_Lifecycle_Design_2026-10-02.md" "docs/operations/"

# Historical Phase 1
safe_git_mv "docs/EDGE_RETAILS_PHASE1_FINANCIAL_IDENTITY_REMEDIATION_REPORT.md" "docs/historical-audits/phase1/"

# Historical Phase 6
safe_git_mv "docs/Phase6_Database_Compatibility_Matrix.md" "docs/historical-audits/phase6/"
safe_git_mv "docs/Phase6_Execution_State.md" "docs/historical-audits/phase6/"
safe_git_mv "docs/Phase6_Operations_Package_Index.md" "docs/historical-audits/phase6/"
safe_git_mv "docs/Phase6_Agent_Handoffs" "docs/historical-audits/phase6/Phase6_Agent_Handoffs"

# Historical Phase 2-3-4
safe_git_mv "docs/EDGE_RETAILS_PHASE2_3_4_REMEDIATION_REPORT.md" "docs/historical-audits/phase2-3-4/"
safe_git_mv "docs/Phase2_Shop_Server_API_Parity_Matrix_2026-09-26.md" "docs/historical-audits/phase2-3-4/"
safe_git_mv "docs/Sprint5_Master_Implementation_Plan.md" "docs/historical-audits/phase2-3-4/"
safe_git_mv "docs/Sprint9_Master_Implementation_Roadmap.md" "docs/historical-audits/phase2-3-4/"
for f in docs/Phase3*.md docs/Phase3*.txt docs/Phase3*.ps1 docs/Phase3*.json docs/Controlled_Demo_*.md; do
    if [ -f "$f" ]; then
        safe_git_mv "$f" "docs/historical-audits/phase2-3-4/"
    fi
done

# Historical Phase 7 in docs/
safe_git_mv "docs/Phase7Pass4_Attributes_Compatibility.md" "docs/historical-audits/phase7/"

# Historical Database in docs/
safe_git_mv "docs/EDGE_RETAILS_PRODUCTION_DATABASE_LAUNCH_INVESTIGATION_2026-10-06.md" "docs/historical-audits/database-audits/"
safe_git_mv "docs/EDGE_RETAILS_FRONTEND_CURRENT_BACKEND_DATABASE_READ_ONLY_AUDIT_2026-10-08.md" "docs/historical-audits/database-audits/"

# Historical Frontend Audits in docs/
safe_git_mv "docs/Frontend_Backend_Forensic_Verification_2026-09-22.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/Frontend_Forensic_UI_UX_Audit_2026-09-24.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/Frontend_Forensic_UI_UX_Remediation_2026-09-25.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/Frontend_Pass1_Candidate_Hash_Manifest.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_FRONTEND_PASS1_VERIFICATION.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_FRONTEND_PASS2_VERIFICATION.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/Frontend_Pass2_Candidate_Hash_Manifest.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_FRONTEND_PASS3_VERIFICATION.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/Frontend_Pass3_Candidate_Hash_Manifest.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_USER_REPORTED_UI_DEFECT_PACK_2026-10-05.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/UI_Defect_Pack_Candidate_Manifest_2026-10-05.json" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_UI_STROKE_CORRECTIONS_2026-10-05.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_FRONTEND_VISUAL_REVIEW_2026-10-05.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_PRODUCTION_LOGO_ALIGNMENT_2026-10-06.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_SUPPLIER_UI_CORRECTION_2026-10-06.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_UNIVERSAL_SURFACE_TEXT_CORRECTION_2026-10-06.md" "docs/historical-audits/frontend-audits/"
safe_git_mv "docs/EDGE_RETAILS_CUSTOMER_KHATA_SUSPENSION_AND_WORKSPACE_CORRECTION_2026-10-06.md" "docs/historical-audits/frontend-audits/"

echo "=== Complete Reorganization Finished Successfully ==="
