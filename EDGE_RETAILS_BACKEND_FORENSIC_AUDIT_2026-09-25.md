# Edge Retails backend forensic audit

**Audit date:** 2026-09-25  
**Mode:** Read-only source and test audit. No application source or configuration was changed.  
**Authority checked:** `docs/Edge_Retails_Final_Architecture_Report_v1.md`, selected final amendments and Annex 233.1, as resolved by `EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md` and `docs/Architecture_Authority_Manifest.json`.

## Executive result

The implementation has material security, inventory, warranty, accounting, retry, and operations defects. The highest exposure is the absence of a server-established user/session identity: terminal authentication does not establish who the cashier is, and business commands carry caller-supplied actor IDs. Separately, stock adjustment mode is persisted but not applied, condition transfers can move the wrong lot cost for serialized units, sale returns do not reject units under active or terminal warranty claims, and recognized losses are omitted from net profit.

The canonical report’s SHA-256 matches the frozen manifest (`12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673`). Later amendments were treated as controlling where they supersede earlier text. I did not treat the untracked v2 planning document or deleted sprint plans as architecture authority.

The focused unit suite ran with `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --no-restore --logger 'trx;LogFileName=backend-forensic-unit.trx' --results-directory .audit-results`. It built Domain, Application, Infrastructure, and Worker and reported **465 passed, 5 failed, 0 skipped**. The failures are three stale desktop-resolution assertions and two tests requiring a deleted Sprint 5 plan; none directly exercises the principal backend findings below. PostgreSQL integration tests were not run because `EDGE_RETAILS_TEST_DB` is unset; the harness explicitly requires an isolated database. The integration tests can mutate schema and data, so no other database was substituted.

## Severity and remediation order

| Priority | Finding IDs | Main exposure |
|---|---|---|
| Critical | C-01, C-02 | Caller-controlled user authority and false physical print success |
| High | H-01–H-22 | Identity, inventory, warranty, money, idempotency, backup, and reporting integrity |
| Medium | M-01–M-10, M-12–M-13 | Permission omissions, weak validation, read-model consistency, operational visibility |
| Low | L-01–L-04 | Minor contract, configuration, and diagnostic defects |

First close the identity/session authority gap and disable simulated printing in production. Then correct stock adjustment semantics and serialized cost provenance, block return/warranty conflicts, implement consistent replay records for all mutations, and repair backup/outbox execution. Reconcile existing data before deploying any constraints that may reveal duplicates or invalid states.

## Findings

### Critical

**C-01 — LAN business writes trust caller-supplied actor IDs and have no live user-session proof.** `TerminalAuthenticationMiddleware` authenticates a terminal from request headers and places it in `HttpContext.Items`; it does not bind a logged-in user or a live user session. The server passes request bodies to `IApplicationGateway` and the handlers authorize the `ActorId`/`CashierUserId` value in those bodies. `ApplicationPermissionAuthorizer` only resolves permissions for that supplied GUID. `CompleteSaleHandler` persists nullable `SessionId` without verifying it belongs to the actor, is current, or is open. The session table and login/logout handlers therefore do not establish authority for remote business commands. Terminal registration is intentionally anonymous in the current LAN controller, so a reachable client can obtain its own terminal credentials and invoke available mutations while impersonating a known privileged actor ID. This permits unapproved purchase, refund, expense, and other protected writes wherever the caller can supply a privileged user ID. It conflicts with canonical Sections 52, 62, 83, 142, and Annex 233.1(I).

Evidence: `src/EdgeRetails.Server/Middleware/TerminalAuthenticationMiddleware.cs` (`InvokeAsync`, terminal-only identity); `src/EdgeRetails.Server/Program.cs` (middleware pipeline); `src/EdgeRetails.Server/Controllers/*.cs` (body-forwarding mutations); `src/EdgeRetails.Application/Features/Identity/IdentityHandlers.cs` (`AuthenticateUserHandler`, `ApplicationPermissionAuthorizer`); `src/EdgeRetails.Application/Features/Terminals/TerminalHandlers.cs` (`RegisterTerminalHandler`); `src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs` (`CompleteSaleCommand.SessionId`).

**C-02 — Production’s default print engine reports success without printing.** The common infrastructure registration binds `IProductionPrintEngine` to `SimulatedProductionPrintEngine`, whose default path returns `Succeeded: true` with no device I/O or job ID. `PrintDocumentHandler` consequently persists a successful print outcome and the outbox can mark the effect complete. There is a WPF print engine source file, but no registration of that implementation was found. A production receipt or purchase document can therefore be reported as printed when no paper was produced. This directly violates canonical Sections 51.1–51.3 and can cause an operator to release goods or close a sale on a false confirmation.

Evidence: `src/EdgeRetails.Infrastructure/InfrastructureServiceCollectionExtensions.cs` (`AddEdgeRetailsInfrastructure` print registrations); `src/EdgeRetails.Infrastructure/Production/Printing/SimulatedProductionPrintEngine.cs` (`PrintAsync`); `src/EdgeRetails.Application/Production/Printing/PrintingHandlers.cs` (`PrintDocumentHandler.HandleAsync`); `src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs` (implementation has no DI registration).

### High

**H-01 — `StockAdjustmentMode` is not applied; physical-count commands post a delta.** `CreateStockAdjustmentHandler` stores `command.Mode` on the document, but the posting path always adds `BaseQuantity` to the selected bucket for increases and subtracts it for decreases. It never computes `target count − current count`. A “set physical count to 10” against a balance of 10 posts 20; repeated submission compounds the error. Zero counts are rejected even though zero is a valid physical target. Canonical Sections 14, 15, 204, and 233.1(C) require explicit, validated mode semantics.

Evidence: `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs` (`CreateStockAdjustmentHandler.HandleAsync`; mode assigned near document creation; bucket deltas in the posting loop).

**H-02 — Inventory-condition transfer does not preserve the selected serialized unit’s lot.** `InventoryConditionService.TransferAsync` locks selected inventory units but then calls the generic FIFO `TransferBucketAsync(productId, from, to, quantity)`. It does not transfer from each unit’s own `InventoryLotId`. When selected serialized units came from different lots, the stock bucket and cost layer can move from unrelated FIFO lots while unit status changes on the selected identities. Future sale/return/valuation provenance diverges. The service also rounds quantity before its serialized whole-unit test, so a fractional value just above an integer can be accepted. Canonical Sections 16–17, 109, 185, and 233.1(C).

Evidence: `src/EdgeRetails.Application/Features/Inventory/InventoryConditionHandlers.cs` (`TransferAsync`); `src/EdgeRetails.Infrastructure/Services/InventoryCostAllocator.cs` (`TransferBucketAsync`); `src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs` (`GetInventoryUnitsForUpdateAsync`).

**H-03 — Scrap cost can be multiplied by quantity a second time during condition transfer.** For scrap, the service sums selected unit acquisition costs into `exactCost` and passes that sum as the allocator’s unit-cost override to `RemoveCarryingValueAsync`, which applies an amount per quantity. Scrapping two serialized units costing 100 and 200 can therefore request removal of 600 rather than 300. Depending on the remaining pool, this either rejects a valid scrap or removes value from unrelated stock. The movement then records this overstated loss. Evidence: `InventoryConditionService.TransferAsync` and `InventoryCostAllocator.RemoveCarryingValueAsync` in the files above.

**H-04 — Sale return can restock a serialized unit still under warranty custody or already terminally replaced/refunded.** The serialized return path verifies that the unit was sold on the sale item, has not previously been returned, and is currently `Sold`. It does not call the repository’s existing `HasActiveClaimForUnitAsync` or `IsUnitTerminallyResolvedAsync`. Customer warranty workflows leave the original unit’s status as `Sold` while it is with the supplier and after some terminal outcomes. A return can therefore restore sellable stock and refund it while the warranty workflow still owns or has financially closed the same physical item. The same omission exists in the commercial-exchange return path. Canonical Sections 112–115, 149, and 223.

Evidence: `src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs` (`PrepareSerializedReturnUnitsAsync`, `RestoreSerializedUnitsAsync`); `src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs` (same helpers); `src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs` (`HasActiveClaimForUnitAsync`, `IsUnitTerminallyResolvedAsync`); `src/EdgeRetails.Infrastructure/Persistence/Configurations/WarrantyConfigurations.cs` (active original-unit uniqueness).

**H-05 — `StockAdjustment` and manual cash effects have no safe replay contract.** Stock-adjustment creation uses a caller correlation value for audit but does not look up a committed operation or fingerprint a payload; replay can create another adjustment number and apply the same inventory delta again. Manual cash movement likewise has no client operation identity. The shared transaction prevents partial commits but does not make a retried committed command idempotent. This is especially dangerous after a client timeout or response loss. Canonical Sections 26, 66, 160, and Annex 233.1(L).

Evidence: `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs` (`CreateStockAdjustmentHandler`); `src/EdgeRetails.Application/Features/Finance/CashSessionHandlers.cs` (`RecordManualCashMovementHandler`); `src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs` (correlation index is not unique); cash configuration has no command-operation key.

**H-06 — Idempotency coverage is inconsistent, and client operation IDs are not globally typed/fingerprinted.** Sale and purchase replays have dedicated lookup logic; warranty operations have a richer record. Several money or inventory commands either replay without comparing the original payload/actor or do not replay at all. Return, purchase return/void, expense, and Thaka flows must be checked separately; no common operation ledger enforces `(operation id, command type, actor, terminal, payload fingerprint, original result)` across them. Same GUIDs can be used in unrelated command tables; some read-model lookups return only one matching entity kind. Same-ID/different-payload retries can therefore return a stale result, execute a second effect, or be ambiguous to recovery. Canonical Sections 26, 66, 160, 225, and Annex 233.1(L).

Evidence: `CompleteSaleHandler` and `CreatePurchaseHandler` perform bespoke operation lookups; `CreateSaleReturnHandler`, finance, and Thaka handlers have different lookup/validation patterns; `src/EdgeRetails.Application/Features/Terminals/OperationStatusQueryHandler.cs` checks a fixed subset of operation stores rather than a typed operation registry.

**H-07 — A completed POS draft cannot be replayed after an uncertain response.** `CompletePosDraftHandler` requires `Status == Open` before delegating to the sale handler. After the first successful transaction it marks the draft `Converted`. A retry using the same operation ID then fails `draft_not_open` before consulting the committed sale result, so the caller cannot recover the result through the original operation. The conversion does not persist a direct SaleId on the draft in the path shown. This breaks the canonical same-operation recovery contract for a high-frequency POS workflow.

Evidence: `src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs` (`CompletePosDraftHandler.HandleAsync`); `src/EdgeRetails.Domain/Sales/PosDraftModels.cs` (draft state/linkage); `src/EdgeRetails.Application/Features/Terminals/OperationStatusQueryHandler.cs`.

**H-08 — Thaka price is based on product base-unit price while quantity and stock use the selected unit conversion.** `IssueThakaMaterialHandler` sets the authoritative charge to `product.DefaultSalePrice` without multiplying by `ProductUnit.FactorToBaseUnit`, while it records entered-unit quantity and consumes converted base quantity. If a permitted pack unit has factor 12, the material issue can charge one base-unit price for a 12-unit pack. The Thaka read catalog currently exposes only factor-1 base mappings, but the command accepts a product-unit ID and validates `CanUseInThaka`; direct callers can still select a non-base mapping. Canonical Sections 109 and 223.

Evidence: `src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs` (`IssueThakaMaterialHandler`, authoritative charge and quantity snapshot); `src/EdgeRetails.Infrastructure/Services/ThakaReadService.cs` (`GetMaterialCatalogAsync`, currently factor-1 UI projection).

**H-09 — Thaka can attach one serialized unit more than once across different issue lines.** Duplicate detection is per input line. The same `InventoryUnitId` can be selected in two lines with different `ProductUnitId`s. Both lines validate the unit as `InStock` before either is consumed; each then decrements a lot and records a material-unit link. The final unit status transitions to issued once, but two issue lines claim the same physical unit and stock/cost are consumed twice. The unique database key is `(MaterialIssueItemId, InventoryUnitId)`, so it does not prevent reuse across two items in the same issue. Canonical Sections 109, 185, and 223.

Evidence: `src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs` (`IssueThakaMaterialHandler` selection/preparation/consumption); `src/EdgeRetails.Infrastructure/Persistence/Configurations/ThakaConfigurations.cs` (`ThakaMaterialIssueUnitConfiguration`).

**H-10 — Customer warranty eligibility is incorrectly tied to purchase provenance.** `CreateWarrantyClaimHandler` requires each sold serialized unit to have `SupplierProductId` and `SourcePurchaseItemId`. Canonical inventory supports opening stock and warranty replacement identities with a non-purchase origin, and a shop-owned replacement can later be sold with a sale link but no purchase item. The handler rejects these otherwise eligible sold units even when warranty dates and ownership qualify. This makes warranty recovery unavailable for legitimate stock origins.

Evidence: `src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs` (`CreateWarrantyClaimHandler`, source validation); `src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs` (origin identity); `src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs` (multiple allowed provenance sources).

**H-11 — Shop-stock warranty resolution does not consistently prove replacement/credit identities belong to the case.** The resolution workflow accepts selected product units in branches that validate status, supplier, or cost without uniformly proving each selected identity is linked to this warranty case and the case’s source item/lot. In particular, the credited-unit path operates on `WITH_SUPPLIER` units selected for the case but does not bind every selected unit to the case’s own outgoing links; replacement receive checks identity input but case association is not enforced as a single invariant across old/new pairs. A caller can resolve one case using another case’s unit of the same product/supplier. Canonical Sections 115, 185, 219.4, and 223.

Evidence: `src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs` (`ReceiveShopStockWarrantyHandler`, selected-unit/credit/replacement branches); inspect case link model in `WarrantyConfigurations.cs` and related domain models.

**H-12 — Serialized identity validation and uniqueness are not consistent across purchase and warranty replacement.** The purchase identity path normalizes IMEI by trim/uppercase rather than applying `IdentityNormalizationRules.NormalizeImei`, so malformed lengths/checksums can be persisted even though adjustment paths use the canonical validator. Batch duplicate detection is also per product and compares existing rows, not all identities accumulated in the current command across serial/IMEI columns; database uniqueness is per individual column, so `imei1` of one new unit can equal `imei2` of another without a constraint conflict. The warranty customer-replacement path has the same missing in-batch cross-column check. Canonical Sections 83, 175, 183, and Annex 233.1(J).

Evidence: `src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs` (`ValidateIdentityBatch` and unit creation); `src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs` (customer replacement identity allocation); `src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs` (`InventoryIdentityExistsAsync`); `src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs` (separate serial/IMEI1/IMEI2 unique indexes).

**H-13 — Warranty return/claim quantity accounting races and command fingerprints are incomplete.** Warranty claim creation checks active and terminal quantities and locks sale rows, but sale-return handling does not share that same gate (H-04). Warranty lifecycle operation fingerprints are assembled as delimited strings and do not uniformly include actor/terminal context; delimiter-bearing notes/reasons can create ambiguous canonical representations unless length/escaping rules are applied before hashing. In addition, customer replacement validation queries existing identity state before new units have all been accumulated, leaving same-command cross-field duplicates possible (H-12). Treat this as a single warranty transaction-integrity gap to address with typed canonical payload hashing and cross-module sale-item/unit locks.

Evidence: `WarrantyHandlers.cs` (`CreateWarrantyClaimHandler`, lifecycle fingerprint helpers, replacement receipt); `EfRepositories.cs` (`GetTerminallyRemovedQuantityAsync`); canonical Sections 185, 225, Annex 233.1(L).

**H-14 — Reporting net profit omits recognized inventory losses.** The canonical equation is gross profit minus posted operating expenses and recognized inventory losses. `ReportingReadService.GetSnapshotAsync` subtracts expenses but never aggregates `InventoryMovement.RecognizedLossAmount`; the trend query has the same omission. Inventory condition transfer, scrap returns, warranty and stocktake paths can record such losses, but dashboards overstate profit for the period. Canonical Sections 47, 132, 226, and Annex 233.1(K).

Evidence: `src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs` (`GetSnapshotAsync`, profit calculation; `BuildTrendAsync`); `src/EdgeRetails.Domain/Inventory/InventoryModels.cs` (`RecognizedLossAmount`); `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs` (loss handling).

**H-15 — Reporting derives shop-day boundaries from host local time, not configured shop time.** `ReportingReadService.ToOffset` applies `TimeZoneInfo.Local`; the canonical clock resolves a configured shop time zone. On a server whose local zone differs from the shop zone, daily/monthly sales and returns fall into the wrong period, and trend buckets use UTC timestamps against local calendar slices. `SystemClock` itself falls back to UTC when the configured time-zone environment value is absent. Canonical Sections 47, 49, Annex 233.1(B).

Evidence: `src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs` (`ResolvePeriod`, `ToOffset`, trend grouping); `src/EdgeRetails.Infrastructure/Services/PlatformServices.cs` (`SystemClock.ResolveShopTimeZone`).

**H-16 — Initial receipts/labels have no transactional outbox producer.** The repository contains `IOutboxWriter.Enqueue` and a print effect consumer, but no business handler calls `Enqueue`. `CompleteSaleHandler` commits a receipt snapshot and returns; no print intent is atomically stored with the sale. `AutoPrintDefault` is read and saved but not used to create a durable request. A crash after sale commit loses the initial print request; a caller retry or manual print creates a new print identity instead of recovering the original request. Canonical Sections 35, 51.1–51.3, 105, and Annex 233.1(E).

Evidence: `rg` across `src/` found only the writer declaration and `OutboxRepository.Enqueue` implementation; `src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs`; `src/EdgeRetails.Application/Production/Outbox/PrintOutboxEffectHandler.cs` (always supplies `PrintJobId: null`).

**H-17 — Outbox work is read without a durable worker claim/lease.** `OutboxRepository.GetPendingMessagesAsync` includes both Pending and Processing rows and does not claim rows with `FOR UPDATE SKIP LOCKED`, an owner lease, or a fencing token. Two worker processes can receive and execute the same effect concurrently; file locks only coordinate the same configured local path and do not cover multiple hosts. Print effects in particular can double-submit. Canonical Sections 51, 57, 66, 185, 225.

Evidence: `src/EdgeRetails.Infrastructure/Repositories/OutboxRepository.cs` (`GetPendingMessagesAsync`); `src/EdgeRetails.Application/Production/Outbox/OutboxProcessor.cs` (`ProcessPendingAsync`).

**H-18 — Print retries can create a second physical print after an uncertain first submission.** The outbox effect payload does not carry a stable print-job ID; it passes null and requests `Initial` on every delivery. If the printer call throws after submission, `PrintDocumentHandler` records `OutcomeUnknown` and rethrows; `OutboxProcessor` treats the exception as retryable. The next delivery creates a new job and submits again. Cancellation after device submission can similarly mark the job Cancelled and allow a later retry. This defeats the print handler’s same-job retry protection. Canonical Sections 51.1–51.3 and Annex 233.1(E).

Evidence: `src/EdgeRetails.Application/Production/Outbox/PrintOutboxEffectHandler.cs`; `src/EdgeRetails.Application/Production/Printing/PrintingHandlers.cs` (`HandleAsync` around engine exceptions/cancellation); `src/EdgeRetails.Application/Production/Outbox/OutboxProcessor.cs` (generic exception retry).

**H-19 — Scheduled backup job never executes a backup.** When a handler and backup directory exist, `ScheduledBackupJob.ExecuteAsync` only logs “Starting scheduled daily backup” and advances `_lastRunUtc`; it never invokes `CreateBackupHandler.HandleAsync`. If the handler or directory is absent it returns silently. The worker can remain healthy while producing no scheduled recovery artifact. Canonical Sections 56–57 and Annex 233.1(G).

Evidence: `src/EdgeRetails.Worker/Jobs/ScheduledBackupJob.cs` (`ExecuteAsync`); `CreateBackupHandler` is available in `src/EdgeRetails.Application/Production/Backup/BackupHandlers.cs`.

**H-20 — Backup encryption has no key identifier/version lookup for rotation and remote recovery.** The AES-GCM envelope uses a magic header and nonce but does not persist a key ID/version and the provider returns one current key. There is no lookup path to retain old decryption keys after rotation or to tell a remote verifier which key lifecycle applies. Existing encrypted backups can become unrecoverable after a key replacement. Canonical Sections 56–57 and Annex 233.1(D).

Evidence: `src/EdgeRetails.Infrastructure/Production/Backup/AesGcmBackupProtector.cs`; `src/EdgeRetails.Application/Production/Backup/BackupProtectionContracts.cs`; `src/EdgeRetails.Infrastructure/Production/Backup/FileProductionMaintenanceIntegrityKeyProvider.cs`.

**H-21 — Operation-status endpoint exposes business-operation existence and details anonymously, but its recovery coverage is incomplete.** `SystemController.GetOperationStatus` is an anonymous exemption in terminal middleware and returns found status and document metadata for a guessed operation ID. The handler checks only a fixed set of sale, return, purchase, void, supplier-payment, and refund stores; it omits warranty, stock adjustments, cash, expense, and Thaka, and one GUID reused across types can produce ambiguous results. The endpoint is neither a complete canonical operation registry nor a safe private recovery query. Canonical Sections 26, 66, 82, 83, and Annex 233.1(L).

Evidence: `src/EdgeRetails.Server/Controllers/SystemController.cs`; `src/EdgeRetails.Server/Middleware/TerminalAuthenticationMiddleware.cs` (anonymous path allowlist); `src/EdgeRetails.Application/Features/Terminals/OperationStatusQueryHandler.cs`.

**H-22 — Posted business dates are accepted from the command instead of derived from the configured shop time zone.** `CreatePurchaseHandler` persists `command.PurchaseDate`, `PostExpenseHandler` persists `command.ExpenseDate`, and Thaka creation persists `command.StartedOn`. Those dates are caller-controlled and are later used in purchase, expense, and project reporting. A client can post a transaction into a different accounting period. The canonical Annex 233.1(B) requires immutable currency and posted business date derived from the configured shop time zone; the existing `IClock.ShopDate` should be authoritative for posting.

Evidence: `src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs` (`CreatePurchaseCommand.PurchaseDate` and purchase construction); `src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs` (`PostExpenseCommand.ExpenseDate` and expense construction); `src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs` (`CreateThakaProjectCommand.StartedOn`); `src/EdgeRetails.Infrastructure/Services/PlatformServices.cs` (`SystemClock.ShopDate`).

### Medium

**M-01 — Role deactivation does not revoke permissions from existing users.** `IdentityReadRepository.GetEffectivePermissionKeysAsync` checks the user and permission active flags but does not join/check `Role.IsActive`. Deactivating a role leaves its granted permissions usable until user reassignment. Authentication checks role status only at login, but command authorization does not.

Evidence: `src/EdgeRetails.Infrastructure/Repositories/SetupIdentityRepositories.cs` (`GetEffectivePermissionKeysAsync`); `src/EdgeRetails.Application/Features/Identity/IdentityHandlers.cs` (`ApplicationPermissionAuthorizer`).

**M-02 — Several mutation handlers have no handler-level permission check.** Stocktake create/start/count/review/post/cancel, open/manual/close cash, product-unit configuration/barcodes, and inventory condition transfer are registered as directly callable application handlers without `IApplicationPermissionAuthorizer` checks. Quotation create/update/issue/cancel handlers also lack those checks, but they are not registered in the common DI composition, so their current reachability is lower; that missing registration is itself an incomplete feature path. Some reachable handlers are protected by surrounding desktop UI flows, but the command boundary itself does not enforce the canonical permission. New transports or internal callers can bypass that UI gate.

Evidence: `src/EdgeRetails.Application/Features/Inventory/StocktakeHandlers.cs`; `CashSessionHandlers.cs`; `Catalog/ProductUnitHandlers.cs`; `Inventory/InventoryConditionHandlers.cs`; `Sales/QuotationHandlers.cs`; compare checked handlers such as `CompleteSaleHandler` and `PostExpenseHandler`.

**M-03 — Production authorization implementation is a no-op.** `DefaultProductionAuthorization.EnsureAuthenticatedAsync` and `EnsurePermissionAsync` always complete successfully and are registered as the default. Print and backup handlers call this interface. The actual desktop runtime does not replace it with a session-backed implementation in the inspected composition. As wired, those handlers do not enforce `SettingsManage` or an authenticated actor. This overlaps C-01 but affects the separate production-operation boundary.

Evidence: `src/EdgeRetails.Infrastructure/Production/DefaultProductionAuthorization.cs`; registration in `InfrastructureServiceCollectionExtensions.cs`; consumers in `Production/Backup/BackupHandlers.cs` and `Production/Printing/PrintingHandlers.cs`.

**M-04 — Terminal registration checks the maximum count but not license validity.** `RegisterTerminalHandler` blocks only when a non-null license payload is present and the active terminal count meets the quota. Missing/corrupt/expired/unavailable license results with a null payload fall through and allow registration. The registered PostgreSQL resource-lock path serializes quota check and insert when the application is composed with its normal transaction and lock services; I found no evidence supporting a cross-process quota race in that configuration. Terminal status updates accept enum integer values without `Enum.IsDefined` and do not re-enforce quota when reactivating a terminal.

Evidence: `src/EdgeRetails.Application/Features/Terminals/TerminalHandlers.cs` (`RegisterTerminalHandler`, `UpdateTerminalStatusHandler`); `src/EdgeRetails.Infrastructure/Repositories/TerminalRepository.cs`; `TerminalConfiguration.cs`.

**M-05 — Authentication uses a four-digit PIN without throttling, lockout, or failed-attempt audit.** PBKDF2-SHA256 is appropriate for storage, but `AuthenticateUserHandler` verifies a four-digit secret with no per-user/device backoff or lockout and no persistent failed-attempt counter. Repeated guesses are cheap to issue against a reachable login surface. Canonical Sections 52, 62, and 83 require security and authority controls around login.

Evidence: `src/EdgeRetails.Application/Features/Identity/IdentityHandlers.cs` (`AuthenticateUserHandler`); `src/EdgeRetails.Infrastructure/Services/IdentitySetupServices.cs` (`Pbkdf2PinCredentialService`).

**M-06 — `StockAdjustment` decreases can omit or misstate recognized loss and provenance.** The handler accepts an arbitrary cost snapshot and its normal negative path removes carrying value without consistently setting `InventoryMovement.RecognizedLossAmount`. The quantity-lot and serialized removal branches silently skip missing/insufficient lot positions in some loops yet still apply aggregate stock/cost changes. There is no whole-command replay protection (H-05), and the actor/product/bucket data is loaded before the deterministic product locks. Canonical Sections 15–17, 204, and 233.1(C/K).

Evidence: `src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs` (movement creation, lot allocation, serialized removal, loss field); `InventoryCostAllocator.cs` (bounded allocation behavior).

**M-07 — Warranty credit/replace resolution lacks a stocktake barrier on all branches.** The inventory repository blocks products in Counting or Review; sale/purchase/condition paths call it, but customer warranty credits/replacements and shop-stock credit/replacement branches can mutate inventory/cost without checking. A stocktake can therefore post a count while these mutations continue. Reverse Thaka material also restores stock without the guard. Canonical Sections 116–119 and 162.

Evidence: `src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs` (`IsProductBlockedByCountingStocktakeAsync`); `src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs` (replacement/credit branches); `src/EdgeRetails.Application/Features/Thaka/ThakaReversalHandlers.cs` (`ReverseThakaMaterialHandler`).

**M-08 — Purchase/warranty IMEIs bypass checksum/length normalization.** `NormalizeImei` validates 14/15 digits and Luhn checksum, but purchase and some warranty replacement paths only uppercase/trim raw values. Invalid manufacturer IDs can become authoritative identifiers and fail matching/scan lookup later. See H-12 for cross-field uniqueness.

Evidence: `src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs` (`NormalizeImei`); `CreatePurchaseHandler.cs`; `WarrantyHandlers.cs`.

**M-09 — Search/overview read paths are inconsistent about filters, bounds, and point-in-time consistency.** Several EF/Dapper read services issue multiple sequential queries without a shared snapshot transaction; dashboards combine values that can come from different committed moments. Warranty intake makes bounded searches across only the first 100 matches per namespace and then truncates to 100, so valid eligible sale lines can be omitted from the queue. Thaka all-project queries aggregate the full issue/payment history; the paged path computes multiple global CTEs, which may become expensive as history grows. Canonical Annex 233.1(G) calls for bounded, governed reads and stable cursors.

Evidence: `src/EdgeRetails.Infrastructure/Services/Phase5OperationsReadServices.cs` (`SearchClaimIntakeAsync`); `ThakaReadService.cs` (`GetProjectsAsync`, `GetProjectsPageAsync`); `BusinessOperationsReadServices.cs` (`GetSnapshotAsync`).

**M-10 — Controller error mapping returns authorization denials as HTTP 400.** Application authorization errors use `authorization.*`, but controller mappings test `StartsWith("auth.")`. Denied commands therefore fall through to Bad Request instead of Forbidden, which gives clients the wrong recovery behavior and obscures security telemetry. Several controller files repeat the same mapping.

Evidence: `src/EdgeRetails.Application/Features/Identity/IdentityHandlers.cs` (`ApplicationPermissionAuthorizer` returns `authorization.denied`); `src/EdgeRetails.Server/Controllers/SalesController.cs` (same pattern in Purchasing, Finance, Warranty, and Terminals controllers).

**M-12 — Serialized catalog policy requires manufacturer serial/IMEI even though the architecture treats those as optional identity overlays.** `Product.ValidateTrackingPolicy` rejects `TrackingMode.Serialized` when both flags are false. Canonical Sections 83 and 175 distinguish mandatory system TrackingCode from optional manufacturer serial and IMEI identifiers. Product setup cannot represent a serialized unit whose only physical identity is the system-issued TrackingCode.

Evidence: `src/EdgeRetails.Domain/Catalog/CatalogModels.cs` (`Product.ValidateTrackingPolicy`); `src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs` (`BuildTrackingCode`).

**M-13 — Transaction runner has no canonical bounded lock-wait retry policy.** `EfTransactionRunner` checks maintenance/disk state, starts a ReadCommitted transaction, and rolls back on failure, but does not classify PostgreSQL deadlock/serialization/lock-timeout errors for a bounded whole-operation retry using the same operation ID. `PostgresOperationLock` uses transaction-scoped advisory locks without an explicit scoped `lock_timeout` or retry telemetry. Under lock contention the 180-second EF command timeout can hold callers for a long time, then return a generic failure even when replay could safely resolve the outcome. Canonical Section 185.1.

Evidence: `src/EdgeRetails.Infrastructure/Services/PlatformServices.cs` (`EfTransactionRunner`, `PostgresOperationLock`, command-timeout resolution); `src/EdgeRetails.Application/Abstractions/PlatformAbstractions.cs` (transaction and lock contracts).

### Low

**L-01 — Product attributes do not enforce a real versioned schema.** `AttributesPolicy.Validate` accepts any positive schema version and valid JSON object; it does not constrain keys, value types, ranges, or reject unknown fields. Version 1 is a number without a defined contract, so readers can interpret the same payload differently after future releases. Canonical Annex 233.1(C).

Evidence: `src/EdgeRetails.Domain/Catalog/CatalogModels.cs` (`AttributesPolicy.Validate`).

**L-02 — Quantity snapshots can round entered quantity differently from the authoritative base quantity.** `TransactionQuantitySnapshot.Create` calculates base quantity from raw entered quantity and rounds it, while the snapshot’s entered quantity is rounded separately. At precision boundaries the persisted entered quantity multiplied by its factor need not equal the persisted base quantity. Reject over-precision or derive both from the same canonical value.

Evidence: `src/EdgeRetails.Domain/Common/QuantityMath.cs` (`TransactionQuantitySnapshot.Create`).

**L-03 — Health endpoint is liveness-only but presents unconditional healthy status.** `SystemController` returns HTTP 200/healthy without checking database, worker, schema, outbox, or backup state. A load balancer/operator can treat a process with no database or worker as ready. Keep liveness separate from readiness/diagnostics and expose those checks through the existing diagnostics service.

Evidence: `src/EdgeRetails.Server/Controllers/SystemController.cs`; `src/EdgeRetails.Infrastructure/Production/Diagnostics/Phase5DiagnosticsService.cs` (separate richer probes).

**L-04 — Host configuration silently falls back to UTC when the shop time zone is unavailable.** `SystemClock.ResolveShopTimeZone` tries the two expected Pakistan IDs and silently returns UTC if neither resolves. Because terminal deployments may have different OS time-zone databases or a malformed environment, business dates then shift at midnight relative to the configured shop policy without a startup failure or prominent health check.

Evidence: `src/EdgeRetails.Infrastructure/Services/PlatformServices.cs` (`SystemClock.ResolveShopTimeZone`); canonical Annex 233.1(B).

## Architecture conflicts and incomplete authority

- `CompleteSaleHandler` validates submitted price equals default catalog price and stores snapshots, but the frozen Section 213 expressly authorizes permissioned overrides with a reason, a stronger below-cost permission, and an audit snapshot. The default-only implementation is an incomplete feature contract; do not reintroduce the superseded “no override” rule.
- `RuntimeLicenseService` and `ProductionStartupCoordinator` exist, and startup probes are registered, but `BackendRuntime.CheckStartupAsync` uses the older database/setup-only readiness service. It does not call the production coordinator, so license, strict migration compatibility, disk, maintenance, and session-recovery gates are not all on the desktop startup path. Server `Program` also starts without running the coordinator.
- `ProductionDiagnosticsService` explicitly reports no concrete remote backup verification and no reconciliation failure authority. These must remain unavailable/action-required; uploaded artifacts or a file heartbeat do not prove remote restore or reconciliation health.
- `ProductionAuditSink`, backup integrity keys, backup encryption keys, and application authorization are separate authorities with different rotation/authentication semantics. The audit sink’s optional HMAC mode is only used when an integrity key provider is supplied; verify production construction and protect audit file permissions/rotation.
- Common infrastructure’s print engine is simulated and the worker’s `ScheduledBackupJob` is a placeholder. The server starts worker jobs independently of whether these authorities are actually configured, which creates misleading operational health.
- `OperationStatusQueryHandler` and business-table operation IDs act as parallel recovery authorities. Canonical Annex 233.1(L) requires one retained, typed, payload-fingerprinted operation record with actor, terminal, status, and original result.

## Test and verification gaps

The current unit suite has meaningful behavioral coverage for sales, purchases, warranty, stock adjustments, terminal concurrency, backup, print, and unknown outcomes. Its current failures are:

| Failing test | Observed cause |
|---|---|
| `Sprint2ForensicAuditTests.MainWindow_MeetsTargetResolutionConformance` | Main window no longer satisfies the old 1366 minimum width assertion |
| `Sprint6Phase2ForensicAuditTests.Phase2_MainWindowLocksCanonicalDesktopMinimum` | Same stale desktop-resolution expectation |
| `Sprint5ForensicAuditTests` (three cases) | Read deleted `docs/Sprint5_Master_Implementation_Plan.md` |

No PostgreSQL integration tests were run because the required isolated `EDGE_RETAILS_TEST_DB` is absent. This leaves live database constraints, lock ordering, transaction rollback, and the concurrency scenarios unverified in this audit run. Relevant existing suites include `Phase2ConcurrencyPostgresTests`, `Phase2TransactionalPostgresTests`, `Phase2ReconciliationPostgresTests`, `Phase3UnknownOutcomeReplayIntegrationTests`, `Phase3CrashRestartIntegrationTests`, `Phase4MultiTerminalConcurrencyTests`, `Phase4LanServerIntegrationTests`, `SerializedSalesPurchasingPostgresTests`, and `ShopHolderOperationalPostgresTests`.

Priority missing regression cases before remediation closes:

1. LAN request with valid terminal secret but absent/expired user session; forged actor and mismatched cash session; inactive role revocation.
2. Stocktake absolute count (including zero), retry after committed response loss, and operation ID reused for a different command/payload.
3. Serialized condition transfer across two purchase lots; scrap carrying-value conservation; stock bucket, exact unit, and lot ledger reconciliation.
4. Sale return against a unit actively at supplier and against a refunded/replaced warranty; concurrent sale return vs warranty transition.
5. Thaka same unit across separate lines and non-base unit charge calculation.
6. Opening-stock/customer-replacement warranty eligibility and shop warranty resolution using a unit from another case.
7. IMEI 14/15 digit, Luhn, cross-column, and within-batch duplicate tests in purchases and customer replacement receipt.
8. Net-profit reconciliation with recognized loss events and reporting across shop timezone boundaries.
9. Two outbox workers racing on one message; printer timeout after physical submission, cancellation after submission, and stable job identity on replay.
10. Scheduled backup creates a verifiable artifact and restores it after key rotation with old key IDs retained.

## Coverage statement

The source inventory contains 216 backend C# files across Application (74), Domain (22), Infrastructure (103), Server (10), and Worker (7). The review traced backend modules, handlers, repositories, read services, transaction and cost services, persistence configuration, and principal end-to-end workflows across those projects and the test projects. Generated EF migration designer files and every UI-only desktop view were not individually re-audited line by line; the desktop composition was inspected where it establishes backend startup, printing, or authorization authority. Accordingly, this is a broad code-path audit with source-evidenced findings, but it is not a literal certification that every generated line or desktop UI event handler was manually inspected. A configured isolated PostgreSQL run and follow-up execution of the listed regression cases are required to close those evidence gaps.

The audit was performed against the working tree, which already contained uncommitted source, migration, and test changes when inspected. I did not modify application source or configuration while auditing; the report file is the only audit artifact added. Since no clean baseline was available, findings describe the inspected working-tree state and should be rechecked against the intended release commit before remediation is prioritized.
