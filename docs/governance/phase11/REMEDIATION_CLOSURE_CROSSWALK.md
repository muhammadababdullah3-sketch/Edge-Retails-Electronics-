# Phase 11 remediation — closure crosswalk

**Program status:** `PHASE11_CERTIFIED_CLOSED` — see [EDGE_RETAILS_PHASE11_FINAL_CERTIFICATION_AND_CLOSURE.md](./EDGE_RETAILS_PHASE11_FINAL_CERTIFICATION_AND_CLOSURE.md).

Maps remediation work to source, tests, and evidence.

| Todo ID | Source focus | Test selectors | Status | Evidence |
|---------|--------------|----------------|--------|----------|
| w1-eligibility-fields | `ReturnExchangeEligibilityAuthority.cs`, `PackSubLedgerAllocationAuthority.cs` | `Phase11ReturnExchangePackSubLedgerTests` | **COMPLETED** | `governance/.../evidence/unit-phase11-remediation.trx` |
| w1-supplier-fallback | Removed dealer-code fallback | Phase11 return tests | **COMPLETED** | same |
| w1-pack-classifier | `ProductPackBehaviorAuthority.cs` | `ProductPackBehaviorAuthorityTests`, pack/sale tests | **COMPLETED** | same |
| w1-condition-inspection | Condition inspection on eligibility | `Phase11ReturnExchangePackSubLedgerTests` | **COMPLETED** | same |
| w1-legacy-return-path | `SaleReturnHandler.cs` fail-closed | Unit Phase11 | **COMPLETED** | same |
| w2-exchange-posting | `CommercialExchangeHandler.cs` SaleReturn posting | `Phase11DurablePostingIntegrationTests` | **COMPLETED** | `evidence/pg18-phase11-integration-v4/*.trx` |
| w2-exchange-posting-order | Outcome ledger after posting audits | Integration slice | **COMPLETED** | same |
| w2-thaka-posting-status | `ThakaCreditRefundHandlers.cs`, `OperationStatusQueryHandler.cs` | `ThakaPostgresTransactionIntegrationTests` | **COMPLETED** | same |
| w3-stocktake-opid | Stocktake desktop + remote services | `Phase11Wave3DesktopCutoverTests` | **COMPLETED** | `evidence/desktop-phase11-wave3-20261025/desktop-current.trx` |
| w3-desktop-exchange-demo | `BackendTransactionService`, `DemoTransactionService` | Gateway / demo fail-closed | **COMPLETED** | unit + desktop TRX |
| w3-remote-pos-workflow | `RemotePosWorkflowService` parity | Desktop cutover | **COMPLETED** | desktop TRX |
| w4-verify-ladder | Full solution | Build + unit + PG18 script | **COMPLETED** | build logs + TRX paths above |
| w0-owner-gates | `REMEDIATION_OWNER_DECISIONS.md` | N/A | **COMPLETED** | `docs/governance/phase11/REMEDIATION_OWNER_DECISIONS.md` |
| w5-governance-d05 | Manifest + closure certificate | Final21 matrix (partial) | **CLOSED** | `EDGE_RETAILS_PHASE11_FINAL_CERTIFICATION_AND_CLOSURE.md`, `SOURCE_AFTER_MANIFEST.json` |
| w5-pos82-* | `PostgresFixtureProfile` Master | 82 FQNs in POS-82 register | **PENDING** | `scripts/Invoke-Pos82OwnedProfileGate.ps1` (sample only) |

**Closure:** Phase 11 certified closed on executable ladder (2076 tests). Optional pre-prod only: independent D05 reviewer, POS-82 full register, full IntegrationTests assembly.

**Canonical PG18 evidence path:** `governance/10-execution-runs/ER-RUN-POS-PHASE11-REMEDIATION-IMPLEMENTATION/evidence/pg18-phase11-integration-v4/`
