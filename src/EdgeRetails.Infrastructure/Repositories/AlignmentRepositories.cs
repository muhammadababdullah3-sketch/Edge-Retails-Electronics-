using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class TraceabilityRepository : ITraceabilityRepository
{
    private readonly EdgeRetailsDbContext _db;

    public TraceabilityRepository(EdgeRetailsDbContext db) => _db = db;

    public Task<SupplierProduct?> GetSupplierProductAsync(
        Guid supplierId,
        Guid productId,
        CancellationToken cancellationToken) =>
        _db.SupplierProducts.SingleOrDefaultAsync(
            x => x.SupplierId == supplierId && x.ProductId == productId,
            cancellationToken);

    public Task<SupplierProduct?> GetSupplierProductForUpdateAsync(
        Guid supplierId,
        Guid productId,
        CancellationToken cancellationToken) =>
        _db.SupplierProducts
            .FromSqlInterpolated(
                $"SELECT * FROM catalog.supplier_products WHERE supplier_id = {supplierId} AND product_id = {productId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<SupplierProduct?> GetSupplierProductByIdForUpdateAsync(
        Guid supplierProductId,
        CancellationToken cancellationToken) =>
        _db.SupplierProducts
            .FromSqlInterpolated(
                $"SELECT * FROM catalog.supplier_products WHERE id = {supplierProductId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<SupplierCodeSequence?> GetSupplierCodeSequenceForUpdateAsync(
        string prefix,
        CancellationToken cancellationToken) =>
        _db.SupplierCodeSequences
            .FromSqlInterpolated(
                $"SELECT * FROM system.supplier_code_sequences WHERE prefix = {prefix} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public void AddSupplierProduct(SupplierProduct supplierProduct) =>
        _db.SupplierProducts.Add(supplierProduct);

    public void AddSupplierCodeSequence(SupplierCodeSequence sequence) =>
        _db.SupplierCodeSequences.Add(sequence);
}

public sealed class SupplierAccountRepository : ISupplierAccountRepository
{
    private readonly EdgeRetailsDbContext _db;

    public SupplierAccountRepository(EdgeRetailsDbContext db) => _db = db;

    public async Task<decimal> GetCurrentBalanceAsync(
        Guid supplierId,
        CancellationToken cancellationToken)
    {
        var balance = await _db.SupplierAccountEntries
            .Where(x => x.SupplierId == supplierId)
            .SumAsync(
                x => x.Direction == SupplierAccountDirection.IncreasePayable
                    ? x.Amount
                    : -x.Amount,
                cancellationToken);

        return decimal.Round(balance, 2, MidpointRounding.AwayFromZero);
    }

    public Task<SupplierPayment?> GetPaymentByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.SupplierPayments.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<SupplierPayment?> GetPaymentForUpdateAsync(
        Guid paymentId,
        CancellationToken cancellationToken) =>
        _db.SupplierPayments
            .FromSqlInterpolated(
                $"SELECT * FROM finance.supplier_payments WHERE id = {paymentId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<SupplierPaymentReversal?> GetPaymentReversalByPaymentAsync(
        Guid paymentId,
        CancellationToken cancellationToken) =>
        _db.SupplierPaymentReversals.SingleOrDefaultAsync(
            x => x.SupplierPaymentId == paymentId,
            cancellationToken);

    public Task<SupplierRefund?> GetRefundByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.SupplierRefunds.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<SupplierRefund?> GetRefundForUpdateAsync(
        Guid refundId,
        CancellationToken cancellationToken) =>
        _db.SupplierRefunds
            .FromSqlInterpolated(
                $"SELECT * FROM finance.supplier_refunds WHERE id = {refundId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<SupplierRefundReversal?> GetRefundReversalByRefundAsync(
        Guid refundId,
        CancellationToken cancellationToken) =>
        _db.SupplierRefundReversals.SingleOrDefaultAsync(
            x => x.SupplierRefundId == refundId,
            cancellationToken);

    public Task<bool> HasSourceEntryAsync(
        SupplierAccountEntryType entryType,
        string referenceType,
        Guid referenceId,
        CancellationToken cancellationToken) =>
        _db.SupplierAccountEntries.AnyAsync(
            x => x.EntryType == entryType &&
                 x.ReferenceType == referenceType &&
                 x.ReferenceId == referenceId,
            cancellationToken);

    public void AddEntry(SupplierAccountEntry entry) => _db.SupplierAccountEntries.Add(entry);
    public void AddPayment(SupplierPayment payment) => _db.SupplierPayments.Add(payment);
    public void AddPaymentReversal(SupplierPaymentReversal reversal) => _db.SupplierPaymentReversals.Add(reversal);
    public void AddRefund(SupplierRefund refund) => _db.SupplierRefunds.Add(refund);
    public void AddRefundReversal(SupplierRefundReversal reversal) => _db.SupplierRefundReversals.Add(reversal);
}

public sealed class PosDraftRepository : IPosDraftRepository
{
    private readonly EdgeRetailsDbContext _db;

    public PosDraftRepository(EdgeRetailsDbContext db) => _db = db;

    public Task<PosDraft?> GetForUpdateAsync(Guid draftId, CancellationToken cancellationToken) =>
        _db.PosDrafts
            .FromSqlInterpolated($"SELECT * FROM sales.pos_drafts WHERE id = {draftId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PosDraftItem>> GetItemsAsync(
        Guid draftId,
        CancellationToken cancellationToken) =>
        await _db.PosDraftItems
            .Where(x => x.DraftId == draftId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public Task<PosDraft?> GetByDraftNumberAsync(
        string draftNumber,
        CancellationToken cancellationToken) =>
        _db.PosDrafts.SingleOrDefaultAsync(
            x => x.DraftNumber == draftNumber,
            cancellationToken);

    public void AddDraft(PosDraft draft) => _db.PosDrafts.Add(draft);
    public void AddItem(PosDraftItem item) => _db.PosDraftItems.Add(item);
    public void RemoveItem(PosDraftItem item) => _db.PosDraftItems.Remove(item);
}
