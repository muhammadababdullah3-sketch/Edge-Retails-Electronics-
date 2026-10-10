# Security Policy

## 🔒 Supported Versions

Only the certified production release branches and current candidate baseline receive security updates:

| Version / Baseline | Supported | Status |
| :--- | :--- | :--- |
| `candidate-r1` (Phase 7 Final21) | :white_check_mark: | Active Security Baseline |
| Phase 6 Release Packages | :white_check_mark: | Maintenance Mode |
| < Phase 6 | :x: | Deprecated / Superceded |

---

## 🛡️ Financial Integrity & Ledger Invariants

Edge Retails enforces strict security safeguards against financial tampering and unauthorized balance adjustments:

1. **Non-Tamper Double-Entry Ledger**:
   - Ledger entries cannot be deleted, modified, or overwritten by design.
   - Any modification attempt without a matching debit/credit pair is treated as a critical security invariant violation.

2. **Cryptographic Manifest Verification**:
   - Release binaries and authoritative architecture files are sealed with SHA-256 cryptographic manifests.
   - Changes to governance-controlled source files require recalculation and signature verification.

3. **Database Credentials & Secrets Management**:
   - Production secrets and database connection strings must never be committed to source control.
   - Environment variables (`EDGE_RETAILS_DB`, `EDGE_RETAILS_TEST_DB`) and machine-level credential stores must be used for configuration.

---

## 🚨 Reporting a Vulnerability

If you discover a security vulnerability or ledger inconsistency within Edge Retails:

1. **Do NOT open a public issue.**
2. Send a detailed report directly to the project security team.
3. Include:
   - Description of the vulnerability and attack vector.
   - Exact steps to reproduce (including transaction payloads or SQL scripts).
   - Potential impact on financial ledger balance or inventory counts.
4. The team will acknowledge receipt within 24 hours and provide an estimated remediation timeline.
