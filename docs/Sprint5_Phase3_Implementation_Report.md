# Sprint 5 Phase 3 — Settings Implementation Report

**Date:** 2026-09-20
**Status:** COMPLETE
**Primary Screen:** 17 — Settings
**Manual visual QA:** deferred
**Backend / worker attachment:** deferred
**Git:** untouched

## Live Figma authority

Implemented against these live Settings states:
- Shop Settings: `14:10297`
- Receipt Settings: `14:10500`
- Users & Access: `14:10966`
- Categories & Units: `14:11379`
- Add Category: `14:11752`
- Add Unit: `14:12149`
- Backup Settings: `14:12554`
- Restore Backup: `14:12812`
- License Settings: `14:13141`
- Import New License: `14:13347`
- Appearance Settings: `14:13589`
- Database Settings: `14:13782`
- Edit User: `15:17474`

## Screen architecture

Settings remains exactly one primary screen. Internal navigation switches eight sections:
1. Shop
2. Receipt
3. Users & Access
4. Categories & Units
5. Backup
6. License
7. Appearance
8. Database

Supporting edit/confirmation workflows remain dialogs and do not create extra primary screens.

## Shop and receipt

Shop supports Shop Name, Owner Name, Phone, Address, logo selection and Save Changes.

Receipt supports printer, paper size, header/footer text, Show Customer, Show Cashier and Auto Print.

The frontend state is temporary and backend-agnostic.

## Users & Access

User list supports Add/Edit, Owner/Manager/Cashier roles and Active/Inactive state.

Safety rules:
- duplicate user names are rejected;
- at least one active Owner must always remain;
- PIN input is masked with a PasswordBox;
- PIN is transient in the frontend shell and is never persisted in `DemoSettingsState`;
- PIN length/format is not invented because the backend architecture does not yet lock a fixed digit count.

The permission matrix matches the Figma/canonical role contract.

## Categories & Units

Categories and units support Add, Edit, Deactivate and Reactivate.

There is no permanent delete path.

Referential-integrity hardening:
- category rename migrates existing product category references;
- unit rename migrates existing product unit references;
- legacy/plural `Pcs` values are recognized as the `Piece / pc` unit so existing products are not orphaned.

## Backup

Backup/Restore UI follows the Figma structure, including history and restore confirmation.

Because the worker/backend is intentionally not attached:
- health/status cards are labeled Integration Pending or Preview Data;
- history rows are explicitly Sample/Cloud Preview;
- Backup Now does not claim a successful real backup;
- Restore captures confirmation only and explicitly says backend restore is not attached.

## License

License information remains a frontend preview.

Import flow:
- file picker for `.lic` / `.key`;
- path is read-only after selection;
- selected file must still exist;
- extension is validated;
- activation is not falsely claimed because licensing backend verification is deferred.

## Appearance

Light / Dark / System uses the existing `IThemeService` and applies the actual runtime theme.

The Settings selection is synchronized back to `ThemeService.CurrentTheme` when theme changes.

## Database diagnostics

The section preserves the Figma diagnostic structure while remaining honest about integration state.

It does not expose:
- PostgreSQL host
- port
- password
- connection string

Until backend attachment, database and worker status are labeled Integration Pending / frontend shell rather than Connected or Running.

## Automated verification before forensic closure

- Debug build: PASS, 0 warnings, 0 errors.
- Debug tests after Phase 3: 82 unit + 1 integration PASS.
- Phase 3 runtime smoke: PASS.
- Last-active-Owner guard: PASS.
- Duplicate user guard: PASS.
- Duplicate category guard: PASS.
- Category/unit deactivate-reactivate: PASS.
- Shop/Receipt state save: PASS.

Further forensic fixes and final authoritative gates are recorded in `Sprint5_Forensic_Audit_Report.md`.
