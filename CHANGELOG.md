# Changelog

All notable changes to the Edge Retails Enterprise Point of Sale platform are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased] - 2026-10-10

### Modernized
- **Enterprise Monorepo Restructuring**:
  - Relocated loose root and docs-level markdown reports into standardized documentation tiers (`docs/architecture/`, `docs/governance/`, `docs/historical-audits/`, `docs/operations/`, `docs/roadmap/`).
  - Implemented `docs/governance/manifests/MANIFEST_PATH_TRANSITION_MAP.json` preserving 100% cryptographic SHA-256 lineage and traceability for all 139 relocated files.
  - Safely migrated historical test run directories (`.audit-results`, `.tracking-results`) to `artifacts/historical-runs/` under the Zero-Deletion governance standard.
  - Added enterprise-grade root documents: `README.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, and `SECURITY.md`.

---

## [Phase 7 Pass 5 Final21 Certified] - 2026-10-09

### Certified
- **Candidate R1 Independent Certification**:
  - Verified 21/21 independent certification criteria across double-entry ledger, concurrency, catalog authority, and tracking code collision resistance.
  - Certified full financial replay safety on void purchase and commercial exchange pathways.
  - Frozen candidate package signed under `candidate-r1`.

### Fixed
- **Frontend Contract Acceptance (F01–F17)**:
  - F03/F05: Remediated financial replay and expense void routes, DTO alignment, and 409 conflict recovery states.
  - F07/F08: Added mutation guards and eliminated warranty timeline race conditions.
  - F11/F12: Enforced authoritative Brand/Category lookup and resolved multi-page (>200 products) catalog contracts.
  - F15: Bounded Khata composite cursor histories, equal-timestamp continuation, and incremental pagination.
  - F16: Aligned system diagnostics, readiness, and cache freshness semantics.

---

## [Phase 6 Release Hardening] - 2026-10-06

### Added
- Automated MSI installer packaging and update pipeline.
- Production database launch scripts and zero-downtime migration verification.
- Diagnostic background engine with disk space, worker heartbeat, and print backlog telemetry.

---

## [Phase 5 Diagnostics & Outbox] - 2026-10-04

### Added
- Outbox pattern for print jobs and external synchronizations.
- Operational health contracts and alerting thresholds.

---

## [Phase 4 Domain Aggregation & UOM] - 2026-10-02

### Added
- Comprehensive unit-of-measure (UOM) scaling and conversion factors.
- Land cost allocation across container receipts.
- Automatic dealer prefix generation for multi-supplier catalogs.

---

## [Phase 3 Immutability & Replay Safety] - 2026-09-29

### Added
- Append-only ChangeTracker boundaries preventing hard entity updates in financial contexts.
- Automated POS draft replay protection.
- Inventory preservation and immutable ledger certification.

---

## [Phase 2 PostgreSQL Concurrency] - 2026-09-27

### Added
- Row-level locking (`FOR UPDATE`) for concurrent checkout and stock adjustment operations.
- Postgres transaction boundaries and isolation level verification.

---

## [Phase 1 Canonical Architecture] - 2026-09-24

### Added
- Canonical architecture specification (`docs/Edge_Retails_Final_Architecture_Report_v1.md`).
- Machine-readable manifest verification (`docs/Architecture_Authority_Manifest.json`).
- Lightweight root architecture pointer (`EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md`).
