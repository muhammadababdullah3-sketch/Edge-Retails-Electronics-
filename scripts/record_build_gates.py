import os
import subprocess
import json
from datetime import datetime, timezone

POS_DIR = r"C:\Users\muham\OneDrive\Desktop\Point of Sale"
RUN_DIR = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-009"
EXPORT_DIR = os.path.join(RUN_DIR, "export")
os.makedirs(EXPORT_DIR, exist_ok=True)

build_tasks = [
    {
        "gate": "GATE16_IntegrationTests_Release",
        "cmd": ["dotnet", "build", "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj", "-c", "Release"]
    },
    {
        "gate": "GATE17_Solution_Debug",
        "cmd": ["dotnet", "build", "EdgeRetails.sln", "-c", "Debug"]
    },
    {
        "gate": "GATE18_Solution_Release",
        "cmd": ["dotnet", "build", "EdgeRetails.sln", "-c", "Release"]
    }
]

builds_output = {}

for b in build_tasks:
    print(f"Executing build gate: {b['gate']}...")
    start_time = datetime.now(timezone.utc)
    res = subprocess.run(b["cmd"], cwd=POS_DIR, capture_output=True, text=True)
    end_time = datetime.now(timezone.utc)

    builds_output[b["gate"]] = {
        "command": " ".join(b["cmd"]),
        "working_directory": POS_DIR,
        "start_time_utc": start_time.isoformat(),
        "end_time_utc": end_time.isoformat(),
        "exit_code": res.returncode,
        "stdout": res.stdout,
        "stderr": res.stderr,
        "status": "SUCCESS" if res.returncode == 0 else "FAILED"
    }
    print(f"  Result: {builds_output[b['gate']]['status']} (Exit Code {res.returncode})")

log_data = {
    "artifact_id": "ER-RUN-20261015-009-BUILD-LOGS",
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "builds": builds_output
}

with open(os.path.join(EXPORT_DIR, "BUILD_LOGS.json"), "w", encoding="utf-8") as f:
    json.dump(log_data, f, indent=2)

print("Build logs recorded successfully in export/BUILD_LOGS.json.")
