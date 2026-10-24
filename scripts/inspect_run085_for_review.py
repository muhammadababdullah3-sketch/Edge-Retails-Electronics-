import os
import sys
import json
import hashlib
from pathlib import Path

pos_root = Path(r"C:\Users\muham\OneDrive\Desktop\Point of Sale")
gov_root = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body")
run_pos = pos_root / "governance/10-execution-runs/ER-RUN-POS-NON89-EXEC-20261023-085"
run_gov = gov_root / "governance/10-execution-runs/ER-RUN-POS-NON89-EXEC-20261023-085"

print("=== CHECK 1: DUAL TREE EQUALITY ===")
manifest_pos = json.loads((run_pos / "MANIFEST.json").read_text(encoding="utf-8"))
manifest_gov = json.loads((run_gov / "MANIFEST.json").read_text(encoding="utf-8"))

print(f"POS files count: {manifest_pos['total_deposited_files']}")
print(f"GOV files count: {manifest_gov['total_deposited_files']}")

mismatches = []
for f_pos, f_gov in zip(manifest_pos["files"], manifest_gov["files"]):
    if f_pos["path"] != f_gov["path"] or f_pos["sha256"] != f_gov["sha256"] or f_pos["size_bytes"] != f_gov["size_bytes"]:
        mismatches.append((f_pos, f_gov))

print(f"Dual-tree mismatches: {len(mismatches)}")

print("\n=== CHECK 2: RAW TRX COMPARISON ===")
trx_names = [
    "phase11-unit-execution-085.trx",
    "phase11-desktop-execution-085.trx",
    "phase11-pg18-execution-085.trx"
]

import xml.etree.ElementTree as ET
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

for name in trx_names:
    p_results = run_pos / "test-results" / name
    p_tests = run_pos / "tests" / name
    
    assert p_results.exists(), f"Missing {p_results}"
    assert p_tests.exists(), f"Missing {p_tests}"
    
    data_results = p_results.read_bytes()
    data_tests = p_tests.read_bytes()
    
    sha_results = hashlib.sha256(data_results).hexdigest()
    sha_tests = hashlib.sha256(data_tests).hexdigest()
    
    assert sha_results == sha_tests, f"Mismatch between test-results and tests for {name}"
    
    tree = ET.parse(p_results)
    counters = tree.getroot().find(".//t:Counters", ns)
    if counters is None:
        counters = tree.getroot().find(".//Counters")
    
    times = tree.getroot().find(".//t:Times", ns)
    if times is None:
        times = tree.getroot().find(".//Times")
        
    c_attr = counters.attrib if counters is not None else {}
    t_attr = times.attrib if times is not None else {}
    
    print(f"File: {name}")
    print(f"  SHA-256: {sha_results}")
    print(f"  Size: {len(data_results)} bytes")
    print(f"  Counters: total={c_attr.get('total')}, passed={c_attr.get('passed')}, failed={c_attr.get('failed')}")
    print(f"  Times: creation={t_attr.get('creation')}, start={t_attr.get('start')}, finish={t_attr.get('finish')}")

print("\n=== CHECK 3: CANDIDATE EXPORT RECONSTRUCTION ===")
inv_file = run_pos / "export/CANDIDATE_FILE_INVENTORY.json"
inv = json.loads(inv_file.read_text(encoding="utf-8"))
print(f"Total candidate files: {inv['total_candidate_files']}")
print(f"Tracked candidate files: {inv['tracked_candidate_files']}")
print(f"Untracked candidate files: {inv['untracked_candidate_files']}")
print(f"Archives: {list(inv['archives'].keys())}")
for arch_key, arch_info in inv['archives'].items():
    arch_path = run_pos / "export" / arch_info["file"]
    actual_sha = hashlib.sha256(arch_path.read_bytes()).hexdigest()
    match = actual_sha.lower() == arch_info["sha256"].lower()
    print(f"  {arch_info['file']}: declared_sha={arch_info['sha256'][:16]}..., actual_sha={actual_sha[:16]}..., matches={match}")

print("\n=== CHECK 4: PHASE 12 EXCLUSION MANIFEST ===")
excl_file = run_pos / "export/PHASE12_EXCLUSION_MANIFEST.json"
excl = json.loads(excl_file.read_text(encoding="utf-8"))
print(f"Policy: {excl['exclusion_policy']}")
print(f"Total excluded files: {excl['total_excluded_files']}")
for ef in excl['files']:
    real_path = pos_root / ef['path']
    real_sha = hashlib.sha256(real_path.read_bytes()).hexdigest() if real_path.exists() else "MISSING"
    print(f"  {ef['path']}: real_exists={real_path.exists()}, sha_match={real_sha == ef['sha256']}")
