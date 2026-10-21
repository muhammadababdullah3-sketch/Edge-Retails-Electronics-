import os
import sys
import json
import hashlib
import subprocess
from datetime import datetime, timezone

POS_DIR = r"C:\Users\muham\OneDrive\Desktop\Point of Sale"
GOV_RUN_DIR = r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-20261015-008"
EXPORT_DIR = os.path.join(GOV_RUN_DIR, "export")
os.makedirs(EXPORT_DIR, exist_ok=True)

def run_cmd(cmd_list, cwd=POS_DIR):
    start = datetime.now(timezone.utc)
    res = subprocess.run(cmd_list, cwd=cwd, capture_output=True, text=True)
    end = datetime.now(timezone.utc)
    return {
        "command": " ".join(cmd_list),
        "working_directory": cwd,
        "start_time_utc": start.isoformat(),
        "end_time_utc": end.isoformat(),
        "exit_code": res.returncode,
        "stdout": res.stdout,
        "stderr": res.stderr,
        "status": "SUCCESS" if res.returncode == 0 else "FAILED"
    }

print("Running Gate 16, 17, 18 builds to capture verifiable logs...")

gate16 = run_cmd(["dotnet", "build", "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj", "-c", "Release"])
with open(os.path.join(EXPORT_DIR, "GATE16_INTEGRATION_TESTS_BUILD.log"), "w", encoding="utf-8") as f:
    f.write(gate16["stdout"] + ("\nSTDERR:\n" + gate16["stderr"] if gate16["stderr"] else ""))

gate17 = run_cmd(["dotnet", "build", "EdgeRetails.sln", "-c", "Debug"])
with open(os.path.join(EXPORT_DIR, "GATE17_SOLUTION_DEBUG_BUILD.log"), "w", encoding="utf-8") as f:
    f.write(gate17["stdout"] + ("\nSTDERR:\n" + gate17["stderr"] if gate17["stderr"] else ""))

gate18 = run_cmd(["dotnet", "build", "EdgeRetails.sln", "-c", "Release"])
with open(os.path.join(EXPORT_DIR, "GATE18_SOLUTION_RELEASE_BUILD.log"), "w", encoding="utf-8") as f:
    f.write(gate18["stdout"] + ("\nSTDERR:\n" + gate18["stderr"] if gate18["stderr"] else ""))

build_logs_data = {
    "artifact_id": "ER-RUN-20261015-008-BUILD-LOGS",
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "builds": {
        "GATE16_IntegrationTests_Release": gate16,
        "GATE17_Solution_Debug": gate17,
        "GATE18_Solution_Release": gate18
    }
}
with open(os.path.join(EXPORT_DIR, "BUILD_LOGS.json"), "w", encoding="utf-8") as f:
    json.dump(build_logs_data, f, indent=2)

print("Build logs generated.")
