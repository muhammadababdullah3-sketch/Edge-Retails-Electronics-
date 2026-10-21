# STAGE 2: Finite Analogous Flow Inventory Across Domains

**Run ID:** `ER-RUN-LOCALFLOW-20261019-075`
**Authority:** `OWNER-UPDATE-20261018-019`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Domain Crosswalk & Analogous State Analysis

The 15 master local flow items (LF01..LF15) represent analogous business operations across retail domains that require durable idempotency, deterministic rejection recovery, and strict custody:

| Code | Domain | Module / Flow | Analogy to Thaka Sub-Ledger | Resolution in Run 075 |
|---|---|---|---|---|
| **LF01** | Sales | Sales Return Dialog | Store credit elimination; cash vs bank refund choice | Complete: Store credit disabled; operator choice preserved |
| **LF02** | Sales | POS Cart Aggregation | Grouped cart lines by {ProductId, ProductUnitId} | Complete: Physical units deduplicated with unique identity guard |
| **LF03** | Finance | Supplier Detail Payments | Compound financial submission tracking & rejection recovery | **Repaired (J2)**: Handled UI command, exact action token matching |
| **LF04** | Finance | Expense Management | Pre-POST validation vs post-commit retry | **Verified (J1)**: Typed PrePostValidationException vs committed retry |
| **LF05** | Purchasing | Purchase Return | Deterministic rejection classifier adoption | Complete: DesktopOperationRejectionClassifier adopted |
| **LF06** | Finance | Supplier Payment Enums | Validation of settlement and advance enums | Complete: Enum range check before posting |
| **LF07** | Finance | Cash Session Close | Route authorization predicate | Complete: SalesPosUse permission required |
| **LF08** | Inventory | Stock Adjustment | Unsubmitted attempt lifecycle & mismatch handling | **Verified (J3)**: GetOrCreate before snapshot, editor unlocked on mismatch |
| **LF09** | Inventory | Pack / Cable Cut Picker | 51st container paging & continuous length | Complete: Targeted cursor queries beyond 50th item |
| **LF10** | Inventory | Stocktake Exact Units | Multi-page unit enumeration bounds | **Verified (J5)**: 201 units multi-page query, explicit cap & cycle errors |
| **LF11** | Sales | Warranty Claims | Serialized unit canonical fingerprinting | **Verified (J4)**: Sorted unit IDs in fingerprint; partial claim allowed |
| **LF12** | Desktop | Main Operations Client | Known business rejection unfreeze | Complete: Classifier recognizes 400/404/422 business codes |
| **LF13** | Finance | Customer Directory | Customer balance net of credit refunds | Complete: Read services calculate net balances accurately |
| **LF14** | Reporting | Thaka Reports | Activity queries net of material reversals | Complete: Net material value calculation |
| **LF15** | Identity | Supervisor Preflight | Strict permission checks for overrides | Complete: SettingsManage or SupervisorPostingOverride required |
