# STAGE 6: Native Worker Capacity and Parallel Plan
**Run ID:** `ER-RUN-POS-NON89-EXEC-20261021-084`

- Coordinator: 1 slot (`lead-coordinator`)
- Active Workers: 2 (`pos-source-integrator`, `test-coordinator`)
- Reviewer: 1 (`reviewer`)
- Capacity Limit: 4 slots (Capacity scope: `all_actors`)
- Verification: PASSED zero errors via `PLAN_VALIDATION.json` (`valid: true`).
- Lease Separation:
  - Integrator held Application and Desktop source leases.
  - Test Coordinator held UnitTests and PerformanceTests leases.
  - Reviewer held read-only leases.
