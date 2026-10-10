# Contributing to Edge Retails

Thank you for your interest in contributing to the Edge Retails Enterprise Point of Sale platform. To maintain financial audit compliance, high data integrity, and strict architectural alignment, all contributions must strictly adhere to the following guidelines.

---

## 🏛️ Architecture Invariants

Edge Retails enforces strict architectural boundaries:

1. **Unidirectional Dependency Flow**:
   - `EdgeRetails.Domain` has **zero** dependencies on other solution projects or external infrastructure packages.
   - `EdgeRetails.Application` depends **only** on `EdgeRetails.Domain`.
   - `EdgeRetails.Infrastructure` implements domain and application interfaces and handles persistence/hardware.
   - `EdgeRetails.Desktop`, `EdgeRetails.Server`, and `EdgeRetails.Worker` are composition roots and orchestrate startup/DI.

2. **Ledger Immutability**:
   - Financial entries (`LedgerEntry`, `KhataAccount`, `CashDrawerTransaction`) are **append-only**.
   - Under no circumstances should financial records be updated or deleted directly. Reversals must be executed via compensating debit/credit events.

3. **Concurrency & Locking Protocol**:
   - Any transaction modifying physical inventory quantities or serial/IMEI ownership must obtain pessimistic row locks (`FOR UPDATE`) within PostgreSQL transaction boundaries.

---

## 🔄 Branching & Pull Request Process

1. **Branch Naming**:
   - `feature/<ticket-id>-<short-description>`
   - `fix/<defect-id>-<short-description>`
   - `remediation/<pass-id>-<target>`

2. **Pre-Commit Verification**:
   Before submitting changes, ensure your local workspace compiles with zero errors and passes all test suites:
   ```powershell
   # Compile entire solution
   dotnet build EdgeRetails.sln -c Release

   # Run automated test suites
   dotnet test EdgeRetails.sln --no-build

   # Verify architecture manifest integrity
   powershell -ExecutionPolicy Bypass -File scripts/Verify-ArchitectureInternationalAuditRemediation.ps1
   ```

3. **Commit Messages**:
   Follow conventional commits format:
   - `feat(catalog): add composite barcode lookup support`
   - `fix(finance): prevent double-reversal on expense void`
   - `refactor(layout): organize documentation into enterprise tiers`

4. **Cryptographic Integrity**:
   - If changes touch files indexed by candidate manifests, update the transition map in `docs/governance/manifests/MANIFEST_PATH_TRANSITION_MAP.json`.

---

## 🧪 Testing Standards

Every contribution modifying business logic or database queries must include automated tests:
- **Domain & Business Invariants**: Added to `tests/EdgeRetails.UnitTests/`.
- **PostgreSQL Transaction & Concurrency**: Added to `tests/EdgeRetails.IntegrationTests/`.
- **Regression Gates**: Existing tests in `Phase*` test classes must remain green without skipping or modification unless explicitly authorized by governance review.

---

## 📋 Code Style & Formatting

- Follow standard .NET coding conventions configured in `.editorconfig`.
- Enable nullable reference types (`<Nullable>enable</Nullable>`).
- Keep code warnings at zero (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).
