"""Preserve the cumulative pre-repair candidate and original failed case identities."""
from pathlib import Path
import collections
import hashlib
import json
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

RUN = Path(__file__).resolve().parent
ROOT = RUN.parents[2]
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
ORIGINAL = ROOT / "governance/10-execution-runs/ER-RUN-POS-PHASE11-CURRENT-VERIFY-20261024-004/evidence/pg18-full-integration-final/full-integration-release.trx"
LATER = ROOT / "governance/10-execution-runs/ER-RUN-POS-PHASE11-CURRENT-VERIFY-20261024-004/evidence/pg18-focused-final-rerun/muham_ALI_2026-10-24_20_00_39_net10.0.trx"

def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()

def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)

def put(name, value):
    p = RUN / name
    if p.exists():
        raise RuntimeError(f"Refusing to overwrite preserved baseline: {p}")
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

def category(m):
    if "permission denied" in m: return "E2_PERMISSIONS"
    if "trying to start process 'dotnet'" in m: return "E3_CHILD_SDK"
    if "requires a valid immutable client operation id" in m: return "B3_OPERATION_ID"
    if "posting.backdate" in m or "Posting date" in m: return "B8_POSTING_CLOCK"
    if "ix_units_name" in m: return "B9_UNIT_UNIQUENESS"
    if "IdempotencyPayloadMismatchException" in m or "IdempotencyContextMismatchException" in m: return "B1_B2_REPLAY"
    if "owned isolated loopback" in m or "Only the disposable model fixture" in m or "cutover gate is not armed" in m: return "E1_FIXTURE_GUARDS"
    return "BEHAVIORAL_ASSERTION"

def main():
    original = ET.parse(ORIGINAL)
    later = {t.get("testName"): t for t in ET.parse(LATER).findall(".//t:UnitTestResult", NS)}
    rows = []
    for n, t in enumerate(sorted(original.findall(".//t:UnitTestResult[@outcome='Failed']", NS), key=lambda t: t.get("testName")), 1):
        name = t.get("testName")
        message = t.findtext(".//t:Message", "", NS)
        stack = t.findtext(".//t:StackTrace", "", NS)
        frames = re.findall(r"in (.*?\.cs):line (\d+)", stack)
        paths = []
        for raw, line in frames:
            p = Path(raw)
            if p.exists() and p.is_relative_to(ROOT):
                rel = p.relative_to(ROOT).as_posix()
                if not any(x["path"] == rel for x in paths):
                    paths.append({"path": rel, "historical_stack_line": int(line), "current_sha256": sha(p)})
        observed = later.get(name)
        later_pass = observed is not None and observed.get("outcome") == "Passed"
        rows.append({"row_id": f"R82-{n:03}", "original_test_id": t.get("testId"), "original_execution_id": t.get("executionId"),
                     "test_name_and_parameters": name, "original_outcome": t.get("outcome"), "original_error": message,
                     "original_stack": stack, "source_references": paths, "symptom_group": category(message),
                     "causal_trace": "Pending source investigation; category is not a root-cause verdict.",
                     "approved_invariant": "Preserve original assertion and fail-closed safety contracts; establish exact controlling contract before changing an oracle.",
                     "repair_or_no_change": "Pending bounded assignment", "affected_selector": "FullyQualifiedName=" + name.split("(")[0],
                     "later_evidence": {"path": str(LATER), "sha256": sha(LATER), "outcome": "Passed", "input_binding": "UNPROVEN"} if later_pass else None,
                     "disposition": "EXISTING_REPAIR_AWAITING_VERIFICATION" if later_pass else "INVESTIGATION_REQUIRED",
                     "final_result": "NOT_RUN_ON_FINAL_CANDIDATE", "final_evidence": None})
    assert len(rows) == 82
    groups = dict(collections.Counter(r["symptom_group"] for r in rows))
    assert sorted(groups.values()) == sorted([28, 26, 8, 11, 4, 2, 2, 1]), groups
    assert sum(r["later_evidence"] is not None for r in rows) == 8
    status = git("status", "--porcelain=v1").decode("utf-8")
    names = git("ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "src", "tests", "scripts", ".config", "global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.config", "EdgeRetails.sln", "EdgeRetails.slnx").decode("utf-8").split("\0")
    inventory = []
    excluded = []
    for name in sorted(set(n for n in names if n)):
        p = ROOT / name
        if not p.is_file(): continue
        if any(x.lower() in {"bin", "obj", "artifacts", "testresults"} for x in Path(name).parts) or p.suffix.lower() in {".zip", ".tmp", ".trx", ".log"}:
            excluded.append(name); continue
        inventory.append({"path": name, "bytes": p.stat().st_size, "sha256": sha(p)})
    changed = git("diff", "--name-only", "HEAD", "--", "src", "tests", "scripts").decode().splitlines()
    untracked = git("ls-files", "--others", "--exclude-standard", "--", "src", "tests", "scripts").decode().splitlines()
    captured = []
    for name in sorted(set(changed + untracked)):
        p = ROOT / name
        if not p.is_file() or p.suffix.lower() not in {".cs", ".csproj", ".ps1", ".props", ".targets"} or any(x.lower() in {"bin", "obj", "artifacts", "testresults"} for x in Path(name).parts): continue
        dst = RUN / "before" / name
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(p, dst)
        captured.append({"path": name, "before_sha256": sha(dst), "bytes": dst.stat().st_size})
    (RUN / "git-status-before.txt").write_text(status, encoding="utf-8")
    (RUN / "tracked-before.patch").write_bytes(git("diff", "--binary", "HEAD", "--", "src", "tests", "scripts"))
    put("BASELINE.json", {"captured_utc": datetime.now(timezone.utc).isoformat(), "workspace": str(ROOT), "head": git("rev-parse", "HEAD").decode().strip(),
                          "branch": git("branch", "--show-current").decode().strip(), "sdk": subprocess.check_output([str(Path.home()/".dotnet/dotnet.exe"), "--version"], cwd=ROOT).decode().strip(),
                          "original_trx": {"path": str(ORIGINAL), "sha256": sha(ORIGINAL)}, "later_trx": {"path": str(LATER), "sha256": sha(LATER)},
                          "source_inventory": inventory, "excluded_generated_paths": excluded, "before_snapshots": captured,
                          "scope": "Cumulative source/build-input observation before this repair wave; not a claim that historical TRX files tested these bytes or that the candidate is frozen."})
    put("FAILURE_REGISTER.json", {"baseline_total": 1094, "baseline_passed": 1012, "original_failed_executions": 82, "groups": groups, "later_pass_rows_unbound": 8, "rows": rows})
    print(json.dumps({"rows": len(rows), "groups": groups, "later_unbound_passes": 8, "input_files": len(inventory), "before_snapshots": len(captured)}))

if __name__ == "__main__": main()
