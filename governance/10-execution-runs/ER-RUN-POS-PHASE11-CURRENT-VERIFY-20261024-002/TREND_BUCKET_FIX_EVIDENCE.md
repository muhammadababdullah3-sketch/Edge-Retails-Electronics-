# Trend bucket correction: source, tests and limits

## Candidate identity

- Base commit: `6b5bbd04f35aa1bce3362751185b60d3cabfc8e6`
- Branch: `feat/phase11-subledger-implementation`
- Changes are uncommitted working-tree bytes. This is a bounded Phase 11 source correction, not a Phase 11 closure certificate.

## Root cause from executable source

`ReportingReadService.BuildTrendAsync` previously grouped event values by database/UTC date and hour, then iterated every hour of each derived UTC date for each shop-time trend slice. A daily point therefore repeated the day's totals; monthly points were not isolated to their shop dates; yearly points could shift at UTC month boundaries. Existing timezone tests checked bucket counts and interval lengths but had not checked which bucket received transaction values.

## Correction

`BusinessOperationsReadServices.cs` now keeps each event timestamp and amount, then streams each category query sequentially into generated shop-time slices using their half-open UTC intervals `[Start, End)`. The binary search assigns a boundary timestamp to the following slice and throws if a period-filtered event cannot be assigned. The processing retains only fixed-size bucket totals rather than materializing all category result sets simultaneously. Expense amounts remain grouped by their approved `DateOnly` posting date and are assigned to monthly/yearly slices by calendar date.

`ShopTimezoneBoundaryPostgresTests.cs` now snapshots the period before inserting the known sale at 2026-10-09 19:30 UTC (2026-10-10 00:30 Asia/Karachi). It asserts the exact +2500 sales and +1000 profit delta lands at daily hour 0, monthly day 10 (index 9), and yearly October (index 9), with no sales/profit delta in other buckets. It also reconciles daily/monthly/yearly sales and profit to their headline totals and monthly/yearly expenses to the expense headline.

## Byte evidence

Before copies are preserved under `evidence/before/batch3/`.

- `src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs`: before `f8eb80059e81e69aed02eed9c6e01ef0f3e5ab7fc978cea033285e3a500fb4ce`; after `652120f401a7dff94b30959c2e1b259fd20b559c7877927635c56a857ba5c8a5`.
- `tests/EdgeRetails.IntegrationTests/Phase11/ShopTimezoneBoundaryPostgresTests.cs`: before `4a700746d8647615d3f6effc11b0ba7e70d2f1ab00aacc399f00f368585f7da1`; after `1733d02b5ee688374231625d406092f299447b42d095847a427727d4e0a7a8f3`.
- Exact unified source diff: `evidence/trend-bucket-fix/source.diff`.
- Test, build and diff hashes: `evidence/trend-bucket-fix/artifact-hashes.json`.

## Raw verification

- Selected Phase 11/catalog PostgreSQL integration filter: 146 passed, 0 failed, 0 skipped. PostgreSQL 18 was freshly initialized under the owned temporary directory on loopback port 55437; migrations/model sync passed; the server was stopped, its owned cluster verified stopped, and generated password file removed. Raw TRX is `evidence/pg18-trend-bucket-verified/muham_ALI_2026-10-24_14_50_14_net10.0.trx`; runner result is `evidence/pg18-trend-bucket-verified/terminal-result.json`.
- Full solution Debug build: 0 warnings, 0 errors; `evidence/solution-debug-trend-verified.log`.
- Full solution Release build: 0 warnings, 0 errors; `evidence/solution-release-trend-verified.log`.
- Unit tests: 1,419 passed, 0 failed; `evidence/unit-trend-verified/muham_ALI_2026-10-24_14_52_03_net10.0.trx`.
- Full Desktop performance test project through `Invoke-DesktopTestRehearsal.ps1` with isolated state: 486 passed, 0 failed; `evidence/desktop-perf-trend-isolated-final/desktop-current.trx` and `desktop-test-result.json`.
- An earlier unisolated direct Desktop test invocation failed 14/486, including denied writes to `C:\ProgramData\EdgeRetails\Desktop` and shared fixture failures. It remains preserved in `evidence/desktop-perf-trend-verified/`; it is not represented as a product pass. The authorized isolated rerun passed the full suite.
- `git diff --check` passed. Git emitted only the existing LF-to-CRLF working-copy warning.

## Independent source review and remaining limit

A read-only source reviewer found the interval assignment and exact bucket assertions correct. Streaming removes the large simultaneous in-memory lists, but each report still transfers every matching event row and aggregates them on the client, so annual/high-volume report latency and network cost have not been measured. This performance question remains open; the selected fixture-scale PostgreSQL run does not establish production-volume performance.

This correction closes only the inspected trend-bucketing defect. The original 125 Phase 11 tasks, 16 capability gates, Final21 criteria, full source/test crosswalk, remaining findings, and distinct D05 whole-phase review are not all accepted. Phase 11 remains open.
