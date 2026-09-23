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
    bool IsSerialized);

public interface IPosCatalogReadService
{
    Task<IReadOnlyList<PosCatalogProductDto>> GetSellableCatalogAsync(
        string? search,
        int pageSize,
        CancellationToken cancellationToken);
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
}
