import os
import re

files_to_check = {
    "PACK": "tests/EdgeRetails.UnitTests/Phase11/PackContainerSubLedgerUnitTests.cs",
    "CONCURRENCY": "tests/EdgeRetails.IntegrationTests/Phase11/PackConcurrencyPostgresTests.cs",
    "TIME": "tests/EdgeRetails.IntegrationTests/Phase11/ShopTimezoneBoundaryPostgresTests.cs",
    "GOLDEN": "tests/EdgeRetails.IntegrationTests/Phase11/GoldenLifecycleTracePostgresTests.cs",
    "CATALOG": "tests/EdgeRetails.UnitTests/Phase11CatalogV3AcceptanceTests.cs"
}

for group, rel_path in files_to_check.items():
    print(f"\n=== Group {group}: {rel_path} ===")
    if not os.path.exists(rel_path):
        print(f"File not found: {rel_path}")
        continue
    with open(rel_path, encoding="utf-8") as f:
        for idx, line in enumerate(f, 1):
            if "public void " in line or "public async Task " in line:
                m = re.search(r"public\s+(?:async\s+Task|void)\s+([A-Za-z0-9_]+)\s*\(", line)
                if m:
                    print(f"  Line {idx:4}: {m.group(1)}")
