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
