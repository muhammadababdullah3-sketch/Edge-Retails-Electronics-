namespace EdgeRetails.Application.Features.Sales;

public sealed record PosCatalogProductDto(
    Guid ProductId,
    Guid ProductUnitId,
    string Name,
    string? Sku,
    string Category,
    string UnitSymbol,
    decimal SellableStock,
    decimal UnitPrice,
    decimal ReferenceCost,
    bool IsSerialized,
    string? Brand = null);

public interface IPosCatalogReadService
{
    Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(
        string? search,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(
        string? search,
        string? category,
        string? brand,
        int pageSize,
        string? afterName,
        Guid? afterId,
        CancellationToken cancellationToken) =>
        GetSellableCatalogAsync(search, pageSize, cancellationToken);
}

public sealed class GetPosCatalogHandler
{
    private readonly IPosCatalogReadService _reads;

    public GetPosCatalogHandler(IPosCatalogReadService reads)
    {
        _reads = reads;
    }

    public Task<IReadOnlyList<PosCatalogProductDto>> HandleAsync(
        string? search,
        int pageSize,
        CancellationToken cancellationToken) =>
        _reads.GetSellableCatalogAsync(search, pageSize, cancellationToken);

    public Task<IReadOnlyList<PosCatalogProductDto>> HandleAsync(
        string? search,
        string? category,
        string? brand,
        int pageSize,
        string? afterName,
        Guid? afterId,
        CancellationToken cancellationToken) =>
        _reads.GetSellableCatalogAsync(search, category, brand, pageSize, afterName, afterId, cancellationToken);
}
