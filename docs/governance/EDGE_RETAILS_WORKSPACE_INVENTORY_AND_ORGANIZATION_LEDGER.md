# EDGE RETAILS — WORKSPACE INVENTORY & CIVILIZED ARCHITECTURE LEDGER
## POST-PHASE 7 REORGANIZATION AND GOVERNANCE PRESERVATION DOSSIER

**Authority:** Lead Architecture & Workspace Governance  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Date:** October 9, 2026  
**Status:** **`STABILIZED_INDEXED_ZERO_DATA_LOSS`**  
**Policy:** **STRICT ZERO DELETION (Preserve 100% of Code, Harness, History, and Proofs)**  

---

## 1. Executive Summary & Purpose

The Edge Retails workspace accumulated significant forensic audits, test run logs, and phase documentation during the intensive Phase 7 / Pass 5 remediation and certification cycles. 

To transform the repository into a clean, disciplined, and civilized structure without violating candidate hashes or deleting critical historical records, this ledger establishes:
1. An authoritative classification of all directories and root files.
2. An explanation of why specific historical files reside in the root (anchored to frozen candidate manifests).
3. The consolidation of raw untracked scratch and prompt files into dedicated folders.
4. A formal proposal for the governance body for future post-certification archival.

---

## 2. Workspace Tier Structure & Directory Taxonomy

```
Point of Sale/
├── src/                                [TIER 1] Production Code (7 Core Projects)
│   ├── EdgeRetails.Domain/             Domain Models, Invariants, Entities
│   ├── EdgeRetails.Application/        Use Cases, Handlers, DTOs, Queries
│   ├── EdgeRetails.Infrastructure/     EF Core DbContext, Repositories, Migrations
│   ├── EdgeRetails.Server/             ASP.NET Core Loopback Shop Server
│   ├── EdgeRetails.Desktop/            WPF MVVM Client Application
│   ├── EdgeRetails.Worker/             Background Maintenance & Printing Host
│   └── EdgeRetails.Recovery/           Hardware/Crash Recovery Host
│
├── tests/                              [TIER 1] Automated Verification (5 Test Projects)
│   ├── EdgeRetails.UnitTests/          Unit Regression Suite (1,163+ Tests)
│   ├── EdgeRetails.IntegrationTests/   PostgreSQL 18 Integration & In-Memory API Suites
│   ├── EdgeRetails.Desktop.PerformanceTests/ WPF UI & Gateway Contract Suites
│   ├── EdgeRetails.PerformanceTests/   Load & Benchmarking Suites
│   └── EdgeRetails.CrashTestHost/      Host Process Isolation Fixtures
│
├── database/                           [TIER 2] Schema & Persistence Authority
│   ├── migrations/                     Raw PostgreSQL Scripts
│   ├── seeds/                          Master Seeding Data
│   └── schema/                         Canonical Snapshot SQL
│
├── build/                              [TIER 2] Shared Build & Tooling Targets
├── installer/                          [TIER 2] WiX Deployment Pipelines
├── publish/                            [TIER 2] Release Packaging Directories
├── scripts/                            [TIER 2] Operations & Maintenance Scripts
│
├── docs/                               [TIER 3] Canonical Architecture & Living Specifications
│   ├── Architecture_Authority_Manifest.json
│   ├── Edge_Retails_Final_Architecture_Report_v1.md
│   ├── EDGE_RETAILS_PHASE7_PASS5_FINAL21_INDEPENDENT_CERTIFICATION.md
│   ├── EDGE_RETAILS_FRONTEND_CONTRACT_ACCEPTANCE.md
│   └── EDGE_RETAILS_17_FINDING_FINAL_STATUS_MATRIX.md
│
├── artifacts/                          [TIER 4] Immutable Certified Packages & Audit Proofs
│   ├── phase7-pass5/remediation-r1/candidate-r1/ (Certified 1,066-File Frozen Package)
│   ├── phase7-pass1-4-remediation/     Historical Pass 1–4 Closure Proofs
│   └── raw-trx-logs/                   Test Run Execution Logs
│
├── [Root Forensic & Audit Documents]   [TIER 5] Phase 7 Governance Records (Candidate Anchored)
│   ├── EDGE_RETAILS_PHASE7_PASS5_*     Frozen Business Authority, Whitelists & Resolutions
│   ├── EDGE_RETAILS_PHASE7_PASS1..4_*  Historical Forensic & Certification Pass Records
│   └── EDGE_RETAILS_DATABASE_*         Database Migration & Inventory Reports
│
└── scratch/                            [TIER 6] Staging, Historical Probes & Debug Dumps
    ├── historical-debug-dumps/         Moved Untracked Text/Prompt Logs (Preserved)
    ├── pass2_snapshots/                Historical Pass 2 Debug Assets
    └── *.py, *.ps1                     Ad-hoc Investigation Probes
```

---

## 3. Detailed Root File Inventory & Manifest Safety

### 3.1. Standard Solution Configuration (Preserved at Root)
* `EdgeRetails.sln`: Main solution file referencing all 12 projects.
* `Directory.Build.props`: Universal build properties and compiler warnings configuration.
* `global.json`: .NET SDK 10.0 version pin.
* `dotnet-tools.json`: Local .NET CLI tool manifests (e.g., `dotnet-ef`).
* `.editorconfig`: Formatting and Roslyn analyzer rules.
* `.gitattributes`: Line ending normalization.
* `.gitignore`: Ignore rules for build outputs, scratch files, and user logs.
* `LICENSE`: Software license agreement.
* `license.erlic`: Cryptographic offline license token.
* `README.md`: Workspace quickstart and solution description.

### 3.2. Phase 7 Forensic Reports (Candidate-Anchored In Root)
* **Count:** 43 Markdown reports + 5 SHA-256 manifests.
* **Why are they in the root?**
  During the external independent certification of Phase 7 / Pass 5 (`FINAL21`), the frozen candidate `candidate-r1` generated `source-inputs.json` and `source-manifest.sha256` covering 801 source files. That manifest indexes these exact files by their root relative paths (e.g., `"EDGE_RETAILS_PHASE7_PASS5_FROZEN_BUSINESS_AUTHORITY.md"`).
* **Governance Rule:** Moving or renaming these files before formal cutover would cause hash verification discrepancy against the frozen `candidate-r1` package. Therefore, **they remain preserved in place** until the governance body approves an archive migration.

### 3.3. Untracked Clutter Resolved (Moved to `scratch/historical-debug-dumps/`)
The following loose debug dumps and scratch prompt files were identified as unmanaged clutter in the root and safely relocated to `scratch/historical-debug-dumps/`:
* `failed_tests.txt` (Older unit test failure trace)
* `failed_tests_pass2.txt` (Older pass 2 failure trace)
* `master_prompt.txt` (Prompt specification log)
* `scratch_prompt4.txt` (0-byte scratch placeholder)
* `scratch_manifest_pre.json` (Preliminary manifest scratch)

> **Integrity Guarantee:** Zero bytes were deleted. All files exist and remain inspectable in `scratch/historical-debug-dumps/`.

---

## 4. Proposed Governance Approval Dossier for Future Archival

When the governance body convenes to unlock post-Phase 7 operations (or at Phase 12 cutover), the following safe reorganization is recommended:

| Current File Pattern | Proposed Clean Destination | Governance Action |
| :--- | :--- | :--- |
| `EDGE_RETAILS_PHASE7_PASS1..5_*.md` | `docs/archive/phase7-certifications/` | Bulk relocate after updating candidate manifest |
| `EDGE_RETAILS_DATABASE_*.md` | `docs/archive/database-forensics/` | Relocate to database documentation |
| `*.sha256` (root level) | `artifacts/phase7-manifests/` | Consolidate with respective artifact packages |
| `.tracking-results-2026-10-03/` | `artifacts/historical-runs/2026-10-03/` | Move into historical runs directory |

---

## 5. Verification & Health Summary

* **Compilation Status:** Solution `EdgeRetails.sln` compiles cleanly with **0 Warnings and 0 Errors**.
* **Git Status:** 100% clean working tree, fully committed and synchronized with remote `origin/tracking-remediation-20261002`.
* **Zero Deletion Standard:** Verified. All production code, tests, certifications, and historical records remain 100% intact.
