using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Production.Printing;

public sealed class EfProductLabelDocumentSource(EdgeRetailsDbContext db) : IProductLabelDocumentSource
{
    public async Task<ProductLabelDocument> LoadProductLabelAsync(Guid productUnitId, CancellationToken cancellationToken = default)
    {
        var productUnit = await db.ProductUnits.AsNoTracking().SingleOrDefaultAsync(x => x.Id == productUnitId, cancellationToken)
            ?? throw new InvalidOperationException("Product unit not found.");
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == productUnit.ProductId, cancellationToken);
        var unit = await db.Units.AsNoTracking().SingleAsync(x => x.Id == productUnit.UnitId, cancellationToken);
        var company = product.CompanyId.HasValue
            ? await db.Companies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == product.CompanyId.Value, cancellationToken) : null;
        var barcode = await db.ProductUnitBarcodes.AsNoTracking().Where(x => x.ProductUnitId == productUnitId && x.IsActive)
            .OrderBy(x => x.Barcode).Select(x => x.Barcode).FirstOrDefaultAsync(cancellationToken);
        return new(productUnit.Id, product.Id, product.Name, company?.Name ?? "Edge Retails", product.Model ?? product.ModelCode,
            product.Sku ?? string.Empty, unit.Name, barcode, product.DefaultSalePrice * productUnit.FactorToBaseUnit);
    }
}
