using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Production.Printing;

public sealed class EfPhysicalStickerDocumentSource : IPhysicalStickerDocumentSource
{
    private readonly EdgeRetailsDbContext _db;

    public EfPhysicalStickerDocumentSource(EdgeRetailsDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<PhysicalItemStickerDocument> LoadStickerDocumentAsync(
        Guid inventoryUnitId,
        bool isReprint = false,
        CancellationToken cancellationToken = default)
    {
        var unit = await _db.InventoryUnits.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == inventoryUnitId, cancellationToken)
            ?? throw new InvalidOperationException($"InventoryUnit '{inventoryUnitId}' was not found.");

        var product = await _db.Products.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == unit.ProductId, cancellationToken)
            ?? throw new InvalidOperationException($"Product '{unit.ProductId}' for unit '{inventoryUnitId}' was not found.");

        EdgeRetails.Domain.Catalog.Company? company = null;
        if (product.CompanyId.HasValue)
        {
            company = await _db.Companies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == product.CompanyId.Value, cancellationToken);
        }

        var shop = await _db.ShopProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProfileKey == "PRIMARY", cancellationToken);

        if (string.IsNullOrWhiteSpace(unit.TrackingCode) || unit.ItemSequence is null || unit.ItemSequence <= 0)
        {
            throw new InvalidOperationException("The committed unit does not have a printable tracking identity.");
        }
        var supplierProduct = unit.SupplierProductId.HasValue
            ? await _db.SupplierProducts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == unit.SupplierProductId.Value, cancellationToken) : null;
        var supplier = supplierProduct is not null
            ? await _db.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == supplierProduct.SupplierId, cancellationToken) : null;
        var purchaseItem = unit.SourcePurchaseItemId.HasValue
            ? await _db.PurchaseItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == unit.SourcePurchaseItemId.Value, cancellationToken) : null;
        var purchase = purchaseItem is not null
            ? await _db.Purchases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseItem.PurchaseId, cancellationToken) : null;

        return new PhysicalItemStickerDocument(
            unit.Id,
            unit.TrackingCode ?? string.Empty,
            company?.Name ?? "Edge Retails",
            product.Name,
            product.Model ?? product.ModelCode,
            unit.ProductSkuSnapshot ?? product.Sku ?? string.Empty,
            unit.ItemSequence ?? 0,
            unit.SerialNumber,
            unit.Imei1,
            unit.Imei2,
            shop?.ShopName ?? "Edge Retails",
            product.DefaultSalePrice,
            isReprint,
            unit.CreatedAt,
            supplier?.Name,
            unit.SupplierCodeSnapshot ?? supplier?.DealerCode,
            purchase?.PurchaseNumber,
            unit.SourcePurchaseItemId,
            unit.SupplierProductId);
    }
}
