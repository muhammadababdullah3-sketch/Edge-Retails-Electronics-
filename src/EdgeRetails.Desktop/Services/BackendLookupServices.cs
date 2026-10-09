using EdgeRetails.Application.Features.Purchasing;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendSupplierPage(IReadOnlyList<BackendSupplierOption> Items, string? NextName, Guid? NextSupplierId);

public interface IBackendSupplierLookupService
{
    Task<BackendSupplierPage> GetSupplierPageAsync(string? search, int pageSize = 50,
        string? beforeName = null, Guid? beforeSupplierId = null, CancellationToken cancellationToken = default);
}

public interface IBackendPurchaseLookupService : IBackendSupplierLookupService
{
    Task<PurchaseCatalogPageDto> GetCatalogPageAsync(PurchaseCatalogPageQuery query, CancellationToken cancellationToken = default);
}
