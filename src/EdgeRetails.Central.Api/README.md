# EdgeRetails.Central.Api — cloud server (control plane)

Product **real server**: license/commercial authority, admin portal, activation, protected signing.

- Separate deploy from shop MSI and from `EdgeRetails.Server` (shop local host on `127.0.0.1:7150`).
- PostgreSQL `central` schema; `DatabaseTargetGuard` rejects shop port `7150` and operational shop DB names.
- Private signing: `EdgeRetails.ProtectedSigner` + `CENTRAL_SIGNER_KEY_STORE_ROOT`.

Start: [CENTRAL_ONE_START_HERE.md](../../CENTRAL_ONE_START_HERE.md), [docs/central/SERVER_DEPLOYMENT_RUNBOOK.md](../../docs/central/SERVER_DEPLOYMENT_RUNBOOK.md).

Boundary: [docs/SHOP_AND_CENTRAL_RUNTIME.md](../../docs/SHOP_AND_CENTRAL_RUNTIME.md).
