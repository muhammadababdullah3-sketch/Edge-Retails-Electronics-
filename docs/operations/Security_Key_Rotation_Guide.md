# Edge Retails — Production Security & Key Rotation Guide

**Document Identifier:** `ER-OPS-SEC-01`  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Canonical Architecture SHA-256:** `12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`  
**Security Scope:** Cryptographic Trust Boundaries, Key Lifecycles, and Rotation Procedures  
**Cryptographic Standards:** RS256 (RSA-SHA256), AES-256-GCM, HMAC-SHA256, PBKDF2-SHA256

---

## 1. Cryptographic Trust Domains & Separation of Concerns

Edge Retails strictly enforces **separation of cryptographic trust domains**. Keys from one domain cannot be repurposed, substituted, or combined with keys from another:

```
+---------------------------------------------------------------------------------------------------+
| ISOLATED CRYPTOGRAPHIC TRUST DOMAINS                                                              |
+---------------------------------------------------------------------------------------------------+
|  1. LICENSING & ENTITLEMENT DOMAIN                                                                |
|     - Primitives: RSA-2048 / RSA-4096 with SHA-256 (RS256).                                       |
|     - Private Key: Guarded in isolated vendor signing server / HSM; NEVER on client hardware.     |
|     - Public Key: Distributed with application binary (ILicensePublicKeyProvider).               |
+---------------------------------------------------------------------------------------------------+
|  2. MULTI-TERMINAL AUTHENTICATION DOMAIN                                                          |
|     - Primitives: SHA-256 / PBKDF2 with client-side secret transmission over HTTPS.               |
|     - Storage: Table system.terminals (auth_secret_hash) — salted cryptographic hash.             |
|     - Verification: Constant-time comparison (FixedTimeEquals) to prevent timing attacks.         |
+---------------------------------------------------------------------------------------------------+
|  3. MAINTENANCE BARRIER INTEGRITY DOMAIN                                                          |
|     - Primitives: Machine-local 32-byte random key with HMAC-SHA256.                              |
|     - Storage: Isolated file with hardened Windows ACLs (System / Current User / Admins only).    |
|     - Purpose: Authenticates production-maintenance.state.json to prevent privilege bypass.      |
+---------------------------------------------------------------------------------------------------+
|  4. BACKUP ENCRYPTION & AUTHENTICATION DOMAIN                                                     |
|     - Primitives: AES-256-GCM payload encryption + HMAC-SHA256 manifest authentication.           |
|     - Key Derivation: HMAC-SHA256 with domain label "EdgeRetails.BackupManifest.HMAC.V1".         |
|     - Purpose: Guaranteed confidentiality and tamper-evidence for offsite database archives.      |
+---------------------------------------------------------------------------------------------------+
```

---

## 2. Key Rotation Procedures

### 2.1 Domain 1: License Verification Key Rotation (Vendor Authority)
When rotating the master vendor RSA signing keypair:

1. **Dual-Key Staging (Version N+1):**
   - The new public key is introduced into `EdgeRetails.Infrastructure` alongside the existing public key:
     ```csharp
     public sealed class ChainedLicensePublicKeyProvider : ILicensePublicKeyProvider
     {
         public IEnumerable<string> GetSupportedPublicKeysPem() => [ NewPublicKeyPem, LegacyPublicKeyPem ];
     }
     ```
2. **Issue New Signed Envelopes:**
   - Active customer licenses are re-signed with the new private key and distributed via the licensing portal or automatic update channel.
3. **Deprecate Legacy Public Key:**
   - In subsequent releases, the legacy public key is removed once all endpoints have migrated.

### 2.2 Domain 2: Terminal HMAC & Client Secret Rotation
Terminal secrets authenticate LAN POS stations against `EdgeRetails.Server`. Secrets should be rotated semi-annually or immediately upon terminal hardware reassignment.

#### Step-by-Step Terminal Secret Rotation:
1. **Generate Cryptographically Secure Terminal Secret:**
   ```powershell
   # Generate 32-byte URL-safe base64 secret
   $bytes = New-Object byte[] 32
   [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
   $newSecret = [Convert]::ToBase64String($bytes)
   ```
2. **Compute Salted Hash for Server Storage:**
   The server stores `auth_secret_hash = SHA256(UTF8(secret + ":" + terminalId))`:
   ```powershell
   $terminalId = "TERM-002"
   $raw = [System.Text.Encoding]::UTF8.GetBytes("$newSecret`:$terminalId")
   $hash = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($raw)).Replace("-","").ToLower()
   ```
3. **Update Database Record:**
   ```sql
   UPDATE system.terminals
   SET auth_secret_hash = '$hash',
       last_seen_at = NOW()
   WHERE terminal_code = 'TERM-002';
   ```
4. **Deploy Secret to Terminal:**
   Update the terminal's local secure configuration (`appsettings.Production.json` or Windows Credential Manager):
   ```json
   {
     "EdgeRetails": {
       "TerminalId": "TERM-002",
       "TerminalSecret": "<newSecret>"
     }
   }
   ```
5. **Re-Test Authentication:**
   Verify terminal captures a fresh snapshot and reconnects with `auth.terminal_unknown` / `auth.invalid_secret` resolved.

### 2.3 Domain 3: Maintenance Barrier Key Rotation
Governed by `FileProductionMaintenanceIntegrityKeyProvider`. The barrier key prevents unauthorized actors from dropping the system out of `RecoveryRequired` without DBA intervention.

#### Procedure:
1. **Engage Maintenance Mode:**
   ```powershell
   & "C:\Program Files\Edge Retails\worker\EdgeRetails.Worker.exe" -- maintenance enter
   ```
2. **Delete Existing Integrity Key File:**
   ```powershell
   $keyPath = "$env:LOCALAPPDATA\EdgeRetails\Production\production-maintenance.key"
   Remove-Item -Path $keyPath -Force
   ```
3. **Re-initialize Integrity Key Provider:**
   Upon next boot, `FileProductionMaintenanceIntegrityKeyProvider` automatically generates a fresh 32-byte cryptographically random key via `RandomNumberGenerator.GetBytes(32)` and hardens Windows ACLs.
4. **Re-sign State Envelope:**
   The barrier automatically re-writes and HMAC-authenticates the current state envelope.

### 2.4 Domain 4: Backup Encryption Key Rotation
When rotating the AES-256-GCM encryption key for database backups:

1. **Configure New Key Material:**
   Update `EDGE_RETAILS_BACKUP_KEY` with a new 256-bit hexadecimal string:
   ```powershell
   [System.Environment]::SetEnvironmentVariable(
       "EDGE_RETAILS_BACKUP_KEY",
       "A4F1D7...<64 hex characters>...",
       [System.EnvironmentVariableTarget]::Machine
   )
   ```
2. **Execute On-Demand Validation Backup:**
   Immediately trigger a manual backup to confirm encryption and HMAC generation with the new key succeed:
   ```powershell
   dotnet run --project "src/EdgeRetails.Worker" -- backup
   ```
3. **Historic Key Ring Maintenance:**
   Retain previous backup keys in a secure offline vault so older `.erbak` archives can be decrypted for disaster recovery or tax compliance audits.

---

## 3. Host Hardening & Windows Access Control (ACL) Enforcement

Edge Retails automatically enforces strict Windows Security Identifiers (SIDs) on all sensitive runtime state directories:

```
+---------------------------------------------------------------------------------------------------+
| HARDENED WINDOWS ACL MATRIX (FileSecurity & DirectorySecurity)                                    |
|                                                                                                   |
|  Allowed Principals:                                                                              |
|  1. Current Authenticated User (Owner)       : FullControl (ContainerInherit | ObjectInherit)      |
|  2. NT AUTHORITY\SYSTEM (LocalSystemSid)     : FullControl (ContainerInherit | ObjectInherit)      |
|  3. BUILTIN\Administrators (AdministratorsSid): FullControl (ContainerInherit | ObjectInherit)     |
|                                                                                                   |
|  Explicit Rule:                                                                                   |
|  - SetAccessRuleProtection(isProtected: true, preserveInheritance: false)                         |
|  - All inherited permissions from Users, Guests, and Everyone are REMOVED.                        |
+---------------------------------------------------------------------------------------------------+
```

---

## 4. Compromise Response Playbook

If a terminal node is physically stolen, compromised by malware, or tampered with:

1. **Immediate Revocation on LAN Server:**
   Execute immediate terminal revocation to permanently block requests:
   ```sql
   UPDATE system.terminals
   SET status = 3, -- Revoked
       auth_secret_hash = 'REVOKED_' || md5(random()::text)
   WHERE terminal_code = 'STOLEN-TERM-01';
   ```
2. **Terminate Active Web API Sessions:**
   The `TerminalAuthenticationMiddleware` will reject subsequent requests with HTTP 403 Forbidden (`auth.terminal_revoked`).
3. **Audit Inspection:**
   Query `identity.audit_logs` to review all sales, returns, and cash movements recorded by the compromised terminal prior to revocation.
