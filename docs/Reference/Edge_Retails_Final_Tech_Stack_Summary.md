# Edge Retails — Final Tech Stack Summary

**Project Type:** Windows Desktop Point of Sale (POS)  
**Architecture:** Local-first, modular desktop application  
**Database:** PostgreSQL (Final / Non-negotiable)  
**FBR Integration:** Not included  
**Licensing:** Local signed license validation  

---

## 1. Final Core Stack

| Layer | Technology |
|---|---|
| Platform | Windows Desktop |
| Frontend | WPF |
| UI Language | XAML |
| Main Language | C# |
| Runtime | .NET 10 LTS |
| UI Pattern | MVVM |
| Architecture | Modular Monolith |
| Primary Database | PostgreSQL |
| PostgreSQL Driver | Npgsql |
| ORM | Entity Framework Core |
| Complex Reports / Optimized Queries | Dapper / Raw SQL |
| Background Tasks | .NET Worker Service |
| Logging | Serilog |
| Testing | xUnit |
| Installer | WiX Toolset + Burn |
| Future LAN/API Layer | ASP.NET Core, only when required |

---

## 2. Frontend

### WPF + C# + XAML

Edge Retails will use **WPF** for the Windows desktop frontend.

Reasons:

- Mature Windows desktop framework
- Strong tooling support
- XAML designer support
- Better ecosystem for enterprise and POS-style applications
- Strong DataGrid and reporting component ecosystem
- Suitable for large form-heavy business applications
- Modern UI is still fully possible through custom styling and Fluent design

### UI Architecture

The UI will follow **MVVM**:

```text
View
  ↓
ViewModel
  ↓
Application Service
  ↓
Domain / Business Logic
  ↓
Infrastructure
  ↓
PostgreSQL
```

Business logic will not be placed directly inside WPF Views or ViewModels.

---

## 3. Backend / Application Logic

The main application backend will be:

```text
C#
+
.NET 10
```

The desktop application will initially communicate directly with the application layer and PostgreSQL.

No unnecessary local HTTP API will be used for a single-PC installation.

```text
WPF
 ↓
Application Layer
 ↓
Domain Layer
 ↓
Infrastructure Layer
 ↓
PostgreSQL
```

ASP.NET Core will only be introduced later if multi-terminal or LAN architecture requires a service boundary.

---

## 4. PostgreSQL Database

PostgreSQL is the **final database choice**.

It will run locally on the Windows POS machine as the primary production database.

```text
Windows PC

├── EdgeRetails.exe
├── EdgeRetails.Worker.exe
└── PostgreSQL Service
      └── edge_retails database
```

### Database Access

```text
C#
 ↓
EF Core / Dapper
 ↓
Npgsql
 ↓
PostgreSQL
```

### Responsibilities

PostgreSQL will store:

- Products
- Categories
- Inventory
- Sales
- Sale Items
- Purchases
- Purchase Items
- Customers
- Suppliers
- Payments
- Returns
- Expenses
- Users
- Roles
- Permissions
- Audit Logs
- Settings
- License-related local state
- Module-specific data

---

## 5. EF Core + Dapper Strategy

### Entity Framework Core

Used for:

- Standard CRUD
- Entity relationships
- Migrations
- Transactions
- Repository operations
- Normal application queries

### Dapper / Raw SQL

Used for:

- Large reports
- Dashboard aggregations
- Complex inventory queries
- Performance-sensitive read operations
- Advanced financial reports

Rule:

```text
Normal Business Operations → EF Core
Complex / Heavy Reporting → Dapper / SQL
```

---

## 6. Modular Monolith Architecture

Edge Retails will be built as a **modular monolith**.

The core application remains common, while different business types can have separate modules.

Suggested solution structure:

```text
EdgeRetails.sln

src/
├── EdgeRetails.Desktop
├── EdgeRetails.Application
├── EdgeRetails.Domain
├── EdgeRetails.Infrastructure
├── EdgeRetails.Modules.Abstractions
├── EdgeRetails.Modules.GeneralRetail
├── EdgeRetails.Modules.Mobile
├── EdgeRetails.Modules.Electronics
├── EdgeRetails.Worker
└── EdgeRetails.Tests
```

### Module Concept

```text
Shared Core
   │
   ├── General Retail Module
   ├── Mobile Module
   └── Electronics Module
```

Each vertical module can implement its own rules without changing the core POS engine.

Example abstractions:

```text
IVerticalModule
IProductAttributeProvider
IInventoryPolicy
IPricingPolicy
ISaleValidationRule
IPurchaseValidationRule
IReceiptExtension
IInventoryTrackingStrategy
```

### V1 Module Loading

V1 will use compile-time modules with Dependency Injection.

Runtime DLL plugin loading will not be used initially.

---

## 7. PostgreSQL JSONB Strategy

PostgreSQL `jsonb` will be used only for **flexible descriptive product attributes**.

Example:

```json
{
  "storage": "256GB",
  "color": "Black",
  "ram": "12GB",
  "model": "SM-S928B"
}
```

These values can live inside:

```text
products.attributes jsonb
```

### Important Rule

Critical identity or transactional data will **not** be stored only in JSONB.

Examples:

- IMEI
- Serial Number
- Unit Status
- Warranty per individual item
- Purchase linkage
- Sale linkage

These will use normal relational PostgreSQL tables.

Example:

```text
inventory_units
---------------
unit_id
product_id
serial_number
imei1
imei2
status
purchase_item_id
sale_item_id
warranty_start
warranty_end
```

---

## 8. Background Worker Service

A separate .NET Worker Service will run as a Windows Service.

```text
EdgeRetails.Worker.exe
```

Responsibilities:

- Automatic database backups
- Cloud backup upload
- Backup verification
- Scheduled maintenance
- Cleanup jobs
- Update checks
- Future synchronization jobs

The POS UI should not be responsible for long-running background tasks.

---

## 9. Backup Architecture

Local PostgreSQL remains the primary database.

Cloud is used for **backup**, not as the primary transactional database.

```text
Local PostgreSQL
       ↓
Backup Engine
       ↓
Compression
       ↓
Encryption
       ↓
Cloud Storage
```

### Standard Backup

- Scheduled PostgreSQL backups
- Compression
- Encryption
- Cloud upload
- Retention policy
- Restore verification

### Advanced Future Backup

For larger deployments:

```text
Base Backup
+
WAL Archiving
+
Point-in-Time Recovery
```

### Important

```text
Cloud Backup ≠ Cloud Sync
```

Backup and future multi-branch synchronization will remain separate systems.

---

## 10. Licensing

Edge Retails will use a **local signed license system**.

No mandatory online license server is required for V1.

Architecture:

```text
License Generator
      ↓
Signed License File
      ↓
Customer PC
      ↓
Edge Retails validates locally
```

### License Can Contain

```text
LicenseId
CustomerName
StoreName
Plan
IssueDate
ExpiryDate
DeviceId
MaxTerminals
EnabledModules
Signature
```

### Security Model

```text
PRIVATE KEY
Held only by Edge Retails owner
      ↓
Signs license

PUBLIC KEY
Embedded in Edge Retails
      ↓
Validates license locally
```

The private key will never be shipped inside the POS application.

---

## 11. FBR Integration

FBR integration is **not part of the Edge Retails scope**.

The application will not include:

```text
FBR APIs
Digital invoice submission
FBR invoice numbers
FBR QR generation
Tax authority sync
Licensed FBR integrator integration
```

The system remains a local commercial POS product with its own licensing and backup architecture.

---

## 12. Installer

Preferred installer technology:

```text
WiX Toolset
+
Burn Bootstrapper
```

Target:

```text
EdgeRetailsSetup.exe
```

Installer can handle:

- Edge Retails desktop app
- Required .NET runtime
- PostgreSQL installation/configuration
- Local database initialization
- Windows Worker Service
- Application shortcuts
- Update components

---

## 13. Logging

Logging technology:

```text
Serilog
```

Logs can include:

- Application errors
- Database errors
- Backup failures
- Printer failures
- Startup / shutdown
- Worker Service errors
- Hardware integration issues

Sensitive information must not be written to logs.

---

## 14. Testing

Testing framework:

```text
xUnit
```

Important business logic to test:

- Sale totals
- Discounts
- Inventory movements
- Returns
- Payments
- Permissions
- Module-specific rules
- Backup logic
- Licensing validation

---

## 15. Future Multi-Terminal Architecture

Initial deployment:

```text
POS PC
  ↓
PostgreSQL Local
```

Future multi-terminal shop:

```text
               Main Shop PC / Server

                 ASP.NET Core
                      │
                 PostgreSQL
                 /    |    \
                /     |     \
             POS-01 POS-02 POS-03
              WPF    WPF    WPF
```

ASP.NET Core will only be added when this architecture is actually required.

---

## 16. Final Architecture

```text
                    EDGE RETAILS
                         │
                WPF + C# + XAML
                         │
                       MVVM
                         │
              Application / Domain
                         │
              Modular Business Layer
               /        |        \
              /         |         \
         General      Mobile    Electronics
          Retail       Module      Module
              \         |         /
               \        |        /
                Infrastructure
                         │
          EF Core + Dapper + Npgsql
                         │
                   PostgreSQL
                         │
               .NET Worker Service
                         │
             Encrypted Cloud Backup
```

Separate local licensing:

```text
Signed License
     ↓
Local Verification
     ↓
Edge Retails Activated
```

---

# Final Locked Stack

```text
Windows Desktop
+
WPF
+
C#
+
XAML
+
.NET 10 LTS
+
MVVM
+
Modular Monolith
+
PostgreSQL
+
Npgsql
+
Entity Framework Core
+
Dapper
+
.NET Worker Service
+
Serilog
+
xUnit
+
WiX Toolset + Burn
+
Encrypted Cloud Backup
+
Local Signed Licensing
```

---

## Final Scope

**Edge Retails is a Windows-first, local-first commercial POS application using PostgreSQL as its primary local database. The software will use WPF and .NET 10, follow MVVM and modular-monolith architecture, support multiple retail verticals through clean module boundaries, perform background backup through a Windows Worker Service, and validate locally generated signed licenses without depending on continuous internet connectivity.**

**Status: Final Tech Stack Summary**
