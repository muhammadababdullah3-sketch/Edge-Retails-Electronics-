# Shop POS vs Central — runtime boundary (code truth)

**Purpose:** One vocabulary for product, support, and diagrams. Short glossary: [CLIENT_AND_CLOUD_TERMINOLOGY.md](CLIENT_AND_CLOUD_TERMINOLOGY.md).

The repo project `EdgeRetails.Server` is **client shop service** — **not** the cloud server.

## Product terms vs repository projects

| Product term | Repository project | Deploy where | Listens / reached by |
|--------------|-------------------|--------------|----------------------|
| **Cloud server** (license, commercial, admin) | `EdgeRetails.Central.Api` (+ `EdgeRetails.Central.*`, `EdgeRetails.ProtectedSigner`) | Vendor cloud / hosted | Internet; admin portal + `/api/activation/v1/*`, `/api/licensing/v1/*`, etc. |
| **Client shop service** (loopback business API on the shop PC) | `EdgeRetails.Server` | Retailer PC (service `EdgeRetailsServer`) | **`http://127.0.0.1:7150`** only; **client app** via `BackendRuntime` / `DesktopApiClient` |
| **Client app** (POS UI) | `EdgeRetails.Desktop` | Retailer PC | Loopback to client shop service only |
| **Client worker** | `EdgeRetails.Worker` | Retailer PC | Same shop DB; heartbeat via `GET /api/system/lifecycle` |

**Invariant:** Retail transaction authority lives in **client PostgreSQL** behind the **client shop service**. The **cloud server** has **no** remote SQL over shop ledger data (`docs/central/ARCHITECTURE.md` §1.1).

## License and keys (forensic)

| Capability | Where (code) |
|------------|----------------|
| **Issue / sign** licenses & entitlements | Central + `ProtectedSigner` (`CENTRAL_SIGNER_KEY_STORE_ROOT`, `IProtectedSigningEngine`) |
| **Admin identity** (portal, step-up, commercial ops) | `EdgeRetails.Central.Api` (`IAdminAuthService`, `CentralRequestAuthorizationFilter`) |
| **Verify** installed license on shop | Shop: `SignedLicenseValidator` + **public key only** (`ProductionLicensePublicKeyProvider`); persist `%ProgramData%\EdgeRetails\license.erlic` (`FileLicenseStore`, `SetupController`) |
| **Shop staff login** | Shop: owner/staff **PIN** (`AuthenticateUserHandler`), not Central admin password |

Clients must **never** hold vendor **private** signing material. See `docs/operations/Production_Licensing_Standard_Operating_Procedure.md` §Strict Key Material Separation.

## What is wired today vs target

| Flow | Status in shop code (monorepo) |
|------|--------------------------------|
| Client app → client shop service (`7150`) | **Implemented** |
| Client shop service → shop PostgreSQL | **Implemented** |
| Client → cloud server HTTP (activation token redeem / bootstrap) | **Partial** — Desktop `FirstSetupViewModel` + `ShopCentralActivationService` when `EDGE_RETAILS_CENTRAL_BASE_URL` is set; renew/reconcile not fully wired |
| Deliver signed `.erlic` to shop | **File-based** at first setup (`POST /api/setup/bootstrap` + `SignedLicenseContent`); issuance authority remains Central-side when that stack is deployed |

Target product: **authority and admin credentials on the cloud server**; shop tier runs offline-capable POS with **verified** entitlements. The gap row above is engineering backlog, not a boundary change.

## Install artifacts

| Artifact | Contains |
|----------|----------|
| Shop MSI (`installer/EdgeRetails.Setup`) | Published binaries under Program Files only — no DB, no license, no service wiring (`Package.wxs`) |
| Central host | Separate deploy per `docs/central/SERVER_DEPLOYMENT_RUNBOOK.md` (Central **cloud** server — not `EdgeRetails.Server`) |

## Diagram (correct labels)

```text
  [ CLOUD — product "server" ]
  EdgeRetails.Central.Api  --->  PostgreSQL (central schema)
  admin login, activation, license issue/sign

            |
            |  signed .erlic / future online activation (target)
            v

  [ CLIENT — retailer PC ]
  Client app (Desktop) ----loopback----> Client shop service (EdgeRetails.Server :7150)
                                              |
                                              v
                                        shop PostgreSQL
```

## Related docs

- [WORKSPACE.md](WORKSPACE.md) — monorepo layout
- [central/ARCHITECTURE.md](central/ARCHITECTURE.md) — Central waves and invariants
- [CENTRAL_ONE_START_HERE.md](../CENTRAL_ONE_START_HERE.md) — Central build entry
