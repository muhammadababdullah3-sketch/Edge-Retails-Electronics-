# Edge Retails — Sprint 7 Phase 2 Progress Report

**Date:** 2026-09-20
**Phase:** Transactional Business Backend Attachment
**Status:** IN PROGRESS
**Git:** untouched

## Phase 2 objective

Replace demo business authority module-by-module with Application/Infrastructure backend services while preserving the completed WPF UI.

The cutover must be vertical, not cosmetic: the same authoritative backend identifiers, stock values and transactional rules must feed both reads and writes.

## First production slice completed

The first Phase 2 slice establishes the POS transactional entry boundary.

Implemented:
- authoritative POS catalog application read contract.
- PostgreSQL/EF implementation for the POS catalog.
- product UUID and default sale-unit UUID projection.
- sellable stock projection from inventory.stock_balances.
- authoritative unit sale price projection using product price + unit conversion factor.
- category and unit metadata projection.
- reference cost and serialized-product flag projection.
- transactional Sales/Purchasing handlers registered in the Infrastructure composition root.
- First Setup handler registered for the same scoped composition model.
- Phase 2 forensic tests proving Desktop still has no direct DB authority.

## Important dependency discovered

The current Sprint 6 POS uses demo product identifiers such as P1/P2 while backend products use UUIDs.

A safe production cutover therefore cannot replace only ITransactionService. The POS catalog and transaction writer must move together or the cart would contain identifiers the backend cannot authoritatively resolve.

The Phase 2 cutover order is therefore:
1. authoritative catalog + live stock.
2. backend sale gateway.
3. sales history/detail/returns.
4. purchasing + inventory views.
5. Thaka.
6. expenses + parties.
7. reporting read models.## Current automated gates

- Debug full solution build: PASS, 0 warnings, 0 errors.
- Debug unit/forensic tests: 138/138 PASS.
- Release full solution build: PASS, 0 warnings, 0 errors.
- Release unit/forensic tests: 138/138 PASS.
- targeted Phase 2 formatter: PASS.
- direct Npgsql/EF authority in Desktop: still prohibited by forensic test.

## Runtime database gate

The real PostgreSQL runtime gate from Phase 1 remains unresolved because EDGE_RETAILS_TEST_DB is not configured with an authenticated isolated database connection.

Phase 2 code continues to be designed against the real PostgreSQL boundary, but transactional runtime PASS will not be claimed until that database gate is available.

## Next Phase 2 implementation slice

Next implementation work should attach:
- backend catalog gateway to NewSaleViewModel.
- UUID ProductUnitId metadata to cart lines.
- scoped CompleteSaleHandler adapter.
- authoritative Sales History + Sale Detail reads.
- sale return adapter.

Only after that vertical Sales slice is closed should Purchases/Inventory UI authority be cut over.

## Sales vertical cutover — current progress

The second Phase 2 slice now connects the completed WPF sales flow to production-facing backend gateways without giving Desktop direct database authority.

Implemented:
- EDGE_RETAILS_DB gated BackendRuntime composition.
- scoped backend POS catalog gateway.
- backend transaction adapter over CompleteSaleHandler and CreateSaleReturnHandler.
- nullable authoritative UserId added to the session contract.
- production sale operations fail closed when no persistent backend actor exists.
- POS products carry backend ProductId and ProductUnitId UUIDs.
- selected customers can carry a backend CustomerId.
- selected demo-only customers are rejected in backend mode rather than silently persisted as Walk-in.
- serialized backend products are rejected until exact serialized-unit selection is attached.
- backend sales do not mutate DemoRetailState stock after commit.
- POS catalog reloads from backend after a production sale.
- Sales History refreshes asynchronously from the backend read model.
- backend invoice identifiers such as SALE-* are preserved exactly in the UI.
- SaleDetail read model now includes exact return-item rows.
- backend sale-return adapter resolves SaleItemId and conversion-factor snapshots before issuing a return.
- backend remains authoritative for sale price, stock, cost, refund amount, inventory movement and idempotency.
- UI invoice-discount projection was corrected to prevent double-discount display.

### Deliberate fail-closed boundaries still present

The following are not bypassed:
- Cash sale requires an open backend cash session.
- Cash refund requires an open backend cash session.
- Production sale requires an authenticated persistent backend UserId.
- Serialized sales require exact serialized InventoryUnitIds.
- demo-only customer records cannot be attached to a production sale.

These boundaries will be removed only by attaching the corresponding authoritative backend flows, not by weakening backend validation.

### Current verification after Sales cutover slice

- targeted formatter: PASS.
- Debug full solution build: PASS, 0 warnings, 0 errors.
- Release full solution build: PASS, 0 warnings, 0 errors.
- Debug unit/forensic tests: 143/143 PASS.
- Release unit/forensic tests: 143/143 PASS.
- offline/demo application startup smoke test with EDGE_RETAILS_DB unset: PASS.
- direct Npgsql/EF operations in Desktop: still prohibited.
- real PostgreSQL transactional runtime verification: pending authenticated test DB configuration.

## Next continuation point

The next Phase 2 slice is Purchases + Inventory authority cutover:
1. authoritative purchase history/detail read model into WPF.
2. supplier UUID propagation.
3. create purchase / purchase return / void purchase gateway.
4. inventory stock/provenance projection into InventoryViewModel.
5. remove local demo stock mutation from backend purchase flows.
6. regression + forensic gates before moving to Thaka.
