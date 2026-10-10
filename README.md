# Edge Retails — Enterprise Point of Sale & Inventory Platform

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen.svg)]()
[![Platform](https://img.shields.io/badge/platform-.NET%2010%20LTS-blue.svg)]()
[![Database](https://img.shields.io/badge/database-PostgreSQL-336791.svg)]()
[![Architecture](https://img.shields.io/badge/architecture-Clean%20%2F%20DDD-orange.svg)]()
[![Certification](https://img.shields.io/badge/governance-Phase%207%20Final21%20Certified-success.svg)]()

Edge Retails is an enterprise-grade Point of Sale (POS), inventory management, and financial ledger platform engineered specifically for electronics, hardware, and mobile retail environments. Built with rigorous domain-driven design (DDD), double-entry accounting guarantees, exact-unit traceability (IMEI, Serial, Tracking Code), and immutable audit logs.

---

## 🏛️ System Architecture

Edge Retails adheres strictly to **Clean Architecture** and **Domain-Driven Design (DDD)** principles with unidirectional dependency flow:

```
[ EdgeRetails.Desktop (WPF / MVVM) ]      [ EdgeRetails.Server (REST API) ]      [ EdgeRetails.Worker (Background Service) ]
                                    \                     |                    /
                                     \                    |                   /
                                      v                   v                  v
                                                [ EdgeRetails.Application ]
                                                            |
                                                            v
                                                  [ EdgeRetails.Domain ]
                                                            ^
                                                            |
                                              [ EdgeRetails.Infrastructure ]
                                          (EF Core, PostgreSQL, Npgsql, Dapper)
```

### Layer Responsibilities

| Layer | Project | Role & Invariants |
| :--- | :--- | :--- |
| **Domain** | `src/EdgeRetails.Domain` | Pure enterprise business entities, value objects, domain events, and invariant rules. Zero external dependencies. |
| **Application** | `src/EdgeRetails.Application` | Use cases, CQRS commands & queries, business transaction pipelines, validation rules, and boundary interfaces. |
| **Infrastructure** | `src/EdgeRetails.Infrastructure` | PostgreSQL persistence, EF Core mappings, raw SQL/Dapper read-models, migrations, outbox dispatch, and file storage. |
| **Server / API** | `src/EdgeRetails.Server` | Shop Server REST API delivering endpoints for multi-terminal sync, catalog queries, and diagnostics. |
| **Desktop Client** | `src/EdgeRetails.Desktop` | Professional WPF MVVM desktop POS interface optimized for barcode/IMEI scanning, touch workflow, thermal receipts, and local resilience. |
| **Background Worker** | `src/EdgeRetails.Worker` | Windows Service / background daemon for automated backups, outbox processing, health diagnostics, and reconciliation. |

---

## 📁 Repository Structure

The repository is structured as a standardized enterprise monorepo:

```
Point of Sale/
├── src/                                  # Production Source Code
│   ├── EdgeRetails.Domain/               # Core Domain Models & Business Invariants
│   ├── EdgeRetails.Application/          # Application Use Cases & CQRS Handlers
│   ├── EdgeRetails.Infrastructure/       # Database Contexts, Migrations & Adapters
│   ├── EdgeRetails.Server/               # Shop Server REST API
│   ├── EdgeRetails.Desktop/              # WPF Desktop Client (MVVM)
│   └── EdgeRetails.Worker/               # Background Worker & Health Engine
│
├── tests/                                # Comprehensive Automated Test Suites
│   ├── EdgeRetails.UnitTests/            # Domain, Accounting & Architecture Tests
│   ├── EdgeRetails.IntegrationTests/     # Postgres Concurrency & Rollback Tests
│   └── ...                               # Component & Performance Test Harnesses
│
├── docs/                                 # Enterprise Documentation & Governance
│   ├── architecture/                     # System Architecture & Invariant Registers
│   ├── governance/                       # Certification Charters & Release Whitelists
│   │   ├── phase7/                       # Phase 7 Resolutions & Verification Pacts
│   │   ├── tracking/                     # Tracking Freeze & Key Lifecycle Records
│   │   └── manifests/                    # Cryptographic Hash Manifests & Transition Maps
│   ├── historical-audits/                # Archived Historical Deep Audits (Phases 1–7)
│   ├── operations/                       # Production Runbooks & Backup Procedures
│   └── roadmap/                          # Roadmap Dossiers & Pre-Execution Registers
│
├── database/                             # Database Baselines, Seed Scripts & Schemas
├── build/                                # Build Props, SQL Alignment & Packaging
├── artifacts/                            # Signed Candidate Packages & Verification Runs
├── scripts/                              # PowerShell & Shell Automation Tools
├── EdgeRetails.sln                       # Unified Solution File
├── Directory.Build.props                 # Monorepo Build Configurations
├── CONTRIBUTING.md                       # Developer & Governance Workflow Guidelines
├── CHANGELOG.md                          # Comprehensive Version History
├── SECURITY.md                           # Security & Immutability Policies
└── README.md                             # Repository Master Documentation
```

---

## 🔒 Financial & Business Invariants

The platform enforces non-negotiable architectural guarantees:
1. **Strict Double-Entry Ledger**: Every financial transaction (sale, return, purchase, expense, Khata credit/debit) creates immutable paired entries. Balances are derived, never directly updated.
2. **Item-Level Traceability**: Exact-unit tracking using composite Tracking Codes (`SupplierPrefix-SKU-Sequence`), Serial Numbers, and IMEI numbers with 100% collision resistance.
3. **Optimistic & Row-Locking Concurrency**: Explicit PostgreSQL row locks (`FOR UPDATE`) protect concurrent stock allocations and cash drawer balances.
4. **Outbox Pattern**: Distributed side effects (print jobs, external sync) are persisted transactionally in PostgreSQL before asynchronous background dispatch.
5. **Zero Deletion Standard**: All inventory changes, voids, and adjustments create compensating audit entries. No hard deletes allowed on operational records.

---

## 🚀 Getting Started

### Prerequisites
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (LTS)
- [PostgreSQL 16+](https://www.postgresql.org/download/)
- Windows 10/11 (for WPF Desktop runtime)

### Build
To build the complete solution with all 12 projects:
```powershell
dotnet build EdgeRetails.sln --configuration Release
```

### Run Tests
To run all automated unit and architecture verification tests:
```powershell
dotnet test EdgeRetails.sln
```

To run architecture compliance verification:
```powershell
powershell -ExecutionPolicy Bypass -File scripts/Verify-ArchitectureInternationalAuditRemediation.ps1
```

---

## 📜 Governance & Certification

- **Certified Baseline**: Phase 7 Pass 5 Final21 Certified Candidate (`candidate-r1`).
- **Cryptographic Lineage**: Tracked in `docs/governance/manifests/MANIFEST_PATH_TRANSITION_MAP.json`.
- **Authority Root Pointer**: [EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md](EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md) pointing to canonical specification [docs/Edge_Retails_Final_Architecture_Report_v1.md](docs/Edge_Retails_Final_Architecture_Report_v1.md).

---

## 📄 License

Proprietary enterprise retail software. Licensed under the terms defined in [LICENSE](LICENSE) and [license.erlic](license.erlic).
