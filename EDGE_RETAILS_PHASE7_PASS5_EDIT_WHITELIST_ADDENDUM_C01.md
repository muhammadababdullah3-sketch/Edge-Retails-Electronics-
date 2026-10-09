# Pass5 C01 exact whitelist addendum — before additional edits

Original whitelist remains immutable SHA03954F1431D36826DA8EBB7D75E02F8DCC13A308DB836C706F28F0DB9BAF6808. New controlled continuation authority: artifacts/phase7-pass5/controlled-continuation-authority-20261005.md; supplied attachmentfb290df5-7557-4e04-a2ba-2cd49e1be618. Lead owns this scope sequentially. No prior focused work rerun without dependency invalidation.

|Additional exact path|Scope|Classification/authority|
|---|---|---|
|src/EdgeRetails.Domain/Finance/SupplierAccountModels.cs|Preserve CashDrawer1/External2; add Bank3/Other4 to existing SupplierSettlementMethod for distinctly persisted canonical refund methods|Continuation §7 explicitly requires Bank/Other. Existing integer Method property has no enum CHECK or Npgsql enum mapping; no schema/migration/configuration/snapshot edit|
|tests/EdgeRetails.IntegrationTests/Phase7Pass5PurchaseReturnPostgresTests.cs (new)|C01 paid/partial/unpaid, Cash/Bank/Other, provenance, permissions, caps, replay/conflict, provisional SQL rollback|NEW_COVERAGE on owned PostgreSQL18/Npgsql only|
|tests/EdgeRetails.UnitTests/Phase7Pass5PurchaseReturnTests.cs (new)|Canonical refund validation and focused C01 integration support|NEW_COVERAGE|

Existing whitelisted SupplierAccountHandlers/PurchaseReturnHandler and Pass1 test paths remain the exact implementation/alignment owners. Existing constructor compatibility may be retained with a fail-closed missing canonical dependency; production DI must supply it. No parallel agent owns these files.

Direct Pass1 C01 alignment planned before edits: AUTHORIZED_ASSERTION_ALIGNMENT per Resolution01 and continuation §11. Cash-return fixtures become genuinely fully paid by a real canonical External payment before return; preserve quantity/value/cash amount/replay/concurrency/real SQL rollback. Expect final supplier0 instead of unpaid liability10000/1000; assert the actual payment separately. Replace anonymous refund ledger/cash references with independently queryable SupplierRefund and canonical references. Strengthen snapshot to include payment/refund facts. Existing invalid-stock/over-return tests and concurrency scheduling remain intact. Existing no-session fixture must be paid so it tests drawer unavailability rather than nonexistent refund credit. No Skip or concurrency change.

Shared canonical posting core must not SaveChanges or create an independent transaction. Standalone handler supplies its transaction/save; PurchaseReturn supplies its existing transaction/save and authoritative pre-return balance less pending return credit. Local return replay uses existing outcome fingerprint; no Phase12 global redesign.
