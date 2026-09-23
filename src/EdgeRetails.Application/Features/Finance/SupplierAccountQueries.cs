using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Application.Features.Finance;

public sealed record SupplierAccountSummaryDto(
    Guid SupplierId,
    decimal CurrentBalance,
    decimal GrossPurchased,
    decimal PurchaseReturnCredits,
    decimal PurchaseVoidReversals,
    decimal WarrantyCredits,
    decimal NetPurchased,
    decimal NetPaidToSupplier,
    decimal OutstandingPayable,
    decimal SupplierCreditOrAdvance,
    decimal NetSupplierRefundsReceived);

public sealed record SupplierKhataEntryDto(
    Guid EntryId,
    string EntryNumber,
    SupplierAccountEntryType EntryType,
    SupplierAccountDirection Direction,
    decimal Amount,
    decimal SignedAmount,
    decimal RunningBalance,
    string ReferenceType,
    Guid? ReferenceId,
    DateTimeOffset OccurredAt,
    Guid ActorId,
    Guid? ClientOperationId,
    string? Note);

public sealed record SupplierPaymentReadDto(
    Guid PaymentId,
    string PaymentNumber,
    decimal Amount,
    SupplierPaymentPurpose Purpose,
    SupplierSettlementMethod Method,
    DateTimeOffset PaidAt,
    SupplierSettlementStatus Status,
    string? ExternalReference,
    string? Note);

public sealed record SupplierRefundReadDto(
    Guid RefundId,
    string RefundNumber,
    decimal Amount,
    SupplierSettlementMethod Method,
    DateTimeOffset ReceivedAt,
    SupplierSettlementStatus Status,
    string? ExternalReference,
    string? ReferenceType,
    Guid? ReferenceId,
    string? Note);

public sealed record SupplierProductContextDto(
    Guid SupplierProductId,
    Guid ProductId,
    string ProductName,
    string? Sku,
    bool IsActive);

public sealed record SupplierWarrantySummaryDto(
    int CustomerClaimCount,
    int ShopStockCaseCount,
    int TrackedUnitCount,
    int CurrentlyWithSupplier,
    int ReadyOrReturned,
    int ClosedCount);

public sealed record SupplierAccountWorkspaceDto(
    SupplierAccountSummaryDto Summary,
    IReadOnlyList<SupplierKhataEntryDto> Statement,
    IReadOnlyList<SupplierPaymentReadDto> Payments,
    IReadOnlyList<SupplierRefundReadDto> Refunds,
    IReadOnlyList<SupplierProductContextDto> SuppliedProducts,
    SupplierWarrantySummaryDto Warranty);

public interface ISupplierAccountReadService
{
    Task<SupplierAccountWorkspaceDto> GetWorkspaceAsync(
        Guid supplierId,
        int pageSize = 200,
        DateTimeOffset? beforeOccurredAt = null,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeEntryId = null,
        CancellationToken cancellationToken = default);
}
