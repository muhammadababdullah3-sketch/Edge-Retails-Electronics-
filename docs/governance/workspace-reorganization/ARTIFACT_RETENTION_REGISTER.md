# Edge Retails — Artifact Retention Register & Storage Management Plan

**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Governance Authority:** Monorepo Storage Management & Artifact Lifecycle  
**Operating Standard:** Canonical Architecture Baseline Sections 170–195, Policy `R1-H01-1` & Absolute Zero-Deletion Standard  
**Date of Ratification:** October 9, 2026  
**Status:** **`CLASSIFIED_PRESERVED_ON_ACTIVE_DISK_ZERO_PRUNING`**  
**Physical Action Taken:** ZERO files pruned, deleted, or purged. All 38,456 artifacts (~21.5 GB) preserved byte-for-byte.  

---

## 1. Executive Summary & Workspace Storage Dynamics

A comprehensive audit of repository storage reveals that **~21.5 GB** (approximately **88.8%**) of the workspace disk footprint (~24.2 GB total) resides within the `artifacts/` tree.

```
+===================================================================================================+
|                                    WORKSPACE STORAGE ALLOCATION                                    |
+===================================================================================================+
| Storage Component                       | File Count   | Disk Footprint | % of Total Workspace    |
+-----------------------------------------+--------------+----------------+-------------------------+
| artifacts/ Root Subsystem               | 38,456 files | ~21.5 GB       | 88.8 %                  |
| bin / obj Compiler Intermediates        | ~9,200 files | ~1.9 GB        |  7.8 %                  |
| src/ Production Source Projects (7)     |    417 files | ~3.8 MB        | <0.1 %                  |
| tests/ Verification Suites (5)          |    310 files | ~2.9 MB        | <0.1 %                  |
| database/ Migrations, Seeds, Schema     |     47 files | ~1.2 MB        | <0.1 %                  |
| scripts/ Automation & Diagnostic Probes |     38 files | ~0.4 MB        | <0.1 %                  |
| docs/ Architecture & Governance         |    188 files | ~4.2 MB        | <0.1 %                  |
| Git Object Database (.git)              |  3,812 files | ~780 MB        |  3.2 %                  |
+-----------------------------------------+--------------+----------------+-------------------------+
| TOTAL WORKSPACE FOOTPRINT               | ~52,468 files| ~24.2 GB       | 100.0 %                 |
+===================================================================================================+
```

### 1.1. Root Cause of Artifact Growth
Between September 30 and October 1, 2026, the engineering team executed an intensive series of deployment and crash-recovery iterations addressing WiX Toolset v4 bootstrapper crashes, DirectX runtime dependency resolution (`D3DCompiler_47_cor3.dll`), and WPF native desktop rendering stability. Each full build produced a complete, self-contained standalone published output (~800 MB each). This resulted in **22 individual compiled release snapshots** under `artifacts/` (`phase3c-recovery-release-20260930-02` through `-21`, plus `-22`), consuming ~17.5 GB alone.

---

## 2. Artifact Classification Taxonomy

Every item under `artifacts/` is formally classified under one of five authoritative retention tiers:

```
+=======================================================================================================================================+
| Artifact Directory / Package          | Size    | Classification Tiers       | Retention Policy & Justification                       |
+=======================================================================================================================================+
| phase7-ultimate-final-closure/        | 1.8 MB  | IMMUTABLE_CERTIFICATION    | PERMANENT ACTIVE LOCAL DISK                            |
|                                       |         | EVIDENCE                   | 14 certified governance reports closing Final21 gates. |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| phase7-final-remaining-closure/       | 3.2 MB  | IMMUTABLE_CERTIFICATION    | PERMANENT ACTIVE LOCAL DISK                            |
|                                       |         | EVIDENCE                   | Candidate R6 freeze package, source-inputs.json.       |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| phase11-preimplementation/            | 0.6 MB  | IMMUTABLE_CERTIFICATION    | PERMANENT ACTIVE LOCAL DISK                            |
|                                       |         | EVIDENCE                   | Phase 11 pre-implementation forensic audit & contracts |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| phase7-pass5/                         | 1.4 MB  | IMMUTABLE_CERTIFICATION    | PERMANENT ACTIVE LOCAL DISK                            |
|                                       |         | EVIDENCE                   | Candidate R1 freeze package & verification scripts.    |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| phase7-pass1-4-remediation/           | 1.1 MB  | IMMUTABLE_CERTIFICATION    | PERMANENT ACTIVE LOCAL DISK                            |
|                                       |         | EVIDENCE                   | Pass 1 through Pass 4 signed SHA-256 manifests.        |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| raw-trx-logs/                         | 14.8 MB | ACTIVE_REGRESSION_TRX      | PERMANENT ACTIVE LOCAL DISK                            |
|                                       |         |                            | Machine-readable TRX logs from all 7 pass test runs.   |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| production/                           | 840 MB  | PRODUCTION_RELEASE_BUILDS  | ACTIVE LOCAL DISK                                      |
|                                       |         |                            | Golden production deployment binaries (Desktop/Server).|
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| release/                              | 812 MB  | PRODUCTION_RELEASE_BUILDS  | ACTIVE LOCAL DISK                                      |
|                                       |         |                            | Master release candidate staging directory.            |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| release-1.0.1/ through release-1.0.5/ | 4.1 GB  | HISTORICAL_RELEASE_BUILDS  | COLD_STORAGE_ARCHIVE_CANDIDATE                         |
|                                       |         |                            | Point release build archives. Retained in place.       |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| phase3-final-closure-20260930-01/     | 780 MB  | HISTORICAL_DEBUG_SNAPSHOT  | COLD_STORAGE_ARCHIVE_CANDIDATE                         |
|                                       |         |                            | Phase 3 final recovery smoke test output.              |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| phase3c-recovery-release-*-02 to -22  | 17.5 GB | HISTORICAL_DEBUG_SNAPSHOT  | COLD_STORAGE_ARCHIVE_CANDIDATE                         |
| (21 Directory Snapshots)              |         |                            | 21 iterative crash recovery build compilations.        |
|                                       |         |                            | 100% PRESERVED IN PLACE pending owner cutover.         |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| database-audit-20261005/              | 18.2 MB | INTERMEDIATE_AUDIT_EXPORT  | HISTORICAL LOCAL DISK                                  |
|                                       |         |                            | Database model inspector binaries and schemas.         |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| DesktopUiStrokeFix_2026-10-05/        | 24.1 MB | INTERMEDIATE_AUDIT_EXPORT  | HISTORICAL LOCAL DISK                                  |
|                                       |         |                            | WPF visual inspection binaries.                        |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| DesktopVisualReview_2026-10-05/       | 24.5 MB | INTERMEDIATE_AUDIT_EXPORT  | HISTORICAL LOCAL DISK                                  |
|                                       |         |                            | WPF theme and brush rendering inspection binaries.     |
+---------------------------------------+---------+----------------------------+--------------------------------------------------------+
| schema_export_20261005_130105.json    | 0.4 MB  | INTERMEDIATE_AUDIT_EXPORT  | HISTORICAL LOCAL DISK                                  |
|                                       |         |                            | PostgreSQL schema JSON dump from Pass 5.               |
+=======================================================================================================================================+
```

---

## 3. Strict Zero-Deletion Cold Storage Proposal

Under the **Absolute Zero-Deletion Policy**, no files have been pruned or deleted. To ensure long-term disk health without violating data retention, the following protocol is proposed for project owner authorization during the Phase 12 cutover window:

### 3.1. Non-Destructive Archival Method
Instead of deleting redundant compiled outputs, the 22 Phase3c directories can be bundled into a solid, multi-threaded Zstandard archive (`.tar.zst` or `.zip` with highest compression ratio):
1. Target Destination: `artifacts/release-archive/phase3c-recovery-snapshots-20260930-20261001.tar.zst`
2. Expected Compression Ratio: **~8:1** (due to identical binary assemblies across 22 snapshots).
3. Estimated Archive Size: **~2.2 GB** (recovering **~15.3 GB** of active disk space).
4. Cryptographic Indexing: A SHA-256 manifest of the archive and every contained snapshot will be published in `docs/governance/`.

### 3.2. Current Operating State
Pending explicit project owner authorization:
- **ZERO** files have been deleted.
- **ZERO** directories have been pruned.
- **ZERO** `bin`/`obj` folders have been cleaned.
- All 38,456 artifacts remain fully accessible at their existing filesystem paths.
