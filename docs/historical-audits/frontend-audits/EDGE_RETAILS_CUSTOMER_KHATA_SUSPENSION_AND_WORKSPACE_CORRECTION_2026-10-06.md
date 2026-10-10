# Customer/khata suspension and workspace correction — 6 October 2026

## Delivery status

**SOURCE_FIXED / ISOLATED_TESTS_PASSED / PRODUCTION_DEPLOYMENT_PENDING.**

The tested Release package is `artifacts/production/pending-customer-khata`, containing Desktop, Server, Worker, a SHA-256 binary manifest, required migration list, and a **review-only** schema script. The running application and installed services have not been replaced. Production data and configuration were not modified.

The user's confirmed rule is: suspension blocks new activity; historical records and recovery/payment remain accessible. This report covers that change and the reported workspace error, not a recertification of the entire project.

## Reported workspace error

The Windows Application event at **2026-10-06 15:25:47** identifies Dapper materialization of `ThakaMaterialLedgerRowDto` as the failure: Npgsql returned `System.DateTime` for `issuedat`, while the public record constructor requires `DateTimeOffset`. This was a mapping failure, not evidence that the connection or project balance was empty.

`src/EdgeRetails.Infrastructure/Services/ThakaReadService.cs`, `GetProjectAsync`, already contains the source correction: internal `ThakaMaterialLedgerDbRow` / `ThakaPaymentLedgerDbRow` receive provider timestamps and convert to UTC offsets before constructing public DTOs. The installed Infrastructure DLL is dated **1 October 2026** and lacks this correction. Updating only Desktop cannot repair this backend failure.

`ThakaWorkspaceViewModel.RefreshBackendAsync` now distinguishes loading, successful snapshots, and failed reads. An initial or subsequent failed refresh displays **Unavailable** amounts, disables mutation commands, and exposes retry/error information. It does not represent a failed read as a zero balance. Light and dark states were rendered from actual WPF views/resources with owned fixtures, without connecting to a shop database.

## Authoritative suspension behaviour

| State | New sale/quotation/material/credit | History | Recovery payment | Resume |
|---|---|---|---|---|
| Active customer and active khata | Existing authorization/business rules apply | Allowed | Allowed | N/A |
| Suspended customer | Blocked by customer guards; material blocked even on active khata | Allowed | Allowed | Customer management action |
| Suspended khata | New material blocked | Allowed | Allowed, including final settlement | Khata management action |
| Settled/closed khata | Existing final-state rules remain | Allowed | No new payment/settlement through workspace actions | Suspension action rejected |

Principal implementation references:

- `Application/Features/Parties/CustomerSuspensionHandler.cs`: `SetCustomerSuspensionHandler.HandleAsync` checks CustomersManage permission, serializes the operation, locks the customer row, protects Walk-in, changes only status/version, writes an audit event and durable outcome atomically.
- `Application/Features/Thaka/ThakaSuspensionHandler.cs`: `SetThakaSuspensionHandler.HandleAsync` checks ThakaManage permission and allows only Active ↔ Suspended transitions. It combines operation and project locks with an atomic audit/outcome/status update.
- `Domain/Thaka/ThakaModels.cs`: `ThakaProjectStatus.Suspended = 4`; this uses the existing integer status column and requires no new suspension migration.
- `Server/Controllers/CustomersController.cs` and `ThakaController.cs`: POST suspension routes derive actor, terminal and session from authenticated context. Client actor fields cannot choose the authority.
- `Application/Features/Thaka/ThakaHandlers.cs`: creation and material issuance lock/check customer activity. Sales completion, exchange and quotation paths lock/check the same customer authority.
- `ThakaPaymentHandlers.cs`, `ThakaSettlementHandlers.cs`, `ThakaReversalHandlers.cs`: Active or Suspended projects permit appropriate recovery operations; financial and inventory rules still apply.
- `CustomersController.UpdateCustomer` always preserves existing activity status. A stale client profile edit containing `IsActive=true` and `PreserveActivityStatus=false` cannot implicitly resume a suspended customer.
- `PartyDirectoryQueries.cs` / `BusinessOperationsReadServices.cs`: management can explicitly include inactive customers, while operational pickers default to active customers.
- `ThakaReadService` includes customer activity in summaries/detail. Both backend adapters map it to the workspace, where material issuance is disabled for a suspended customer while recovery remains enabled.

Both new status handlers reject operation-ID reuse with a different payload or owner. Replaying a successfully committed old suspension after a later resume returns its original success without changing current status. Remote Desktop adapters persist status-operation intent across network response loss and service restart.

## Universal detail activation

`Desktop/Controls/OpenOnClick.cs` is a shared card/row activation behaviour. It supports pointer and Enter/Space activation, ignores nested buttons/editable inputs, and avoids activation after a drag. Actual pointer, keyboard and nested-button routing were exercised in the WPF harness.

It is applied to Thaka cards and table rows, customer rows, supplier rows, inventory product rows, product-management rows, sales rows and purchase rows, each using the existing detail/workspace command. Dashboard Thaka cards now open the selected project directly. Returning from a workspace refreshes the cached project directory so status changes are reflected in lists/filtering. The explicit action buttons remain available.

## Verification and evidence

- **53/53 integration/API/PostgreSQL tests passed**, with zero skipped tests in the selected scope. Includes timestamp read contracts, suspension/recovery, unauthorized transitions, status visibility, profile preservation, old replay, operation conflicts, concurrent duplicate status changes, API session/permission checks and session actor attribution. Evidence: `scratch/customer-khata-postgres-certification.txt`.
- **4/4 durable Desktop intent tests passed**, including customer and khata suspension response-loss/restart replay. Evidence: `scratch/customer-khata-desktop-tests.txt`.
- **22/22 workspace/interaction assertions passed**, across Light and Dark: pending read, unavailable totals, retry, suspended khata, suspended customer, closed-account actions, keyboard/pointer opening and nested-action isolation. Evidence and renders: `scratch/UserReportedUiDefectPack/2026-10-04/workspace-hardening`.
- **750/750 universal WPF assertions passed**, rendering 52 views in two themes (104 captures). Evidence: `scratch/UserReportedUiDefectPack/2026-10-04/universal-ui/runtime.txt`. This is UI fixture validation, not live operational workflow certification.
- Release Desktop, Server and Worker publish succeeded. Integration Release build succeeded with zero warnings/errors.
- API test composition was corrected to use an explicitly owned sequence authority fixture. It no longer attempts to inspect workstation production sequence custody. Production custody checks were not weakened.

All write tests and migration application used an owned, disposable PostgreSQL 18 cluster. The final runner root is recorded in its evidence log; the cluster was stopped and retained. No operational sale, payment, suspension, inventory edit or migration was issued.

## Production blockers and prepared rollout

Read-only migration metadata shows **20 installed migrations**, ending at `20260930065058_Phase3COwnerPinAuthorizationConsumption`. The tested source requires **22**, including:

1. `20261002101709_TrackingManufacturerIdentityAuthorityV1`
2. `20261006083906_Phase7Pass5WarrantySourceAuthority`

The installed service's current Ready response is relative to its old migration assembly. It does not prove compatibility with the new package. Deploying the new binaries alone would create a pending-schema readiness failure. These two migrations predate this suspension work; silently skipping them or changing readiness to ignore them would be incorrect.

The tracking migration normalizes/backfills existing manufacturer identifiers and rejects collisions. Per the user's explicit restriction, the legacy Unicode assessment remains **NOT_RUN_ENVIRONMENT / REQUIRES_APPROVED_DATA_SOURCE**. No legacy inventory data was inspected. An approved source/backup and its migration assessment are required before applying that cutover to production. The generated SQL is for review only and has not been executed against the shop database.

The current Windows token is not Administrator, so it cannot replace/start/stop the installed Windows services. The production Desktop process was still running during assessment and was not force-closed.

Prepared rollout utility: `scripts/Install-CustomerKhataCertifiedBinaries.ps1`. It requires an Administrator shell and a closed app, verifies every package hash, reads only migration metadata, rejects a schema mismatch **before replacing files**, preserves appsettings/connection configuration, backs up binary folders, replaces the coordinated service/frontend binaries, checks readiness and rolls binary files back on failure. It never applies the SQL or changes database contents. It also retires only this workspace's older after-close updater to prevent a stale overwrite.

Required completion order: approved protected database backup and legacy cutover assessment; approved migration rollout; then coordinated Administrator binary deployment and installed-app verification. The stable desktop shortcut continues to target the production folder. It was not redirected to a mock preview or to an incompatible staged build.

The deployment script has been parsed and its staged manifests verified; its Administrator production mutation/rollback path has **not** been executed. Production deployment remains pending, not certified complete.
