import os
import sys
import subprocess
import json
import re
from datetime import datetime, timezone

def run_and_capture(cmd, cwd="."):
    start_time = datetime.now(timezone.utc).isoformat()
    proc = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    end_time = datetime.now(timezone.utc).isoformat()

    # Parse warning and error counts
    warning_match = re.search(r"(\d+)\s+Warning\(s\)", proc.stdout)
    error_match = re.search(r"(\d+)\s+Error\(s\)", proc.stdout)

    warnings = int(warning_match.group(1)) if warning_match else 0
    errors = int(error_match.group(1)) if error_match else 0

    return {
        "command": " ".join(cmd),
        "working_directory": os.path.abspath(cwd),
        "start_time_utc": start_time,
        "end_time_utc": end_time,
        "exit_code": proc.returncode,
        "warning_count": warnings,
        "error_count": errors,
        "stdout": proc.stdout,
        "stderr": proc.stderr,
        "status": "SUCCESS" if proc.returncode == 0 else "FAILED"
    }

print("Executing GATE16: IntegrationTests Release build...")
gate16 = run_and_capture(["dotnet", "build", "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj", "-c", "Release"])
print(f"GATE16: exit={gate16['exit_code']}, warnings={gate16['warning_count']}, errors={gate16['error_count']}")

print("Executing GATE17: Solution Debug build...")
gate17 = run_and_capture(["dotnet", "build", "EdgeRetails.sln", "-c", "Debug"])
print(f"GATE17: exit={gate17['exit_code']}, warnings={gate17['warning_count']}, errors={gate17['error_count']}")

print("Executing GATE18: Solution Release build...")
gate18 = run_and_capture(["dotnet", "build", "EdgeRetails.sln", "-c", "Release"])
print(f"GATE18: exit={gate18['exit_code']}, warnings={gate18['warning_count']}, errors={gate18['error_count']}")

build_logs = {
    "artifact_id": "ER-RUN-20261015-003-BUILD-LOGS",
    "timestamp_utc": datetime.now(timezone.utc).isoformat(),
    "builds": {
        "GATE16_IntegrationTests_Release": gate16,
        "GATE17_Solution_Debug": gate17,
        "GATE18_Solution_Release": gate18
    }
}

os.makedirs("artifacts/governance_w4/export", exist_ok=True)
with open("artifacts/governance_w4/export/BUILD_LOGS.json", "w", encoding="utf-8") as f:
    json.dump(build_logs, f, indent=2)

with open("artifacts/governance_w4/export/GATE16_INTEGRATION_TESTS_BUILD.log", "w", encoding="utf-8") as f:
    f.write(gate16["stdout"] + "\n" + gate16["stderr"])

with open("artifacts/governance_w4/export/GATE17_SOLUTION_DEBUG_BUILD.log", "w", encoding="utf-8") as f:
    f.write(gate17["stdout"] + "\n" + gate17["stderr"])

with open("artifacts/governance_w4/export/GATE18_SOLUTION_RELEASE_BUILD.log", "w", encoding="utf-8") as f:
    f.write(gate18["stdout"] + "\n" + gate18["stderr"])

print("Build logs captured successfully.")
