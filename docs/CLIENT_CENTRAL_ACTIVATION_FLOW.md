# Client ↔ Central activation (admin approval required)

**Cloud server:** `EdgeRetails.Central.Api`  
**Client:** `EdgeRetails.Desktop` + installation identity on the shop PC  
**Config:** `EDGE_RETAILS_CENTRAL_BASE_URL` (you will provide; e.g. `https://central.edgeretails.example`)

## Product rule (your requirement)

License **must not** activate until **admin approves** in the cloud portal. The client only **requests** and **polls**; signing keys stay on Central.

## End-to-end flow

```text
1. Client install
   → Initialize-EdgeRetailsClientInstallation.ps1 (terminal pairing DPAPI)
   → Register client shop service + worker

2. First setup — Online activation
   → User enters activation token (from vendor after purchase)
   → Client creates/stores installation RSA key (machine DPAPI)
   → POST /api/licensing/v1/bootstrap/challenge
   → POST /api/licensing/v1/primary/register
   → POST /api/licensing/v1/activation/redeem  → redemption PENDING

3. Cloud admin (portal)
   → Sees pending activation queue
   → Clicks APPROVE (activation.review)
   → Issues certificate (activation cert issue) when policy allows

4. Client polls (same PC, installation proof headers)
   → GET /api/licensing/v1/activation/{redemptionId}/status  → SUCCEEDED when approved
   → GET /api/licensing/v1/installations/{installationId}/delivery
        PENDING_ADMIN_APPROVAL | APPROVED_AWAITING_CERTIFICATE | READY + signedLicenseJson

5. Client local setup
   → POST /api/setup/bootstrap with signedLicenseJson (same as .erlic body)
   → Shop fields + owner PIN → shop PostgreSQL

6. Daily operation
   → Offline POS; optional reconcile later when online
```

## Offline fallback

User may still import a signed `.erlic` file (vendor-delivered) when the shop has no internet at setup time.

## What is NOT on the client

| Never on client | Stays on cloud |
|-----------------|----------------|
| Vendor private signing key | `ProtectedSigner` |
| Central admin password / MFA | Admin portal |
| Raw activation token in logs | Central DB (hash only) |

## Code map

| Piece | Location |
|-------|----------|
| HTTP + bootstrap crypto | `ShopCentralActivationClient` (Infrastructure) |
| Central delivery endpoint | `LicensingController` `GET installations/{id}/delivery` |
| Challenge exposes shop/license ids | `BootstrapChallengeDto.shopId` / `licenseId` |
| First setup UI | `FirstSetupViewModel` online path |
| Base URL | `EDGE_RETAILS_CENTRAL_BASE_URL` |

## Admin checklist (operations)

1. Commercial order paid → issue activation token to customer (secure channel).  
2. When client redemption appears: review hardware/shop binding → **Approve**.  
3. Ensure license **policy** configured → **Issue certificate**.  
4. Client auto-downloads on next poll; completes shop setup.
