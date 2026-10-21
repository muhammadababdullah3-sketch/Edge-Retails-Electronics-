"""Test Coordinator Execution Script for ER-RUN-20261018-034.
Executes the consolidated test union targeting disposable PostgreSQL 18 on loopback port 55437.
Captures raw transcripts, TRX files, database custody, and before/after conservation proof.
"""
import os
import sys
import subprocess
import json
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

POS_DIR = Path(r"C:\Users\muham\OneDrive\Desktop\Point of Sale")
GOV_RUN_DIR = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261018-034")
TEST_RESULTS_DIR = GOV_RUN_DIR / "test-results"
PG_CUSTODY_DIR = GOV_RUN_DIR / "pg-custody"
TEST_RESULTS_DIR.mkdir(parents=True, exist_ok=True)
PG_CUSTODY_DIR.mkdir(parents=True, exist_ok=True)

TARGET_HOST = "127.0.0.1"
TARGET_PORT = 55437
TARGET_DB = "edgeretails_test_run20261012_001"
TARGET_USER = "er_provisioner"
CLUSTER_DIR = Path(r"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005")
PW_FILE = CLUSTER_DIR / "provpw.tmp"
PSQL_EXE = r"C:\Program Files\PostgreSQL\18\bin\psql.exe"

# 1. Fail-closed disposable PG18 custody assertion
assert PW_FILE.exists(), f"Password file {PW_FILE} not found."
PW = PW_FILE.read_text(encoding="utf-8").strip()

conn_str = f"Host={TARGET_HOST};Port={TARGET_PORT};Database={TARGET_DB};Username={TARGET_USER};Password={PW}"
os.environ["EDGE_RETAILS_TEST_DB"] = conn_str
os.environ["PGPASSWORD"] = PW

transcript_lines = []
def log(msg: str):
    ts = datetime.now(timezone.utc).isoformat()
    entry = f"[{ts}] {msg}"
    print(entry)
    transcript_lines.append(entry)

log("Starting Test Coordinator Execution for ER-RUN-20261018-034...")

def run_psql(args):
    cmd = [
        PSQL_EXE,
        "-h", TARGET_HOST,
        "-p", str(TARGET_PORT),
        "-U", TARGET_USER,
        "-d", TARGET_DB,
        "-v", "ON_ERROR_STOP=1",
        "-A", "-t"
    ] + args
    res = subprocess.run(cmd, capture_output=True, text=True)
    return res.returncode, res.stdout.strip(), res.stderr.strip()

# Verify PG18 Custody
rc, ver_out, ver_err = run_psql(["-c", "SELECT version();"])
assert rc == 0 and "PostgreSQL 18" in ver_out, f"Disposable PG18 verification failed: {ver_out} {ver_err}"
rc, port_out, _ = run_psql(["-c", "SHOW port;"])
assert port_out == str(TARGET_PORT), f"Port mismatch: {port_out}"
rc, db_out, _ = run_psql(["-c", "SELECT current_database();"])
assert db_out == TARGET_DB, f"Database mismatch: {db_out}"
rc, user_out, _ = run_psql(["-c", "SELECT current_user;"])
assert user_out == TARGET_USER, f"User mismatch: {user_out}"

custody_report = f"""POSTGRESQL 18 DISPOSABLE CUSTODY VERIFICATION
Run: ER-RUN-20261018-034
Coordinator Runtime: tc-pg18-run034-e143
Timestamp UTC: {datetime.now(timezone.utc).isoformat()}
Cluster Data Dir: {CLUSTER_DIR / 'pgdata'}
Target Host: {TARGET_HOST} (Strict loopback)
Target Port: {TARGET_PORT} (Strictly isolated from 5432)
Target Database: {TARGET_DB}
Target User: {TARGET_USER}
Database Engine: {ver_out}
Custody Status: FAIL_CLOSED_VERIFIED_AND_LOCKED
"""
(PG_CUSTODY_DIR / "CUSTODY_PROOF.txt").write_text(custody_report, encoding="utf-8")
log("[OK] PostgreSQL 18 custody verified and recorded.")

# Pre-test baseline survey
tables = [
    "catalog.categories",
    "catalog.products",
    "catalog.units",
    "catalog.companies",
    "inventory.units",
    "inventory.pack_allocations",
    "inventory.stock_balances",
    "inventory.unit_identity_claims",
    "sales.sales",
    "inventory.movements"
]
pre_counts = {}
for t in tables:
    rc, cnt, _ = run_psql(["-c", f"SELECT count(*) FROM {t};"])
    pre_counts[t] = int(cnt)
log(f"Pre-test baseline survey captured: {pre_counts}")

test_suites = [
    {
        "id": "catalog_v3_lifecycle",
        "description": "Catalog V3 Seed Lifecycle, Dry-Run, Upgrade, Concurrency & Rollback Rejection Tests (P11-IMP-040 & 041)",
        "project": "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj",
        "filter": "FullyQualifiedName~CatalogV3SeedLifecyclePostgresTests",
        "trx_file": "run034_catalog_v3_lifecycle.trx",
        "log_file": "run034_catalog_v3_lifecycle.log",
        "expected_tests": 9
    },
    {
        "id": "catalog_v3_acceptance",
        "description": "Catalog V3 Acceptance & Hierarchy Behavioral Tests (P11-IMP-040)",
        "project": "tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj",
        "filter": "FullyQualifiedName~Phase11CatalogV3AcceptanceTests",
        "trx_file": "run034_catalog_v3_acceptance.trx",
        "log_file": "run034_catalog_v3_acceptance.log",
        "expected_tests": 26
    },
    {
        "id": "task060_price_override",
        "description": "Task 060 Below-Cost Price Override Authority & Cash Integrity Tests (P11-IMP-060)",
        "project": "tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj",
        "filter": "FullyQualifiedName~Phase3PosPriceOverrideTests",
        "trx_file": "run034_task060_price_override.trx",
        "log_file": "run034_task060_price_override.log",
        "expected_tests": 5
    },
    {
        "id": "golden_and_owner_14cases",
        "description": "Run 032 Golden Scenarios A-D & Owner Reconciliation Multi-Period Matrix (P11-IMP-098 & P11-IMP-106)",
        "project": "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj",
        "filter": "FullyQualifiedName~Phase7Pass5GoldenScenariosPostgresTests|FullyQualifiedName~Phase7Pass5OwnerReconciliationPostgresTests",
        "trx_file": "run034_golden_and_owner_14cases.trx",
        "log_file": "run034_golden_and_owner_14cases.log",
        "expected_tests": 14
    },
    {
        "id": "sibling_financial_audit",
        "description": "Sibling Financial Audit & Expense Compensating Void Tests (P11-IMP-077 & P11-IMP-081)",
        "project": "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj",
        "filter": "FullyQualifiedName~Phase7Pass1ExpensePostgresTests|FullyQualifiedName~Phase7Pass5FinancePostgresTests",
        "trx_file": "run034_sibling_financial_audit.trx",
        "log_file": "run034_sibling_financial_audit.log",
        "expected_tests": 13
    },
    {
        "id": "integration_affected_union",
        "description": "Consolidated Integration Affected Union (14 Golden/Owner + 7 Pass1 Expense Tests)",
        "project": "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj",
        "filter": "FullyQualifiedName~Phase7Pass1ExpensePostgresTests|FullyQualifiedName~Phase7Pass5GoldenScenariosPostgresTests|FullyQualifiedName~Phase7Pass5OwnerReconciliationPostgresTests",
        "trx_file": "run034_integration_affected_union.trx",
        "log_file": "run034_integration_affected_union.log",
        "expected_tests": 21
    }
]

ns = {'ns': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
summary_results = []

for suite in test_suites:
    trx_path = TEST_RESULTS_DIR / suite["trx_file"]
    log_path = TEST_RESULTS_DIR / suite["log_file"]

    cmd = [
        "dotnet", "test",
        str(POS_DIR / suite["project"]),
        "-c", "Release",
        "--no-build",
        "--filter", suite["filter"],
        "--logger", f"trx;LogFileName={trx_path}"
    ]
    log(f"Executing suite: {suite['id']} ({' '.join(cmd)})...")
    res = subprocess.run(cmd, cwd=POS_DIR, capture_output=True, text=True)

    log_content = f"COMMAND: {' '.join(cmd)}\nEXIT_CODE: {res.returncode}\n\nSTDOUT:\n{res.stdout}\n\nSTDERR:\n{res.stderr}\n"
    log_path.write_text(log_content, encoding="utf-8")

    assert trx_path.exists(), f"TRX file {trx_path} was not created!"
    tree = ET.parse(trx_path)
    root = tree.getroot()

    counters = root.find(".//ns:ResultSummary/ns:Counters", ns)
    total = int(counters.get("total", "0")) if counters is not None else 0
    passed = int(counters.get("passed", "0")) if counters is not None else 0
    failed = int(counters.get("failed", "0")) if counters is not None else 0

    log(f"Suite {suite['id']}: total={total}, passed={passed}, failed={failed}, exit_code={res.returncode}")
    assert failed == 0, f"Suite {suite['id']} had {failed} test failures!"
    assert total >= suite["expected_tests"], f"Suite {suite['id']} expected at least {suite['expected_tests']} tests, got {total}"

    unit_results = root.findall(".//ns:UnitTestResult", ns)
    test_names = [r.get("testName") for r in unit_results]

    summary_results.append({
        "suite_id": suite["id"],
        "description": suite["description"],
        "project": suite["project"],
        "filter": suite["filter"],
        "trx_file": suite["trx_file"],
        "log_file": suite["log_file"],
        "total": total,
        "passed": passed,
        "failed": failed,
        "outcome": "PASSED" if failed == 0 and total > 0 else "FAILED",
        "test_names": test_names
    })

# Post-test baseline survey & verification
post_counts = {}
for t in tables:
    rc, cnt, _ = run_psql(["-c", f"SELECT count(*) FROM {t};"])
    post_counts[t] = int(cnt)
log(f"Post-test survey captured: {post_counts}")

categories_conserved = pre_counts["catalog.categories"] == post_counts["catalog.categories"]
companies_conserved = pre_counts["catalog.companies"] == post_counts["catalog.companies"]
log(f"Core taxonomy/operational categories conserved: {categories_conserved} ({post_counts['catalog.categories']} rows)")
log(f"Core company master conserved: {companies_conserved} ({post_counts['catalog.companies']} rows)")
assert categories_conserved, "Categories count changed!"
assert companies_conserved, "Companies count changed!"

# Save TEST_EXECUTION_SUMMARY.json
summary_doc = {
    "report_type": "RUN034_CONSOLIDATED_TEST_EXECUTION_SUMMARY",
    "run_id": "ER-RUN-20261018-034",
    "executed_at_utc": datetime.now(timezone.utc).isoformat(),
    "coordinator_runtime_id": "tc-pg18-run034-e143",
    "target_database": TARGET_DB,
    "target_port": TARGET_PORT,
    "authority": "OWNER-UPDATE-20261018-004",
    "pre_test_counts": pre_counts,
    "post_test_counts": post_counts,
    "categories_conserved": categories_conserved,
    "companies_conserved": companies_conserved,
    "total_suites_executed": len(summary_results),
    "total_tests_executed": sum(s["total"] for s in summary_results),
    "total_tests_passed": sum(s["passed"] for s in summary_results),
    "total_tests_failed": sum(s["failed"] for s in summary_results),
    "overall_outcome": "ALL_TESTS_PASSED",
    "suites": summary_results
}
(TEST_RESULTS_DIR / "TEST_EXECUTION_SUMMARY.json").write_text(json.dumps(summary_doc, indent=2), encoding="utf-8")
(TEST_RESULTS_DIR / "TEST_EXECUTION_TRANSCRIPT.txt").write_text("\n".join(transcript_lines), encoding="utf-8")
log("[OK] Test execution summary and raw transcript saved.")
print("=== CONSOLIDATED TEST UNION EXECUTION COMPLETE: 100% PASSED ===")
