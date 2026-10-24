# Fail-Open Authorizer Audit — Disposition Beyond CAT-107's Scope

**Run:** `ER-RUN-CATALOG-CORRECTION-20261021-107`  
**Date:** 2026-10-22  
**Trigger:** A fail-open guard was found and fixed in `ProductUnitHandlers`. This document records the
repo-wide sweep for the same pattern, so the fix is not mistaken for a one-off.

## The pattern

```csharp
private readonly IApplicationPermissionAuthorizer? _authorizer;   // nullable
...
if (_authorizer is not null && command.ActorId != Guid.Empty)      // skips when either fails
{
    var auth = await _authorizer.AuthorizeAsync(...);
    if (!auth.IsSuccess) return Result.Failure(...);
}
```

Two independent ways to skip the check entirely:

1. `_authorizer is null` — the guard body never runs, and the mutation proceeds unauthenticated.
2. `command.ActorId == Guid.Empty` — same, the caller supplies no actor and is never evaluated.

That is fail-open by construction: the absence of a security dependency is treated as permission to
proceed, when it must be treated as denial.

## What was fixed inside CAT-107

`src/EdgeRetails.Application/Features/Catalog/ProductUnitHandlers.cs` — both
`ConfigureProductUnitsHandler` and `SetProductUnitBarcodeHandler`. The check is now unconditional and
a missing authorizer fails closed with `catalog.authorization_unavailable`. Pinned by six tests in
`tests/EdgeRetails.UnitTests/ProductUnitHandlerAuthorizationTests.cs`, including one that removes the
authorizer and asserts **no write occurred**, not merely that an error surfaced.

## Full sweep result

Searched every `IApplicationPermissionAuthorizer?` injection and every `_authorizer is not null`
guard across `src/`.

| File | Line | Affected handler | Permission | In CAT-107 scope |
| :--- | ---: | :--- | :--- | :--- |
| `Features/Catalog/ProductUnitHandlers.cs` | 59 | `ConfigureProductUnitsHandler` | `InventoryManage` | **Yes — fixed** |
| `Features/Catalog/ProductUnitHandlers.cs` | 210 | `SetProductUnitBarcodeHandler` | `InventoryManage` | **Yes — fixed** |
| `Features/Finance/CashSessionHandlers.cs` | 42 | `OpenCashSessionHandler` | `SalesPosUse` | No |
| `Features/Finance/CashSessionHandlers.cs` | 444 | `CloseCashSessionHandler` | `SalesPosUse` | No |
| `Features/Inventory/StocktakeHandlers.cs` | 189 | stocktake create/handle path | `InventoryManage` | No |
| `Features/Sales/QuotationHandlers.cs` | 64 | create quotation | `SalesCreate` | No |
| `Features/Sales/QuotationHandlers.cs` | 253 | quotation mutation | `SalesCreate` | No |
| `Features/Sales/QuotationHandlers.cs` | 456 | quotation mutation | `SalesCreate` | No |
| `Features/Sales/QuotationHandlers.cs` | 532 | quotation mutation | `SalesCreate` | No |

**Seven call sites outside catalog remain fail-open.** They are recorded here as an open follow-up
item and are deliberately **not** modified in this run.

## Why they were not changed here

1. **Out of scope.** CAT-107 is a catalog correction. Editing Finance, Sales and Inventory posting
   handlers would pull three unrelated subsystems into a catalog review, and each needs its own
   concurrency, idempotency and posting evidence.
2. **The blast radius is real.** `OpenCashSessionHandler` and `CloseCashSessionHandler` sit on the
   money path. Changing a guard there without the corresponding cash-session regression evidence
   would be exactly the kind of unproven edit this run exists to stop.
3. **Not currently exploitable in production**, for the specific reason in the next section. That
   makes it a latent defect, not an active breach — but latent is not fixed.

## Why it is latent rather than active today

`IApplicationPermissionAuthorizer` is registered unconditionally:

```
src/EdgeRetails.Infrastructure/InfrastructureServiceCollectionExtensions.cs:280
services.AddScoped<IApplicationPermissionAuthorizer, ApplicationPermissionAuthorizer>();
```

So `_authorizer` is never null under the production host, and the `is not null` branch always
engages. Separately, `ApplicationPermissionAuthorizer.AuthorizeAsync` already rejects an empty actor:

```csharp
// Features/Identity/IdentityHandlers.cs:312
if (actorId == Guid.Empty || string.IsNullOrWhiteSpace(permissionKey))
    return Result.Failure("authorization.invalid_request", ...);
```

So the `&& command.ActorId != Guid.Empty` clause is **redundant but not harmful** against the real
implementation: it short-circuits a check that would have denied anyway. The two clauses are not
equally benign:

- `ActorId != Guid.Empty` — redundant with the authorizer's own check. Harmless today, but it
  duplicates a security decision at the call site, where a future authorizer change would not be
  reflected.
- `_authorizer is not null` — genuinely dangerous. If DI registration is ever made conditional, moved
  behind a feature flag, or a handler is constructed in a different host or a test seam, the
  mutation proceeds with no permission evaluation at all.

## Recommended follow-up (not done)

1. Make `IApplicationPermissionAuthorizer` a **required** constructor dependency in all seven
   handlers, removing the `?` and the default `= null`.
2. Delete the `&& command.ActorId != Guid.Empty` short-circuit; let the authorizer make that
   decision in one place.
3. Add a unit test per handler mirroring `ProductUnitHandlerAuthorizationTests`: construct without
   the authorizer, assert denial **and** assert no repository write.
4. Consider a compile-time guard, e.g. a Roslyn analyzer or an architecture test asserting that no
   handler takes a nullable `IApplicationPermissionAuthorizer`.

## Status

`OPEN` — follow-up item, outside CAT-107. CAT-107's own two sites are closed and proven. CAT-107
acceptance does not depend on the other seven, but this document must travel with the run so the
remaining exposure is not lost when the catalog work is signed off.
