# Evidence Crosswalk: Run 076

**Run ID:** `ER-RUN-LOCALFLOW-20261019-076`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## Catalog of Deposited Evidence

```json
{
  "run076_desktop_performance_safety_tests.trx": {
    "sha256": "2feede65edc3b1c1131e0a281050cb7238f92b22b6fc0692d23d59d47d44bc86",
    "size_bytes": 664539,
    "classification": "CONTEMPORANEOUS_RUN076_EVIDENCE",
    "tests_passed": 409,
    "tests_total": 409,
    "duration": "25 s"
  },
  "run076_unit_tests.trx": {
    "sha256": "2d172038f70196dfb45187df2f788cef1e634194eb83679f51255a5c926adba7",
    "size_bytes": 2029240,
    "classification": "CONTEMPORANEOUS_RUN076_EVIDENCE",
    "tests_passed": 1374,
    "tests_total": 1374,
    "duration": "17 s"
  },
  "build.log": {
    "sha256": "2a5ebe854f3ca197d4086e6b5f909fb54cafa978860efffca5304686b930844c",
    "size_bytes": 2021,
    "classification": "CONTEMPORANEOUS_RUN076_EVIDENCE",
    "command": "dotnet build",
    "warnings": 0,
    "errors": 0,
    "projects_built": 12
  },
  "run044_historical_thaka_pack_subledger_tests.trx": {
    "sha256": "9a09c553b7a1ed136ee0c9b0f41981e9ba71c22a6abce0e1d7b2179c3618d7c4",
    "size_bytes": 22352,
    "classification": "HISTORICAL_PRIOR_EVIDENCE_RECONCILED",
    "origin_date": "2026-10-18T21:50:00 local",
    "tests_passed": 14,
    "tests_total": 14,
    "note": "Reconciled as historical prior evidence; no disposable PG18 execution claimed in Run 076."
  },
  "run044_historical_postgresql_read_contract_tests.trx": {
    "sha256": "1ce7a12161a2dbf5b4b520bd4ae6d6bd413a82ed07bbe964f419aca5bcc868e5",
    "size_bytes": 10264,
    "classification": "HISTORICAL_PRIOR_EVIDENCE_RECONCILED",
    "origin_date": "2026-10-18T06:24:00 local",
    "tests_passed": 5,
    "tests_total": 5,
    "note": "Reconciled as historical prior evidence; no disposable PG18 execution claimed in Run 076."
  }
}
```

## Matrix of Claims vs Verified Evidence

1. **J1 Pre-POST vs Retry Operation ID Reuse**: Verified by `Targeted_J1` in `run076_desktop_performance_safety_tests.trx`. Single server cash effect qualified as UNPROVEN / OPEN.
2. **J2 Command Boundary & Reversal Handling**: Verified by `Targeted_J2` in `run076_desktop_performance_safety_tests.trx`. Invoked all 5 commands via `ICommand.Execute` with 0 unhandled escaping exceptions.
3. **J3 Stock Adjustment Recovery**: Verified by `Targeted_J3` in `run076_desktop_performance_safety_tests.trx` (mock proxy seam).
4. **J4 Warranty Identity & Partial Claims**: Verified by `Targeted_J4` in `run076_desktop_performance_safety_tests.trx` (production `WarrantyViewModel` boundary).
5. **J5 Cursor Pagination & Cycle Detection**: Verified by `Targeted_J5` in `run076_desktop_performance_safety_tests.trx` (fake HTTP paging seam).
6. **Solution Compilation**: Verified by raw deposited `build.log` (12 projects, 0 errors, 0 warnings).
7. **Regression Safety**: Verified by `run076_unit_tests.trx` (1,374 passed out of 1,374) and `run076_desktop_performance_safety_tests.trx` (409 passed out of 409).
8. **Historical Integration Prior Evidence**: Reconciled Pack (14 cases) and PG read contract (5 cases) TRX from 18 Oct.
