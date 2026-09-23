using EdgeRetails.Domain.Thaka;

namespace EdgeRetails.Application.Features.Thaka;

public sealed record ThakaProjectPageQuery(
    string? Search = null,
    ThakaProjectStatus? Status = null,
    int PageSize = 200,
    DateOnly? BeforeStartedOn = null,
    Guid? BeforeProjectId = null);

public sealed record ThakaProjectPageDto(
    IReadOnlyList<ThakaProjectSummaryDto> Items,
    DateOnly? NextStartedOn,
    Guid? NextProjectId,
    bool HasMore,
    int TotalActiveCount,
    decimal TotalActiveMaterialValue,
    decimal TotalActiveBalance);

public sealed record ThakaProjectSummaryDto(
    Guid ProjectId,
    string ProjectNumber,
    Guid CustomerId,
    string ProjectName,
    string CustomerName,
    string? CustomerPhone,
    string? SiteAddress,
    DateOnly StartedOn,
    ThakaProjectStatus Status,
    decimal MaterialValue,
    decimal Paid,
    decimal SettlementDiscount,
    decimal Balance,
    string? Note); public sealed record ThakaMaterialLedgerRowDto(
    Guid MaterialIssueId,
    string ChallanNumber,
    DateTimeOffset IssuedAt,
    Guid ProductId,
    string ProductName,
    Guid ProductUnitId,
    string UnitSymbol,
    decimal EnteredQuantity,
    decimal UnitCharge,
    decimal LineCharge,
    bool IsReversed);

public sealed record ThakaPaymentLedgerRowDto(
    Guid PaymentId,
    string ReceiptNumber,
    DateTimeOffset RecordedAt,
    ThakaPaymentMethod PaymentMethod,
    decimal Amount, Guid RecordedBy,
    string? Reference,
    bool IsReversed);

public sealed record ThakaProjectDetailDto(
    ThakaProjectSummaryDto Project,
    IReadOnlyList<ThakaMaterialLedgerRowDto> Materials,
    IReadOnlyList<ThakaPaymentLedgerRowDto> Payments);

public sealed record ThakaCatalogItemDto(
    Guid ProductId,
    Guid ProductUnitId,
    string Name,
    string? Sku,
    string UnitSymbol,
    decimal SellableStock,
    decimal UnitCharge,
    bool IsSerialized); public interface IThakaReadService
{
    Task<IReadOnlyList<ThakaProjectSummaryDto>> GetProjectsAsync(
        CancellationToken cancellationToken);

    Task<ThakaProjectPageDto> GetProjectsPageAsync(
        ThakaProjectPageQuery query,
        CancellationToken cancellationToken);

    Task<ThakaProjectDetailDto?> GetProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ThakaCatalogItemDto>> GetMaterialCatalogAsync(
        CancellationToken cancellationToken);
}
