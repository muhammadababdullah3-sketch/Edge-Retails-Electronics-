using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE: page projection behavior; SQL translation/search/collation are separately proved in PostgreSQL.
public sealed class Phase7Pass4LookupTests
{
    [Fact]
    public async Task CursorRequiresNameAndProductIdTogether()
    {
        await using var db = Db();
        var reads = new PurchaseCatalogReadService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => reads.SearchAsync(new(AfterName: "A"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => reads.SearchAsync(new(AfterProductId: Guid.NewGuid()), default));
    }

    [Fact]
    public async Task ProductPageIsResolvedBeforeUomExpansion_WithExactStockAndInactiveHistory()
    {
        await using var db = Db();
        var piece = new Unit { Name = "Piece", Symbol = "P" };
        var pack = new Unit { Name = "Pack", Symbol = "K" };
        var first = new Product { Name = "A", BaseUnitId = piece.Id, IsActive = true };
        var second = new Product { Name = "B", BaseUnitId = piece.Id, IsActive = true };
        var inactive = new Product { Name = "Historical", BaseUnitId = piece.Id, IsActive = false };
        db.Units.AddRange(piece, pack); db.Products.AddRange(first, second, inactive);
        foreach (var product in new[] { first, second, inactive })
        {
            db.ProductUnits.Add(new ProductUnit { ProductId = product.Id, UnitId = piece.Id, IsActive = product.IsActive, CanPurchase = true, FactorToBaseUnit = 1 });
            db.ProductUnits.Add(new ProductUnit { ProductId = product.Id, UnitId = pack.Id, IsActive = product.IsActive, CanPurchase = true, FactorToBaseUnit = 3 });
        }
        db.StockBalances.Add(new StockBalance { ProductId = second.Id, SellableQty = 45.5m });
        await db.SaveChangesAsync();
        var reads = new PurchaseCatalogReadService(db);
        var page1 = await reads.SearchAsync(new(PageSize: 1), default);
        Assert.Equal(2, page1.Items.Count); Assert.All(page1.Items, x => Assert.Equal(first.Id, x.ProductId));
        Assert.Equal(first.Id, page1.NextProductId);
        var page2 = await reads.SearchAsync(new(PageSize: 1, AfterName: page1.NextName, AfterProductId: page1.NextProductId), default);
        Assert.Equal(2, page2.Items.Count); Assert.All(page2.Items, x => { Assert.Equal(second.Id, x.ProductId); Assert.Equal(45.5m, x.SellableStock); });
        Assert.Null(page2.NextProductId);
        Assert.Empty((await reads.SearchAsync(new(ProductId: inactive.Id), default)).Items);
        Assert.Equal(2, (await reads.SearchAsync(new(ProductId: inactive.Id, IncludeInactive: true), default)).Items.Count);
    }

    private static EdgeRetailsDbContext Db() => new(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
