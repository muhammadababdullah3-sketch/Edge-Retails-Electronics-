using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Application.Features.Warranty;

public enum WarrantyWorkKind
{
    CustomerClaim = 1,
    ShopStock = 2
}

public sealed record WarrantyDashboardSummaryDto(
    int OpenCount,
    int WithSupplierCount,
    int ReadyCount,
    int ClosedCount,
    int CustomerClaimCount,
    int ShopStockCaseCount,
    int TrackedUnitCount);

public sealed record WarrantyQueueRowDto(
    WarrantyWorkKind Kind,
    Guid WorkId,
    string Number,
    string ProductName,
    string CustomerOrSource,
    string SupplierName,
    DateTimeOffset CreatedAt,
    string Status,
    string Custody,
    string Resolution,
    string? TrackingCode,
    string? SerialNumber,
    string? Imei1,
    string? Imei2);

public sealed record WarrantyEventDto(
    Guid EventId,
    Guid ClaimId,
    WarrantyClaimStatus Status,
    WarrantyCustody Custody,
    string EventType,
    string? Note,
    Guid ActorId,
    DateTimeOffset OccurredAt);

public sealed record WarrantyDashboardDto(
    WarrantyDashboardSummaryDto Summary,
    IReadOnlyList<WarrantyQueueRowDto> Rows);

public sealed record WarrantyClaimIntakeUnitDto(
    Guid InventoryUnitId,
    string? TrackingCode,
    string? SerialNumber,
    string? Imei1,
    string? Imei2,
    bool Eligible,
    string? EligibilityCode);

public sealed record WarrantyClaimIntakeRowDto(
    Guid SaleId,
    string InvoiceNumber,
    Guid? CustomerId,
    string CustomerName,
    Guid SaleItemId,
    Guid ProductId,
    string ProductName,
    decimal BaseQuantity,
    DateOnly? WarrantyValidUntil,
    bool Eligible,
    string? EligibilityCode,
    IReadOnlyList<WarrantyClaimIntakeUnitDto> Units);

public interface IWarrantyReadService
{
    Task<WarrantyDashboardDto> GetDashboardAsync(
        string? search,
        int pageSize = 100,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeWorkId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WarrantyEventDto>> GetClaimTimelineAsync(
        Guid claimId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WarrantyClaimIntakeRowDto>> SearchClaimIntakeAsync(
        string search,
        CancellationToken cancellationToken);
}
