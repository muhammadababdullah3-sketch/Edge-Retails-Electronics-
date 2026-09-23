# Phase 4 — Multi-Terminal & Operational Runtime Closure Report

**Document Version:** 1.0.0  
**Status:** **CLOSED & FORMALLY CERTIFIED**  
**Date:** September 23, 2026  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Database Engine:** PostgreSQL 18.x  

---

## 1. Executive Summary & Architectural Mandate

Phase 4 establishes the local LAN shop server and multi-terminal operational runtime for the Edge Retails POS platform, strictly fulfilling Canonical Architecture Sections 81, 82, 83, and 233.1.

Under Phase 4:
1. **Local LAN Shop Server (`EdgeRetails.Server`):** An ASP.NET Core Web API acts as the local shop command authority. It directly reuses existing Application and Domain handlers with **zero duplicate business logic** and **zero cloud control plane**. Terminals communicate exclusively over authenticated HTTPS/REST and **never connect directly to PostgreSQL**.
2. **Application Gateway Abstraction (`IApplicationGateway`):** Presentation ViewModels interact exclusively through `IApplicationGateway`. ViewModels have zero knowledge of standalone vs LAN mode, zero direct `DbContext` dependencies, and zero `if (LAN)` conditional branching.
3. **Terminal Identity & Quota Lifecycle:** Terminal identity (`TerminalId`) is decoupled from User identity (`UserId`). Statuses (`Active`, `Suspended`, `Revoked`) are enforced at the server. `MaxTerminals` quota is enforced under PostgreSQL transactional advisory locks. Terminals never receive database credentials or encryption keys.
4. **Network Connectivity State Machine:** Client-side connectivity machine transitions through `Connected`, `Degraded`, `Reconnecting`, and `Disconnected`. Offline authoritative writes are strictly prohibited; mutations in non-connected states fail closed immediately with error code `'network.offline_mutation_forbidden'`.
5. **Unknown-Outcome Replay Recovery:** On communication drops during in-flight mutations, the client replays with the identical `ClientOperationId`. The server's idempotency engine returns the committed transaction result (`WasExisting = true`) without duplicating inventory movements, cash movements, or outbox events.
6. **Reconnect Revalidation:** Upon reconnecting, the terminal queries authoritative server truth (product prices, sellable stock, supplier Khata balances, cash session status, and active counting stocktake locks) before resuming operations.
7. **10 Concurrency Races on PostgreSQL 18:** 10 mandatory multi-terminal concurrency races are verified live against disposable PostgreSQL 18.
8. **Forensic Certification:** Agent H conducted an independent adversarial audit, verifying 100% compliance with zero defects.

---

## 2. Multi-Agent Execution Record

Phase 4 was implemented and verified using real autonomous subagents coordinated by the Main Orchestrator:

| Agent | Conversation ID | Type / Role | Scope Assigned | Key Deliverables & Test Verification |
| :--- | :--- | :--- | :--- | :--- |
| **Main Orchestrator** | `a05820bd-be11-4daf-b0f9-f09329c9c85b` | Main Coordinator | Architecture Authority, Migration, Rehearsal, System Integration | Append-only migration `20260923071510_Phase4MultiTerminalSchema.cs`, EF drift verification, `Invoke-Phase4MultiTerminalRehearsal.ps1`, Closure Report |
| **Agent B & C** | `a9148fcb-905d-41a8-863b-33b0a22aebbc` | `self` / Gateway & Terminal Specialist | `IApplicationGateway`, Local/Remote Gateways, State Machine, Terminal Handlers | `Phase4GatewayAndTerminalUnitTests.cs`: **42 / 42 PASS**. Fail-closed write protection, header propagation, license quota checks. |
| **Agent A** | `4bff9a04-3e28-4d82-ae3f-bcb74d24a670` | `self` / LAN Server Specialist | `EdgeRetails.Server`, Middlewares, Controllers | `Phase4LanServerIntegrationTests.cs`: **26 / 26 PASS**. Terminal authentication, protocol version negotiation, maintenance mode guard. |
| **Agent D, E & F** | `d2b19be7-8f0f-46b4-98c0-3ceac475e268` | `self` / Replay & Revalidation Specialist | `AuthoritativeRevalidationHandler`, `OperationStatusQueryHandler`, Replay recovery | `Phase4UnknownOutcomeAndRevalidationTests.cs`: **22 / 22 PASS**. Revalidation of stock/prices/khata/locks, unknown-outcome replay recovery. |
| **Agent G** | `8452a4e7-2f34-464f-8dee-809ad18e6922` | `self` / Concurrency Specialist | 10 PostgreSQL 18 multi-terminal races | `Phase4MultiTerminalConcurrencyTests.cs`: **10 / 10 Concurrency Races PASS** against disposable PostgreSQL 18. |
| **Agent H** | `c64d7fd1-28f2-4a0b-b901-33b155a46d61` | `research` / Independent Forensic Verifier | Read-only adversarial audit across all Phase 4 code, tests, schema, and security | Full Forensic Certification Report: **VERDICT: FULLY CERTIFIED (PASS)**. Zero defects found. |

---

## 3. Subsystem Implementation Details

### 3.1 Local LAN Shop Server (`EdgeRetails.Server`)
- **Project Location:** `src/EdgeRetails.Server/EdgeRetails.Server.csproj`
- **Target Framework:** `net10.0` (ASP.NET Core Web API)
- **Database Boundary:** The server is the sole entity possessing PostgreSQL connection credentials and running EF Core migrations. Terminals receive zero connection strings.
- **Middleware Pipeline:**
  1. `ProtocolCompatibilityMiddleware`: Validates incoming `X-Protocol-Version` against `TerminalProtocol.CurrentProtocolVersion` (`"1.0.0"`). Incompatible versions rejected with HTTP 400 Bad Request (`protocol.incompatible`).
  2. `MaintenanceModeGuardMiddleware`: When maintenance mode is active (`EDGE_RETAILS_MAINTENANCE_MODE=1`), blocks mutating POST requests with HTTP 503 Service Unavailable (`system.maintenance_mode`), while keeping `/api/system/health` available.
  3. `TerminalAuthenticationMiddleware`: Validates `X-Terminal-Id` header on authenticated endpoints. Missing or unknown terminal returns HTTP 401 Unauthorized (`auth.terminal_id_missing` / `auth.terminal_unknown`). Suspended or Revoked status returns HTTP 403 Forbidden (`auth.terminal_suspended` / `auth.terminal_revoked`). Client secrets are authenticated with SHA-256 (`auth.invalid_secret`).
- **Controllers:** `SalesController`, `PurchasingController`, `FinanceController`, `WarrantyController`, `TerminalsController`, and `SystemController` route directly to Application handlers without duplicating business rules.

### 3.2 Application Gateway Abstraction (`IApplicationGateway`)
- **Interface:** `src/EdgeRetails.Application/Gateways/IApplicationGateway.cs`
- **Local Implementation:** `LocalApplicationGateway.cs` routes requests in-process via `IServiceProvider` for Standalone POS mode.
- **Remote Implementation:** `RemoteApplicationGateway.cs` serializes requests to `EdgeRetails.Server` over HTTP for LAN Terminal mode, attaching `X-Terminal-Id`, `X-Terminal-Secret`, and `X-Protocol-Version` headers.
- **Fail-Closed Offline Write Protection:** Both local and remote gateways inspect `CanMutate`. If connectivity is `Disconnected`, `Reconnecting`, or `Degraded`, all mutating commands fail closed immediately with error code `network.offline_mutation_forbidden` without writing shadow state or transmitting network calls.
- **UI Decoupling:** Presentation layer (`EdgeRetails.Desktop`) has zero `if (LAN)` conditionals and zero direct database dependencies.

### 3.3 Terminal Identity & Quota Lifecycle
- **Domain Model:** `src/EdgeRetails.Domain/SystemConfiguration/TerminalModels.cs` defines `Terminal` with properties `Id`, `TerminalCode`, `Name`, `Status`, `HardwareFingerprint`, `ProtocolVersion`, `LastKnownIpAddress`, `RegisteredAt`, `LastSeenAt`, `AuthSecretHash`.
- **Status Lifecycle:** `TerminalStatus.Active = 1`, `TerminalStatus.Suspended = 2`, `TerminalStatus.Revoked = 3`.
- **Quota Serialization:** `RegisterTerminalHandler` acquires a PostgreSQL transactional advisory lock `("terminal", "registration_quota")` to prevent race conditions during concurrent terminal registration, rejecting registrations that exceed `licenseState.Payload.MaxTerminals` with `terminals.capacity_exceeded`.

### 3.4 Unknown-Outcome Replay Recovery
- **Handler:** `OperationStatusQueryHandler.cs` queries committed records across `sales`, `returns`, `purchases`, `purchase_returns`, `purchase_voids`, `supplier_payments`, and `supplier_refunds` by `ClientOperationId`.
- **Handler Replay:** When an in-flight mutation times out or network drops, the client replays with the identical `ClientOperationId`. Handlers detect the existing committed record and return `WasExisting = true` with zero duplicate ledger movements or outbox events. Payload changes return `idempotency.payload_mismatch`.

### 3.5 Reconnect Revalidation
- **Handler:** `AuthoritativeRevalidationHandler.cs` provides a complete server truth snapshot upon terminal reconnection:
  - Validates terminal registration and non-revoked status.
  - Returns current server UTC time and protocol version compatibility.
  - Queries active cash session open/closed state (`CashSessionIsOpen`, `ActiveCashSessionId`).
  - Identifies products locked under an active counting stocktake (`ActiveStocktakeLockedProductIds`).
  - Returns authoritative catalog prices, versions, and sellable stock balances.
  - Returns live supplier Khata payable balances (`CurrentPayableBalance`).

---

## 4. PostgreSQL 18 Concurrency Verification

All 10 mandatory multi-terminal concurrency races were implemented and verified in `tests/EdgeRetails.IntegrationTests/Phase4MultiTerminalConcurrencyTests.cs` using real PostgreSQL 18 instances, `Barrier(2)` synchronization, and isolated DI scopes:

| Race # | Concurrency Scenario | Invariant Enforced | Verification Result |
| :---: | :--- | :--- | :---: |
| **Race 1** | Concurrent terminal registration racing against `MaxTerminals` | PostgreSQL transactional advisory lock (`pg_advisory_xact_lock`) prevents quota breach; allows exact quota capacity, rejects excess with `terminals.capacity_exceeded`. | **PASS** |
| **Race 2** | Concurrent sales from Terminal A & B competing for last serialized unit | Row-level locks and `IResourceLock` serialize sales; exactly 1 winner sells the unit, loser rejected, zero duplicate sales. | **PASS** |
| **Race 3** | Concurrent quantity sales exceeding lot balance | Advisory product locks serialize lot consumption; zero oversell, database balance remains strictly non-negative. | **PASS** |
| **Race 4** | Concurrent cash session mutation vs close from separate terminals | Row locks (`FOR UPDATE`) on cash session serialize operations; mutation rejected if session closed, or included in closing cash if committed first. | **PASS** |
| **Race 5** | Concurrent supplier Khata payments exceeding outstanding payable | Advisory locks on `resource:supplier-account:<id>` serialize ledger entries; prevents over-settlement with `supplier.payment_exceeds_payable`. | **PASS** |
| **Race 6** | Concurrent warranty claims on same serialized unit from two terminals | Advisory unit lock prevents duplicate active claims; exactly 1 claim created, second rejected with `warranty.active_claim_exists`. | **PASS** |
| **Race 7** | Concurrent purchase return vs sale on same inventory lot | Both operations acquire product advisory lock; exactly 1 succeeds, remaining stock strictly non-negative. | **PASS** |
| **Race 8** | Concurrent stocktake counting lock vs sale mutation | Active counting stocktake locks products; competing sale rejected with `inventory.stocktake_blocks_product`. | **PASS** |
| **Race 9** | Concurrent unknown-outcome replay vs fresh request with duplicate `ClientOperationId` | `IOperationLock` serializes execution; exactly 1 handler executes (`WasExisting=false`), replay recovers (`WasExisting=true`), zero duplicate ledger effects. | **PASS** |
| **Race 10** | Concurrent terminal heartbeat/mutation vs revocation | Revoked terminal status immediately enforced across database; subsequent heartbeats/mutations blocked with `terminals.revoked`. | **PASS** |

---

## 5. Schema Census & Verification Gates

### 5.1 Canonical Schema Census (63 Tables)
Database migration `20260923071510_Phase4MultiTerminalSchema.cs` introduced table `system.terminals`, bringing the authoritative PostgreSQL schema to exactly **63 tables**:
- `catalog` (6): `categories`, `product_unit_barcodes`, `product_units`, `products`, `supplier_products`, `units`
- `finance` (10): `cash_movements`, `cash_sessions`, `expense_categories`, `expense_subcategories`, `expenses`, `supplier_account_entries`, `supplier_payment_reversals`, `supplier_payments`, `supplier_refund_reversals`, `supplier_refunds`
- `identity` (6): `permissions`, `role_permissions`, `roles`, `user_permission_overrides`, `user_sessions`, `users`
- `inventory` (14): `cost_states`, `lot_bucket_balances`, `lot_consumptions`, `lots`, `movement_effects`, `movement_units`, `movements`, `stock_adjustment_items`, `stock_adjustments`, `stock_balances`, `stocktake_items`, `stocktake_unit_checks`, `stocktakes`, `units`
- `parties` (2): `customers`, `suppliers`
- `sales` (12): `pos_draft_items`, `pos_drafts`, `quotation_items`, `quotation_operations`, `quotations`, `return_item_units`, `return_items`, `returns`, `sale_item_units`, `sale_items`, `sale_payments`, `sales`
- `system` (8): `__ef_migrations_history`, `document_sequences`, `installation_state`, `outbox_messages`, `receipt_template_settings`, `shop_profile`, `supplier_code_sequences`, `terminals`
- `warranty` (5): `claim_events`, `claim_item_units`, `claim_items`, `claims`, `shop_stock_cases`

### 5.2 Verification Gates Summary

```
================================================================================
PHASE 4 FORENSIC VERIFICATION MATRIX
================================================================================
Release Build:                       PASS (0 warnings, 0 errors, 9 projects)
Unit Test Suite:                     PASS (439 / 439 passed, 0 failed)
Phase 4 Unit Tests:                  PASS (64 / 64 passed)
Phase 4 Integration Tests:           PASS (36 / 36 passed: 26 Server + 10 Concurrency)
Phase 3 Safety Tests:                PASS (18 / 18 passed)
Phase 2 Regression Tests:            PASS (32 / 32 passed)
EF Core Model Drift:                 ZERO DRIFT (No changes made to model)
Canonical Architecture SHA-256:      MATCH (12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673)
Architecture Verifier:               PASS (234 sections, 840 fences)
Phase 4 Rehearsal:                   PHASE4_MULTI_TERMINAL_REHEARSAL_PASS
Phase 3 Rehearsal:                   PHASE3_PRODUCTION_SAFETY_REHEARSAL_PASS
Phase 2 Rehearsal:                   PHASE2_DISPOSABLE_POSTGRES_REHEARSAL_PASS
Agent H Forensic Verdict:            FULLY CERTIFIED (PASS) - Zero Defects
================================================================================
```

---

## 6. Formal Sign-Off

Phase 4 (Multi-Terminal & Operational Runtime) has satisfied all canonical architecture requirements, passed all live concurrency races against PostgreSQL 18, and completed independent adversarial forensic certification.

- **Phase 1:** CLOSED
- **Phase 2:** CLOSED
- **Phase 3:** CLOSED
- **Phase 4:** **CLOSED**

All development and test activity on Phase 4 is complete. In accordance with operational protocol, the system is now parked in a stable, verified green state awaiting the User's explicit command for Phase 5 or subsequent instructions.
