# CAT-107 Route Authorization — What Was Proven, and the Pipeline Gap Found

**Run:** `ER-RUN-CATALOG-CORRECTION-20261021-107`  
**Date:** 2026-10-22  
**Subject:** CAT-107 open item (2), "route matrix incomplete"

## What changed

Previously only `POST /api/catalog/upgrade/fixed-units` had a route-level authorization test.
The other fourteen mutating routes on `CatalogController` had none, so a regression that dropped
a permission check anywhere else would have gone undetected.

All fourteen now carry:

1. a controller-level `RequirePermission(PermissionKeys.InventoryManage)` guard placed before any
   handler or repository call, and
2. a `CatalogMutationRouteAuthorizationMatrixTests` entry proving 401 / 403 / not-denied behavior.

The matrix is **discovered from controller metadata**, not hand-listed in isolation. A new
`HttpPost`/`HttpPut`/`HttpPatch`/`HttpDelete` action that lacks a matrix entry fails
`MatrixCoversEveryMutatingControllerAction`, and a matrix entry whose route no longer exists fails
the same test from the other direction. Route coverage therefore cannot silently rot.

## The pipeline gap this exposed

The first rehearsal run failed 6 of 56 with:

```
Expected: Forbidden
Actual:   BadRequest
```

The cause is architectural, not cosmetic:

- Authentication is enforced **outermost**, in middleware. It runs before model binding. All fifteen
  routes returned `401 auth.session_missing` anonymously, as required.
- Authorization (`RequirePermission`) is called **inside the action body**, after model binding.

So for any route whose body has a non-nullable member, an authenticated caller *without*
`InventoryManage` who sends a malformed body receives `400 BadRequest` from MVC's automatic
model-state filter. The action body never runs, so the permission check never runs, and the caller
learns `400` rather than `403`.

### Security impact: none demonstrated

Nothing is mutated on that path. No handler, no repository, no database connection. The request dies
at model binding. Confidentiality and integrity both hold.

### Why it is still recorded as a gap

1. **The 403 path was not actually being tested.** The original matrix sent bare `{}` bodies. On the
   six routes with required members that answer `400`, so the assertion never reached the guard. The
   test was measuring model binding, not authorization. That is precisely the kind of
   "green but proving nothing" result this run exists to eliminate.
2. **Response codes leak route shape.** An unprivileged caller can distinguish "this route has a
   required body member" from "it does not" by the status code alone, without holding the permission.
   That is a minor information disclosure.
3. **Ordering is fragile.** The permission decision is downstream of parsing. Any future
   expensive or side-effecting model binder or input formatter would execute before authorization.

## How each point is addressed now

| Concern | Resolution |
| :--- | :--- |
| 403 path untested | Matrix bodies are now complete and bind cleanly, so `RequirePermission` is actually reached and `403 authorization.denied` is asserted per route. |
| Malformed-body path uncovered | New `MutatingRoute_UnprivilegedCallerWithMalformedBody_NeverSucceeds` sends `{}` from an unprivileged caller on all fifteen routes and asserts **not 2xx**. It does not pin `400` specifically, so it keeps holding if authorization is later hoisted ahead of binding and the code becomes `403`. |
| Guard can be bypassed by dropping it | `MatrixCoversEveryMutatingControllerAction` reflects over the controller and fails on any uncovered mutating action. |
| Handler-level defense in depth | `ProductUnitHandlers` (`ConfigureProductUnitsHandler`, `SetProductUnitBarcodeHandler`) previously had `if (_authorizer is not null && ActorId != Guid.Empty)`, which **skipped** the check when the authorizer was absent or the actor was empty. Both are now unconditional and fail closed with `catalog.authorization_unavailable`; six regression tests in `ProductUnitHandlerAuthorizationTests` pin that. |

## Deliberately NOT done

**Authorization was not moved ahead of model binding in this run.** That would mean introducing an
`IAuthorizationFilter` or a global action filter and re-plumbing all controllers — a cross-cutting
change well outside CAT-107's scope, and one that needs its own review and its own regression
evidence. The gap is documented here and remains **OPEN** as a follow-up item. It is not claimed as
resolved, and CAT-107 does not depend on it being resolved: the security property (no unprivileged
mutation is possible) holds on both paths, and both paths are now tested.

## Residual risk statement

An unprivileged authenticated caller can learn, per route, whether that route's body has a required
member (400 vs 403), and can trigger 400s. They cannot read data, cannot mutate data, and cannot
reach any handler. This is accepted for CAT-107 and flagged for the follow-up item above.
