# Controlled realistic demo business pilot — execution ledger

Authority: user request024a6977-87e8-495f-b01d-31fa7695e5ac. Installed release1.0.11 candidate22. This is a separately authorized small operator pilot, not formal Phase3D certification or authorization to load Rs5,000,000 inventory. Phase3 remains OPEN. Historical empty-data and failed evidence are preserved.

| Gate | Scope | Evidence Required | Command/Test | Status |
|---|---|---|---|---|
| Governance preflight | Current frozen authority and pilot authorization | No explicit conflict; preserve formal certification phase order | Bounded parent and independent read-only scope review | PASS; isolated-business-mutation rule applies to formal certification, not all authorized operator activity |
| Release/authentication | Actual installed1.0.11 and authenticated Owner | PID/path/version and actual shell result; no PIN access | Read-only census and Computer Use | IN PROGRESS; user reports successful restart login |
| Identity authority | Supplier/Company/Category/Model/Product/Tracking codes and units | Exact canonical implementation and live selected masters | Targeted source reads and read-only installed UI | IN PROGRESS; no generated-value claim before actual creation |
| Step0 backup | Fresh approved backup and actual disposablePG18 restore | Archive/hash/TOC/history/schema/cleanup allPASS | Canonical backup workflow and reviewed restore authority | NOT STARTED; must pass before business creation |
| Step1 baseline | Required table/stock/account totals | Safe before snapshot with stable scope | Authorized read-only PostgreSQL diagnostic | NOT STARTED |
| Steps2–8 pilot masters | Two suppliers, cashier, four customers, project, five products | Normal UI, canonical generation, no duplicate masters/products | Actual installed workflows | GATED on backup and baseline |
| Steps9–15 purchases/receipts/Khata | Small mixed-payment purchases and same-product second supplier | Documents, physical receipt, codes/sequences/provenance/accounting | Normal installed workflows plus read-only forensics | GATED |
| Steps16–29 operator scenarios | Positive search, cashier permissions, sales/draft/print/return/warranty/project/stocktake | Real installed results, actual hardware limitations disclosed | Normal installed workflows; human credential entry | GATED |
| Steps30–31 reconciliation/verdict | Before/after stock, money, exact units, codes, provenance | Every amount/count reconciles; no silent failed operation | Evidence tables and read-only verification | GATED |
| Full population | Rs5,000,000 /170–190products | PilotPASS then separate human authorization | None during this pilot | NOT AUTHORIZED TO START |

Preflight finding: frozen contract3D.2 and line23 restrict formal business-mutation certification to isolated PostgreSQL18 after3C lock. Underlying2fa0ac88 section3D.2 prohibits unsafe/destructive live commercial records solely for certification. Latest request separately authorizes a small clearly identified DEMO business pilot through normal installed operator paths. Independent bounded reviewer agrees there is no explicit conflict from these provisions. No formal3D result is credited from pilot operations.

No business records created yet. No fresh pre-pilot backup yet. CONTROLLED DEMO BUSINESS DATA PILOT START marker is reserved for the verified-backup/baseline checkpoint immediately before the first business creation; this preflight does not claim pilot execution started.

## Step0 installed failure — checkpoint2026-10-01

Authenticated Amir/Owner shell observed in installedDesktopPID17888. Product Management0products and Settings render normally. Canonical Backup Now invoked once around14:21 Pakistan time; creation failed with misleading record-changed message. Read-only history refresh around14:29 completed fromServer with no verified backups. No successful fresh archive/restore result, no business creation. Step0=FAIL; all business steps remain gated, not skipped/assumedPASS. Baseline full census not run because backup prerequisite failed. Required key persistent-source presence checks were false; precise Server exception is not independently captured. See Controlled_Demo_Pilot_Backup_Remediation_2026-10-01.md for preserved failure, qualified root-cause evidence, authority and concrete remediation.

Source terminology finding only, not pilot generation proof: semantic ProductCode is stored in Product.Sku; no second ProductCode property was found in the inspected Product model. Formula BuildProductCode: normalizedCompanyCode + normalizedCategorySymbol + '-' + normalizedModelCode. TrackingCode: DealerCode + '-' + normalizedSku + '-' + itemSequence formattedD6. SupplierCode is named DealerCode in this implementation; prefix is first two ASCII letters of supplier name and a persisted/high-water sequence. Display names starting DEMO therefore both begin DE; do not force JA/BI prefixes or fabricate expected numeric allocations. SupplierProduct has supplier/product IDs and NextItemSequence initialized1; actual authoritative high-water/allocations remain unexecuted and are not claimedPASS.

No new backup key/config/source/service/registry change, no new release, no recovery ceremony. User question about approved backup-key provisioning and separate recovery custody is pending. Full populationNOTAPPROVED. First blocker: complete approved backup protection/setup and prove fresh canonical backup plus actual disposablePG18restore before any demo records.
