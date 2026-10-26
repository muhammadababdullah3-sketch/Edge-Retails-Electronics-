# EdgeRetails.Server — client shop service (not the cloud server)

**Product name:** Edge Retails **client shop service**.  
**Cloud server:** `EdgeRetails.Central.Api` (vendor-hosted).

This executable is part of the **client** install on the retailer PC:

- Loopback only: `http://127.0.0.1:7150`
- Windows service: `EdgeRetailsServer` (display name: Edge Retails Client Shop Service)
- Talks to local PostgreSQL; called by the client app (`EdgeRetails.Desktop`)

See [docs/CLIENT_AND_CLOUD_TERMINOLOGY.md](../../docs/CLIENT_AND_CLOUD_TERMINOLOGY.md).
