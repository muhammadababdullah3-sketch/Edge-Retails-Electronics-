# CAT Correction 107 — Baseline

- Run: `ER-RUN-CATALOG-CORRECTION-20261021-107`
- Authority: user approval of the bounded CAT-106 correction plan in this session; source lineage includes OWNER-UPDATE-20261018-019 and OWNER-UPDATE-20261019-007 plus CAT repair prompt 104. This run is not a new Phase8/9 authority.
- Observed UTC: 2026-10-21T12:51:35Z
- POS HEAD: `ea305c76ac9efeb6c109e182d54c3d7a942a05ff`
- CAT-106: preserved as delivered for distinct root review; no modifications to run 106.
- Phase 11 D05 and Phase8/9 entry: remain separate and unaccepted.
- DB state observation: ports 5432 and 7150 have listeners (`postgres.exe`, `EdgeRetails.Server.exe`); 55437, 55454 and 55455 have no listeners; `EDGE_RETAILS_TEST_DB` is unset. No DB connection or DB operation was performed for this baseline.
- Source files were already modified/untracked before this correction. `BEFORE_HASHES.json` was initially captured; a later interruption changed some source before-snapshot copies. `BEFORE_ACTUAL_MANIFEST.json` hashes the bytes currently preserved under `before/`; the CAT-106 exact-candidate copies are separately in `before/attempts/cat106-source/`. Do not treat an inconsistent before copy as original; retain both and report the mismatch explicitly.
- Expanded owner scope: distinct ThakaCreditRefund prerequisite migration, CAT taxonomy/ClassRef persistence/importer, fixed-unit-only explicit existing-shop upgrade preparation, and exact selected-class filtering. Categories are excluded pending owner-approved mapping. Phase8/9 remain excluded. All unrelated dirty work must remain untouched.
