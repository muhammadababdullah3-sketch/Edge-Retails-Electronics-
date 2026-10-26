# Edge Retails CENTRAL-ONE — Consolidated Security Model & Threat Matrix (Waves 1–6)

## 1. Authentication & Session Architecture

### 1.1 Password Hashing & Secret Protection
* **Algorithm:** PBKDF2 with HMAC-SHA256, 16-byte cryptographically secure random salt, and 100,000 iterations.
* **Storage:** Passwords and TOTP secrets are stored with salt. TOTP secret generation utilizes Base32 encoding with high-entropy cryptographic seeds.

### 1.2 Two-Stage Authentication Protocol
1. **Pre-Authentication Challenge (`POST /api/admin/v1/auth/login`):**
   * Verification of primary credentials.
   * On success, issues a short-lived `LoginChallenge` (5-minute TTL).
   * **Invariants:** Returns `businessAuthority: false`, `kind: "MFA_CHALLENGE"`, and zero application capabilities or session tokens.
2. **MFA Verification (`POST /api/admin/v1/auth/mfa`):**
   * Validates RFC 6238 TOTP proof against the challenge.
   * On valid verification, constructs a stateful `AdminSession` and issues a session cookie.
   * **Invariants:** Enforces `mfaSatisfied: true`. Pre-auth challenge is consumed atomically and cannot be replayed.

### 1.3 Session Security & Transport Controls
* **Cookie Attributes:** Session cookie `central_session` is issued with:
  * `HttpOnly: true` (prevents JavaScript access and XSS theft).
  * `Secure: true` (strictly HTTPS only).
  * `SameSite: SameSiteMode.Strict` (prevents cross-origin transmission).
* **Timeouts:**
  * Sliding Idle Timeout: 15 minutes of inactivity terminates the session.
  * Absolute Lifetime Timeout: Maximum 8 hours from creation.
* **Security Revision Revalidation:**
  * Any privilege mutation, role assignment, role revocation, or credential reset increments `AdminUser.SecurityRevision`.
  * Sessions record the user's `SecurityRevision` at login. When `session.SecurityRevision < user.SecurityRevision`, the session is rejected with HTTP 401 Unauthorized.

### 1.4 CSRF / Origin Protection
* Cookie-authenticated mutating requests (`POST`, `PUT`, `DELETE`, `PATCH`) are intercepted by `CsrfProtectionMiddleware`.
* Enforces validation of `Origin` or `Referer` headers against allowed hosts (`localhost`, configured base domain). Requests lacking valid origin headers are rejected with HTTP 403 Forbidden (`CSRF_REJECTED`).

### 1.5 Rate Limiting & Brute Force Throttling
* `RateLimitingMiddleware` monitors requests to `/api/admin/v1/auth/login` and `/api/admin/v1/auth/mfa`.
* Restricts requests to 5 attempts per IP per 1-minute sliding window. Excess requests are rejected with HTTP 429 Too Many Requests (`RATE_LIMITED`).

---

## 2. Authorization, Capabilities & RBAC

### 2.1 Granular Capability Model
Central strictly rejects universal `IsAdmin` or `SysAdmin` bypass flags. Permissions are expressed as explicit fine-grained capability strings:
* `admin.manage`: Admin user and role management.
* `admin.users.read`: Query and view admin user directories.
* `license.issue`: Commercial license issuance.
* `license.renew`: License term renewal.
* `license.policy`: Security policy update.
* `signer.sign`: Internal signing authority.
* `signer.rotate`: Cryptographic key rotation.
* `signer.revoke`: Emergency cryptographic revocation.
* `storage.read`: Query and download encrypted offsite storage.
* `support.manage`: Triage and manage customer support tickets.

### 2.2 Action-Bound Step-Up Grants (`StepUpGrant`)
Sensitive administrative operations require fresh step-up authorization:
* Step-up grants bind `(SessionId, AdminUserId, ActionScope, TargetPayloadSha256)`.
* Grants carry a short validity window (maximum 5 minutes) and can only be consumed once.
* Privilege or security revision updates immediately invalidate active step-up grants.

---

## 3. Cryptographic Authority & Key Custody

### 3.1 Protected Signing Engine (`ProtectedSigningEngine`)
* **Algorithm:** RS256 (`RSA-SHA256` with PKCS#1 v1.5 padding) conforming strictly to POS `RsaSha256LicenseSignatureVerifier`.
* **Isolated Private Key Custody:** Private key material (`IPrivateKeyStore`) is held exclusively in memory within `EdgeRetails.ProtectedSigner` and is inaccessible via external API endpoints or database storage.
* **Pre-Authorization Requirement:** Signing requires a pre-persisted, authorized `CentralSigningRequest` in `CentralDbContext`. Direct untracked signing is blocked.
* **Trust Set Versioning:** Central publishes public key descriptors via `GET /api/licensing/v1/trust-set`. Key statuses progress from `ACTIVE` to `VERIFY_ONLY` (rotation) to `REVOKED`. Emergency revocation increments the `EmergencyRolloverRevision`.

### 3.2 Authenticated Encrypted Storage
* Offsite ciphertext backups and support attachments use **AES-256-GCM** authenticated envelope encryption.
* Unique 96-bit random IV/nonce per object; 128-bit authentication tag guarantees confidentiality and tamper-evident integrity.
* SHA-256 content addressing prevents hash collision and detects bit rot.

---

## 4. Threat Matrix & Defense-in-Depth

| Threat | Attack Vector | Mitigation / Control |
|---|---|---|
| **Session Hijacking** | XSS / Cookie theft | `HttpOnly`, `Secure`, `SameSite=Strict` cookies, 15-min idle / 8-hr absolute timeout, TLS 1.3 |
| **CSRF Attacks** | Cross-origin form submission | Strict origin/referer header verification on all mutating routes (`CsrfProtectionMiddleware`) |
| **Brute Force Login** | Credential stuffing on auth routes | Rate limiting (max 5 attempts/min), PBKDF2-100k iteration stretching |
| **Pre-Auth Authority Escalation** | Calling business API with login challenge | Login challenges carry `businessAuthority: false` and grant zero API route access |
| **Privilege Persistence After Demotion** | Admin session continues after role revocation | Security revision bumping on user; active sessions with stale revisions fail immediately |
| **Payload Tampering / MITM** | Altering command body in transit | Server-side canonical JSON re-hashing; mismatched SHA-256 rejects with `PAYLOAD_HASH_MISMATCH` |
| **Idempotency Replay Attack** | Resending operation with altered payload | Operation identity tuple validation; duplicate `ClientOperationId` with different payload fails with `OPERATION_ID_CONFLICT` |
| **Audit Log Tampering** | Malicious admin deleting audit trail | EF Core `AppendOnlyAuditInterceptor` blocks `Update` and `Delete` at persistence layer |
| **Private Key Exfiltration** | Database dump or API extraction | In-memory private key custody (`ProtectedSigningEngine`); private keys are never stored in DB or exported via API |
| **Signature / Key Confusion** | Signing with revoked or cross-purpose key | Verification of key status (`ACTIVE`), validity dates, and purpose match before signing |
| **Outbox Message Duplication** | Multiple delivery attempts by worker | Consumer effect deduplication table with PostgreSQL unique constraint (`ON CONFLICT DO NOTHING`) |
| **Hostile Hardware Cloning** | Replaying stolen credentials on different PC | `POST /api/licensing/v1/reconcile` enforces hardware fingerprint matching against registered installation or approved review |
| **Production DB Overwrite** | Connecting Central to POS or default Postgres | Fail-closed `DatabaseTargetGuard` rejects ports 5432, 7150, forbidden database names, non-loopback IPs |
