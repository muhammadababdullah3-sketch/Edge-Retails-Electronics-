# Edge Retails — Enterprise Monorepo Target Architecture & Documentation Taxonomy

**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Governance Authority:** Enterprise Monorepo Architecture & Workspace Governance  
**Operating Standard:** Canonical Architecture Baseline Sections 170–195, Policy `R1-H01-1` & Final21 Certification Gate System  
**Audit Execution Mode:** ZERO-DELETION NON-DESTRUCTIVE REPOSITORY GOVERNANCE  
**Date of Ratification:** October 9, 2026  
**Status:** **`APPROVED_TARGET_TOPOLOGY_NON_DESTRUCTIVE`**  

---

## 1. Principles of the Enterprise Monorepo Standard

To align the Edge Retails repository with enterprise .NET monorepo standards (conforming to Microsoft and .NET Foundation engineering principles) while strictly maintaining 100% data preservation and cryptographic candidate hash invariance, the following core architecture rules govern the repository:

1. **Strict Separation of Concerns:** Core production projects reside exclusively under `src/`, automated verification suites under `tests/`, deployment installers under `installer/`, and operational scripts under `scripts/`.
2. **Deterministic Root Surface:** The root directory is reserved for solution files (`.sln`), global build orchestration (`Directory.Build.props`, `global.json`, `dotnet-tools.json`), Git configuration (`.gitignore`, `.gitattributes`), licensing files (`LICENSE`, `license.erlic`), and authoritative entry-point orientation (`README.md`).
3. **No Naive Root Caps:** An arbitrary "10-file root limit" must **never** be enforced by blindly moving files that are cryptographically indexed by certified release manifests. Preserving historical provenance and certification compliance takes absolute precedence over cosmetic root tidiness.
4. **Hierarchical Documentation Taxonomy:** All living architectural specifications, operational runbooks, architectural decision records (ADRs), and historical forensic audits are structured logically under `docs/`.
5. **Immutable Artifact Preservation:** The `artifacts/` hierarchy stores cryptographically frozen candidate builds, release archives, and test execution logs. Artifacts must never be modified, overwritten, or silently deleted.

---

## 2. Target Monorepo Directory Topology

```
Point of Sale/
│
├── .editorconfig                          Universal code styling & Roslyn analyzer rules
├── .gitattributes                         Line ending normalization & Git attributes
├── .gitignore                             Comprehensive Git ignore definitions
├── Directory.Build.props                  Global MSBuild project configuration
├── EdgeRetails.sln                        Visual Studio master solution file
├── LICENSE                                Software commercial license agreement
├── license.erlic                          Cryptographic offline license token
├── README.md                              Repository quickstart & developer guide
├── dotnet-tools.json                      Local .NET CLI tool manifest (dotnet-ef)
├── global.json                            Authoritative .NET SDK version pin (10.0.100)
│
├── src/                                   PRODUCTION SOURCE SUBSYSTEMS (7 Projects)
│   ├── EdgeRetails.Domain/                Entities, value objects, domain invariants (Zero deps)
│   ├── EdgeRetails.Application/           MediatR use cases, handlers, DTOs, query contracts
│   ├── EdgeRetails.Infrastructure/        EF Core DbContext, PostgreSQL 18 migrations, repositories
│   ├── EdgeRetails.Server/                ASP.NET Core loopback Web API host
│   ├── EdgeRetails.Desktop/               WPF client application, MVVM views & view models
│   ├── EdgeRetails.Worker/                Background daemon host, printing spooler, worker tasks
│   └── EdgeRetails.Recovery/              Hardware crash recovery & database emergency utility
│
├── tests/                                 AUTOMATED VERIFICATION SUITES (5 Projects)
│   ├── EdgeRetails.UnitTests/             Domain, application, UOM & unit regression suites (1,170 tests)
│   ├── EdgeRetails.IntegrationTests/      PostgreSQL 18 integration, concurrency & financial suites (194 tests)
│   ├── EdgeRetails.Desktop.PerformanceTests/ WPF UI rendering, virtualization & gateway contract suites (76 tests)
│   ├── EdgeRetails.PerformanceTests/      System load, concurrency & MediatR dispatch latency benchmarks
│   └── EdgeRetails.CrashTestHost/         Host crash isolation fixtures & recovery verification
│
├── database/                              CANONICAL PERSISTENCE ASSETS
│   ├── migrations/                        Raw SQL migration scripts
│   ├── seeds/                             Authoritative reference & seed datasets
│   └── schema/                            PostgreSQL DDL schema definitions
│
├── installer/                             DEPLOYMENT PACKAGING (WiX Toolset v4)
│   ├── EdgeRetails.Setup/                 WiX MSI package project & harvesting directives
│   └── EdgeRetails.Bootstrapper/          WiX Burn bootstrapper EXE with prerequisite checks
│
├── build/                                 SHARED MSBUILD & CI/CD ASSETS
│   ├── Sprint8.Release.props              Release build compilation overrides
│   └── phase1_alignment.sql               Persistence alignment build hooks
│
├── scripts/                               OPERATIONAL RUNBOOKS & REHEARSAL SCRIPTS
│   ├── diagnostics/                       Emergency diagnostic probes & crash inspection tooling
│   ├── Invoke-*.ps1                       PostgreSQL rehearsal & verification automation scripts
│   ├── Test-*.ps1                         System readiness, schema & security assertions
│   └── Verify-*.ps1                       Architecture authority & model sync verification
│
├── docs/                                  CANONICAL & HISTORICAL DOCUMENTATION TREE
│   │
│   ├── architecture/                      [LIVING ARCHITECTURE SPECIFICATIONS]
│   │   ├── Architecture_Authority_Manifest.json          Canonical manifest (SHA256 059DDB08...)
│   │   ├── Edge_Retails_Final_Architecture_Report_v1.md  Master architecture report (Sec 170-233.1)
│   │   ├── EDGE_RETAILS_BUSINESS_INVARIANTS_REGISTRY.md  Core business invariant catalog
│   │   └── Edge_Retails_Antigravity_Master_Architecture... Complete frontend/backend architecture v2
│   │
│   ├── governance/                        [CERTIFICATION & REORGANIZATION RECORDS]
│   │   ├── EDGE_RETAILS_PHASE7_PASS5_FINAL21_INDEPENDENT_CERTIFICATION.md
│   │   ├── EDGE_RETAILS_FRONTEND_CONTRACT_ACCEPTANCE.md
│   │   ├── EDGE_RETAILS_17_FINDING_FINAL_STATUS_MATRIX.md
│   │   ├── EDGE_RETAILS_FINAL_REMEDIATION_CLOSURE_REPORT.md
│   │   └── workspace-reorganization/      Workspace inventory, relocation matrix, verification
│   │
│   ├── adr/                               [ARCHITECTURAL DECISION RECORDS]
│   │   ├── ADR-001-Physical-Tracking-Identity-V1.md
│   │   ├── ADR-002-PostgreSQL-18-Deterministic-Lock-Hierarchy.md
│   │   ├── ADR-003-Pack-Container-SubLedger-Zero-Duplicate-Tables.md
│   │   └── ADR-004-Shop-Timezone-Authority-Standard.md
│   │
│   ├── operations/                        [PRODUCTION RUNBOOKS & OPERATIONAL GUIDES]
│   │   ├── Backup_Restore_Runbook.md
│   │   ├── Support_Diagnostics_Error_Catalog.md
│   │   ├── Production_Release_Checklist_Known_Limitations.md
│   │   ├── Database_Migration_Map.md
│   │   ├── Production_Licensing_Standard_Operating_Procedure.md
│   │   ├── Security_Key_Rotation_Guide.md
│   │   ├── Disaster_Recovery_Runbook.md
│   │   ├── Upgrade_Guide.md
│   │   └── Installer_Deployment_Guide.md
│   │
│   └── historical-audits/                 [ARCHIVED HISTORICAL AUDITS & REPORTS]
│       ├── phase1/                        Phase 1 financial identity & remediation reports
│       ├── phase2-3-4/                    Phase 2–4 remediation reports, API parity, smoke tests
│       ├── phase6/                        Phase 6 execution state & agent handoffs
│       ├── phase7/                        Phase 7 pre-implementation audits, live delta audits, whitelists
│       └── database-audits/               Database migration, index health, schema inventory reports
│
├── artifacts/                             FROZEN RELEASE PACKAGES & VERIFIED TEST EVIDENCE
│   ├── phase7-ultimate-final-closure/     14 certified governance reports (Final21 Gate Closure)
│   ├── phase7-final-remaining-closure/    Candidate R6 freeze package & cryptographic hash evidence
│   ├── phase11-preimplementation/         Phase 11 pre-implementation forensic audit deliverables
│   ├── phase7-pass5/                      Candidate R1 frozen package & verification scripts
│   ├── raw-trx-logs/                      Historical test execution TRX logs across all passes
│   └── release-archive/                   (Future cold-storage target for 22 Phase3c snapshots)
│
└── scratch/                               TRANSIENT INVESTIGATION PROBES & DEBUG DUMPS
    ├── historical-debug-dumps/            Relocated transient prompts and scratch files (Preserved)
    ├── pass2_snapshots/                   Historical Pass 2 investigation snapshots
    └── *.py, *.ps1                        Ad-hoc local diagnostic probes
```

---

## 3. Searchable Documentation Cross-Reference Map

The table below links every major subsystem, governance phase, and technical discipline to its authoritative documentation:

| Subject Area | Primary Authoritative Document | Canonical Location | Scope & Coverage |
|---|---|---|---|
| **Master Architecture** | `docs/Edge_Retails_Final_Architecture_Report_v1.md` | `docs/architecture/` | Sections 0–233.1: Canonical architecture, domain invariants, tracking policy. |
| **Authority Manifest** | `docs/Architecture_Authority_Manifest.json` | `docs/architecture/` | Cryptographic SHA256 hashes (`059DDB08...`) and revision checkpoints. |
| **Business Invariants** | `EDGE_RETAILS_BUSINESS_INVARIANTS_REGISTRY.md` | `docs/architecture/` | Core accounting, inventory, and transaction conservation invariants. |
| **Phase 7 Final Certification** | `FINAL_PHASE7_GOVERNANCE_CLOSURE_REPORT.md` | `artifacts/phase7-ultimate-final-closure/` | Master Final21 gate certification, closing blockers P7-B01 through P7-B05. |
| **Frontend Contract** | `docs/EDGE_RETAILS_FRONTEND_CONTRACT_ACCEPTANCE.md` | `docs/governance/` | Desktop POS UX contract, virtualization, pagination, and styling. |
| **17-Finding Matrix** | `docs/EDGE_RETAILS_17_FINDING_FINAL_STATUS_MATRIX.md`| `docs/governance/` | Definitive disposition of historical findings F01 through F17. |
| **Database Migrations** | `docs/operations/Database_Migration_Map.md` | `docs/operations/` | Chronological lineage of all 24 PostgreSQL EF Core migrations. |
| **Backup & Recovery** | `docs/operations/Backup_Restore_Runbook.md` | `docs/operations/` | Hot backup, point-in-time recovery, disaster recovery runbooks. |
| **Licensing SOP** | `docs/operations/Production_Licensing_Standard_Operating_Procedure.md` | `docs/operations/` | Offline cryptographic licensing issuing, validation, and key rotation. |
| **Phase 11 Forensic Audit** | `PHASE11_DEEP_FORENSIC_PREIMPLEMENTATION_AUDIT.md`| `artifacts/phase11-preimplementation/` | One-pass forensic audit across Workstreams 11A–11I with implementation delta. |
| **Workspace Reorganization**| `WORKSPACE_INVENTORY.md` | `docs/governance/workspace-reorganization/`| Authoritative repository inventory, safe relocation matrix, and retention rules. |

---

## 4. Non-Destructive Manifest Anchoring Policy

### 4.1. The Manifest Anchoring Dilemma
During the intensive certification passes of Program Phase 7, release candidates (`candidate-r1` through `phase7-unified-final-r6`) generated formal cryptographic manifests:
- `artifacts/phase7-pass5/remediation-r1/candidate-r1/source-inputs.json`
- `artifacts/phase7-final-remaining-closure/phase7-unified-final-r6/source-inputs.json`
- `EDGE_RETAILS_PHASE7_PASS5_FINAL_SOURCE_MANIFEST.sha256`

These manifests index files by their exact path string relative to the repository root:
$$\text{e.g.,}\quad \texttt{"Path": "EDGE\_RETAILS\_PHASE7\_PASS5\_FROZEN\_BUSINESS\_AUTHORITY.md"}$$

If an automated process physically renames or moves these files into `docs/historical-audits/`, the relative path string in `source-inputs.json` fails to resolve, breaking the cryptographic manifest digest:
$$\mathbf{D21ED4D9CB07C8930CA3131AFFF4B01875D160CD8F8D3457B01D61449A73FB0F}$$

### 4.2. Safe Additive Resolution
1. **Creation of Target Structure:** Create all enterprise directories (`docs/architecture/`, `docs/governance/`, `docs/adr/`, `docs/historical-audits/`) additively.
2. **Authoritative Relocation Matrix:** Maintain [SAFE_RELOCATION_MATRIX.md](SAFE_RELOCATION_MATRIX.md) mapping every file to its target path.
3. **Preservation at Root Pending Owner Cutover:** Keep the 51 certified governance and manifest files at their original root paths.
4. **Symlink / Forwarding Bridge:** Once the project owner authorizes physical relocation during the Phase 12 or Phase 13 cutover window, files will be moved simultaneously with a signed update to the release candidate manifests.
