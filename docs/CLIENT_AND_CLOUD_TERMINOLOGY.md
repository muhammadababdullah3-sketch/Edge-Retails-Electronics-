# Client vs cloud — words we use

Use this in support, UI, and diagrams. **Do not call the shop PC install “the server.”**

| Say | Meaning | Repo (engineers only) |
|-----|---------|------------------------|
| **Cloud server** | Vendor-hosted license, admin, signing | `EdgeRetails.Central.Api` |
| **Client** / **shop client** | Everything on the retailer’s PC | `EdgeRetails.Desktop` + client shop service + PostgreSQL |
| **Client shop service** | Background API on the shop PC (loopback) | `EdgeRetails.Server` → Windows service `EdgeRetailsServer` |
| **Client app** | POS UI | `EdgeRetails.Desktop` |

Customer-facing text: **“Edge Retails shop service”** or **“shop service”** — never **“your server”** for the client tier.

## Keys on the client (what must not leak)

| Material | Belongs on client? | Notes |
|----------|-------------------|--------|
| Vendor **private** signing key | **Never** | Cloud `ProtectedSigner` only; shop rejects private key in `EDGE_RETAILS_LICENSE_PUBLIC_KEY` |
| Signed `license.erlic` | **Yes** (after issue) | Issued from cloud; shop verifies offline |
| Embedded license **public** key in shop build | **Yes** (verify only) | Not a secret; not “giving client the server key” |
| Terminal pairing secret | **Yes** (client PC only) | DPAPI file `%ProgramData%\\EdgeRetails\\installation\\terminal-credential.dpapi` (provisioned by `Initialize-EdgeRetailsClientInstallation.ps1`). Legacy env `EDGE_RETAILS_TERMINAL_SECRET` for rehearsals only — **not** license signing |
| Central **admin** password / MFA | **Never** on shop PC | Cloud portal only |

Do not tell retailers to configure `EDGE_RETAILS_LICENSE_PUBLIC_KEY` or `license.pub` unless vendor IT is repairing a broken install.

Runtime detail: [SHOP_AND_CENTRAL_RUNTIME.md](SHOP_AND_CENTRAL_RUNTIME.md).
