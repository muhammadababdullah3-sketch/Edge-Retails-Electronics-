"""Initialize baseline proof, capacity observation, and parallel plan for ER-RUN-LOCALFLOW-20261018-072."""
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path

RUN_DIR = Path(r"C:\Users\muham\OneDrive\Desktop\Edge Retails Goverence Body\governance\10-execution-runs\ER-RUN-LOCALFLOW-20261018-072")
RUN_DIR.mkdir(parents=True, exist_ok=True)

NOW_UTC = datetime.now(timezone.utc).isoformat()
NOW_PKT = "2026-10-18T22:05:00+05:00"

# 1. BASELINE_PROOF.json
baseline_proof = {
    "schema_version": 1,
    "run_id": "ER-RUN-LOCALFLOW-20261018-072",
    "authority": "OWNER-UPDATE-20261018-019",
    "sustained_authority": "17Oct007",
    "timestamp_utc": NOW_UTC,
    "timestamp_local_pkt": NOW_PKT,
    "predecessor_runs": {
        "run044": {
            "run_id": "ER-RUN-MON-W4-20261018-044",
            "scope": "MON-W4-001 / P11-IMP-108 concrete assertion repair",
            "status": "DELIVERED_FOR_DISTINCT_ROOT_REVIEW",
            "candidate_bundle_hash": "fe5a418f9680e76c0ad3db3e646d42079de5b46a6f8304731bce90b4064d1c6b",
            "test_passes": "14/14"
        },
        "run071": {
            "run_id": "ER-RUN-THAKA-AUDIT-20261018-071",
            "scope": "Thaka read-only source review and confirmed findings THAKA-AUD-01..04",
            "status": "AUDIT_COMPLETED"
        }
    },
    "invariants": {
        "phase12_status": "ACCEPTED_AND_CLOSED",
        "single_machine_boundary": "ONE-shop/ONE-primary-machine POS strictly enforced; no CENTRAL-ONE external cloud",
        "disposable_pg18_custody": {
            "pid": 9992,
            "port": 55437,
            "database": "edgeretails_test_run20261012_001",
            "isolation": "Loopback 127.0.0.1:55437; zero contact with port 5432"
        }
    }
}
(RUN_DIR / "BASELINE_PROOF.json").write_text(json.dumps(baseline_proof, indent=2), encoding="utf-8")
print("[OK] Wrote BASELINE_PROOF.json")

# 2. CAPACITY_OBSERVATION.json
capacity_obs = {
    "schema_version": 1,
    "run_id": "ER-RUN-LOCALFLOW-20261018-072",
    "authority": "OWNER-UPDATE-20261018-019",
    "timestamp_utc": NOW_UTC,
    "timestamp_local_pkt": NOW_PKT,
    "observation_mechanism": "ListAgents native query + process inspection",
    "system_capacity_limit": 10,
    "capacity_scope": "all_actors",
    "current_allocations": {
        "integrator_coordinator": {
            "name": "point-of-sale-82 [981c08]",
            "role": "Gemini POS Source Integrator & Coordinator",
            "slots": 1
        },
        "peer_sessions": [
            {
                "name": "point-of-sale-01 [b56d5c]",
                "status": "busy",
                "slots": 1
            }
        ],
        "active_children": 0,
        "available_slots_for_dispatch": 8
    },
    "planned_workers_count": 6,
    "within_capacity": True
}
(RUN_DIR / "CAPACITY_OBSERVATION.json").write_text(json.dumps(capacity_obs, indent=2), encoding="utf-8")
print("[OK] Wrote CAPACITY_OBSERVATION.json")

# 3. PARALLEL_PLAN.json
parallel_plan = {
    "schema_version": 1,
    "status": "DISPATCHING",
    "plan_id": "ER-RUN-LOCALFLOW-20261018-072-PARALLEL-PLAN",
    "authority": "OWNER-UPDATE-20261018-019",
    "phase_wave": "Phase11 LOCAL-FLOW master: repair Thaka and audit equivalent local POS flows",
    "workspace": "C:/Users/muham/OneDrive/Desktop/Point of Sale",
    "baseline_proof": "governance/10-execution-runs/ER-RUN-LOCALFLOW-20261018-072/BASELINE_PROOF.json",
    "capacity_limit": 10,
    "capacity_scope": "all_actors",
    "coordinator_slots": 1,
    "other_active_slots": 1,
    "requested_parallelism": 6,
    "capacity_proof": "governance/10-execution-runs/ER-RUN-LOCALFLOW-20261018-072/CAPACITY_OBSERVATION.json",
    "integrator_id": "gemini_pos_source_integrator",
    "integrator_runtime_id": "session-e143b7ec-1d5c-4ad8-ad06-4bbe1dd20736",
    "test_coordinator_id": "gemini_pos_source_integrator",
    "integrator_write_paths": [
        "src/EdgeRetails.Application/Features/Thaka/ThakaSettlementHandlers.cs",
        "src/EdgeRetails.Application/Features/Thaka/ThakaReversalHandlers.cs",
        "src/EdgeRetails.Application/Features/Thaka/ThakaReadService.cs",
        "src/EdgeRetails.Desktop/Services/IBackendThakaService.cs",
        "src/EdgeRetails.Desktop/Services/BackendThakaService.cs",
        "src/EdgeRetails.Desktop/Services/DirectBackendThakaService.cs",
        "src/EdgeRetails.Desktop/ViewModels/Thaka/ThakaWorkspaceViewModel.cs",
        "src/EdgeRetails.Desktop/ViewModels/Thaka/ThakaFinalSettlementViewModel.cs",
        "src/EdgeRetails.Desktop/Views/Thaka/ThakaWorkspaceView.xaml"
    ],
    "resource_owners": {
        "disposable_postgres_55437": "gemini_pos_source_integrator",
        "local_source_tree": "gemini_pos_source_integrator"
    },
    "workers": [
        {
            "id": "worker-lf-072-a",
            "role": "Lane A: Thaka Lifecycle, Payments, Reopening, Reversal & Findings THAKA-AUD-01..04 Auditor",
            "status": "DISPATCHING",
            "completion_outcome": "PENDING",
            "runtime_id": None,
            "read_only": True,
            "can_run_tests": False,
            "write_paths": [],
            "read_paths": [
                "src/EdgeRetails.Domain/Thaka/ThakaModels.cs",
                "src/EdgeRetails.Application/Features/Thaka/",
                "src/EdgeRetails.Server/Controllers/ThakaController.cs",
                "src/EdgeRetails.Desktop/ViewModels/Thaka/",
                "src/EdgeRetails.Desktop/Services/IBackendThakaService.cs"
            ],
            "depends_on": [],
            "resources": [],
            "original_task_ids": [
                "OWNER019-LF-01",
                "OWNER019-LF-02",
                "OWNER019-LF-03",
                "OWNER019-LF-04",
                "OWNER019-LF-05"
            ],
            "proof_paths": [
                "governance/10-execution-runs/ER-RUN-LOCALFLOW-20261018-072/workers/worker-lf-072-a/RESULT.md"
            ]
        },
        {
            "id": "worker-lf-072-b",
            "role": "Lane B: Sales, Returns, Exchanges, Cart & Customer Directory Lifecycle Auditor",
            "status": "DISPATCHING",
            "completion_outcome": "PENDING",
            "runtime_id": None,
            "read_only": True,
            "can_run_tests": False,
            "write_paths": [],
            "read_paths": [
                "src/EdgeRetails.Application/Features/Sales/",
                "src/EdgeRetails.Application/Features/Identity/",
                "src/EdgeRetails.Server/Controllers/SalesController.cs",
                "src/EdgeRetails.Desktop/ViewModels/Sales/"
            ],
            "depends_on": [],
            "resources": [],
            "original_task_ids": [
                "OWNER019-LF-01",
                "OWNER019-LF-06",
                "OWNER019-LF-07"
            ],
            "proof_paths": [
                "governance/10-execution-runs/ER-RUN-LOCALFLOW-20261018-072/workers/worker-lf-072-b/RESULT.md"
            ]
        },
        {
            "id": "worker-lf-072-c",
            "role": "Lane C: Purchasing, Intake, Returns, Voids, Supplier Balance/Advances/Refunds Auditor",
            "status": "DISPATCHING",
            "completion_outcome": "PENDING",
            "runtime_id": None,
            "read_only": True,
            "can_run_tests": False,
            "write_paths": [],
            "read_paths": [
                "src/EdgeRetails.Application/Features/Purchasing/",
                "src/EdgeRetails.Application/Features/Finance/SupplierAccountHandlers.cs",
                "src/EdgeRetails.Application/Features/Finance/SupplierOpeningBalanceHandler.cs",
                "src/EdgeRetails.Server/Controllers/PurchasesController.cs",
                "src/EdgeRetails.Desktop/ViewModels/Purchasing/"
            ],
            "depends_on": [],
            "resources": [],
            "original_task_ids": [
                "OWNER019-LF-01",
                "OWNER019-LF-06",
                "OWNER019-LF-07"
            ],
            "proof_paths": [
                "governance/10-execution-runs/ER-RUN-LOCALFLOW-20261018-072/workers/worker-lf-072-c/RESULT.md"
            ]
        },
        {
            "id": "worker-lf-072-d",
            "role": "Lane D: Cash Sessions, Movements, Reconciliation, Expenses & Expense Voids Auditor",
            "status": "DISPATCHING",
            "completion_outcome": "PENDING",
            "runtime_id": None,
            "read_only": True,
            "can_run_tests": False,
            "write_paths": [],
            "read_paths": [
                "src/EdgeRetails.Application/Features/Finance/CashSessionHandlers.cs",
                "src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs",
                "src/EdgeRetails.Server/Controllers/CashSessionsController.cs",
                "src/EdgeRetails.Server/Controllers/ExpensesController.cs",
                "src/EdgeRetails.Desktop/ViewModels/Finance/"
            ],
            "depends_on": [],
            "resources": [],
            "original_task_ids": [
                "OWNER019-LF-01",
                "OWNER019-LF-06",
                "OWNER019-LF-07"
            ],
            "proof_paths": [
                "governance/10-execution-runs/ER-RUN-LOCALFLOW-20261018-072/workers/worker-lf-072-d/RESULT.md"
            ]
        },
        {
            "id": "worker-lf-072-e",
            "role": "Lane E: Inventory Catalog, Pack Allocation, Stocktake, Tracking & Warranty Auditor",
            "status": "DISPATCHING",
            "completion_outcome": "PENDING",
            "runtime_id": None,
            "read_only": True,
            "can_run_tests": False,
            "write_paths": [],
            "read_paths": [
                "src/EdgeRetails.Application/Features/Inventory/",
                "src/EdgeRetails.Application/Features/Catalog/",
                "src/EdgeRetails.Application/Features/Tracking/",
                "src/EdgeRetails.Server/Controllers/InventoryController.cs",
                "src/EdgeRetails.Desktop/ViewModels/Inventory/"
            ],
            "depends_on": [],
            "resources": [],
            "original_task_ids": [
                "OWNER019-LF-01",
                "OWNER019-LF-06",
                "OWNER019-LF-07"
            ],
            "proof_paths": [
                "governance/10-execution-runs/ER-RUN-LOCALFLOW-20261018-072/workers/worker-lf-072-e/RESULT.md"
            ]
        },
        {
            "id": "worker-lf-072-f",
            "role": "Lane F: Cross-Module Desktop, Services, Adapters, Permissions & Recovery Auditor",
            "status": "DISPATCHING",
            "completion_outcome": "PENDING",
            "runtime_id": None,
            "read_only": True,
            "can_run_tests": False,
            "write_paths": [],
            "read_paths": [
                "src/EdgeRetails.Desktop/Services/",
                "src/EdgeRetails.Application/Features/Terminals/OperationOutcomeLedger.cs",
                "src/EdgeRetails.Application/Features/Terminals/OperationStatusQueryHandler.cs",
                "src/EdgeRetails.Desktop/ViewModels/ShellViewModel.cs"
            ],
            "depends_on": [],
            "resources": [],
            "original_task_ids": [
                "OWNER019-LF-01",
                "OWNER019-LF-06",
                "OWNER019-LF-08"
            ],
            "proof_paths": [
                "governance/10-execution-runs/ER-RUN-LOCALFLOW-20261018-072/workers/worker-lf-072-f/RESULT.md"
            ]
        }
    ]
}
(RUN_DIR / "PARALLEL_PLAN.json").write_text(json.dumps(parallel_plan, indent=2), encoding="utf-8")
print("[OK] Wrote PARALLEL_PLAN.json")
