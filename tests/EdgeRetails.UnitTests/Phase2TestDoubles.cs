using System.Collections.Concurrent;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.UnitTests;

internal sealed class Phase2TestDoubles
{
    public FakeCatalogRepository Catalog { get; } = new();
    public FakeInventoryRepository Inventory { get; } = new();
    public FakeInventoryCostAllocator CostAllocator { get; }
    public FakePartyRepository Parties { get; } = new();
    public FakeTraceabilityRepository Traceability { get; } = new();
    public FakePurchasingRepository Purchasing { get; } = new();
    public FakeSalesRepository Sales { get; } = new();
    public FakeQuotationRepository Quotations { get; } = new();
    public FakeCashRepository Cash { get; } = new();
    public FakeSupplierAccountRepository SupplierAccounts { get; } = new();
    public FakeWarrantyRepository Warranty { get; } = new();
    public FakePosDraftRepository PosDrafts { get; } = new();
    public FakeThakaRepository Thaka { get; } = new();
    public FakeExpenseRepository Expenses { get; } = new();
    public FakeOperationLock OperationLock { get; } = new();
    public FakeResourceLock ResourceLock { get; } = new();
    public FakeBusinessAuditWriter Audit { get; } = new();
    public FakeDocumentNumberService Numbers { get; } = new();
    public FakeReceiptSnapshotProvider ReceiptSnapshots { get; } = new();
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
    public FakeTransactionRunner Transactions { get; } = new();
    public FakePermissionAuthorizer Authorization { get; } = new();
    public FakeUnitOfWork UnitOfWork { get; } = new();

    public Phase2TestDoubles()
    {
        CostAllocator = new FakeInventoryCostAllocator(Inventory);
    }
}

internal sealed class FakePurchasingRepository : IPurchasingRepository
{
    public Dictionary<Guid, Purchase> Purchases { get; } = new();
    public List<PurchaseItem> PurchaseItems { get; } = new();
    public List<PurchaseItemUnit> PurchaseItemUnits { get; } = new();
    public Dictionary<Guid, PurchaseReturn> PurchaseReturns { get; } = new();
    public List<PurchaseReturnItem> PurchaseReturnItems { get; } = new();
    public List<PurchaseReturnItemUnit> PurchaseReturnItemUnits { get; } = new();
    public Dictionary<Guid, PurchaseVoid> PurchaseVoids { get; } = new();

    public Task<Purchase?> GetPurchaseByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Purchases.Values.FirstOrDefault(p => p.ClientOperationId == clientOperationId));

    public Task<Purchase?> GetPurchaseForUpdateAsync(Guid purchaseId, CancellationToken cancellationToken) =>
        Task.FromResult(Purchases.TryGetValue(purchaseId, out var p) ? p : null);

    public Task<Purchase?> GetPurchaseBySupplierInvoiceAsync(Guid supplierId, string normalizedSupplierInvoiceNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Purchases.Values.FirstOrDefault(p =>
            p.SupplierId == supplierId &&
            string.Equals(p.SupplierInvoiceNumber, normalizedSupplierInvoiceNumber, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<PurchaseItem>> GetPurchaseItemsAsync(Guid purchaseId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PurchaseItem>>(PurchaseItems.Where(i => i.PurchaseId == purchaseId).ToList());

    public Task<PurchaseItem?> GetPurchaseItemForUpdateAsync(Guid purchaseItemId, CancellationToken cancellationToken) =>
        Task.FromResult(PurchaseItems.FirstOrDefault(i => i.Id == purchaseItemId));

    public Task<IReadOnlyList<PurchaseItemUnit>> GetPurchaseItemUnitsAsync(Guid purchaseItemId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PurchaseItemUnit>>(PurchaseItemUnits.Where(u => u.PurchaseItemId == purchaseItemId).ToList());

    public Task<PurchaseReturn?> GetReturnByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(PurchaseReturns.Values.FirstOrDefault(r => r.ClientOperationId == clientOperationId));

    public Task<decimal> GetReturnedBaseQuantityAsync(Guid purchaseItemId, CancellationToken cancellationToken) =>
        Task.FromResult(PurchaseReturnItems.Where(r => r.PurchaseItemId == purchaseItemId).Sum(r => r.BaseQuantity));

    public Task<bool> HasCompletedReturnAsync(Guid purchaseId, CancellationToken cancellationToken) =>
        Task.FromResult(PurchaseReturns.Values.Any(r => r.PurchaseId == purchaseId));

    public Task<PurchaseVoid?> GetVoidByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(PurchaseVoids.Values.FirstOrDefault(v => v.ClientOperationId == clientOperationId));

    public Task<bool> HasVoidAsync(Guid purchaseId, CancellationToken cancellationToken) =>
        Task.FromResult(PurchaseVoids.Values.Any(v => v.PurchaseId == purchaseId));

    public void AddPurchase(Purchase purchase) => Purchases[purchase.Id] = purchase;
    public void AddPurchaseItem(PurchaseItem item) => PurchaseItems.Add(item);
    public void AddPurchaseItemUnit(PurchaseItemUnit itemUnit) => PurchaseItemUnits.Add(itemUnit);
    public void AddReturn(PurchaseReturn purchaseReturn) => PurchaseReturns[purchaseReturn.Id] = purchaseReturn;
    public void AddReturnItem(PurchaseReturnItem item) => PurchaseReturnItems.Add(item);
    public void AddReturnItemUnit(PurchaseReturnItemUnit itemUnit) => PurchaseReturnItemUnits.Add(itemUnit);
    public void AddVoid(PurchaseVoid purchaseVoid) => PurchaseVoids[purchaseVoid.Id] = purchaseVoid;
}

internal sealed class FakeSalesRepository : ISalesRepository
{
    public Dictionary<Guid, Sale> Sales { get; } = new();
    public List<SaleItem> SaleItems { get; } = new();
    public List<SalePayment> SalePayments { get; } = new();
    public List<SaleItemUnit> SaleItemUnits { get; } = new();
    public Dictionary<Guid, SaleReturn> SaleReturns { get; } = new();
    public List<SaleReturnItem> SaleReturnItems { get; } = new();
    public List<SaleReturnItemUnit> SaleReturnItemUnits { get; } = new();

    public Task<Sale?> GetSaleByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Sales.Values.FirstOrDefault(s => s.ClientOperationId == clientOperationId));

    public Task<Sale?> GetSaleForUpdateAsync(Guid saleId, CancellationToken cancellationToken) =>
        Task.FromResult(Sales.TryGetValue(saleId, out var s) ? s : null);

    public Task<IReadOnlyList<SaleItem>> GetSaleItemsAsync(Guid saleId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SaleItem>>(SaleItems.Where(i => i.SaleId == saleId).ToList());

    public Task<SaleItem?> GetSaleItemForUpdateAsync(Guid saleItemId, CancellationToken cancellationToken) =>
        Task.FromResult(SaleItems.FirstOrDefault(i => i.Id == saleItemId));

    public Task<IReadOnlyList<SaleItemUnit>> GetSaleItemUnitsAsync(Guid saleItemId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SaleItemUnit>>(SaleItemUnits.Where(u => u.SaleItemId == saleItemId).ToList());

    public Task<SalePayment?> GetSalePaymentAsync(Guid saleId, CancellationToken cancellationToken) =>
        Task.FromResult(SalePayments.FirstOrDefault(p => p.SaleId == saleId));

    public Task<decimal> GetReturnedBaseQuantityAsync(Guid saleItemId, CancellationToken cancellationToken) =>
        Task.FromResult(SaleReturnItems.Where(r => r.SaleItemId == saleItemId).Sum(r => r.BaseQuantity));

    public Task<decimal> GetRefundedAmountAsync(Guid saleItemId, CancellationToken cancellationToken) =>
        Task.FromResult(SaleReturnItems.Where(r => r.SaleItemId == saleItemId).Sum(r => r.RefundAmount));

    public Task<decimal> GetReturnedOriginalCostAmountAsync(Guid saleItemId, CancellationToken cancellationToken) =>
        Task.FromResult(SaleReturnItems.Where(r => r.SaleItemId == saleItemId).Sum(r => r.OriginalCostAmount));

    public Task<IReadOnlySet<Guid>> GetReturnedInventoryUnitIdsAsync(Guid saleItemId, CancellationToken cancellationToken)
    {
        var itemIds = SaleReturnItems.Where(r => r.SaleItemId == saleItemId).Select(r => r.Id).ToHashSet();
        var unitIds = SaleReturnItemUnits.Where(u => itemIds.Contains(u.SaleReturnItemId)).Select(u => u.InventoryUnitId).ToHashSet();
        return Task.FromResult<IReadOnlySet<Guid>>(unitIds);
    }

    public Task<SaleReturn?> GetReturnByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(SaleReturns.Values.FirstOrDefault(r => r.ClientOperationId == clientOperationId));

    public void AddSale(Sale sale) => Sales[sale.Id] = sale;
    public void AddSaleItem(SaleItem item) => SaleItems.Add(item);
    public void AddSalePayment(SalePayment payment) => SalePayments.Add(payment);
    public void AddSaleItemUnit(SaleItemUnit itemUnit) => SaleItemUnits.Add(itemUnit);
    public void AddReturn(SaleReturn saleReturn) => SaleReturns[saleReturn.Id] = saleReturn;
    public void AddReturnItem(SaleReturnItem item) => SaleReturnItems.Add(item);
    public void AddReturnItemUnit(SaleReturnItemUnit itemUnit) => SaleReturnItemUnits.Add(itemUnit);
}

internal sealed class FakeQuotationRepository : IQuotationRepository
{
    public Dictionary<Guid, Quotation> Quotations { get; } = new();
    public List<QuotationItem> Items { get; } = new();
    public List<QuotationOperation> Operations { get; } = new();

    public Task<Quotation?> GetQuotationAsync(Guid quotationId, CancellationToken cancellationToken) =>
        Task.FromResult(Quotations.TryGetValue(quotationId, out var q) ? q : null);

    public Task<Quotation?> GetQuotationForUpdateAsync(Guid quotationId, CancellationToken cancellationToken) =>
        GetQuotationAsync(quotationId, cancellationToken);

    public Task<IReadOnlyList<QuotationItem>> GetItemsAsync(Guid quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<QuotationItem>>(Items.Where(i => i.QuotationId == quotationId).ToList());

    public Task<QuotationOperation?> GetOperationByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Operations.FirstOrDefault(o => o.ClientOperationId == clientOperationId));

    public void AddQuotation(Quotation quotation) => Quotations[quotation.Id] = quotation;
    public void AddItem(QuotationItem item) => Items.Add(item);
    public void RemoveItem(QuotationItem item) => Items.Remove(item);
    public void AddOperation(QuotationOperation operation) => Operations.Add(operation);
}

internal sealed class FakeCashRepository : ICashRepository
{
    public Dictionary<Guid, CashSession> Sessions { get; } = new();
    public List<CashMovement> Movements { get; } = new();

    public Task<CashSession?> GetOpenSessionForUpdateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.Values.FirstOrDefault(s => s.Status == CashSessionStatus.Open));

    public Task<CashSession?> GetSessionForUpdateAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.TryGetValue(sessionId, out var s) ? s : null);

    public Task<IReadOnlyList<CashMovement>> GetMovementsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CashMovement>>(Movements.Where(m => m.CashSessionId == sessionId).ToList());

    public void AddSession(CashSession session) => Sessions[session.Id] = session;
    public void AddMovement(CashMovement movement) => Movements.Add(movement);
}

internal sealed class FakeSupplierAccountRepository : ISupplierAccountRepository
{
    public List<SupplierAccountEntry> Entries { get; } = new();
    public Dictionary<Guid, SupplierPayment> Payments { get; } = new();
    public List<SupplierPaymentReversal> PaymentReversals { get; } = new();
    public Dictionary<Guid, SupplierRefund> Refunds { get; } = new();
    public List<SupplierRefundReversal> RefundReversals { get; } = new();

    public Task<decimal> GetCurrentBalanceAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        var supplierEntries = Entries.Where(e => e.SupplierId == supplierId);
        var balance = supplierEntries.Sum(e =>
            e.Direction == SupplierAccountDirection.IncreasePayable
                ? e.Amount
                : -e.Amount);
        return Task.FromResult(balance);
    }

    public Task<SupplierPayment?> GetPaymentByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Payments.Values.FirstOrDefault(p => p.ClientOperationId == clientOperationId));

    public Task<SupplierPayment?> GetPaymentForUpdateAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(Payments.TryGetValue(paymentId, out var p) ? p : null);

    public Task<SupplierPaymentReversal?> GetPaymentReversalByPaymentAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentReversals.FirstOrDefault(r => r.SupplierPaymentId == paymentId));

    public Task<SupplierRefund?> GetRefundByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Refunds.Values.FirstOrDefault(r => r.ClientOperationId == clientOperationId));

    public Task<SupplierRefund?> GetRefundForUpdateAsync(Guid refundId, CancellationToken cancellationToken) =>
        Task.FromResult(Refunds.TryGetValue(refundId, out var r) ? r : null);

    public Task<SupplierRefundReversal?> GetRefundReversalByRefundAsync(Guid refundId, CancellationToken cancellationToken) =>
        Task.FromResult(RefundReversals.FirstOrDefault(r => r.SupplierRefundId == refundId));

    public Task<bool> HasSourceEntryAsync(SupplierAccountEntryType entryType, string referenceType, Guid referenceId, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.Any(e => e.EntryType == entryType && e.ReferenceType == referenceType && e.ReferenceId == referenceId));

    public void AddEntry(SupplierAccountEntry entry) => Entries.Add(entry);
    public void AddPayment(SupplierPayment payment) => Payments[payment.Id] = payment;
    public void AddPaymentReversal(SupplierPaymentReversal reversal) => PaymentReversals.Add(reversal);
    public void AddRefund(SupplierRefund refund) => Refunds[refund.Id] = refund;
    public void AddRefundReversal(SupplierRefundReversal reversal) => RefundReversals.Add(reversal);
}

internal sealed class FakeWarrantyRepository : IWarrantyRepository
{
    public Dictionary<Guid, WarrantyClaim> Claims { get; } = new();
    public List<WarrantyClaimItem> ClaimItems { get; } = new();
    public List<WarrantyClaimItemUnit> ClaimItemUnits { get; } = new();
    public List<WarrantyClaimEvent> ClaimEvents { get; } = new();
    public List<WarrantyOperation> Operations { get; } = new();
    public Dictionary<Guid, ShopStockWarrantyCase> ShopStockCases { get; } = new();

    public Task<WarrantyClaim?> GetClaimForUpdateAsync(Guid claimId, CancellationToken cancellationToken) =>
        Task.FromResult(Claims.TryGetValue(claimId, out var c) ? c : null);

    public Task<WarrantyClaim?> GetClaimByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Claims.Values.FirstOrDefault(c => c.ClientOperationId == clientOperationId));

    public Task<WarrantyOperation?> GetOperationByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Operations.FirstOrDefault(o => o.ClientOperationId == clientOperationId));

    public Task<IReadOnlyList<WarrantyClaimItem>> GetClaimItemsAsync(Guid claimId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WarrantyClaimItem>>(ClaimItems.Where(i => i.ClaimId == claimId).ToList());

    public Task<WarrantyClaimItem?> GetClaimItemForUpdateAsync(Guid claimItemId, CancellationToken cancellationToken) =>
        Task.FromResult(ClaimItems.FirstOrDefault(i => i.Id == claimItemId));

    public Task<IReadOnlyList<WarrantyClaimItemUnit>> GetClaimItemUnitsAsync(Guid claimItemId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WarrantyClaimItemUnit>>(ClaimItemUnits.Where(u => u.ClaimItemId == claimItemId).ToList());

    public Task<IReadOnlyList<WarrantyClaimItemUnit>> GetClaimUnitsAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var itemIds = ClaimItems.Where(i => i.ClaimId == claimId).Select(i => i.Id).ToHashSet();
        return Task.FromResult<IReadOnlyList<WarrantyClaimItemUnit>>(ClaimItemUnits.Where(u => itemIds.Contains(u.ClaimItemId)).ToList());
    }

    public Task<bool> HasActiveClaimForUnitAsync(Guid inventoryUnitId, CancellationToken cancellationToken)
    {
        var activeItemIds = (from item in ClaimItems
                             join claim in Claims.Values on item.ClaimId equals claim.Id
                             where claim.Status != WarrantyClaimStatus.Cancelled &&
                                   claim.Status != WarrantyClaimStatus.Closed
                             select item.Id).ToHashSet();

        return Task.FromResult(ClaimItemUnits.Any(u => activeItemIds.Contains(u.ClaimItemId) && u.OriginalInventoryUnitId == inventoryUnitId));
    }

    public Task<bool> IsUnitTerminallyResolvedAsync(Guid inventoryUnitId, CancellationToken cancellationToken)
    {
        var resolvedItemIds = (from item in ClaimItems
                               join claim in Claims.Values on item.ClaimId equals claim.Id
                               where claim.Status == WarrantyClaimStatus.Closed
                               select item.Id).ToHashSet();

        return Task.FromResult(ClaimItemUnits.Any(u => resolvedItemIds.Contains(u.ClaimItemId) && u.OriginalInventoryUnitId == inventoryUnitId));
    }

    public Task<decimal> GetActiveClaimedQuantityAsync(Guid saleItemId, CancellationToken cancellationToken)
    {
        var activeClaims = Claims.Values
            .Where(c => c.Status != WarrantyClaimStatus.Cancelled &&
                        c.Status != WarrantyClaimStatus.Closed)
            .Select(c => c.Id)
            .ToHashSet();

        var qty = ClaimItems
            .Where(i => i.OriginalSaleItemId == saleItemId && activeClaims.Contains(i.ClaimId))
            .Sum(i => i.Quantity);

        return Task.FromResult(qty);
    }

    public Task<decimal> GetTerminallyRemovedQuantityAsync(Guid saleItemId, CancellationToken cancellationToken)
    {
        var closedClaims = Claims.Values
            .Where(c => c.Status == WarrantyClaimStatus.Closed)
            .Select(c => c.Id)
            .ToHashSet();

        var qty = ClaimItems
            .Where(i => i.OriginalSaleItemId == saleItemId && closedClaims.Contains(i.ClaimId))
            .Sum(i => i.Quantity);

        return Task.FromResult(qty);
    }

    public Task<ShopStockWarrantyCase?> GetShopStockCaseAsync(Guid caseId, CancellationToken cancellationToken) =>
        Task.FromResult(ShopStockCases.TryGetValue(caseId, out var c) ? c : null);

    public Task<ShopStockWarrantyCase?> GetShopStockCaseForUpdateAsync(Guid caseId, CancellationToken cancellationToken) =>
        GetShopStockCaseAsync(caseId, cancellationToken);

    public void AddClaim(WarrantyClaim claim) => Claims[claim.Id] = claim;
    public void AddClaimItem(WarrantyClaimItem item) => ClaimItems.Add(item);
    public void AddClaimItemUnit(WarrantyClaimItemUnit itemUnit) => ClaimItemUnits.Add(itemUnit);
    public void AddClaimEvent(WarrantyClaimEvent claimEvent) => ClaimEvents.Add(claimEvent);
    public void AddOperation(WarrantyOperation operation) => Operations.Add(operation);
    public void AddShopStockCase(ShopStockWarrantyCase warrantyCase) => ShopStockCases[warrantyCase.Id] = warrantyCase;
}

internal sealed class FakePosDraftRepository : IPosDraftRepository
{
    public Dictionary<Guid, PosDraft> Drafts { get; } = new();
    public List<PosDraftItem> Items { get; } = new();

    public Task<PosDraft?> GetForUpdateAsync(Guid draftId, CancellationToken cancellationToken) =>
        Task.FromResult(Drafts.TryGetValue(draftId, out var d) ? d : null);

    public Task<IReadOnlyList<PosDraftItem>> GetItemsAsync(Guid draftId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PosDraftItem>>(Items.Where(i => i.DraftId == draftId).ToList());

    public Task<PosDraft?> GetByDraftNumberAsync(string draftNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Drafts.Values.FirstOrDefault(d => string.Equals(d.DraftNumber, draftNumber, StringComparison.OrdinalIgnoreCase)));

    public void AddDraft(PosDraft draft) => Drafts[draft.Id] = draft;
    public void AddItem(PosDraftItem item) => Items.Add(item);
    public void RemoveItem(PosDraftItem item) => Items.Remove(item);
}

internal sealed class FakeThakaRepository : IThakaRepository
{
    public Dictionary<Guid, ThakaProject> Projects { get; } = new();
    public List<ThakaMaterialIssue> MaterialIssues { get; } = new();
    public List<ThakaMaterialIssueItem> MaterialIssueItems { get; } = new();
    public List<ThakaMaterialIssueUnit> MaterialIssueUnits { get; } = new();
    public List<ThakaPayment> Payments { get; } = new();
    public List<ThakaSettlement> Settlements { get; } = new();
    public List<ThakaReopening> Reopenings { get; } = new();
    public List<ThakaMaterialReversal> MaterialReversals { get; } = new();
    public List<ThakaPaymentReversal> PaymentReversals { get; } = new();

    public Task<ThakaProject?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult(Projects.TryGetValue(projectId, out var p) ? p : null);

    public Task<ThakaProject?> GetProjectForUpdateAsync(Guid projectId, CancellationToken cancellationToken) =>
        GetProjectAsync(projectId, cancellationToken);

    public Task<ThakaMaterialIssue?> GetIssueByOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(MaterialIssues.FirstOrDefault(i => i.ClientOperationId == clientOperationId));

    public Task<ThakaMaterialIssue?> GetIssueForUpdateAsync(Guid materialIssueId, CancellationToken cancellationToken) =>
        Task.FromResult(MaterialIssues.FirstOrDefault(i => i.Id == materialIssueId));

    public Task<ThakaPayment?> GetPaymentByOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Payments.FirstOrDefault(p => p.ClientOperationId == clientOperationId));

    public Task<ThakaPayment?> GetPaymentForUpdateAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(Payments.FirstOrDefault(p => p.Id == paymentId));

    public Task<ThakaSettlement?> GetSettlementByOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Settlements.FirstOrDefault(s => s.ClientOperationId == clientOperationId));

    public Task<ThakaMaterialReversal?> GetMaterialReversalByOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(MaterialReversals.FirstOrDefault(r => r.ClientOperationId == clientOperationId));

    public Task<ThakaMaterialReversal?> GetMaterialReversalByIssueAsync(Guid materialIssueId, CancellationToken cancellationToken) =>
        Task.FromResult(MaterialReversals.FirstOrDefault(r => r.MaterialIssueId == materialIssueId));

    public Task<ThakaPaymentReversal?> GetPaymentReversalByOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentReversals.FirstOrDefault(r => r.ClientOperationId == clientOperationId));

    public Task<ThakaPaymentReversal?> GetPaymentReversalByPaymentAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentReversals.FirstOrDefault(r => r.PaymentId == paymentId));

    public Task<IReadOnlyList<ThakaMaterialIssueItem>> GetIssueItemsAsync(Guid materialIssueId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ThakaMaterialIssueItem>>(MaterialIssueItems.Where(i => i.MaterialIssueId == materialIssueId).ToList());

    public Task<IReadOnlyList<ThakaMaterialIssueUnit>> GetIssueUnitsAsync(Guid materialIssueItemId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ThakaMaterialIssueUnit>>(MaterialIssueUnits.Where(u => u.MaterialIssueItemId == materialIssueItemId).ToList());

    public Task<decimal> GetGrossMaterialChargesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var reversedIssueIds = MaterialReversals.Where(r => r.ProjectId == projectId).Select(r => r.MaterialIssueId).ToHashSet();
        var activeIssueIds = MaterialIssues.Where(i => i.ProjectId == projectId && !reversedIssueIds.Contains(i.Id)).Select(i => i.Id).ToHashSet();
        return Task.FromResult(MaterialIssueItems.Where(i => activeIssueIds.Contains(i.MaterialIssueId)).Sum(i => i.LineCharge));
    }

    public Task<decimal> GetPaymentsCollectedAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var reversedPaymentIds = PaymentReversals.Where(r => r.ProjectId == projectId).Select(r => r.PaymentId).ToHashSet();
        return Task.FromResult(Payments.Where(p => p.ProjectId == projectId && !reversedPaymentIds.Contains(p.Id)).Sum(p => p.Amount));
    }

    public Task<decimal> GetSettlementDiscountsAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult(Settlements.Where(s => s.ProjectId == projectId).Sum(s => s.SettlementDiscount));

    public Task<ThakaSettlement?> GetLatestSettlementAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult(Settlements.Where(s => s.ProjectId == projectId).OrderByDescending(s => s.SettledAt).FirstOrDefault());

    public void AddProject(ThakaProject project) => Projects[project.Id] = project;
    public void AddMaterialIssue(ThakaMaterialIssue issue) => MaterialIssues.Add(issue);
    public void AddMaterialIssueItem(ThakaMaterialIssueItem item) => MaterialIssueItems.Add(item);
    public void AddMaterialIssueUnit(ThakaMaterialIssueUnit unit) => MaterialIssueUnits.Add(unit);
    public void AddPayment(ThakaPayment payment) => Payments.Add(payment);
    public void AddSettlement(ThakaSettlement settlement) => Settlements.Add(settlement);
    public void AddReopening(ThakaReopening reopening) => Reopenings.Add(reopening);
    public void AddMaterialReversal(ThakaMaterialReversal reversal) => MaterialReversals.Add(reversal);
    public void AddPaymentReversal(ThakaPaymentReversal reversal) => PaymentReversals.Add(reversal);
}

internal sealed class FakeExpenseRepository : IExpenseRepository
{
    public Dictionary<Guid, Expense> Expenses { get; } = new();
    public Dictionary<Guid, ExpenseCategory> Categories { get; } = new();
    public Dictionary<Guid, ExpenseSubcategory> Subcategories { get; } = new();

    public Task<Expense?> GetByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(Expenses.Values.FirstOrDefault(e => e.ClientOperationId == clientOperationId));

    public Task<Expense?> GetForUpdateAsync(Guid expenseId, CancellationToken cancellationToken) =>
        Task.FromResult(Expenses.TryGetValue(expenseId, out var e) ? e : null);

    public Task<ExpenseCategory?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(Categories.TryGetValue(categoryId, out var c) ? c : null);

    public Task<ExpenseSubcategory?> GetSubcategoryAsync(Guid subcategoryId, CancellationToken cancellationToken) =>
        Task.FromResult(Subcategories.TryGetValue(subcategoryId, out var s) ? s : null);

    public Task<IReadOnlyList<ExpenseCategory>> GetCategoriesAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExpenseCategory>>(Categories.Values.ToList());

    public Task<IReadOnlyList<ExpenseSubcategory>> GetSubcategoriesAsync(Guid? categoryId, bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExpenseSubcategory>>(Subcategories.Values.Where(s => categoryId == null || s.CategoryId == categoryId).ToList());

    public void AddCategory(ExpenseCategory category) => Categories[category.Id] = category;
    public void AddSubcategory(ExpenseSubcategory subcategory) => Subcategories[subcategory.Id] = subcategory;
    public void AddExpense(Expense expense) => Expenses[expense.Id] = expense;
}

internal sealed class FakeOperationLock : IOperationLock
{
    public ConcurrentDictionary<Guid, byte> LockedOperations { get; } = new();

    public Task AcquireAsync(Guid clientOperationId, CancellationToken cancellationToken)
    {
        LockedOperations.TryAdd(clientOperationId, 1);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDocumentNumberService : IDocumentNumberService
{
    private readonly ConcurrentDictionary<string, int> _sequences = new();

    public Task<string> NextAsync(string series, CancellationToken cancellationToken)
    {
        var seq = _sequences.AddOrUpdate(series, 1, (_, current) => current + 1);
        return Task.FromResult($"{series}-{seq:D6}");
    }
}

internal sealed class FakeReceiptSnapshotProvider : IReceiptSnapshotProvider
{
    public Task<string> CaptureAsync(CancellationToken cancellationToken) =>
        Task.FromResult("{\"ReceiptConfigVersion\":1,\"Header\":\"Edge Retails\",\"Footer\":\"Thank You!\"}");
}
