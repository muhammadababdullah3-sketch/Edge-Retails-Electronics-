# Edge Retails CENTRAL-ONE — Consolidated Schema & Entity Lifecycle (Waves 1–6)

## 1. Schema Isolation & Complete Table Inventory

All Central tables reside exclusively within the PostgreSQL schema `central`. No tables are created in the `public` schema, and zero shop operational tables (`sales`, `inventory`, `cash_sessions`, `subledger`) are referenced or created.

### 1.1 Complete Central Table Inventory (24 Tables)

| Table Name | Entity Class | Wave | Primary Responsibility |
|---|---|:---:|---|
| `__ef_migrations_history` | EF Core Migration History | 1 | Schema migration tracking scoped to `central` schema |
| `admin_users` | `AdminUser` | 1 | Admin user identity, PBKDF2 hash, salt, TOTP secret, security revision |
| `admin_sessions` | `AdminSession` | 1 | Active authenticated admin sessions, idle/absolute timestamps, token hashes |
| `admin_roles` | `AdminRole` | 1 | Defined RBAC roles and assigned granular capability string sets |
| `admin_user_roles` | `AdminUserRole` | 1 | Many-to-many join between admin users and admin roles |
| `login_challenges` | `LoginChallenge` | 1 | Short-lived pre-auth challenges (`businessAuthority: false`) |
| `step_up_grants` | `StepUpGrant` | 1 | Action-scoped fresh step-up authorization grants |
| `operation_records` | `CentralOperationRecord` | 1 | Immutable idempotency ledger `(Scope, OpId, OpType, Sha256)` |
| `audit_records` | `CentralAuditRecord` | 1 | Append-only audit trail with actor, action, timestamp, and details |
| `outbox_messages` | `CentralOutboxMessage` | 1/5 | Transactional outbox messages for reliable downstream delivery |
| `outbox_consumer_effects` | `CentralConsumerEffect` | 5 | Deduplication ledger for outbox consumers (`ON CONFLICT DO NOTHING`) |
| `signing_keys` | `CentralSigningKeyRecord` | 1 | Registered RS256 public keys, algorithm, validity, and status |
| `signing_requests` | `CentralSigningRequest` | 1/5 | Historical record of signing requests, canonical payloads, and digests |
| `trust_sets` | `CentralTrustSetRecord` | 1 | Versioned snapshots of active public keys and revoked key IDs |
| `customers` | `Customer` | 2 | Customer commercial entity, name, billing email, status |
| `shops` | `Shop` | 2 | Shop entity, unique alphanumeric code, legal name, status |
| `plan_catalog_items` | `PlanCatalogItem` | 2 | Commercial catalog items (Target, TermKind, Price, Currency) |
| `pricing_snapshots` | `PricingSnapshot` | 2 | Immutable pricing snapshot bound to shop at order creation |
| `orders` | `Order` | 2 | Commercial purchase order, status (`DRAFT`, `PAID_APPROVED`, etc.) |
| `payment_evidences` | `PaymentEvidence` | 2 | Uploaded payment proof metadata and reference to object storage |
| `payment_review_records` | `PaymentReviewRecord` | 2 | Admin manual review record approving/rejecting payment proofs |
| `upload_sessions` | `UploadSession` | 2 | Short-lived session for chunked or direct proof upload |
| `commercial_licenses` | `CommercialLicense` | 2 | Issued commercial license, term kind, authority revision, single-primary |
| `commercial_entitlements` | `CommercialEntitlement` | 2 | Independent add-on entitlements (Support / Premium Upgrade) |
| `installations` | `Installation` | 2 | Registered POS workstation installation, machine fingerprint, RSA key |
| `activation_tokens` | `ActivationToken` | 2 | Time-limited high-entropy token for workstation registration |
| `activation_redemptions` | `ActivationRedemption` | 2 | Record of redeemed activation token and resulting installation |
| `license_certificates` | `LicenseCertificate` | 2 | Cryptographically signed lease certificate issued to installation |
| `hardware_reviews` | `HardwareReview` | 2 | Workflow record for approving hardware replacement / fingerprint drift |
| `primary_transfers` | `PrimaryTransfer` | 2 | Controlled reassignment of Primary POS authority between machines |
| `sensitive_approval_requests`| `SensitiveApprovalRequest` | 2 | Dual-admin approval workflow for high-risk operations |
| `shop_enforcement_policies` | `ShopEnforcementPolicy` | 2 | Signed enforcement mode (`FULL_STOP` vs `CONFIGURABLE_EXTENSION`) |
| `encrypted_objects` | `EncryptedObjectRecord` | 3 | Authenticated ciphertext object metadata (AES-256-GCM, SHA-256) |
| `support_tickets` | `SupportTicket` | 3 | Customer support ticket, priority, status, attachments |

---

## 2. Comprehensive Entity Lifecycle & Transition Invariants

### 2.1 Commercial License (`CommercialLicense`)
```
[Paid Approved Order] ──► Issue() ──► ACTIVE
                                        │
                ┌───────────────────────┼──────────────────────┐
                ▼                       ▼                      ▼
             PAUSED                 SUSPENDED               EXPIRED
                │                       │               (Fixed-Term only)
                ▼                       ▼                      │
             ACTIVE                  ACTIVE                    ▼
                                                            REVOKED
```
* **LIFETIME Invariant:** When `TermKind == "LIFETIME"`, `ValidUntilUtc` is strictly `null`. It never transitions to `EXPIRED`.
* **FIXED_TERM Invariant:** Requires explicit `ValidUntilUtc`. Transitions to `EXPIRED` when `trustedTimeUtc > ValidUntilUtc`.
* **Authority Revision:** Any status mutation (`Pause`, `Resume`, `Suspend`, `Reinstate`, `Revoke`, `Renew`) increments `AuthorityRevision` by 1 and invalidates older signed certificate lineages.

### 2.2 Commercial Entitlement (`CommercialEntitlement`)
* Represents add-on features: `SUPPORT` and `PREMIUM_UPGRADE`.
* **Status Progression:** `NOT_PURCHASED` $\to$ `ACTIVE` $\to$ `EXPIRED`.
* **Addon Expiration Separation:** When `ValidUntilUtc` passes, `CheckExpiration` transitions entitlement to `StatusExpired`. The underlying `CommercialLicense` remains `ACTIVE`.

### 2.3 Installation & Terminal Lifecycle (`Installation`)
* **Registration:** Workstation submits machine fingerprint and public key PEM via `POST /api/licensing/v1/activate`. Initial status: `Registered`.
* **Approval:** Admin or automated rule approves installation $\to$ status: `Approved`.
* **Reconciliation:** Heartbeat via `POST /api/licensing/v1/reconcile` updates `LastHeartbeatUtc` and issues signed entitlement envelope.
* **Deactivation / Retirement:** Setting status to `Deactivated` or `Revoked` rejects subsequent reconciliations.

### 2.4 Transactional Outbox Messages (`CentralOutboxMessage`)
```
[Event Emitted] ──► PENDING (RetryCount = 0)
                       │
             ┌─────────┴─────────┐
             ▼                   ▼
    DISPATCHED (success)     FAILED (RetryCount++)
                                 │
                                 ├──(RetryCount < maxRetries)──► PENDING (backoff)
                                 │
                                 └──(RetryCount >= maxRetries)──► DEAD_LETTER (isolated)
```

### 2.5 Encrypted Storage Objects (`EncryptedObjectRecord`)
* **Creation:** Payload encrypted via AES-256-GCM; record stores `EncryptedObjectRecord` with `ContentSha256`, `CiphertextBytes`, `Nonce`, and `Tag`.
* **Storage Tier:** `HOT` (standard retrieval) $\to$ `WARM` (infrequent) $\to$ `COLD` (archive) $\to$ `TOMBSTONE` (purged).

### 2.6 Append-Only Audit Trail (`CentralAuditRecord`)
* Enforced by `AppendOnlyAuditInterceptor` at DbContext level.
* Any attempt to update (`EntityState.Modified`) or delete (`EntityState.Deleted`) throws `InvalidOperationException`.
* Sensitive parameters (passwords, TOTP seeds, private keys) are redacted prior to persistence.
