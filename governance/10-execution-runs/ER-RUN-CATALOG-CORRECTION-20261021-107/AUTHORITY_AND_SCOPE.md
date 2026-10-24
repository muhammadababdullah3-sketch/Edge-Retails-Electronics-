# Authority and scope — CAT correction 107

- Direct execution approval: the user approved the bounded CAT correction plan in this session after the independent review of CAT-106.
- Source lineage: OWNER-UPDATE-20261018-019 and OWNER-UPDATE-20261019-007; CAT prompt review 104 (`READY` for finalization) is supplemental criteria, not itself owner dispatch. Thaka policy source OWNER-UPDATE-20261019-001 is preserved where relevant.
- Current POS HEAD at baseline: `ea305c76ac9efeb6c109e182d54c3d7a942a05ff`.
- CAT-106 remains unchanged and is only `DELIVERED_FOR_DISTINCT_ROOT_REVIEW`.
- This correction does not accept Phase11/D05, tracking audit, or activate combined Phase8/9; it excludes Phase8/9 source changes, deployment, production DB operations, unsafe publication, credentials, and secrets.
- Native ownership check: POS peer point-of-sale-61 reports no source/test/DB/run leases in CAT paths; peer point-of-sale-03 reports Central-only work and no POS source/runtime/DB leases. Current session owns CAT correction integration. The actual history/consumption of the untracked 20261021120000 migration cannot be verified from non-production sources; therefore preserve it and add a forward migration rather than editing it.
- Disposable DB baseline observation: ports 5432 (postgres.exe PID 5076) and 7150 (EdgeRetails.Server.exe PID 3404) listening, never use; 55437/55454/55455 absent; `EDGE_RETAILS_TEST_DB` unset. No database connection was made. PG 18.6 binaries exist at `C:\Program Files\PostgreSQL\18\bin` (initdb version was queried only); available port 55640 was checked free before any cluster start.
- Before hashes: see `BEFORE_HASHES.json`; exact pre-edit CAT source snapshots in `before/`.
