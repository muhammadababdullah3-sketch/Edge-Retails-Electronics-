# STAGE 6: Native Worker Capacity and Parallel Plan

**Run ID:** `ER-RUN-LOCALFLOW-20261019-080`
**Authority:** `OWNER-UPDATE-20261018-019` / `OWNER-UPDATE-20261019-001` / `OWNER-UPDATE-20261019-007`

- **Capacity Limit**: 4 total runtime slots
- **Capacity Scope**: `all_actors` (1 coordinator, 0 other active, 3 available worker slots)
- **Requested Parallelism**: 2 workers
- **Integrator**: `pos-source-integrator` (`actor-pos-integrator-080`)
- **Test Coordinator**: `test-coordinator` (`actor-test-coordinator-080`)
- **Reviewer**: `reviewer` (`actor-reviewer-080`)
- **Plan Validation Result**: Verified via `tools/verify_parallel_plan.py` — PASSED with 0 errors, 0 warnings.
