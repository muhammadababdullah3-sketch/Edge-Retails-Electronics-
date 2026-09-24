# Agent C — Installer, Release Pipeline & Packaging Handoff

**Phase:** 6 — Final Certification  
**Agent:** C — Installer & Release Pipeline Specialist  
**Date:** 2026-09-24  
**Verdict:** ✅ **PASS**

---

## 1. Executive Summary

Agent C has audited, verified, and certified the Edge Retails Windows packaging and installation pipeline in accordance with Canonical Architecture Section 8.4 and 8.5.

The installation architecture strictly follows the **Zero Data Loss Invariant**: application binaries are isolated in `%ProgramFiles%\Edge Retails`, while persistent databases, secrets, keys, and transaction ledgers reside exclusively in dedicated directories `%ProgramData%\EdgeRetails` and `%LocalAppData%\EdgeRetails\Production`.

---

## 2. Key Deliverables Audited & Verified

### 2.1 WiX v4 MSI Package Definition
- **File:** `installer/EdgeRetails.Setup/Package.wxs`
- **Scope:** Per-machine installation to `ProgramFiles64Folder\Edge Retails`.
- **Upgrade Policy:** `MajorUpgrade` scheduled `afterInstallInitialize` (guarantees atomic binary replacement).
- **Data Protection:** Does NOT declare, manage, or remove any database or mutable business state directories in MSI component tables.

### 2.2 WiX v4 Bootstrapper Bundle
- **File:** `installer/EdgeRetails.Bootstrapper/Bundle.wxs`
- **Product:** `EdgeRetailsSetup.exe`
- **Payload:** Bundles the WiX v4 MSI with prerequisite detection (.NET 10 desktop runtime / self-contained fallback).

### 2.3 Automated Release Pipeline Script
- **File:** `scripts/Publish-Release.ps1` (133 lines)
- **Functions:**
  1. Compiles solution in Release mode.
  2. Publishes self-contained `win-x64` WPF executable with single-file packaging.
  3. Builds MSI package via WiX CLI.
  4. Builds Bootstrapper bundle via WiX CLI.
  5. Computes SHA-256 hashes and generates `release-manifest.json`.

### 2.4 Installer Data Preservation Static Security Audit
- **File:** `scripts/Verify-InstallerDataPreservation.ps1`
- **Execution Result:** `Installer data-preservation static audit PASS` (verified by live PowerShell execution).

### 2.5 Lifecycle Preservation Integration Test
- **File:** `scripts/Test-InstallerLifecyclePreservation.ps1` (140 lines)
- **Coverage:** Rehearses fresh install -> write sentinel data -> major upgrade -> verify sentinel data preserved -> uninstall -> verify database intact.

---

## 3. Verdict: PASS ✅
The installer and release pipeline specifications are verified, compliant with architecture requirements, and certified for production release.
