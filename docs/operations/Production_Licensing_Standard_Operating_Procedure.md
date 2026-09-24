# Edge Retails — Production Licensing Standard Operating Procedure (SOP)

**Document Version:** 1.0.0  
**Phase:** Phase 6 — Final Certification & Long-Term Maintenance  
**Document ID:** ER-OPS-LIC-01  
**Target Systems:** Windows 10/11 Pro (x64/ARM64), Windows Server 2022/2025  
**File Format:** Signed Production License (`.erlic`)  
**Cryptographic Algorithm:** RSA-2048 with SHA-256 (RS256)

---

## 1. Overview & Architectural Policy

Edge Retails enforces fail-closed production licensing for all production deployments. The system prohibits in-memory or demo fallback modes when running in production (`DOTNET_ENVIRONMENT=Production` or `--production`).

### Key Principles
1. **Authoritative Format:** The production license file must use the extension `.erlic`. (Legacy `.lic` and `.key` files are rejected or redirected).
2. **Asymmetric Cryptography:** Licenses are cryptographically signed by the vendor using an RSA-2048 private key. Clients verify the signature using the vendor master RSA-2048 public key.
3. **Hardware Fingerprint Binding:** Each license payload is bound to the target machine's unique hardware identity (CPU, Motherboard, and Disk identifiers) via `IDeviceIdentityProvider` (`WindowsMachineIdentityProvider`).
4. **Strict Key Material Separation:** Clients must **never** possess or store private key material. Any presence of private keys in public key configuration or storage results in an immediate fail-closed `InvalidOperationException`.

---

## 2. Production License File Format (`.erlic`)

An authoritative `.erlic` license is a JSON document containing the Base64-encoded UTF-8 JSON payload and its cryptographic RSA-SHA256 signature:

```json
{
  "version": 1,
  "payload": "<Base64-Encoded-LicensePayload-Json>",
  "signature": "<Base64-Encoded-RSA-SHA256-Signature>",
  "algorithm": "RS256"
}
```

### Decoded Payload Structure
```json
{
  "licenseId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "customerId": "8b512c12-3294-4d6b-9c71-29e2f41c0a01",
  "customerName": "Edge Retails Electronics Hub",
  "deviceId": "WIN-MCH-7FA89B10D4",
  "issuedAtUtc": "2026-09-01T00:00:00Z",
  "expiresAtUtc": "2027-09-01T00:00:00Z",
  "maxTerminals": 10,
  "features": ["pos", "purchasing", "inventory", "thaka", "warranty", "reports"]
}
```

---

## 3. Public Key Distribution & Configuration

The client verifies license signatures using the authoritative vendor public key. The key resolution order is deterministic:

1. **Environment Variable Override:**  
   `EDGE_RETAILS_LICENSE_PUBLIC_KEY` — Contains either the literal PEM text (`-----BEGIN PUBLIC KEY-----...`) or the absolute path to a PEM file.
2. **Machine Keys Directory:**  
   `%ProgramData%\EdgeRetails\keys\license.pub` — Standard location populated during system provisioning or enterprise deployment.
3. **Embedded Vendor Master Public Key:**  
   Built into `ProductionLicensePublicKeyProvider` as the canonical default.

> [!CAUTION]
> **Zero Private Key Tolerance:**
> If `EDGE_RETAILS_LICENSE_PUBLIC_KEY` or `license.pub` contains private key headers (`BEGIN RSA PRIVATE KEY` or `BEGIN PRIVATE KEY`), the system throws `InvalidOperationException: License verification requires public-key material only. Private key material is forbidden.`

---

## 4. License Installation Procedures

### Method A: First-Run Desktop Setup Wizard (Interactive)
1. Launch `EdgeRetails.Desktop.exe`.
2. When the First Setup Wizard appears, navigate to the **License Activation** step.
3. Click **Browse License** and select the customer's signed `.erlic` license file.
4. The system automatically validates:
   - RSA-SHA256 signature against the vendor public key.
   - Machine identity match between the machine fingerprint and `deviceId`.
   - Expiration date (`expiresAtUtc > UtcNow`).
5. Upon successful validation, the license is saved to `%ProgramData%\EdgeRetails\license.erlic`.

### Method B: Silent Enterprise Deployment (Unattended)
For SCCM / Intune / GPO deployments:
1. Copy the provisioned `.erlic` file to:
   ```powershell
   Copy-Item ".\Store01.erlic" -Destination "C:\ProgramData\EdgeRetails\license.erlic" -Force
   ```
2. Set permissions so that the Edge Retails service account and users have Read access:
   ```powershell
   icacls "C:\ProgramData\EdgeRetails\license.erlic" /grant "Users:(R)"
   ```
3. Restart `EdgeRetails.Worker` service and launch `EdgeRetails.Desktop.exe`.

---

## 5. Machine Identity Fingerprint Generation

To obtain a machine's hardware fingerprint prior to license issuance:

```powershell
# Query unique machine hardware identifiers
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).ProcessorId
$board = (Get-CimInstance Win32_BaseBoard).SerialNumber
$disk = (Get-CimInstance Win32_DiskDrive | Select-Object -First 1).SerialNumber
$raw = "$cpu-$board-$disk"
$bytes = [System.Text.Encoding]::UTF8.GetBytes($raw)
$sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
$fingerprint = -join ($sha[0..15] | ForEach-Object { "{0:X2}" -f $_ })
Write-Host "Machine Hardware Fingerprint: $fingerprint"
```

---

## 6. Troubleshooting & Diagnostics

| Diagnostic Code | Root Cause | Operator Action |
| :--- | :--- | :--- |
| `LICENSE_CONFIGURATION_INVALID` | Missing or corrupted public key, or private key material mistakenly deployed. | Verify `EDGE_RETAILS_LICENSE_PUBLIC_KEY` contains public key only. Ensure `license.pub` does not contain private keys. |
| `LICENSE_INVALID` | Signature validation failure, tamper detected, or invalid Base64 payload. | Re-acquire clean `.erlic` file from vendor portal; verify file was not corrupted during transit. |
| `LICENSE_EXPIRED` | System clock is past `expiresAtUtc`. | Renew annual subscription and apply new `.erlic` file via setup or `%ProgramData%\EdgeRetails\license.erlic`. |
| `LICENSE_DEVICE_MISMATCH` | License was issued for a different hardware fingerprint. | Generate machine fingerprint (Section 5) and issue replacement license. |
| `LICENSE_MISSING` | No `.erlic` file found in `%ProgramData%\EdgeRetails\license.erlic`. | Complete setup wizard or place `.erlic` file in `%ProgramData%\EdgeRetails`. |
