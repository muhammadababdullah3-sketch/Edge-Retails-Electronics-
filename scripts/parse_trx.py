import os
import glob
import json
import xml.etree.ElementTree as ET

trx_files = glob.glob("artifacts/governance_w3/raw/*.trx")
ns = {'ns': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

summary = {}

for trx in trx_files:
    fname = os.path.basename(trx)
    tree = ET.parse(trx)
    root = tree.getroot()
    results = root.findall('.//ns:UnitTestResult', ns)

    test_list = []
    for r in results:
        test_list.append({
            "test_name": r.attrib.get("testName"),
            "test_id": r.attrib.get("testId"),
            "execution_id": r.attrib.get("executionId"),
            "outcome": r.attrib.get("outcome"),
            "duration": r.attrib.get("duration"),
            "start_time": r.attrib.get("startTime"),
            "end_time": r.attrib.get("endTime")
        })
    summary[fname] = {
        "count": len(test_list),
        "tests": test_list
    }

with open("artifacts/governance_w3/raw/trx_summary.json", "w", encoding="utf-8") as f:
    json.dump(summary, f, indent=2)

print("Parsed TRX files:")
for k, v in summary.items():
    print(f"  {k}: {v['count']} tests")
