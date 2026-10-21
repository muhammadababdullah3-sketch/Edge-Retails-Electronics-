import json

matrix_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-004\review\W4_FULL_SCAN_MATRIX.json"
with open(matrix_path, encoding="utf-8") as f:
    m = json.load(f)

print(f"Matrix tasks: {len(m['tasks'])}")
for t in m["tasks"]:
    print(f"{t['TaskId']:12} | {t['Task']:50} | {t['status']:35} | planned={t['planned_test_ids_count']:2} | verified={t['verified_test_count']:2}")

index_path = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-004\W4_DELIVERY_INDEX.json"
with open(index_path, encoding="utf-8") as f:
    idx = json.load(f)

print(f"\nDelivery Index payload files: {idx['total_payload_files']}, total bytes: {idx['total_payload_bytes']}")
