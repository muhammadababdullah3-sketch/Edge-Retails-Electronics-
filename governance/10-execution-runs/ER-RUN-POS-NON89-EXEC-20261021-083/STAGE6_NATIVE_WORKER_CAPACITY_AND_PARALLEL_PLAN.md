# STAGE 6: Native Worker Capacity and Parallel Plan
**Run ID:** `ER-RUN-POS-NON89-EXEC-20261021-083`

- Coordinator: 1 slot (`lead-coordinator`)
- Active Workers: 2 (`pos-source-integrator`, `test-coordinator`)
- Reviewer: 1 (`reviewer`)
- Capacity Limit: 4 slots (Capacity scope: `all_actors`)
- Verification: PASSED zero errors via `tools/verify_parallel_plan.py` (`valid: true`).
- Lease Separation: Integrator held Desktop source leases; Test Coordinator held test leases; Reviewer held read-only leases.
