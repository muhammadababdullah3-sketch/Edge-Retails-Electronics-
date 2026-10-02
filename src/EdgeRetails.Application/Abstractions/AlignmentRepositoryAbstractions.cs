using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Sales;

namespace EdgeRetails.Application.Abstractions;

public interface ITraceabilityRepository
{
    Task<SupplierProduct?> GetSupplierProductAsync(
        Guid supplierId,
        Guid productId,
        CancellationToken cancellationToken);

    Task<SupplierProduct?> GetSupplierProductForUpdateAsync(
        Guid supplierId,
        Guid productId,
        CancellationToken cancellationToken);

    Task<SupplierProduct?> GetSupplierProductByIdForUpdateAsync(
        Guid supplierProductId,
        CancellationToken cancellationToken);

    Task<SupplierCodeSequence?> GetSupplierCodeSequenceForUpdateAsync(
        string prefix,
        CancellationToken cancellationToken);

    void AddSupplierProduct(SupplierProduct supplierProduct);
    void AddSupplierCodeSequence(SupplierCodeSequence sequence);
}

public interface ISupplierAccountRepository
{
    Task<decimal> GetCurrentBalanceAsync(Guid supplierId, CancellationToken cancellationToken);
    Task<SupplierPayment?> GetPaymentByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken);
    Task<SupplierPayment?> GetPaymentForUpdateAsync(Guid paymentId, CancellationToken cancellationToken);
    Task<SupplierPaymentReversal?> GetPaymentReversalByPaymentAsync(Guid paymentId, CancellationToken cancellationToken);
    Task<SupplierRefund?> GetRefundByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken);
    Task<SupplierRefund?> GetRefundForUpdateAsync(Guid refundId, CancellationToken cancellationToken);
    Task<SupplierRefundReversal?> GetRefundReversalByRefundAsync(Guid refundId, CancellationToken cancellationToken);
    Task<bool> HasSourceEntryAsync(
        SupplierAccountEntryType entryType,
        string referenceType,
        Guid referenceId,
        CancellationToken cancellationToken);

    void AddEntry(SupplierAccountEntry entry);
    void AddPayment(SupplierPayment payment);
    void AddPaymentReversal(SupplierPaymentReversal reversal);
    void AddRefund(SupplierRefund refund);
    void AddRefundReversal(SupplierRefundReversal reversal);
}

public interface IPosDraftRepository
{
    Task<PosDraft?> GetForUpdateAsync(Guid draftId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PosDraftItem>> GetItemsAsync(Guid draftId, CancellationToken cancellationToken);
    Task<PosDraft?> GetByDraftNumberAsync(string draftNumber, CancellationToken cancellationToken);
    void AddDraft(PosDraft draft);
    void AddItem(PosDraftItem item);
    void RemoveItem(PosDraftItem item);
}

public interface ISequenceHighWaterService
{
    long GetDealerPrefixHighWater(string prefix);
    void RecordDealerPrefixHighWater(string prefix, long sequence);
    long GetSupplierProductHighWater(Guid supplierId, Guid productId);
    void RecordSupplierProductHighWater(Guid supplierId, Guid productId, long sequence);
}

public sealed class NullSequenceHighWaterService : ISequenceHighWaterService
{
    public static readonly NullSequenceHighWaterService Instance = new();
    public long GetDealerPrefixHighWater(string prefix) => 0;
    public void RecordDealerPrefixHighWater(string prefix, long sequence) { }
    public long GetSupplierProductHighWater(Guid supplierId, Guid productId) => 0;
    public void RecordSupplierProductHighWater(Guid supplierId, Guid productId, long sequence) { }
}
