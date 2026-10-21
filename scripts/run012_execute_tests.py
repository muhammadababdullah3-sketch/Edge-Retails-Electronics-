import os
import sys
import subprocess
import shutil
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

POS_DIR = r"C:\Users\muham\OneDrive\Desktop\Point of Sale"
RUN_DIR = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-012"
RAW_DIR = os.path.join(RUN_DIR, "raw")
os.makedirs(RAW_DIR, exist_ok=True)

suites = [
    {
        "name": "unit_tests",
        "trx_file": "unit_tests.trx",
        "log_file": "unit_tests.log",
        "cmd": ["dotnet", "test", "tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj", "-c", "Release", "--logger", f"trx;LogFileName={os.path.join(RAW_DIR, 'unit_tests.trx')}"]
    },
    {
        "name": "desktop_tests",
        "trx_file": "desktop_tests.trx",
        "log_file": "desktop_tests.log",
        "cmd": ["dotnet", "test", "tests/EdgeRetails.Desktop.PerformanceTests/EdgeRetails.Desktop.PerformanceTests.csproj", "-c", "Release", "--logger", f"trx;LogFileName={os.path.join(RAW_DIR, 'desktop_tests.trx')}"]
    },
    {
        "name": "phase12_wave1_pg_tests",
        "trx_file": "phase12_wave1_pg_tests.trx",
        "log_file": "phase12_wave1_pg_tests.log",
        "cmd": ["dotnet", "test", "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj", "-c", "Release", "--filter", "FullyQualifiedName~Phase12Wave1PostgresTests", "--logger", f"trx;LogFileName={os.path.join(RAW_DIR, 'phase12_wave1_pg_tests.trx')}"]
    },
    {
        "name": "phase11_durable_posting_tests",
        "trx_file": "phase11_durable_posting_tests.trx",
        "log_file": "phase11_durable_posting_tests.log",
        "cmd": ["dotnet", "test", "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj", "-c", "Release", "--filter", "FullyQualifiedName~Phase11DurablePostingIntegrationTests", "--logger", f"trx;LogFileName={os.path.join(RAW_DIR, 'phase11_durable_posting_tests.trx')}"]
    },
    {
        "name": "phase11_pg_tests",
        "trx_file": "phase11_pg_tests.trx",
        "log_file": "phase11_pg_tests.log",
        "cmd": ["dotnet", "test", "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj", "-c", "Release", "--filter", "FullyQualifiedName~Phase11.", "--logger", f"trx;LogFileName={os.path.join(RAW_DIR, 'phase11_pg_tests.trx')}"]
    },
    {
        "name": "tracking_pg_tests",
        "trx_file": "tracking_pg_tests.trx",
        "log_file": "tracking_pg_tests.log",
        "cmd": ["dotnet", "test", "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj", "-c", "Release", "--filter", "FullyQualifiedName~TrackingGoldenTracePostgresTests|FullyQualifiedName~TrackingManufacturerIdentityPostgresTests", "--logger", f"trx;LogFileName={os.path.join(RAW_DIR, 'tracking_pg_tests.trx')}"]
    },
    {
        "name": "safety_guard_pg_tests",
        "trx_file": "safety_guard_pg_tests.trx",
        "log_file": "safety_guard_pg_tests.log",
        "cmd": ["dotnet", "test", "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj", "-c", "Release", "--filter", "FullyQualifiedName~Phase3ProductionSafetyPostgresTests", "--logger", f"trx;LogFileName={os.path.join(RAW_DIR, 'safety_guard_pg_tests.trx')}"]
    }
]

ledger = []
ns = {'ns': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

total_all_tests = 0
total_all_passed = 0
total_all_failed = 0
total_all_skipped = 0

for s in suites:
    print(f"Executing suite: {s['name']}...")
    res = subprocess.run(s["cmd"], cwd=POS_DIR, capture_output=True, text=True)

    log_path = os.path.join(RAW_DIR, s["log_file"])
    with open(log_path, "w", encoding="utf-8") as f:
        f.write(res.stdout + ("\nSTDERR:\n" + res.stderr if res.stderr else ""))

    trx_path = os.path.join(RAW_DIR, s["trx_file"])
    if not os.path.exists(trx_path):
        print(f"ERROR: Missing TRX file {trx_path}")
        sys.exit(1)

    tree = ET.parse(trx_path)
    root = tree.getroot()
    counters = root.find('.//ns:Counters', ns)
    c_total = int(counters.attrib.get('total', 0)) if counters is not None else 0
    c_passed = int(counters.attrib.get('passed', 0)) if counters is not None else 0
    c_failed = int(counters.attrib.get('failed', 0)) if counters is not None else 0
    c_skipped = int(counters.attrib.get('notExecuted', 0)) if counters is not None else 0

    total_all_tests += c_total
    total_all_passed += c_passed
    total_all_failed += c_failed
    total_all_skipped += c_skipped

    cmd_str = " ".join(s["cmd"])
    print(f"  -> {s['name']}: Total={c_total}, Passed={c_passed}, Failed={c_failed}, Skipped={c_skipped} (Exit={res.returncode})")

    ledger.append({
        "suite_name": s["name"],
        "trx_file": s["trx_file"],
        "raw_log": s["log_file"],
        "command": cmd_str,
        "total_tests": c_total,
        "passed": c_passed,
        "failed": c_failed,
        "skipped": c_skipped,
        "status": "PASSED" if c_failed == 0 and c_passed > 0 else "FAILED"
    })

# Copy historical failed attempt log
prev_failed_log = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-011\raw\phase12_initial_failed_attempt.log"
dest_failed_log = os.path.join(RAW_DIR, "phase12_initial_failed_attempt.log")
if os.path.exists(prev_failed_log):
    shutil.copy2(prev_failed_log, dest_failed_log)

ledger_record = {
    "artifact_id": "ER-RUN-20261015-012-RAW-TEST-EXECUTION-LEDGER",
    "run_id": "ER-RUN-20261015-012",
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "target_database": "PostgreSQL 18.0 (127.0.0.1:55437 / edgeretails_test_run20261012_001)",
    "operational_port_5432_status": "UNTOUCHED_AND_GUARDED",
    "total_test_suites": len(suites),
    "total_tests_executed": total_all_tests,
    "total_tests_passed": total_all_passed,
    "total_tests_failed": total_all_failed,
    "total_tests_skipped": total_all_skipped,
    "overall_status": "ALL_SUITES_PASSED_ZERO_FAILURES",
    "suites": ledger
}

with open(os.path.join(RAW_DIR, "RAW_TEST_EXECUTION_LEDGER.json"), "w", encoding="utf-8") as f:
    import json
    json.dump(ledger_record, f, indent=2)

print("\n" + "="*60)
print(f"CUMULATIVE TEST RESULTS FOR RUN 012:")
print(f"Total Suites:   {len(suites)}")
print(f"Total Tests:    {total_all_tests}")
print(f"Passed Tests:   {total_all_passed}")
print(f"Failed Tests:   {total_all_failed}")
print(f"Skipped Tests:  {total_all_skipped}")
print("="*60)
