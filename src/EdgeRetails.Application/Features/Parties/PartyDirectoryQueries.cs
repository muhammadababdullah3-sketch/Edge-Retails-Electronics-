namespace EdgeRetails.Application.Features.Parties;

public sealed record CustomerDirectoryDto(
    Guid CustomerId,
    string Name,
    string? Phone,
    string? Address,
    string? Notes,
    decimal NetSales,
    DateTimeOffset? LastSaleAt,
    string? ActiveThakaProject);

public sealed record SupplierDirectoryDto(
    Guid SupplierId,
    string Name,
    string? Phone,
    string? City,
    string? Address,
    string? Notes,
    decimal NetPurchases,
    DateOnly? LastPurchaseDate);

public interface IPartyDirectoryReadService
{
    Task<IReadOnlyList<CustomerDirectoryDto>> GetCustomersAsync(
        string? search,
        int pageSize = 100,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierDirectoryDto>> GetSuppliersAsync(
        string? search,
        int pageSize = 100,
        CancellationToken cancellationToken = default);
}
