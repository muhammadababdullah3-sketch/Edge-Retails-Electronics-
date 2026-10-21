import subprocess
import os
from pathlib import Path

pw_file = Path(r"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005\provpw.tmp")
password = pw_file.read_text(encoding="utf-8").strip()

env = os.environ.copy()
env["PGPASSWORD"] = password

queries = {
    "A": "SELECT id, invoice_number, subtotal, grand_total, completed_at FROM sales.sales WHERE completed_at >= '2035-01-10 00:00:00+00' AND completed_at < '2035-01-11 00:00:00+00' ORDER BY completed_at;",
    "B_audit_events": "SELECT id, action, entity_type, entity_id, correlation_id, summary, occurred_at FROM audit.business_events WHERE action LIKE '%EXPENSE%' OR action = 'FINANCIAL_POSTING' ORDER BY occurred_at DESC LIMIT 10;",
    "B_posting_audits": "SELECT id, operation_type, entity_id, entity_number, posting_date, shop_date, event_timestamp_utc FROM posting.posting_audit_entries ORDER BY event_timestamp_utc DESC LIMIT 5;",
    "C": "SELECT id, invoice_number, subtotal, grand_total, completed_at FROM sales.sales WHERE completed_at >= '2035-01-12 00:00:00+00' AND completed_at < '2035-01-13 00:00:00+00' ORDER BY completed_at;",
    "D": "SELECT id, movement_type, reference_type, reference_id, recognized_loss_amount, occurred_at FROM inventory.movements WHERE occurred_at >= '2035-02-15 00:00:00+00' AND occurred_at < '2035-02-16 00:00:00+00' AND recognized_loss_amount > 0 ORDER BY occurred_at;"
}

for k, q in queries.items():
    cmd = [r"C:\Program Files\PostgreSQL\18\bin\psql.exe", "-h", "127.0.0.1", "-p", "55437", "-U", "er_provisioner", "-d", "edgeretails_test_run20261012_001", "-c", q]
    res = subprocess.run(cmd, env=env, capture_output=True, text=True)
    print(f"=== Golden Scenario {k} Diagnostic Output ===")
    print(res.stdout)
    if res.stderr:
        print("STDERR:", res.stderr)
