# Edge Retails — Antigravity Master Architecture & Remaining Implementation Handoff

**Document Type:** Canonical implementation handoff  
**Purpose:** Transfer the latest agreed Edge Retails architecture into Antigravity and define the remaining frontend, backend, licensing, multi-seat, installer, security, and certification work.  
**Date:** 2026-09-24  
**Target Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Current verified repository baseline used during the latest forensic scan:** `main` at commit `8dd615b39aa1315393d57c9b94c5693d0ee12470`

---

# 1. Purpose of This Document

This document consolidates the architecture decisions made after the latest forensic reviews of:

- the existing Edge Retails POS frontend,
- the existing Edge Retails backend,
- the shared-shop multi-seat model,
- Primary vs Secondary workstation responsibilities,
- barcode / scanner workflows,
- runtime/server/database authority,
- commercial licensing,
- vendor licensing control plane,
- Admin Portal,
- installation and deployment,
- security boundaries,
- cash-session behavior,
- update / restore behavior,
- and the remaining implementation gaps.

This is **not** a historical sprint report.

It is the implementation handoff that Antigravity should use to continue the project.

Historical sprint reports, old implementation notes, obsolete architecture drafts, and superseded assumptions must not be treated as authority when they conflict with this document and the current canonical architecture documents.

---

# 2. Existing Canonical Documents to Preserve

Before implementation, Antigravity must inspect the current live versions of the following if they exist:

1. `docs\Architecture_Authority_Manifest.json`
2. `docs\Edge_Retails_Final_Architecture_Report_v1.md`
3. `docs\Frontend_Backend_Forensic_Verification_2026-09-22.md`
4. `docs\Sprint9_Master_Implementation_Roadmap.md`
5. Current Admin Portal architecture / wireframe specification
6. Current licensing / activation / machine-binding architecture report
7. `docs\operations\Backup_Restore_Runbook.md`
8. `docs\operations\Upgrade_Guide.md`

This document should be added to that authoritative set.

---

# 3. Non-Negotiable Current Architecture

## 3.1 One Shop = One Authoritative Business System

For a normal commercial shop:

```text
ONE Shop
    ↓
ONE Commercial License
    ↓
ONE Primary Installation
    ↓
ONE EdgeRetails.Server
    ↓
ONE EdgeRetails.Worker
    ↓
ONE PostgreSQL Database
    ↓
N Purchased Workstation Seats
```

Seats do **not** create separate copies of the shop.

There is no database-per-seat model.

There is no stock synchronization between seats.

There is no accounting synchronization between seats.

All workstations operate on the same authoritative shop database through the same Primary Shop Server.

---

# 4. Primary and Secondary Workstation Model

## 4.1 Primary Machine

The Primary machine is the commercial installation root.

It owns:

```text
EdgeRetails.Desktop
EdgeRetails.Server
EdgeRetails.Worker
PostgreSQL
Commercial License
Shop Server Identity
Seat Management
Backup / Restore
Database Maintenance
Server Networking
Primary Infrastructure Diagnostics
```

The Primary machine also consumes the first interactive workstation seat in the current commercial model.

Example:

```text
Commercial License
Seats Purchased = 2

Seat 1 = Primary Machine
Seat 2 = Secondary Workstation
```

## 4.2 Secondary Workstation

The Secondary machine is a full Edge Retails workstation attached to the Primary shop.

It owns:

```text
EdgeRetails.Desktop
Workstation bootstrap/runtime
Local printer support
Local barcode scanner support
Local cash drawer / peripheral support
Local UI preferences
Workstation updater
```

It does **not** own:

```text
PostgreSQL
EdgeRetails.Server
EdgeRetails.Worker
Commercial .erlic authority
Database migrations
Backup / Restore authority
Primary Server configuration
Seat installation authority
```

The Secondary workstation never receives the PostgreSQL connection string.

---

# 5. Full Features on Every Seat

A seat is **not** a user role.

Do not model:

```text
Seat 1 = Cashier
Seat 2 = Manager
```

Both Primary and Secondary can run the complete Edge Retails business application.

Examples:

```text
Dashboard
POS
Sales History
Thaka / Projects
Purchases
Product Management
Inventory
Expenses
Customers
Suppliers
Warranty
Reports
Settings
```

Actual access is determined by:

```text
User
→ Role
→ Permissions
```

A manager can use POS on Seat 2.

An owner can use POS on Seat 1.

A cashier can use any workstation where policy permits login.

Seat identity and user identity are separate.

---

# 6. Primary-Only Infrastructure Controls

Even if the Owner logs in on a Secondary workstation, some controls must remain unavailable because they belong to the Primary infrastructure authority.

## 6.1 Primary-Only Controls

```text
Seats & Connected Systems
Install New Workstation
Disconnect Workstation
Replace Workstation
Commercial License Import / Replace
Primary Machine Transfer / Recovery
Backup
Restore
Database Maintenance
Database Migration
Shop Server Network Configuration
Shop Server TLS / Identity Configuration
Server / Worker Service Management
Workstation Update Orchestration
Primary Infrastructure Diagnostics
```

## 6.2 Shared Business Controls

These remain available on any workstation where the logged-in user has permission:

```text
POS
Sales
Purchases
Inventory
Product Management
Customers
Suppliers
Warranty
Reports
Thaka
Expenses
Shop Profile
Users & Roles
Categories & Units
Receipt Template
```

## 6.3 Workstation-Local Controls

Each workstation owns its own device configuration:

```text
Receipt Printer
Paper Size / Driver Mapping
Barcode Scanner
Cash Drawer
Customer Display
Local UI Preferences
Local Device Diagnostics
```

---

# 7. Authorization Formula

Authorization must no longer depend only on user permission.

The effective decision is:

```text
Effective Authorization
=
User Permission
AND
Machine / Node Capability
AND
Current Runtime State
```

Example:

```text
Owner + Primary + settings.seats.manage
→ Install Secondary Workstation = ALLOWED

Owner + Secondary + settings.seats.manage
→ Install Secondary Workstation = DENIED
```

Do not create artificial roles such as `OwnerPrimary` and `OwnerSecondary`.

Machine authority is a separate dimension.

---

# 8. Required Node Role Model

Introduce a first-class runtime role:

```text
EdgeRetailsNodeRole
-------------------
PrimaryAuthority
SecondaryWorkstation
```

This must be used by:

- frontend navigation,
- Settings composition,
- API authorization,
- backup/restore authorization,
- seat-management authorization,
- commercial-license operations,
- server/network operations,
- diagnostics,
- update orchestration.

It must not be a frontend-only flag.

---

# 9. Final Runtime Topology

```text
                    COMMERCIAL SHOP

              Vendor Commercial License
                    Seats = N
                         │
                         ▼
              PRIMARY AUTHORITY MACHINE
        ┌────────────────────────────────┐
        │ EdgeRetails.Desktop            │
        │ EdgeRetails.Server             │
        │ EdgeRetails.Worker             │
        │ PostgreSQL                     │
        │ Commercial License             │
        │ Shop Server Identity           │
        │ Seat Management                │
        └───────────────┬────────────────┘
                        │
                    HTTPS / LAN
                        │
          ┌─────────────┼─────────────┐
          ▼             ▼             ▼
    Secondary 1    Secondary 2    Secondary N
       Desktop        Desktop         Desktop
```

All authoritative business reads and writes flow through:

```text
Desktop
→ EdgeRetails.Server
→ Application
→ Domain
→ Infrastructure
→ PostgreSQL
```

---

# 10. Production Desktop Must Stop Owning Backend Authority

The current Desktop runtime still creates backend services and database scopes in-process.

Current examples include:

```text
BackendRuntime
BackendTransactionService
BackendPurchasingInventoryService
BackendProductManagementService
BackendThakaService
BackendBusinessOperationsService
BackendWorkflowReadService
BackendSettingsService
```

This must be removed from the **production business authority path**.

Final rule:

```text
Primary Desktop
→ Shop Server API via localhost

Secondary Desktop
→ Shop Server API via LAN
```

Primary and Secondary should use the same business client contracts.

The main difference is only the server endpoint and node role.

---

# 11. Recommended Desktop Client Contracts

Instead of one giant application gateway, use domain-focused clients.

Example:

```text
IAuthenticationClient
IDashboardClient
ISalesClient
IPosClient
IPurchasingClient
ICatalogClient
IInventoryClient
IStocktakeClient
IExpenseClient
ICustomerClient
ISupplierClient
IThakaClient
IWarrantyClient
IReportsClient
ISettingsClient
IWorkstationClient
IDiagnosticsClient
```

All clients communicate with the Primary `EdgeRetails.Server`.

---

# 12. Current Server API Coverage Is Incomplete

Current Server controller coverage is not enough for full-feature Secondary use.

Existing coverage includes mainly:

```text
Sales
Purchasing
Finance
Warranty
Terminals
System
```

Remaining remote API coverage must include:

```text
Authentication
Dashboard
POS Catalog / Search
Product Management
Catalog
Inventory
Exact-Unit Inventory
Stocktake
Customers
Suppliers
Supplier Khata full lifecycle
Expenses
Thaka
Warranty full lifecycle
Reports
Users / Roles / Permissions
Shop Settings
Receipt Template
Workstation Device Metadata
Seats & Connected Systems
Diagnostics
Printing document retrieval
Update bootstrap
```

Full Secondary support cannot be certified until every canonical screen can operate through the server boundary.

---

# 13. Commercial Licensing Model

Vendor authority should treat a normal shop as:

```text
Customer
↓
Shop
↓
Commercial License
↓
Purchased Seat Count
↓
Primary Commercial Installation
```

The Vendor Control Plane should not require support approval every time a normal Secondary workstation is replaced.

Secondary seats are locally managed allocations of the purchased seat entitlement.

---

# 14. Primary Commercial Installation

The Primary remains strongly bound because it owns:

```text
Commercial License
Shop Server
Database
Shop Server Identity
Primary Installation Identity
```

Primary replacement is a controlled vendor-side recovery / transfer operation.

Secondary replacement is not.

This distinction must never be merged.

---

# 15. Secondary Seat Entitlement

Example:

```text
CommercialLicense = LIC-001
PurchasedSeats = 2
```

Primary creates local workstation slots:

```text
Seat 1 = PRIMARY
Seat 2 = AVAILABLE
```

When Seat 2 is assigned:

```text
Seat 2
→ TerminalRegistration
→ Secondary Workstation
```

If that PC is replaced:

```text
Old terminal → DETACHED
Seat 2 → new terminal
```

No vendor approval is required.

No replacement counter.

No cooldown.

No transfer fee.

No artificial machine-change limitation.

---

# 16. Local Workstation Seat Model

Recommended entity:

```text
WorkstationSeat
---------------
SeatSlotId
SeatNumber
SeatType
State
CurrentTerminalId nullable
AssignedAtUtc nullable
UpdatedAtUtc
Version
```

Recommended states:

```text
AVAILABLE
PAIRING
ASSIGNED
```

The commercial entitlement limits how many local workstation seats may be assigned.

---

# 17. Terminal Registration Model

Recommended evolution of current `system.terminals`:

```text
TerminalRegistration
--------------------
TerminalId
SeatSlotId
WorkstationInstallationId
DisplayName
MachineName
Status
CredentialIdentity
ProtocolVersion
LastKnownIpAddress
RegisteredAtUtc
LastSeenAtUtc
DetachedAtUtc nullable
SecurityEpoch
Version
```

Recommended statuses:

```text
PAIRING
ACTIVE
SUSPENDED
DETACHED
```

`DETACHED` is important for ordinary workstation replacement.

A historical Terminal record should not be deleted.

---

# 18. Secondary Workstation Installation Workflow

## 18.1 Primary Initiates

Primary owner opens:

```text
Settings
→ Seats & Connected Systems
→ Install Workstation
```

Server verifies:

```text
Commercial License active
Purchased seat available
Seat slot available
Maintenance state normal
Primary node confirmed
Authorized user session confirmed
```

Then creates `WorkstationPairingSession`.

---

# 19. Workstation Pairing Session

Recommended model:

```text
WorkstationPairingSession
-------------------------
PairingSessionId
SeatSlotId
RequestedByUserId
CreatedAtUtc
ExpiresAtUtc
SecretHash
State
ExpectedShopServerId
BootstrapProtocolVersion
ClientOperationId
PayloadHash
Version
```

States:

```text
CREATED
CLAIMED
WAITING_PRIMARY_APPROVAL
APPROVED
COMPLETED

EXPIRED
CANCELLED
REJECTED
```

The pairing secret must be cryptographically random.

A short six-digit code may be used only as a human confirmation code, not as the real secret.

---

# 20. Workstation Installer Packaging

Do not dynamically modify or generate a new executable per seat if that would invalidate the vendor code signature.

Use:

```text
EdgeRetailsWorkstationSetup.exe
```

as a vendor-signed generic installer.

Primary generates a small pairing descriptor such as:

```text
Seat2.erpair
```

Containing only safe bootstrap information:

```text
PairingSessionId
ShopServerId
ShopName
Server identity fingerprint
Server discovery hints
Port
Bootstrap protocol version
Expiry
High-entropy pairing token
```

Never include:

```text
Database password
Database connection string
Owner PIN
Vendor signing private key
Commercial license secret
```

---

# 21. Secondary Installer Role

The Secondary installer must install only:

```text
EdgeRetails.Desktop
Workstation bootstrap runtime
Local printing/scanner/peripheral support
Workstation updater
```

It must not install:

```text
PostgreSQL
EdgeRetails.Server
EdgeRetails.Worker
Database migration tools as active authority
```

---

# 22. Secondary First-Run Wizard

The current Primary-style First Setup flow must not appear.

Secondary wizard should be:

```text
Workstation Setup
↓
Find / Verify Primary
↓
Claim Pairing
↓
Wait for Primary Approval
↓
Configure Workstation
↓
Login
```

It must not show:

```text
License import
Shop creation
Owner creation
Database setup
PostgreSQL setup
```

---

# 23. Shop Server Identity

Primary must have a persistent cryptographic identity.

Recommended:

```text
ShopServerIdentity
------------------
ShopServerId
PublicKey
ProtectedPrivateKey
CreatedAtUtc
```

Secondary trusts:

```text
ShopServerId
+
Server public identity
```

Do not use IP address, MAC address, or hostname as authority.

Those are discovery metadata only.

---

# 24. LAN Discovery

Implement a convenience discovery layer:

```text
IShopServerDiscovery
```

Possible implementation:

```text
UDP discovery / local discovery protocol
```

Primary advertises only minimal safe metadata:

```text
ShopServerId
HostName
Port
BootstrapProtocolVersion
```

Discovery does not equal trust.

Trust is established through the pairing data and server cryptographic identity.

---

# 25. Manual Discovery Fallback

Discovery may fail because of:

```text
guest Wi-Fi isolation
broadcast blocking
VLANs
router policies
Windows Public network profile
```

Installer must allow manual:

```text
hostname
or
IP address
```

entry.

Server identity verification still remains mandatory.

---

# 26. HTTPS / Server Network Boundary

Primary Server must use an explicit production network contract.

Recommended:

```text
HTTPS only
Default TCP port: 7150
Private LAN / selected interfaces
```

Firewall scope:

```text
Private profile
Local subnet or configured LAN
```

Do not expose the shop business API broadly on public interfaces by default.

Current firewall/service scripts must be aligned to this real server binding.

---

# 27. Terminal Credential

During pairing, Secondary creates a secure workstation identity.

Recommended:

```text
WorkstationInstallationId
+
Terminal key pair
```

or an equally strong machine-protected credential.

Private credential:

```text
Windows machine protected
Restricted filesystem ACL
Never logged
Never included in normal configuration JSON
```

Simple folder copying must not clone an active terminal.

---

# 28. Primary Approval Step

After Secondary claims the pairing session, Primary should show:

```text
New Workstation

Seat: 2
Machine: OFFICE-PC
IP: 192.168.1.x
Version: x.y.z

Confirmation Code:
482 917

[Attach Workstation]
[Reject]
```

Secondary displays the same confirmation code.

Primary approval must be required before the seat becomes active.

---

# 29. Atomic Seat Assignment

On Primary approval:

```text
Lock WorkstationSeat
Lock PairingSession
Revalidate commercial seat entitlement
Revalidate pairing state
```

Then atomically:

```text
Seat → ASSIGNED
Terminal → ACTIVE
PairingSession → COMPLETED
```

Two machines must never simultaneously acquire one seat.

---

# 30. No Automatic Seat Release

Important invariant:

```text
OFFLINE != DETACHED
```

If a workstation:

```text
sleeps
hibernates
loses Wi-Fi
is powered off
```

its seat remains assigned.

A seat becomes available only when the Primary explicitly:

```text
Disconnects
or
Replaces
```

the workstation.

---

# 31. Secondary Replacement Workflow

Primary only:

```text
Settings
→ Seats & Connected Systems
→ Seat N
→ Replace Workstation
```

New machine pairs and reaches:

```text
WAITING_FOR_CUTOVER
```

Then Primary performs one atomic cutover:

```text
Old Terminal → DETACHED
New Terminal → ACTIVE
Seat → New Terminal
```

There must never be two active machines on one seat.

---

# 32. Dead Secondary Machine

If the old PC is dead or missing:

```text
Primary
→ Seat
→ Replace
```

The old PC is not required.

No vendor support is required.

No hardware-transfer workflow is required.

No replacement limit applies.

---

# 33. Secondary Uninstall / Reinstall

Uninstalling Secondary must not silently release the seat.

Primary remains authority.

Fresh reinstall creates a new workstation identity and is attached through the normal Replace / Pair flow.

Historical terminal records remain preserved.

---

# 34. Runtime Security Requires Two Identities

Every Secondary business request must prove:

```text
1. Workstation / Terminal identity
2. Human UserSession identity
```

Terminal identity alone is not enough.

User identity alone is not enough.

---

# 35. Current Actor Spoofing Gap Must Be Removed

Network clients must not be able to select authoritative:

```text
ActorUserId
CashierUserId
```

in request payloads.

Correct flow:

```text
HTTP request
↓
Terminal authentication
↓
UserSession authentication
↓
RequestExecutionContext
```

Trusted context provides:

```text
CurrentTerminalId
CurrentUserId
CurrentSessionId
CorrelationId
```

Internal application commands receive actor identity from this context.

Client payload actor IDs are not trusted.

---

# 36. Remote Authentication API

Add remote server endpoints such as:

```text
GET  /api/auth/accounts
POST /api/auth/login
POST /api/auth/logout
GET  /api/auth/session
```

Secondary login must not use in-process Desktop database services.

---

# 37. UserSession Must Be Terminal-Bound

Extend:

```text
UserSession
-----------
SessionId
UserId
TerminalId
ClientSessionId
StartedAtUtc
EndedAtUtc
IsRevoked
```

Server must know:

```text
which user
on which workstation
during which session
```

for every important operation.

---

# 38. Request Execution Context

Introduce a server-side request context:

```text
RequestExecutionContext
-----------------------
TerminalId
UserId
SessionId
NodeRole
CorrelationId
ClientOperationId
```

This should be available to application authorization and audit.

---

# 39. Production Authorization Must Be Real

The current default production authorization implementation is effectively a no-op.

That is not acceptable for:

```text
Backup
Restore
License replacement
Primary-only infrastructure commands
```

Replace it with real session-aware authorization.

For example:

```text
RequireUserPermission(permission)
RequireNodeCapability(capability)
RequireSessionActive()
RequireTerminalActive()
```

---

# 40. Primary-Only API Enforcement

Examples:

```text
POST /api/workstations/pairing
POST /api/workstations/{seat}/replace
POST /api/workstations/{seat}/disconnect
POST /api/backup/create
POST /api/restore/prepare
POST /api/restore/cutover
POST /api/license/replace
POST /api/server/network
```

must require:

```text
Authorized user
AND
Current node = PrimaryAuthority
```

Secondary raw API calls must return:

```text
PRIMARY_NODE_REQUIRED
```

---

# 41. Operation Status Endpoint

The current operation-status route should not remain anonymously accessible in the multi-seat LAN runtime.

It can return business metadata such as document numbers and entity IDs.

Require terminal/user authorization as appropriate.

---

# 42. Settings Architecture Refactor

Current Settings mixes shared, local, and Primary-only concerns.

Refactor into three scopes.

## Shared Shop Settings

```text
Shop
Users & Access
Categories & Units
Receipt Template
```

## This Workstation

```text
Printer
Paper Size
Barcode Scanner
Cash Drawer
Customer Display
Appearance
Local Device Diagnostics
Connection Diagnostics
```

## Primary Infrastructure

```text
Seats & Connected Systems
Commercial License
Backup & Restore
Database
Server
Network
Updates
Primary Diagnostics
```

Secondary should not display Primary Infrastructure sections.

---

# 43. Read-Only License Summary on Secondary

Secondary may display:

```text
License Status: Active
Plan: Annual
Purchased Seats: 2
This Workstation: Seat 2
Primary: MAIN-PC
```

but it must not expose actions such as:

```text
Import License
Replace License
Transfer Primary
```

---

# 44. Barcode / Scanner Architecture

POS must support all three entry paths:

```text
Keyboard search
Mouse selection
Barcode / scanner input
```

Current exact resolution order should be preserved:

```text
1. TrackingCode exact
2. Serial exact
3. IMEI exact
4. Product Unit Barcode exact
5. SKU exact
6. Product Name / Brand / Model search
```

---

# 45. Scanner Input Capture Gap

Current scanner behavior is primarily search-box-focus dependent.

Introduce:

```text
IPosInputCaptureService
```

or equivalent.

It should support keyboard-wedge scanners without requiring the search textbox to always have focus.

Requirements:

```text
ordered scan queue
scanner terminator handling
focus-safe routing
no silent scan loss
clear success/error feedback
```

---

# 46. POS Cart Identity Correction

Current non-serialized cart merge behavior must include selling unit identity.

Correct key:

```text
NonSerializedLine
=
ProductId
+
ProductUnitId
```

Exact-unit line:

```text
ProductId
+
ProductUnitId
+
InventoryUnitId
```

Do not merge Piece and Box simply because ProductId matches.

---

# 47. Unit-Factor Stock Validation

Stock availability shown in POS must respect:

```text
FactorToBaseUnit
```

Example:

```text
12 base pieces
Box factor = 6
Available boxes = 2
```

UI must not allow 12 boxes simply because base stock is 12.

Server remains final authority.

---

# 48. Barcode Normalization

Use normalized barcode / SKU uniqueness.

Recommended:

```text
NormalizedBarcode
NormalizedSku
```

with authoritative unique indexes where appropriate.

---

# 49. Multi-Terminal Concurrency

Existing PostgreSQL concurrency design should be preserved.

Do not rewrite the existing locking/idempotency protections unless needed.

The shared-shop model specifically depends on correct concurrency for:

```text
serialized sale races
quantity stock oversell
purchase-return vs sale
stocktake vs sale
supplier payments
warranty exact-unit operations
unknown-outcome replay
```

---

# 50. Cash Session Architecture Must Change

Current cash session model is effectively shop-global.

That is incorrect for multiple POS workstations with separate physical drawers.

Add:

```text
CashSession.TerminalId
```

Invariant:

```text
maximum one OPEN CashSession per TerminalId
```

not one open session for the entire shop.

---

# 51. Cash Movement Must Be Terminal-Aware

Every physical-cash movement must resolve the current terminal's cash session.

Impacted flows include:

```text
Cash Sale
Sale Refund
Expense
Thaka Payment
Purchase Cash Payment
Purchase Return
Supplier Payment
Supplier Refund
Manual Cash In / Out
```

A management workstation with no open drawer session should not silently post into another terminal's drawer.

---

# 52. Reporting Remains Shop-Wide

Terminal-specific drawer custody does not create separate accounting.

Reports may show:

```text
Shop total
Drawer / terminal breakdown
Cashier breakdown
```

but all data remains in one database.

---

# 53. Printing Architecture

Authoritative document content belongs to the Server.

Physical printing belongs to the current workstation.

Flow:

```text
Secondary POS
→ Server commits sale
→ Server returns authoritative receipt document
→ Secondary local print engine
→ Secondary local printer
```

Do not assume the Primary Worker can print to a USB printer attached to a Secondary workstation.

---

# 54. Local Peripheral Scope

Workstation-local:

```text
Printer name
Paper driver mapping
Barcode scanner
Cash drawer
Customer display
```

Shop-shared:

```text
Receipt header/footer
document numbering
receipt business policy
shop identity
```

---

# 55. Update Architecture

Current exact protocol equality is too strict for staged multi-seat updates.

Introduce:

```text
BootstrapProtocolVersion
ApplicationProtocolVersion
MinimumSupportedApplicationProtocol
CurrentApplicationProtocol
```

Bootstrap endpoints remain available for:

```text
health
version
pairing
update-required status
update metadata
update download
diagnostics
```

even when business protocol is incompatible.

---

# 56. Primary-Controlled Workstation Updates

Primary obtains / verifies the vendor release.

Connected Systems can show:

```text
Seat 2
Office-PC
Current Version: 1.4
Required Version: 1.5

[Update]
```

Secondary downloads the vendor-signed workstation package from Primary over LAN.

Secondary does not require its own Internet access.

---

# 57. Database Migrations

Only Primary authority may apply database migrations.

Secondary updater must never run EF migrations against the shop DB.

---

# 58. Backup / Restore and Terminal Security Rollback

Restoring an old database could restore old terminal assignment records.

Example:

```text
Monday: Seat2 = PC-A
Tuesday: Seat2 = PC-B
Wednesday: Restore Monday backup
```

Without protection, old PC-A could appear active again.

Introduce a protected Primary-side:

```text
ShopSecurityEpoch
```

kept outside ordinary PostgreSQL rollback authority.

After restore:

```text
SecurityEpoch++
```

Existing Secondary credentials must be revalidated / reauthorized.

A database restore must never silently resurrect detached workstations.

---

# 59. Primary Identity Persistence

Normal:

```text
Application Upgrade
Repair Install
Server Service Re-registration
```

must preserve:

```text
ShopServerId
ShopServer identity key
Primary Installation identity
Commercial license state
```

These must live outside MSI-owned binary replacement.

---

# 60. Connectivity Behavior

## Vendor Internet Down

If Primary local commercial license remains valid:

```text
Primary works
Secondary works
```

No per-operation vendor call.

## Secondary ↔ Primary LAN Down

Secondary business writes stop.

## Primary Server / Database Down

All authoritative business writes stop.

Do not introduce offline shadow mutations on Secondary.

---

# 61. Secondary Local Cache Policy

Allowed:

```text
in-memory UI cache
local UI preferences
printer/scanner settings
safe non-authoritative cached metadata
```

Not allowed as authority:

```text
offline stock edits
offline sales queue
offline purchase queue
offline inventory mutations
```

---

# 62. Drafts

POS drafts remain server-side.

A draft created from Secondary is stored in the shared DB.

This avoids local draft divergence.

---

# 63. Diagnostics Split

## Primary Diagnostics

```text
PostgreSQL
Schema
Worker
Backup
License
Server TLS
Disk
Maintenance
Seat Health
Outbox
```

## Secondary Diagnostics

```text
Primary connectivity
Latency
Terminal authentication
User session
Protocol compatibility
Printer
Scanner
Cash drawer
Client version
```

Secondary must not pretend to own database health.

---

# 64. Implementation Order After Architecture Closure

Implementation must begin with the existing POS, not the Admin Portal.

Do not rewrite the current completed business system from zero.

Use a forensic gap-driven implementation sequence.

## Stage 1 — POS Runtime Boundary

1. Introduce `EdgeRetailsNodeRole`.
2. Make Primary Desktop use Shop Server API.
3. Remove production direct-DbContext/Desktop backend authority.
4. Build complete remote client contracts.
5. Expand Server APIs to all canonical screens.
6. Implement real server-side user sessions.
7. Bind UserSession to Terminal.
8. Remove network ActorId trust.
9. Replace no-op production authorization.

## Stage 2 — Primary / Secondary Topology

1. Add `ShopServerIdentity`.
2. Add `WorkstationSeat`.
3. Evolve `TerminalRegistration`.
4. Add `WorkstationPairingSession`.
5. Add Primary-only `Seats & Connected Systems`.
6. Build Secondary workstation installer.
7. Build pairing flow.
8. Build attach / detach / replace.
9. Add machine-role authorization.
10. Add LAN discovery + manual fallback.

## Stage 3 — Multi-Seat Business Corrections

1. Make CashSession terminal-aware.
2. Route cash movements by Terminal.
3. Add terminal provenance to important mutations/audit.
4. Complete shared multi-seat concurrency tests.
5. Ensure every screen reads/writes through same authoritative server.

## Stage 4 — POS Input / Scanner Hardening

1. Add focus-safe scanner capture.
2. Ordered scan queue.
3. ProductUnit-aware cart identity.
4. FactorToBaseUnit stock handling.
5. Barcode/SKU normalization.
6. Exact-unit duplicate/error feedback.
7. Multi-seat scan/sale concurrency tests.

## Stage 5 — Settings / Peripheral Scope

1. Split shared vs workstation-local vs Primary infrastructure Settings.
2. Add local printer/scanner/cash-drawer configuration.
3. Remove Primary-only controls from Secondary.
4. Add server-side Primary-node enforcement.

## Stage 6 — Updates / Restore / Security Hardening

1. Bootstrap protocol.
2. Rolling protocol compatibility.
3. Primary-controlled Secondary updates.
4. ShopSecurityEpoch.
5. Post-restore terminal reauthorization.
6. TLS/firewall hardening.
7. Credential protection.
8. Pairing/replacement idempotency.

## Stage 7 — Vendor Licensing Control Plane / Admin Portal

After the shop runtime / multi-seat foundation is operational, continue implementation of the Vendor Control Plane and Admin Portal according to the previously designed six-phase backend architecture.

---

# PART II - VENDOR SERVER-SIDE SYSTEM

The Vendor Server-Side System is a separate product from the shop POS runtime.

It contains two major surfaces:

```text
A. Admin Portal Frontend
B. Vendor Licensing Control Plane Backend
```

The Admin Portal is the human operational frontend.

The Vendor Licensing Control Plane is the backend authority.

The browser must never become commercial, licensing, payment, activation, signing, transfer, or audit authority.

---

# 65. Vendor Server-Side Topology

Canonical topology:

```text
ADMIN USERS
    ↓ HTTPS
Admin Portal Frontend
    ↓
Vendor Admin API
    ↓
Vendor Application Layer
    ↓
Vendor Domain / Policies
    ↓
Vendor PostgreSQL

                         ┌─────────────────────┐
                         │ Protected Signing   │
Certificate Request ───→ │ Service / Key Store│
                         └─────────────────────┘

Shop Primary Server
    ↓ HTTPS
Vendor Licensing API
    ↓
same Vendor Application / Domain authority
```

The system should preferably remain a modular monolith for V1 rather than being split into unnecessary microservices.

Recommended projects:

```text
EdgeRetails.Vendor.Domain
EdgeRetails.Vendor.Application
EdgeRetails.Vendor.Infrastructure
EdgeRetails.Vendor.Contracts
EdgeRetails.Vendor.Api
EdgeRetails.Vendor.AdminPortal
EdgeRetails.Vendor.Worker
```

A physically separate protected signing service may be introduced for production signing.

---

# 66. Server-Side Authority Boundaries

## Admin Portal Frontend Owns

```text
Rendering
Forms
Navigation
Filters
Tables
Case views
Confirmation UX
Local UI state
Accessibility
Operator workflow presentation
```

## Vendor Backend Owns

```text
Commercial pricing
Payment state
License state
Seat entitlement
Primary Installation authority
Activation validity
Hardware comparison
Transfer / rebind policy
Certificate issuance eligibility
RBAC
MFA/session authority
Dual approval
PII authorization
Audit
Idempotency
Concurrency
Notifications
```

## Protected Signing Service Owns

```text
Private signing key
Cryptographic signing
Signing-key lifecycle
```

The Admin Portal must never hold or receive the private signing key.

---

# 67. Vendor Backend Bounded Contexts

Recommended logical modules:

```text
VendorIdentity
CustomerManagement
Commerce
Payments
Licensing
PrimaryInstallations
Activation
Certificates
HardwareReview
LicenseOperations
SupportEntitlements
SecurityAudit
Notifications
```

Cross-module mutations must go through Application commands and domain policies.

Do not let controllers update EF entities directly.

---

# 68. Vendor Database Schemas

Recommended PostgreSQL schemas:

```text
vendor_identity
customers
commerce
payments
licensing
installations
activation
certificates
operations
support
security
audit
notifications
```

Important rule:

```text
Admin Portal
≠ database browser
```

There must be no generic "edit row" interface.

---

# 69. Core Vendor Domain Hierarchy

Canonical commercial hierarchy:

```text
Customer
↓
Shop
↓
CommercialLicense
↓
Purchased Seat Entitlement
↓
PrimaryInstallation
↓
HardwareFingerprint
↓
InstallationCertificate
```

Important distinctions:

```text
Customer != Shop
Shop != User
CommercialLicense != PrimaryInstallation
Seat Entitlement != Secondary Workstation
PrimaryInstallation != Certificate
Payment Approval != Activation Approval
```

Normal Secondary workstation attachment remains shop-local under the Primary POS architecture.

The Vendor Control Plane controls the purchased seat count, not ordinary replacement of Secondary PCs.

---

# 70. Vendor Core Aggregates

Expected aggregates include:

```text
Customer
Shop

LicensePlan
CommercialLicense

Order
PricingSnapshot

PaymentProof
PaymentReview
PaymentClarificationRequest

PrimaryInstallation
HardwareFingerprint

ActivationToken
ActivationRequest

CertificateIssuanceRequest
InstallationCertificate

HardwareReviewCase
MachineTransferRequest

SupportEntitlement
UpgradeEntitlement

SensitiveApprovalRequest

AdminUser
AdminRole
Permission
AdminSession
AdminMfaCredential

AuditEvent
AdminNotification
```

Do not merge separate lifecycle concepts only to reduce table count.

---

# 71. Commercial License Model

A `CommercialLicense` represents the purchased shop-level commercial authority.

Suggested data:

```text
CommercialLicense
-----------------
CommercialLicenseId
CustomerId
ShopId
PlanId

Status

ValidFromUtc
ValidUntilUtc nullable

PurchasedSeatCount

CreatedFromOrderId
AuthorityRevision

CreatedAtUtc
UpdatedAtUtc
Version
```

Suggested states:

```text
ACTIVE
PAUSED
SUSPENDED
EXPIRED
REVOKED
```

Exact status transitions must be explicit commands.

There must be no generic:

```text
UpdateLicenseStatus(newStatus)
```

endpoint.

---

# 72. Purchased Seat Entitlement

Vendor seat authority means:

```text
CommercialLicense.PurchasedSeatCount
```

Example:

```text
Shop License
Seats = 2
```

means the Primary POS system may operate:

```text
Seat 1 = Primary workstation
Seat 2 = one attached Secondary workstation
```

Normal Secondary machine replacement does not need Vendor transfer approval.

Add-seat flow remains commercial:

```text
Add Seat Order
↓
Backend Pricing
↓
Payment
↓
Payment Approval
↓
PurchasedSeatCount++
↓
Primary reconciliation
```

Seat reduction must not silently push the shop below its currently active workstation usage.

---

# 73. Server-Side Commerce Architecture

Orders must be explicit commercial cases.

Suggested `OrderType`:

```text
NEW_LICENSE
RENEWAL
ADD_SEAT
PLAN_CHANGE
SUPPORT_RENEWAL
UPGRADE
```

Suggested order data:

```text
OrderId
CustomerId
ShopId
OrderType
RequestedPlanId
RequestedSeatCount
PricingSnapshotId

Currency
Subtotal
Discount
Total

Status

CreatedAtUtc
UpdatedAtUtc

ClientOperationId
PayloadHash
Version
```

Frontend must never calculate authoritative prices.

---

# 74. Pricing Authority

Introduce:

```text
ICommercialPricingService
```

Backend generates immutable:

```text
PricingSnapshot
```

Snapshot should preserve:

```text
plan
seat count
billing period
base price
discount
tax if applicable
currency
total
pricing rule version
created time
```

The browser displays backend calculations.

The browser never decides the amount that creates a license.

---

# 75. Payment Evidence Architecture

Customer payment proof must be stored as immutable evidence.

Recommended:

```text
PaymentProof
------------
PaymentProofId
OrderId

EvidenceObjectId
EvidenceSha256
OriginalFileName
ContentType
SizeBytes

DeclaredAmount
TransactionReference nullable

SubmittedAtUtc
SubmittedBy

Status
Version
```

Evidence must be stored privately.

Never expose permanent public object-storage URLs.

---

# 76. Payment Upload Security

Payment evidence is untrusted content.

The backend should enforce:

```text
Maximum upload size
Allowed media/document types
Real content-type verification
Malware scanning / quarantine
SHA-256 hashing
Private object storage
Short-lived authorized download URLs
```

The final `PaymentProof` record should reference a finalized immutable object.

Avoid creating a database proof row before the file has safely completed upload and validation.

---

# 77. Payment Review Model

Payment evidence and payment decision are separate.

Recommended:

```text
PaymentReview
-------------
PaymentReviewId
OrderId
PaymentProofId
ReviewerAdminUserId

Decision
ReasonCode
InternalNote

ReviewedAtUtc

ClientOperationId
PayloadHash
Version
```

Possible decisions:

```text
APPROVED
REJECTED
CLARIFICATION_REQUIRED
```

Historical proof submissions must not be overwritten.

---

# 78. Payment Lifecycle

Recommended operational flow:

```text
DRAFT
↓
PAYMENT_SUBMITTED
↓
PAYMENT_REVIEW_PENDING
├── CLARIFICATION_REQUIRED
│       ↓
│  CUSTOMER_RESUBMITTED
│       ↓
│  PAYMENT_REVIEW_PENDING
│
├── PAYMENT_REJECTED
│
└── PAYMENT_APPROVED
```

Payment approval must be idempotent.

Exactly one final authoritative approval state must exist for an Order.

Concurrent reviewers must not produce duplicate commercial issuance.

---

# 79. Order to Commercial License Issuance

For a new-license Order:

```text
Payment Approved
↓
Create CommercialLicense
↓
Set PurchasedSeatCount
↓
Create Primary Activation Eligibility
↓
Audit
↓
Outbox events
```

Use a database uniqueness guarantee such as:

```text
CreatedFromOrderId UNIQUE
```

so replay cannot issue two licenses.

---

# 80. Renewal Architecture

Renewal flow:

```text
Renewal Order
↓
Backend Pricing
↓
Payment
↓
Payment Approval
↓
CommercialLicense validity extended
↓
AuthorityRevision++
↓
Certificate reconciliation / reissue required
```

Never edit an already signed certificate in place.

A material renewal produces a new certificate or an authoritative reconciliation result.

---

# 81. Commercial Extension

Administrative extension is distinct from normal renewal.

Require:

```text
LicenseId
OldExpiry
ExtensionDays
NewExpiry
Reason
InternalNote
ActorAdminUserId
ClientOperationId
```

Extension must be audited.

Large/sensitive extension policies may require a Sensitive Approval.

---

# 82. Primary Installation Model

Only the Primary commercial installation is vendor machine-bound in the current multi-seat design.

Suggested:

```text
PrimaryInstallation
-------------------
PrimaryInstallationId
CommercialLicenseId
ShopId

InstallationId
DeviceId

CurrentHardwareFingerprintId

Status

FirstRegisteredAtUtc
ActivatedAtUtc nullable
LastReconciledAtUtc nullable

AuthorityRevision
Version
```

Secondary workstations do not become independent Vendor PrimaryInstallations.

---

# 83. Primary Installation Identity

Primary machine identity should separate:

```text
InstallationId
DeviceId
HardwareFingerprint
Installation cryptographic identity
```

Recommended:

```text
InstallationId
+
HardwareFingerprintHash
+
InstallationPublicKeyFingerprint
```

The Primary machine generates an installation key pair.

Private installation key stays machine-protected.

Vendor stores the public identity.

This is separate from the Vendor certificate-signing key.

---

# 84. Hardware Fingerprint Architecture

Strong anchors may include:

```text
System UUID
Motherboard UUID / Serial
TPM identity where available
```

Secondary signals may include:

```text
Disk identity
CPU identity
Windows machine identity
```

Peripheral changes must not invalidate the Primary license.

Hardware comparison result:

```text
NO_MATERIAL_CHANGE
MINOR_CHANGE
MAJOR_CHANGE
IDENTITY_UNCERTAIN
```

Raw hardware serials should be minimized where possible.

Prefer normalized hashed signals plus presence/quality metadata.

---

# 85. Activation Token Architecture

Activation token is one-time bootstrap authority for the Primary installation.

Recommended states:

```text
ISSUED
REDEEMED
PENDING_APPROVAL
CONSUMED

EXPIRED
REJECTED
REVOKED
```

Token must bind to:

```text
CommercialLicenseId
PrimaryInstallationId
HardwareFingerprintHash
```

Store a cryptographic hash of the raw activation token, not the raw secret.

---

# 86. Activation Token TTL

Recommended policy:

```text
TTL applies to first redemption.
```

If first redemption occurs before expiry:

```text
ActivationRequest remains reviewable
```

even if human approval happens later.

If first redemption occurs after expiry:

```text
ACTIVATION_KEY_EXPIRED
```

---

# 87. Activation Request

Recommended:

```text
ActivationRequest
-----------------
ActivationRequestId
ActivationTokenId
CommercialLicenseId
PrimaryInstallationId
HardwareFingerprintId
AppVersion

ValidationSnapshotJson
Status

ReviewedByAdminUserId nullable
ReviewedAtUtc nullable
DecisionReason nullable

ClientOperationId
PayloadHash
Version
```

Activation approval must revalidate mutable authority before final approval.

A stale validation snapshot alone is not enough.

---

# 88. Hard Failures vs Reviewable Conditions

Admin approval must not become a universal bypass.

## Hard Deny

Examples:

```text
Activation token consumed
Wrong PrimaryInstallationId
Commercial license revoked
Payment not approved
Invalid token
Invalid Vendor signature
Cross-Shop mismatch
```

## Manual Review

Examples:

```text
Weak hardware identity
Major hardware change
Missing TPM
Conflicting secondary hardware signals
Identity uncertainty
```

Only policy-defined reviewable conditions can move to manual review.

---

# 89. Activation Approval to Certificate Issuance

Flow:

```text
ActivationRequest validated
↓
Admin approves
↓
ActivationToken consumed
↓
CertificateIssuanceRequest created
↓
Protected Signing Service signs
↓
InstallationCertificate persisted
↓
Primary receives certificate
```

Admin Portal never calls the private key directly.

---

# 90. Certificate Issuance Request

Recommended:

```text
CertificateIssuanceRequest
--------------------------
CertificateIssuanceRequestId
PrimaryInstallationId
CommercialLicenseId

AuthorityRevision
PayloadHash

Status
PENDING
SIGNING
ISSUED
FAILED
CANCELLED

AttemptCount
FailureCode nullable

CreatedAtUtc
UpdatedAtUtc
```

Before signing:

```text
current CommercialLicense.AuthorityRevision
must equal
request.AuthorityRevision
```

Otherwise:

```text
CERTIFICATE_ISSUANCE_STALE
```

This prevents signing stale authority after suspension/revocation/renewal.

---

# 91. Installation Certificate

Certificate is immutable.

Suggested signed payload:

```text
CertificateId
CertificateVersion

CommercialLicenseId
CustomerId
ShopId

PrimaryInstallationId
InstallationId

HardwareFingerprintHash
InstallationPublicKeyFingerprint

Plan
PurchasedSeatCount
EnabledModules

ValidFromUtc
ValidUntilUtc nullable

AuthorityRevision

SigningKeyId
```

Do not put unnecessary PII inside the signed certificate.

---

# 92. Certificate Lineage

Per Primary Installation:

```text
maximum one ACTIVE certificate
```

New certificate:

```text
Old ACTIVE → SUPERSEDED
New → ACTIVE
```

Certificate history remains immutable.

Use a sequence or supersession link.

---

# 93. Protected Signing Service

Production signing flow:

```text
Vendor Application
↓
CertificateIssuanceRequest
↓
Protected Signing Service
↓
Signature
```

Signing service validates:

```text
SigningRequestId
PayloadHash
Authorized caller
Signing key status
```

Signing should be idempotent for the same SigningRequestId and payload.

---

# 94. Signing Key Management

Recommended key states:

```text
PENDING
ACTIVE
VERIFY_ONLY
RETIRED
REVOKED
```

Production should use a protected operational signing key.

A root trust model may authorize operational signing keys.

Development and Production trust domains must remain separate.

Production builds must not accept arbitrary environment-based replacement of trusted Vendor keys.

---

# 95. Offline Primary License Runtime

After successful activation:

```text
Primary Shop Server
→ validates signed certificate locally
```

Vendor Internet is not required for every transaction.

Local effective state depends on:

```text
Certificate validity
Primary installation identity
Hardware identity
Trusted clock
Commercial capability claims
Last known reconciliation state
```

Remote revoke/suspend cannot instantly affect a fully offline machine.

It becomes effective at the next reconciliation or certificate expiry unless a future bounded offline lease policy is introduced.

---

# 96. Trusted Clock

Primary must protect against simple system-clock rollback.

Recommended protected state:

```text
LastTrustedUtc
LastSuccessfulRunUtc
LastVerifiedVendorUtc
LicenseValidUntilUtc
ClockRollbackDetected
```

Trusted clock state must not rely solely on editable PostgreSQL values.

Use machine-protected tamper-evident local storage.

---

# 97. Vendor Reconciliation API

Primary periodically or manually reconciles:

```text
PrimaryInstallationId
CurrentCertificateId
AuthorityRevision
HardwareFingerprintHash
AppVersion
LastKnownServerTime
```

Backend responds with:

```text
Commercial state
Current AuthorityRevision
Required action
Replacement certificate if needed
Hardware review requirement
Vendor trusted time
Signing-key trust updates if supported
```

Reconciliation must be authenticated using the Primary installation cryptographic identity.

---

# 98. Hardware Review Case

Recommended:

```text
HardwareReviewCase
------------------
HardwareReviewCaseId
PrimaryInstallationId

PreviousFingerprintId
CurrentFingerprintId

Classification
RiskLevel

Status

AssignedAdminUserId nullable

Decision
DecisionReason
ResolvedAtUtc nullable

Version
```

Suggested flow:

```text
OPEN
↓
UNDER_REVIEW
├── MORE_INFORMATION_REQUIRED
├── REBIND_APPROVAL_PENDING
├── TRANSFER_RECOMMENDED
├── REJECTED
└── RESOLVED
```

---

# 99. Primary Hardware Rebind

Rebind means:

```text
same legitimate Primary commercial installation relationship continues
after a major repair/change
```

Flow:

```text
Hardware Review
↓
Identity verified
↓
Rebind approved
↓
Old fingerprint preserved historically
↓
New fingerprint binding
↓
AuthorityRevision++
↓
New certificate
```

Rebind is not Secondary seat replacement.

---

# 100. Primary Machine Transfer

Transfer means moving Primary commercial authority to a different Primary machine.

Recommended lifecycle:

```text
REQUESTED
↓
IDENTITY_VERIFICATION
↓
OLD_INSTALLATION_REVIEW
↓
PENDING_APPROVAL
↓
APPROVED
↓
OLD_PRIMARY_RETIRED
↓
NEW_PRIMARY_ACTIVATION_ISSUED
↓
NEW_PRIMARY_ACTIVATION_APPROVED
↓
COMPLETED
```

Side states:

```text
MORE_INFO
REJECTED
CANCELLED
```

Do not copy the old signed certificate to the new Primary.

---

# 101. Lost Primary Hardware Recovery

Use the transfer domain with a recovery type:

```text
STANDARD_TRANSFER
LOST_HARDWARE_RECOVERY
```

Old Primary:

```text
LOST_HARDWARE / RETIRED
```

New Primary:

```text
new InstallationId
new hardware fingerprint
new activation
new certificate
```

This heavy recovery flow is only for the Primary commercial installation.

---

# 102. Vendor License Lifecycle Commands

Use explicit commands:

```text
PauseCommercialLicense
ResumeCommercialLicense
SuspendCommercialLicense
RevokeCommercialLicense
RenewCommercialLicense
ExtendCommercialLicense
IncreaseSeatEntitlement
ReduceSeatEntitlement
```

Never expose a generic status setter.

Each command has its own rules, authorization, reason requirements, audit, and idempotency.

---

# 103. Sensitive Approval

High-risk actions may require a separate `SensitiveApprovalRequest`.

Examples:

```text
Commercial License Revocation
High-risk Primary Transfer
Conflicting Hardware Rebind
Forced Activation Override
Security Override
Highest-risk Admin Privilege Escalation
```

Rules:

```text
Requester != Approver
Payload hash binds approval to exact action
Approval expires
State revalidated before execution
```

Self-approval is forbidden.

---

# 104. Admin Identity Architecture

Vendor Admin identity is separate from:

```text
Shop User identity
Primary Installation identity
Secondary Terminal identity
```

Suggested admin state:

```text
INVITED
ACTIVE
LOCKED
DISABLED
```

Historical Admin identity must remain available for audit attribution.

---

# 105. Admin Roles and Permissions

Use capability-based RBAC.

No single universal:

```text
IsAdmin
```

Suggested role families:

```text
Support Agent
Payment Reviewer
Licensing Operator
Senior Licensing Admin
Read-Only Auditor
System Administrator
```

System Administrator must not automatically become a Commercial Licensing Superuser.

---

# 106. Admin Permission Examples

Examples:

```text
customer.view
customer.view_sensitive

payment.view
payment.view_evidence
payment.approve
payment.reject

license.view
license.renew
license.pause
license.suspend
license.revoke

activation.view
activation.approve
activation.reject

hardware.review
hardware.approve_rebind

transfer.view
transfer.approve

audit.view
audit.export

admin_user.manage
role.manage
security.review
```

Every backend command checks permission independently.

---

# 107. Admin Authentication

Recommended flow:

```text
Admin login
↓
Password / configured identity provider
↓
MFA
↓
AdminSession
↓
Portal
```

Exact MFA provider can remain configurable.

MFA behavior is architectural.

Provider choice is implementation detail.

---

# 108. Step-Up Authentication

High-risk actions should support fresh step-up authentication.

Examples:

```text
Revoke license
High-risk Primary transfer
Conflicting rebind
Security override
Privilege escalation
```

Step-up state must be:

```text
short-lived
session-bound
action-scope aware
```

---

# 109. Admin Session Model

Suggested:

```text
AdminSession
------------
AdminSessionId
AdminUserId

CreatedAtUtc
LastActivityAtUtc
ExpiresAtUtc
AbsoluteExpiresAtUtc

MfaSatisfiedAtUtc

SecurityRevision

IpAddress
UserAgentHash

RevokedAtUtc nullable
RevocationReason nullable
```

Support:

```text
idle timeout
absolute lifetime
single-session revoke
logout all
active session list
forced revalidation after privilege/security change
```

---

# 110. Admin Security Revision

When:

```text
Role changes
Permission changes
Account disabled
Security reset
```

increment:

```text
AdminSecurityRevision
```

Session revision mismatch requires re-authentication/revalidation.

This prevents stale powerful sessions from retaining removed authority.

---

# 111. PII Protection

Sensitive data includes:

```text
CNIC
Phone
WhatsApp
Email
Address
Payment evidence
Hardware identity signals
```

Default portal rendering should mask sensitive values.

Reveal requires explicit permission.

Sensitive reveal should itself be auditable.

Do not write raw PII into general application logs.

---

# 112. Admin Audit Model

Audit must be append-only and immutable from the Portal.

Suggested:

```text
AuditEvent
----------
AuditEventId
OccurredAtUtc

ActorAdminUserId nullable
ActorRoleSnapshot

ActionCode
TargetType
TargetId

CustomerId nullable
ShopId nullable

PreviousState
NewState

ReasonCode nullable
HumanNote nullable

RequestId
CorrelationId
ClientOperationId

SessionId nullable
SourceIp nullable

Result
SUCCESS
DENIED
FAILED
```

Audit denied and failed attempts, not only successes.

---

# 113. Audit Storage Hardening

Application audit identity should have:

```text
INSERT
READ
```

but not:

```text
UPDATE
DELETE
```

for audit data.

Optional tamper-evident enhancement:

```text
SequenceNumber
PreviousEventHash
EventHash
```

Audit export must itself be audited.

---

# 114. Admin Portal Frontend Information Architecture

Recommended primary navigation:

```text
Dashboard

Work Queue
    Payments
    Activations
    Hardware Reviews
    Primary Transfers
    Sensitive Approvals

Customers & Shops

Commercial Licenses

Primary Installations

Support / Upgrades

Audit

Administration
    Admin Users
    Roles & Permissions
    Sessions / Security
```

The Portal should be case-oriented and queue-first.

Do not design it as a database CRUD console.

---

# 115. Admin Portal Dashboard

Dashboard should prioritize actionable operational queues.

Recommended widgets:

```text
Payments Awaiting Review
Activations Awaiting Review
Hardware Reviews
Primary Transfers
Sensitive Approvals
Licenses Expiring Soon
Security Alerts
Recent High-Risk Actions
```

Counts must come from backend read models.

Do not calculate queue authority by loading all rows into the browser.

---

# 116. Work Queue Frontend

Common queue fields:

```text
Case Type
Case ID
Customer
Shop
Current State
Priority
Opened At
Age
Assigned Admin
Risk / Attention marker
```

Recommended default ordering:

```text
Priority
then oldest unresolved
```

Queue assignment is an operational aid, not domain authority.

The backend still validates every state transition.

---

# 117. Customer 360 Frontend

Customer 360 should centralize:

```text
Customer identity
Shops
Orders
Payments
Commercial Licenses
Seat Entitlements
Primary Installations
Activations
Hardware Reviews
Transfers
Support / Upgrades
Recent Audit
```

It should be a purpose-built investigation view.

Avoid ten disconnected CRUD pages.

---

# 118. Commercial License Frontend

License detail should display:

```text
License ID
Customer
Shop
Plan
Status
Validity
Purchased Seat Count
Primary Installation
Current Certificate
Authority Revision
Support Entitlement
Upgrade Entitlement
Recent Operations
Audit Timeline
```

Actions should be explicit:

```text
Renew
Extend
Increase Seats
Reduce Seats
Pause
Resume
Suspend
Revoke
```

Each action opens a purpose-built workflow with consequence preview.

---

# 119. Payment Review Frontend

Payment review screen should show:

```text
Order summary
Backend pricing snapshot
Submitted evidence
Declared amount
Transaction reference
Previous submissions
Customer/shop identity
Potential duplicate signals
Audit history
```

Actions:

```text
Approve
Reject
Request Clarification
```

Approval button must not directly calculate/create arbitrary licensing data in the browser.

---

# 120. Activation Review Frontend

Activation review should show:

```text
Commercial License
Primary Installation
Activation token state
Hardware identity summary
Fingerprint quality
Validation results
Previous activation history
Payment approval evidence
Risk / warning conditions
```

Actions:

```text
Approve
Reject
Request More Information
Open Hardware Review
```

Hard-deny conditions must not expose an override button unless an explicitly designed Exceptional Authorization workflow applies.

---

# 121. Hardware Review Frontend

Show comparison:

```text
Previous hardware
Current hardware
Changed strong anchors
Changed secondary anchors
Classification
Risk
Evidence
History
```

Possible operator actions:

```text
Approve Rebind
Recommend Primary Transfer
Request Information
Reject
```

The Portal must never allow manually typing a replacement HardwareFingerprintHash.

---

# 122. Primary Transfer Frontend

Transfer case should show:

```text
Old Primary
New Primary request
Customer verification
Commercial License
Shop
Old certificate
Old reconciliation status
Reason
Risk
Approval requirements
```

The Portal should make it obvious that:

```text
Secondary seat replacement != Primary transfer
```

Do not route normal workstation replacement into this queue.

---

# 123. Dangerous Action UX

Generic:

```text
Are you sure?
Yes / No
```

is insufficient for high-risk operations.

Backend should provide an impact preview.

Example revocation preview:

```text
Active license
Purchased seats
Primary installation
Current certificate
Expected local effect
Offline enforcement limitation
Required second approval
```

Final confirmation requires:

```text
Target identity
Reason
Expected version
ClientOperationId
```

---

# 124. Global Search Frontend

Search should support:

```text
Customer name
Shop name
Phone
Email
CNIC where authorized
CommercialLicenseId
OrderId
PrimaryInstallationId
Payment reference
```

Backend search results are permission-filtered and PII-masked.

Do not let search become a sensitive-data enumeration bypass.

---

# 125. Admin Notifications

Operational notifications may include:

```text
New payment proof
Activation awaiting review
Hardware review opened
Primary transfer awaiting action
Sensitive approval awaiting second reviewer
License expiring
Security incident
```

Notifications are not domain authority.

Reading a notification does not close the underlying case.

---

# 126. Notification Delivery Backend

Use Outbox-driven delivery.

Possible channels:

```text
Portal notification center
Email
SMS / WhatsApp later if required
```

Exact providers are implementation decisions.

A failed notification must not rollback the commercial business transaction that generated it.

---

# 127. Vendor Worker Responsibilities

`EdgeRetails.Vendor.Worker` may process:

```text
Transactional outbox
Certificate issuance retries
Notification delivery
Queue projections
Expiry processing
Reconciliation support jobs
Security cleanup
Admin session cleanup
Audit export jobs
Backup jobs
Operational health projections
```

Consumers must be idempotent.

---

# 128. Server-Side API Separation

Use separate route/authentication surfaces.

## Human Admin API

```text
/api/admin/*
```

Authentication:

```text
AdminSession
MFA
RBAC
```

## Shop Primary Licensing API

```text
/api/licensing/*
```

Authentication:

```text
Primary Installation identity
Signed request/challenge
```

## Internal Protected APIs

```text
/internal/*
```

Authentication:

```text
service-to-service credentials
network restrictions
```

Do not reuse one credential model for all three surfaces.

---

# 129. Recommended Admin APIs

Examples:

```text
POST /api/admin/auth/login
POST /api/admin/auth/mfa/verify
POST /api/admin/auth/logout
GET  /api/admin/me
GET  /api/admin/me/capabilities
GET  /api/admin/sessions

GET  /api/admin/dashboard
GET  /api/admin/queues/payments
GET  /api/admin/queues/activations
GET  /api/admin/queues/hardware-reviews
GET  /api/admin/queues/transfers
GET  /api/admin/queues/sensitive-approvals

GET  /api/admin/customers
GET  /api/admin/customers/{id}
GET  /api/admin/shops/{id}

POST /api/admin/orders
POST /api/admin/payments/{id}/approve
POST /api/admin/payments/{id}/reject
POST /api/admin/payments/{id}/request-clarification

POST /api/admin/licenses/{id}/renew
POST /api/admin/licenses/{id}/extend
POST /api/admin/licenses/{id}/increase-seats
POST /api/admin/licenses/{id}/reduce-seats
POST /api/admin/licenses/{id}/pause
POST /api/admin/licenses/{id}/resume
POST /api/admin/licenses/{id}/suspend
POST /api/admin/licenses/{id}/revoke

POST /api/admin/activations/{id}/approve
POST /api/admin/activations/{id}/reject

POST /api/admin/hardware-reviews/{id}/approve-rebind
POST /api/admin/transfers/{id}/approve

GET /api/admin/audit
POST /api/admin/audit/exports
```

Exact URLs may change, but authority separation should not.

---

# 130. Recommended Shop Primary Licensing APIs

Examples:

```text
POST /api/licensing/primary/register
POST /api/licensing/activation/redeem
GET  /api/licensing/activation/{id}/status

POST /api/licensing/certificates/current
POST /api/licensing/reconcile

POST /api/licensing/hardware/compare
POST /api/licensing/hardware/review-request

GET  /api/licensing/entitlement
GET  /api/licensing/time
```

Bootstrap endpoints should be tightly rate-limited.

---

# 131. Error Contract

Use stable machine-readable error codes.

Examples:

```text
auth.admin_session_required
auth.mfa_required
authorization.denied
authorization.step_up_required

payment.already_finalized
payment.evidence_invalid

license.not_active
license.expired
license.revoked
license.seat_reduction_blocked

activation.token_invalid
activation.token_expired
activation.token_consumed
activation.installation_mismatch
activation.hardware_mismatch

certificate.issuance_stale
certificate.signing_unavailable

hardware.review_required
transfer.invalid_state

idempotency.conflict
concurrency.conflict
rate_limit.exceeded
```

Frontend should localize/present messages from codes without inventing business decisions.

---

# 132. Idempotency

All retry-sensitive operations use:

```text
ClientOperationId
PayloadHash
OperationType
ResultReference
CreatedAtUtc
```

Rule:

```text
same operation id + same payload
→ return existing result

same operation id + different payload
→ IDEMPOTENCY_CONFLICT
```

Apply to:

```text
Order creation
Payment approval
Commercial License issuance
Seat entitlement changes
Activation redemption
Activation approval
Certificate issuance
Renewal
Extension
Rebind
Primary transfer
Revocation
Sensitive approval execution
```

---

# 133. Concurrency

Use optimistic concurrency and database locks where needed.

Important Vendor races:

```text
Two admins approve same payment
Two activations consume same token
Renewal vs suspension
Certificate issuance vs revocation
Seat increase vs seat reduction
Rebind vs transfer
Two sensitive-approval executions
```

No last-write-wins for commercial/security authority.

---

# 134. Transactional Outbox

Any mutation that must trigger asynchronous work writes:

```text
Domain change
+
Outbox event
```

in the same DB transaction.

Examples:

```text
PaymentApproved
CommercialLicenseCreated
LicenseRenewed
LicenseSuspended
ActivationApproved
CertificateIssuanceRequested
PrimaryTransferApproved
LicenseExpiring
```

Worker processes events at least once.

Consumers must deduplicate by MessageId.

---

# 135. Rate Limiting

Apply configurable rate limiting to:

```text
Admin login
MFA verification
Activation redemption
Primary registration
Reconciliation abuse
Global search
Sensitive PII reveal
Evidence download
```

Exact thresholds remain security configuration.

---

# 136. Secret Management

Secrets include:

```text
Vendor DB credentials
Admin auth keys
MFA provider credentials
Object storage credentials
Service-to-service credentials
Operational signing credentials
```

They must not live in:

```text
source repository
browser bundle
ordinary audit logs
plain config files committed to source
```

Use a production secret provider.

---

# 137. Logging

Structured log context should include:

```text
Timestamp
Service
Environment
RequestId
CorrelationId
AdminUserId nullable
PrimaryInstallationId nullable
Action
Result
ErrorCode
```

Never log:

```text
password
MFA secret
activation raw token
private signing key
full sensitive PII
raw payment evidence
terminal secret
```

---

# 138. Observability

Recommended metrics:

```text
Pending payment count
Oldest payment age
Pending activation count
Oldest activation age
Hardware review age
Transfer age
Sensitive approval age

Admin login failures
MFA failures
Permission denials

Activation replay attempts
Certificate issuance latency
Certificate issuance failures

Reconciliation failures
Outbox backlog
DB latency
Worker health
Backup health
```

Do not put PII in metric labels.

---

# 139. Vendor Backup / Disaster Recovery

Vendor PostgreSQL is commercial authority and requires production backup.

Requirements:

```text
Automated backups
Encrypted backup storage
Integrity verification
Retention
Off-site copy
Restore rehearsal
```

Evidence object storage must also be backed up or lifecycle-protected.

The private signing key is not part of an ordinary PostgreSQL backup.

Signing-key recovery requires a separate security process.

---

# 140. Deployment Topology

Recommended production deployment:

```text
Internet
↓
Reverse Proxy / WAF / TLS
↓
Vendor.Api
↓
Vendor Application
↓
Vendor PostgreSQL

Vendor.AdminPortal
↓
Vendor.Api

Vendor.Worker
↓
Vendor PostgreSQL / Outbox / Object Storage

Certificate Issuance
↓
Protected Signing Service / HSM / Key Vault

Private Payment Evidence Storage

Central Logging / Metrics / Alerting
```

Use network separation for protected signing infrastructure.

---

# 141. Admin Portal Frontend Technical Boundaries

The frontend should use:

```text
typed API client
server-derived permissions
server-derived queue counts
server-derived pricing
server-derived impact previews
server-derived state transitions
```

Frontend may optimistically update harmless UI state, but must refresh authoritative case state after a mutation.

Do not store sensitive commercial state as browser-only truth.

---

# 142. Admin Portal Frontend State Management

Recommended categories:

```text
Session State
Navigation State
Filter / Sort State
Case Draft State
Server Query Cache
```

Do not copy entire domain aggregates into mutable client-side stores and treat them as authoritative.

Use explicit query models.

---

# 143. Admin Portal Read Models

Backend should expose purpose-built read models:

```text
AdminDashboardReadModel
PaymentReviewReadModel
ActivationReviewReadModel
HardwareReviewReadModel
PrimaryTransferReadModel
CommercialLicenseDetailReadModel
PrimaryInstallationDetailReadModel
Customer360ReadModel
AuditSearchReadModel
```

Avoid leaking raw EF entities to the frontend.

---

# 144. Frontend Concurrency UX

When backend returns:

```text
CONCURRENCY_CONFLICT
```

Portal should:

```text
show that the case changed
reload authoritative state
preserve unsaved operator note where safe
require operator to reassess
```

Never silently overwrite another admin's decision.

---

# 145. Frontend Idempotency UX

For dangerous mutation clicks:

```text
Generate ClientOperationId once at user intent boundary
```

Retry uses the same ID.

Do not generate a new operation ID on every HTTP retry.

This applies to:

```text
Approve Payment
Approve Activation
Renew
Extend
Revoke
Approve Rebind
Approve Transfer
```

---

# 146. Server-Side Frontend Accessibility / Operational UX

Admin Portal must support:

```text
keyboard navigation
clear focus states
usable table filters
error summaries
non-color-only status indication
confirmation text for dangerous actions
loading states
empty states
retry states
permission-denied states
```

This is especially important for operational queue work.

---

# 147. Server-Side Frontend Security UX

When a user lacks a capability:

Prefer:

```text
hide irrelevant unavailable action
```

or show read-only context when useful.

But backend must still reject unauthorized calls.

For PII:

```text
masked by default
[Reveal] only when capability allows
```

For dangerous actions:

```text
show impact
show target
require reason
show second-approval requirement
```

---

# 148. Vendor System Production Certification

Certification must cover:

```text
Customer / Shop identity
Backend pricing
Payment evidence
Payment approval idempotency
Commercial License issuance
Seat entitlement
Primary activation
Hardware binding
Certificate signing
Offline validation
Renewal
Suspension / revocation
Primary rebind
Primary transfer
Admin auth
MFA
RBAC
Dual approval
PII masking
Audit immutability
Concurrency
Outbox
Backup / restore
Deployment
```

---

# 149. Vendor End-to-End Certification Journeys

## Journey A - New Commercial Shop

```text
Customer
→ Shop
→ Order
→ Pricing
→ Payment Proof
→ Payment Approval
→ Commercial License
→ Purchased Seats
→ Primary Installation
→ Activation Token
→ Activation Approval
→ Certificate
→ Primary Shop Server ACTIVE
```

## Journey B - Add Seat

```text
Commercial License
→ Add Seat Order
→ Payment
→ Approval
→ PurchasedSeatCount++
→ Primary reconciliation
→ Primary may attach another Secondary workstation
```

No Vendor hardware-transfer workflow for that Secondary workstation.

## Journey C - Renewal

```text
Renewal Order
→ Payment
→ Approval
→ validity extension
→ AuthorityRevision++
→ replacement certificate / reconciliation
```

## Journey D - Primary Hardware Rebind

```text
Hardware change
→ Review
→ Rebind approval
→ new fingerprint binding
→ new certificate
```

## Journey E - Primary Machine Transfer

```text
Transfer request
→ identity verification
→ approval
→ old Primary retired
→ new Primary activated
→ new certificate
```

## Journey F - Revocation

```text
Revoke request
→ sensitive approval if required
→ execute
→ audit
→ AuthorityRevision++
→ next Primary reconciliation / expiry enforcement
```

---

# 150. Vendor System Acceptance Criteria

Server-side frontend and backend are ready only when:

```text
1. Portal contains no generic database CRUD.
2. Browser cannot calculate authoritative commercial pricing.
3. Payment approval is idempotent and race-safe.
4. Payment approval and activation approval remain separate.
5. Purchased seat count is Vendor commercial authority.
6. Normal Secondary replacement remains shop-local.
7. Primary activation is machine-bound.
8. Certificates are signed only by protected signing infrastructure.
9. Signing private key never reaches Portal/POS/ordinary DB.
10. Admin authentication and MFA are production-safe.
11. RBAC has no universal IsAdmin bypass.
12. High-risk actions support dual approval where required.
13. PII is masked and access is auditable.
14. Audit cannot be edited/deleted through application authority.
15. Queues are backend-derived operational read models.
16. All dangerous actions have consequence-aware confirmation.
17. All retry-sensitive mutations use idempotency.
18. Concurrency conflicts never silently overwrite decisions.
19. Vendor backup and restore are tested.
20. End-to-end licensing journeys pass.
```

---

# PART III - CROSS-SYSTEM IMPLEMENTATION AND CERTIFICATION

# 151. Cross-System Idempotency

Both Shop POS and Vendor Control Plane must consistently use:

```text
ClientOperationId
PayloadHash
OperationType
ResultReference
```

Same ID + same payload:

```text
return original result
```

Same ID + different payload:

```text
IDEMPOTENCY_CONFLICT
```

Do not allow different subsystems to invent incompatible retry behavior.

---

# 152. Cross-System Concurrency

Important shared race classes:

```text
Two terminals sell same serialized unit
Two terminals oversell quantity stock
Seat replacement vs old terminal transaction
Two pairing clients claim one seat
Two admins approve same payment
Activation approval vs license suspension
Certificate issuance vs revocation
Renewal vs Primary transfer
Restore vs active workstation credentials
```

Use explicit locking/optimistic concurrency per aggregate.

---

# 153. Cross-System Security Boundaries

Never trust:

```text
Client-supplied ActorId
Client-supplied NodeRole
Client-supplied Shop authority
Client-supplied CommercialLicense state
Client-supplied payment amount
Client-supplied permission claims
Frontend-only state transition checks
```

The authoritative server derives identity and authority from authenticated context and persisted state.

---

# 154. Required New / Changed POS Persistence

Expected POS-side schema evolution includes:

```text
system.workstation_seats
system.workstation_pairing_sessions
system.terminals extensions
identity.user_sessions.TerminalId
finance.cash_sessions.TerminalId
runtime/security epoch state
```

`ShopServerIdentity` may be stored in protected Primary state outside normal DB rollback authority, with only safe metadata in PostgreSQL if useful.

Exact EF migrations must be based on the live model.

---

# 155. Required New Vendor Persistence

Expected Vendor-side persistence includes:

```text
vendor_identity.admin_users
vendor_identity.admin_sessions
vendor_identity.admin_mfa_credentials
vendor_identity.roles
vendor_identity.permissions
vendor_identity.role_permissions

customers.customers
customers.shops

commerce.orders
commerce.pricing_snapshots

payments.payment_proofs
payments.payment_reviews
payments.payment_clarifications

licensing.license_plans
licensing.commercial_licenses

installations.primary_installations
installations.hardware_fingerprints

activation.activation_tokens
activation.activation_requests

certificates.certificate_issuance_requests
certificates.installation_certificates
certificates.signing_key_metadata

operations.hardware_review_cases
operations.machine_transfer_requests
operations.sensitive_approval_requests

support.support_entitlements
support.upgrade_entitlements

audit.audit_events

notifications.admin_notifications

system/outbox equivalent for Vendor Worker
```

Exact table names can evolve, but authority separation should remain.

---

# 156. POS Multi-Seat Test Matrix

## Installation / Pairing

```text
Primary can create pairing only when seat available
Secondary cannot create pairing
Copied pairing package cannot activate two PCs
Expired pairing rejected
Wrong ShopServer identity rejected
Two PCs race for one seat -> exactly one wins
Offline workstation does not release seat
Replacement atomically detaches old and activates new
Old terminal denied after replacement
```

## Authentication

```text
Terminal required
UserSession required
Session bound to terminal
Actor spoof rejected
Disabled user rejected
Revoked session rejected
Detached terminal rejected
Secondary cannot invoke Primary-only endpoints
```

## Business

```text
Seat1 and Seat2 simultaneous normal sales
Same serial race -> one winner
Quantity oversell blocked
Purchase vs sale race safe
Stocktake vs sale race safe
Warranty race safe
Supplier payment replay safe
```

## Cash

```text
Seat1 and Seat2 can each have own open drawer
Seat2 movement cannot enter Seat1 drawer
Drawer closure isolated by terminal
Shop report aggregates all drawers
```

## Scanner

```text
Keyboard search
Mouse add
Barcode scan
Serial scan
IMEI scan
Rapid scan queue
Duplicate exact-unit rejection
Piece vs Box separate cart lines
FactorToBaseUnit availability
```

## Update / Restore

```text
Compatible old workstation continues
Too-old workstation receives Update Required
Bootstrap update path works
Restore does not resurrect detached terminals
Security epoch revalidation works
```

---

# 157. Vendor Control Plane Test Matrix

## Commerce / Payments

```text
Pricing server authoritative
Pricing snapshot immutable
Evidence immutable
Two payment approvals race safely
Unknown-outcome replay returns same result
Different payload replay conflicts
New-license order creates one CommercialLicense
Add-seat order increments entitlement once
```

## Activation

```text
Expired token rejected before first redemption
Consumed token rejected
Wrong Primary Installation rejected
Hardware mismatch handled
Reviewable weak fingerprint enters review
Hard failure cannot be manually approved
Activation approval creates one issuance request
```

## Certificates

```text
Stale AuthorityRevision cannot be signed
Signing retry is idempotent
Only one ACTIVE certificate per Primary
Renewal supersedes old certificate
Invalid signature rejected by Primary runtime
Unknown signing key fails safely
```

## Security

```text
MFA required
Disabled admin session rejected
Role change invalidates effective authority
Requester cannot second-approve own action
PII reveal requires permission
Audit mutation unavailable
Private signing material never returned
```

---

# 158. POS Multi-Seat Acceptance Criteria

POS multi-seat architecture is complete only when:

```text
1. One shop uses one authoritative PostgreSQL DB.
2. Primary hosts the only Shop Server and Worker.
3. Secondary has no DB credentials.
4. Both seats have full business features.
5. User permissions determine business access.
6. Node role determines infrastructure authority.
7. Owner on Secondary cannot manage seats / DB / commercial license / server.
8. Secondary installation is initiated from Primary.
9. Pairing is secure and one-time.
10. Secondary replacement is unlimited and owner-controlled.
11. One seat cannot be active on two workstations.
12. Offline does not auto-release a seat.
13. Terminal + UserSession are both authenticated.
14. Network ActorId spoofing is impossible.
15. All Desktop business reads/writes go through Server.
16. Cash sessions are terminal-aware.
17. Printing/scanning use local hardware with central business authority.
18. Updates do not deadlock Secondary clients.
19. Restore cannot resurrect detached terminals.
20. Existing business concurrency protections remain green.
```

---

# 159. Important Superseded Assumptions

Do not implement the following older ideas.

## Superseded - Server on Every Workstation

Do not implement:

```text
Secondary Desktop
→ Secondary EdgeRetails.Server
→ Primary EdgeRetails.Server
```

Final design uses one Shop Server on Primary.

## Superseded - Vendor Activation for Every Secondary Seat

Secondary workstation replacement is shop-owner controlled.

Do not require Vendor hardware transfer/rebind for normal Secondary changes.

## Superseded - Seat Defines Role

Seat is workstation capacity, not Cashier/Manager.

## Superseded - Separate DB Per Seat

Never create separate seat databases for this architecture.

## Superseded - Frontend-Only Primary Control Hiding

Primary-only restrictions are server-enforced.

---

# 160. Things Antigravity Must Not Do

Do not:

```text
Restart completed sprints
Redo exact-unit architecture without a concrete blocking regression
Replace the existing POS from zero
Create separate databases for seats
Put DB credentials on Secondary
Add offline business mutation queues on Secondary
Make IP/MAC address the commercial identity
Allow Secondary to manage Primary infrastructure
Trust ActorUserId from network requests
Use no-op production authorization
Auto-release seats because a workstation went offline
Allow restore to resurrect old terminal authority
Make scanner business resolution client-authoritative
Put commercial pricing authority in Admin Portal frontend
Expose Vendor private signing key
Create generic DB CRUD Admin Portal
Route normal Secondary replacement through Primary transfer workflow
```

---

# 161. Recommended Implementation Sequence for Antigravity

Implementation should begin with the current POS, not the Admin Portal.

## Phase A - POS Runtime Boundary

```text
NodeRole
Server-only production authority
Primary Desktop through localhost Server
Full remote API coverage
Remote authentication
Terminal-bound UserSession
RequestExecutionContext
Remove ActorId trust
Real production authorization
```

## Phase B - Primary / Secondary Workstations

```text
ShopServerIdentity
WorkstationSeat
TerminalRegistration changes
PairingSession
Seats & Connected Systems UI
Secondary installer
LAN discovery
Pairing / approval
Attach / detach / replace
Primary-only endpoint authorization
```

## Phase C - Multi-Seat Business Hardening

```text
Terminal-aware cash sessions
Terminal-aware cash movements
Terminal provenance
Concurrent business regression
```

## Phase D - POS Input / Peripheral Hardening

```text
Focus-safe scanner input
Ordered scan queue
ProductUnit cart key
FactorToBaseUnit availability
Barcode normalization
Local printing/scanning settings
```

## Phase E - Update / Restore / Runtime Security

```text
TLS / firewall
Bootstrap protocol
Rolling compatibility
Primary-controlled Secondary updates
ShopSecurityEpoch
Restore reauthorization
Credential protection
```

## Phase F - Vendor Backend

```text
Vendor solution foundation
Admin Identity / RBAC / MFA
Customers / Shops
Commerce
Payments
Commercial Licenses
Seat entitlement
Primary Installation
Activation
Certificates
Hardware Review
Primary Transfer
Audit
Worker
Backup / DR
```

## Phase G - Admin Portal Frontend

```text
Shell
Dashboard
Work Queues
Customer 360
Payments
Commercial Licenses
Primary Installations
Activation Review
Hardware Review
Primary Transfer
Audit
Administration / Security
```

## Phase H - Full Cross-System Certification

```text
POS multi-seat E2E
Vendor licensing E2E
Primary activation E2E
Add-seat E2E
Secondary pairing/replacement E2E
Renewal E2E
Primary transfer E2E
Security certification
Backup / restore certification
Release / installer certification
```

---

# 162. Final Architectural Summary

The intended Edge Retails product is:

```text
VENDOR CONTROL PLANE
    Commercial authority
    Payments
    Licensing
    Primary activation
    Signing
    Admin Portal
        │
        │ Commercial license / seat entitlement
        ▼
PRIMARY SHOP INSTALLATION
    Commercial root
    Shop Server
    Worker
    PostgreSQL
    Seat Management
        │
        ├── Seat 1 / Primary Desktop
        ├── Seat 2 / Secondary Desktop
        ├── Seat 3 / Secondary Desktop
        └── ...
```

Business authority is centralized in the Primary Shop Server and one PostgreSQL database.

Workstation access scales through purchased seats.

Human permissions control business actions.

Primary node authority controls infrastructure actions.

Vendor Control Plane controls commercial licensing and the Primary commercial installation.

Normal Secondary machine replacement remains local and owner-controlled.

The Admin Portal is a secure operational frontend over Vendor backend authority.

This is the architecture Antigravity should implement.
