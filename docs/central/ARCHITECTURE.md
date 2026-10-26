# Edge Retails CENTRAL-ONE — Consolidated Architecture Specification (Waves 1–6)

## 1. Executive Summary and System Boundary

Edge Retails CENTRAL-ONE establishes the consolidated cloud control-plane, commercial governance, identity, licensing authority, and cryptographic trust root for the Edge Retails monorepo ecosystem. Spanning 6 engineering waves and 30 foundational tasks (`CONE-T01` through `CONE-T30`), Central-One provides centralized commercial authority while strictly enforcing the architectural autonomy and operational boundaries of local Point of Sale (POS) shops.

### 1.1 Authority Allocation and Strict Boundary Invariants
* **Point of Sale (POS) Local Authority:** Single shop / one Primary machine POS architecture. POS operational authority resides strictly within `Desktop -> loopback local server -> local PostgreSQL business authority`. The local POS database is the sole authority for sales transactions, inventory movements, physical unit intakes, cash sessions, khata credit ledgers, and subledger double-entry accounting. Central holds **zero** remote shop SQL execution authority and **zero** cloud transaction authority over stock, cash, or retail accounting.
* **Central Control-Plane Authority:** Central maintains exclusive, authoritative control over commercial contracts, admin identity and access governance (RBAC/MFA/Step-Up), licensing lifecycle and capability issuance, cryptographic trust roots and signing keys, activation token distribution, encrypted offsite object storage custody, and customer support coordination.
* **Single-Primary V1 Invariant:** Central enforces that each licensed shop possesses exactly one Primary workstation authorized for operational transactions (`PrimaryCapacity = 1`). Central never activates secondary, LAN, or multi-seat quota features in V1.
* **Monorepo and Namespace Boundary:** Shop POS and Central share one git repository but **separate deployables**. Central code lives only under `EdgeRetails.Central.*`, `EdgeRetails.ProtectedSigner`, `contracts/central/`, and `docs/central/`. Shop POS code lives under `src/EdgeRetails.{Domain,Application,Infrastructure,Server,Worker,Desktop,Recovery}`. Cross-cutting shop changes must not embed Central SQL or remote shop execution in the Central host; Central must not hold authority over live shop ledger data.

---

## 2. Monorepo Tiers and Project Layout

Central components reside entirely within dedicated project projects under the `EdgeRetails.Central.slnx` solution:

| Project / Directory | Namespace / Scope | Architectural Role |
|---|---|---|
| `src/EdgeRetails.Central.Domain/` | `EdgeRetails.Central.Domain.*` | Core domain entities, value objects, domain events, domain exceptions, and commercial invariants. Zero external dependencies. |
| `src/EdgeRetails.Central.Application/` | `EdgeRetails.Central.Application.*` | Application abstractions, command/query contracts, DTOs, canonical JSON serializer, operation ledger, outbox dispatcher, and workflow services. |
| `src/EdgeRetails.Central.Infrastructure/` | `EdgeRetails.Central.Infrastructure.*` | Persistence layer, `CentralDbContext` (scoped strictly to `central` schema), EF Core configurations, append-only audit interceptor, and `DatabaseTargetGuard`. |
| `src/EdgeRetails.Central.Api/` | `EdgeRetails.Central.Api.*` | ASP.NET Core host, REST controllers, secure cookie session handler, CSRF origin verification, rate limiting, and exception middleware. |
| `src/EdgeRetails.ProtectedSigner/` | `EdgeRetails.ProtectedSigner.*` | Isolated in-memory private key custody, RS256 PKCS#1 v1.5 signing engine, public trust set descriptors, and emergency key rollover. |
| `contracts/central/` | `contracts.schema.json`, `CONTRACT_ROUTES.json` | Formal JSON Schema transport specifications, canonical route definitions, and contract fixtures. |
| `docs/central/` | Runbooks, Architecture, Schema, Security | Operator guides, schema lifecycles, disaster recovery runbooks, and security threat matrices. |
| `tests/EdgeRetails.Central.UnitTests/` | `EdgeRetails.Central.UnitTests.*` | Isolated domain, application, and cryptographic unit tests (85 passing tests). |
| `tests/EdgeRetails.Central.IntegrationTests/` | `EdgeRetails.Central.IntegrationTests.*` | End-to-end integration tests targeting disposable PostgreSQL 18.6 on loopback port 55454 (64 passing tests). |

---

## 3. Wave-by-Wave Architecture Synthesis

Central-One was engineered and validated through six systematic waves:

### 3.1 Wave 1: Control-Plane Foundation & Cryptographic Custody (`CONE-T01` – `CONE-T06`)
* **Operation Identity & Idempotency:** Universal tuple $(\text{ActorScope}, \text{ClientOperationId}, \text{OperationType}, \text{PayloadSha256})$. Canonical serialization ensures deterministic property ordering (`StringComparer.Ordinal`) and SHA-256 digests. Replaying completed operations returns recorded outcomes; payload tampering triggers `PAYLOAD_HASH_MISMATCH`.
* **Transactional Atomicity:** Mutating operations execute in a unified transaction spanning Domain Mutation + Outcome Record + Append-Only Audit Record + Outbox Message.
* **RS256 Protected Signer:** In-memory private key custody (`IPrivateKeyStore`) isolated from API and database. Issues RS256 PKCS#1 v1.5 signatures verified against POS `RsaSha256LicenseSignatureVerifier`.
* **Database Target Guard:** Fail-closed guard strictly enforcing non-operational, disposable loopback PostgreSQL 18 targets and rejecting default ports (5432, 7150) or forbidden database names.

### 3.2 Wave 2: Commercial Governance, Licensing & Activation (`CONE-T07` – `CONE-T14`)
* **Commercial Plan & Pricing Snapshots:** Immutable pricing snapshots lock unit prices and currency at order creation time.
* **Payment Review & Proof Evidence:** Manual payment review workflow (`PaymentReviewRecord`) tracking reviewer identity, reviewed amounts, approval decisions, and upload sessions.
* **Commercial Term Invariant (LIFETIME Base + Independent Add-ons):**
  * `LIFETIME` base commercial license has strictly `validUntilUtc = null` and indefinite commercial validity.
  * `FIXED_TERM` support and premium add-ons (`CommercialEntitlement`) carry finite expiration dates. Expiration of support does not invalidate or expire the underlying Lifetime base license.
* **Terminal Activation & Certificate Lineage:** Activation token generation (`ActivationToken`), single-use redemption (`ActivationRedemption`), hardware review approval workflows (`HardwareReview`), and RSA key-bound license certificate issuance (`LicenseCertificate`).
* **Primary Transfer & Dual Approval:** Controlled reassignment of Primary POS authority (`PrimaryTransfer`), single-primary capacity enforcement, and dual-admin sensitive action approvals (`SensitiveApprovalRequest`).

### 3.3 Wave 3: Encrypted Object Storage & Customer Support (`CONE-T15` – `CONE-T19`)
* **Authenticated Encrypted Storage:** AES-256-GCM envelope encryption for offsite ciphertext backups. SHA-256 content addressing, per-object metadata custody, and quota enforcement per shop.
* **Support Ticket Lifecycle:** Ticket creation (`SupportTicket`), priority triaging, status transitions (`OPEN` $\to$ `IN_PROGRESS` $\to$ `RESOLVED` $\to$ `CLOSED`), and attachment binding to encrypted storage.

### 3.4 Wave 4: Admin Portal, Customer 360 & Metrics (`CONE-T20` – `CONE-T23`)
* **Admin Portal Session Context:** Active session tracking, session revocation across devices, and RBAC context delivery.
* **Authoritative Metrics & Customer 360:** Aggregated shop metrics (`activeShopsCount`, `lifetimeBaseCount`, `fixedTermBaseCount`, `expiringFixedTermBasesCount`, `expiringAddonsCount`). Strict exclusion of lifetime base licenses from expiring license counts. Customer 360 aggregates license state, addons, orders, and tickets with tenant isolation.
* **Queue Triage:** Paginated admin queues for pending payments, hardware reviews, transfer approvals, and support tickets.

### 3.5 Wave 5: Outbox Isolation, Reconciliation & Operations Replay (`CONE-T24` – `CONE-T27`)
* **Transactional Outbox Dead-Letter Isolation (`CONE-T24`):** Outbox messages progress from `PENDING` $\to$ `DISPATCHED` or `FAILED`. Upon reaching `maxRetries` (default 5), messages transition to `DEAD_LETTER` and are isolated from normal polling loops. Consumer effect deduplication uses PostgreSQL `INSERT ... ON CONFLICT ("MessageId") DO NOTHING` on `central.outbox_consumer_effects`.
* **Cryptographic Entitlement Reconciliation (`CONE-T25`):** `POST /api/licensing/v1/reconcile` processes heartbeat reconciliation, validates hardware fingerprints against approved installations or hardware reviews, verifies lineage, and issues cryptographically signed `SignedEntitlementDto` envelopes via `IProtectedSigningEngine`.
* **Operations Outcome Replay & Scope Isolation (`CONE-T26`):** Exposes immutable execution status over `/api/admin/v1/operations/{id}` and `/api/licensing/v1/operations/{id}` with cross-tenant and actor-scope isolation.
* **Disaster Recovery Rehearsal (`CONE-T27`):** Target guard validated restore rehearsal verifying non-operational disposable loopback targets and fail-closed rejections.

### 3.6 Wave 6: Reconciliation Union, Runbooks & Final Governance Closure (`CONE-T28` – `CONE-T30`)
* **Proof Oracle Union (`CONE-T28`):** Complete automated integration suite binding and validating all 6 foundational proof oracles (`LIFE-BASE`, `LIFE-ADDON`, `LIFE-COUNTS`, `LIFE-DESKTOP`, `LIFE-SECURITY`, `TIMED-REGRESSION`).
* **Runbooks & Operational Documentation (`CONE-T29`):** Comprehensive procedures for cluster management, disaster recovery, outbox monitoring, key rollover, and provider handoff.
* **Immutable Delivery Packaging & Distinct Review (`CONE-T30`):** Complete 14-member delivery manifest, cryptographic SHA-256 digests, native TRX test proofs (149 passing tests), and governance state lock.

---

## 4. Key Invariant Matrix

| Invariant | Enforcement Mechanism | Failure / Violation Behavior |
|---|---|---|
| **No POS Directory Mutation** | Monorepo isolation boundaries | Build/harness reject if any file in `src/EdgeRetails.*` outside `Central` is touched |
| **Fail-Closed Database Guard** | `DatabaseTargetGuard` at connection initialization | Throws `DatabaseTargetGuardException`; rejects port 5432, port 7150, production DB names |
| **Single-Primary V1** | `CommercialLicense.PrimaryCapacity = 1`, `Installation.Approve` | Rejects secondary terminal activations; enforces transfer approval before machine migration |
| **LIFETIME Base Null Expiry** | `CommercialTerm` value object constructor | Throws `CentralDomainException("LIFETIME_MUST_HAVE_NULL_EXPIRY")` if expiry provided |
| **Independent Addon Expiry** | `CommercialEntitlement` separate lifecycle | Addon expiration marks addon `EXPIRED` while base license remains `ACTIVE` |
| **Outbox Deduplication** | PostgreSQL unique constraint on `MessageId` | `ON CONFLICT ("MessageId") DO NOTHING` ensures idempotent downstream processing |
| **Append-Only Audit Trail** | EF Core `AppendOnlyAuditInterceptor` | Throws `InvalidOperationException` on entity update or delete attempts |
| **Private Key Custody** | In-memory `IPrivateKeyStore` in `ProtectedSigner` | Keys cannot be queried via API or persisted in database |
