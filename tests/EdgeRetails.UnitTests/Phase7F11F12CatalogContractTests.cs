using EdgeRetails.Domain.Catalog;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.UnitTests;

public sealed class Phase7F11F12CatalogContractTests
{
    [Theory]
    [InlineData("Name", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public async Task DirectReadRejectsIncompleteCursor(string? name, bool hasId)
    {
        await using var db = new EdgeRetailsDbContext(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase("f11-f12-" + Guid.NewGuid()).Options);
        await Assert.ThrowsAsync<ArgumentException>(() => new PosCatalogReadService(db)
            .GetSellableCatalogAsync(null, null, null, 100, name, hasId ? Guid.NewGuid() : null, default));
    }

    [Fact]
    public async Task FiltersAndEqualNameCompositePagesPreserveEveryMatchingProduct()
    {
        await using var db = new EdgeRetailsDbContext(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase("f11-f12-" + Guid.NewGuid()).Options);
        var unit = new Unit { Name = "Piece", Symbol = "pc" };
        var category = new Category { Name = "Electrical", IdentitySymbol = "EL" };
        db.AddRange(unit, category);
        var expected = new List<Guid>();
        for (var i = 0; i < 253; i++)
        {
            var product = new Product { Name = "Equal name", Sku = $"SKU-{i}", Brand = i == 251 ? "Other" : "Canonical",
                CategoryId = i == 252 ? null : category.Id, BaseUnitId = unit.Id, DefaultSalePrice = 12.5m };
            db.Products.Add(product);
            db.ProductUnits.Add(new ProductUnit { ProductId = product.Id, UnitId = unit.Id, FactorToBaseUnit = 1,
                IsActive = true, CanSell = true, IsDefaultSaleUnit = true });
            if (i < 251)
            {
                expected.Add(product.Id);
            }
        }
        await db.SaveChangesAsync();
        var reads = new PosCatalogReadService(db);
        var collected = new List<Guid>();
        string? name = null;
        Guid? id = null;
        var pageCounts = new List<int>();
        for (var pageNumber = 0; pageNumber < 4; pageNumber++)
        {
            var page = await reads.GetSellableCatalogAsync(null, "Electrical", "Canonical", 100, name, id, default);
            pageCounts.Add(page.Count);
            Assert.All(page, row => { Assert.Equal("Canonical", row.Brand); Assert.Equal("Electrical", row.Category); });
            collected.AddRange(page.Select(row => row.ProductId));
            if (page.Count == 0)
            {
                break;
            }
            name = page[^1].Name;
            id = page[^1].ProductId;
        }
        Assert.Equal(new[] { 100, 100, 51, 0 }, pageCounts);
        Assert.Equal(expected.Order(), collected);
        Assert.Equal(251, collected.Distinct().Count());
        var other = await reads.GetSellableCatalogAsync(null, "Electrical", "Other", 100, null, null, default);
        Assert.Single(other);
        var uncategorized = await reads.GetSellableCatalogAsync(null, "Uncategorized", "Canonical", 100, null, null, default);
        Assert.Single(uncategorized);
        var legacy = await reads.GetSellableCatalogAsync("SKU-251", 100, default);
        Assert.Equal("Other", Assert.Single(legacy).Brand);
    }

    [Fact]
    public async Task NullAndEmptyBrandRemainUnbrandedAndReadSizeIsCapped()
    {
        await using var db = new EdgeRetailsDbContext(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase("f11-f12-" + Guid.NewGuid()).Options);
        var unit = new Unit { Name = "Piece", Symbol = "pc" };
        db.Units.Add(unit);
        foreach (var brand in new string?[] { null, "", "Named" })
        {
            var product = new Product { Name = "Product", Sku = Guid.NewGuid().ToString(), Brand = brand, BaseUnitId = unit.Id };
            db.Products.Add(product);
            db.ProductUnits.Add(new ProductUnit { ProductId = product.Id, UnitId = unit.Id, FactorToBaseUnit = 1,
                IsActive = true, CanSell = true, IsDefaultSaleUnit = true });
        }
        await db.SaveChangesAsync();
        var reads = new PosCatalogReadService(db);
        var unbranded = await reads.GetSellableCatalogAsync(null, null, "Unbranded", 100, null, null, default);
        Assert.Equal(2, unbranded.Count);
        Assert.All(unbranded, row => Assert.Null(row.Brand));
        Assert.Single(await reads.GetSellableCatalogAsync(null, null, null, 1, null, null, default));
    }
}
