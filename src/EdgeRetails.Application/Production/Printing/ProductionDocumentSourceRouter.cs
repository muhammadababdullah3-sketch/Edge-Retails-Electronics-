namespace EdgeRetails.Application.Production.Printing;

/// <summary>
/// Routes each canonical production document to exactly one Application-owned query adapter.
/// The live merge must register all seven sources; Desktop remains a renderer only and never
/// queries PostgreSQL/DbContext directly.
/// </summary>
public interface IProductionDocumentKindSource
{
    ProductionDocumentKind Kind { get; }
    Task<ProductionDocument> LoadAsync(Guid businessDocumentId, CancellationToken cancellationToken = default);
}

public sealed class ProductionDocumentSourceRouter : IProductionDocumentSource
{
    private readonly IReadOnlyDictionary<ProductionDocumentKind, IProductionDocumentKindSource> _sources;

    public ProductionDocumentSourceRouter(IEnumerable<IProductionDocumentKindSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var materialized = sources.ToArray();
        var duplicates = materialized.GroupBy(x => x.Kind).Where(x => x.Count() != 1).Select(x => x.Key).ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException($"Production document source registration must be unique. Invalid kinds: {string.Join(", ", duplicates)}.");
        }

        var map = materialized.ToDictionary(x => x.Kind);
        var missing = Enum.GetValues<ProductionDocumentKind>().Where(x => !map.ContainsKey(x)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"All canonical production document sources must be registered. Missing: {string.Join(", ", missing)}.");
        }

        _sources = map;
    }

    public Task<ProductionDocument> LoadAsync(ProductionDocumentKind kind, Guid businessDocumentId, CancellationToken cancellationToken = default)
        => _sources[kind].LoadAsync(businessDocumentId, cancellationToken);
}
