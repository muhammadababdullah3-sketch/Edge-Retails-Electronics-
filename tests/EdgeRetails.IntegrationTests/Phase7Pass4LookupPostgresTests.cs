using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

// NEW_COVERAGE: actual PostgreSQL queries, Product-page/UOM boundary and exact stock authority.
[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass4LookupPostgresTests
{
    [Fact]
    public async Task ProductBeyond500AndSupplierBeyond200AreSearchable_WithStablePagesAndAuthoritativeStock()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
        var prefix = "G09" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var unit = new Unit { Name = prefix + " Piece", Symbol = prefix + "P", IsActive = true };
        var alternate = new Unit { Name = prefix + " Pack", Symbol = prefix + "K", IsActive = true };
        db.Units.AddRange(unit, alternate);
        var products = Enumerable.Range(0, 501).Select(i => new Product {
            Name = $"{prefix}-{i:D4}", Sku = $"{prefix}-{i:D4}", BaseUnitId = unit.Id,
            IsActive = true, TrackingMode = TrackingMode.Quantity }).ToArray();
        db.Products.AddRange(products);
        foreach (var product in products)
        {
            db.ProductUnits.Add(new ProductUnit { ProductId = product.Id, UnitId = unit.Id,
                FactorToBaseUnit = 1m, IsActive = true, CanPurchase = true, CanSell = true,
                IsDefaultPurchaseUnit = true, IsDefaultSaleUnit = true });
            db.ProductUnits.Add(new ProductUnit { ProductId = product.Id, UnitId = alternate.Id,
                FactorToBaseUnit = 2m, IsActive = true, CanPurchase = true, CanSell = true });
        }
        db.StockBalances.Add(new StockBalance { ProductId = products[^1].Id, SellableQty = 73.25m });
        db.ProductCostStates.Add(new ProductCostState { ProductId = products[^1].Id, MovingAverageCost = 19.75m });
        var suppliers = Enumerable.Range(0, 201).Select(i => new Supplier {
            Name = $"{prefix}-{i:D4}", City = "Same city", IsActive = true, CreatedAt = DateTimeOffset.UtcNow }).ToArray();
        db.Suppliers.AddRange(suppliers);
        var duplicate1 = new Supplier { Name = prefix + " duplicate", City = "Same city", IsActive = true };
        var duplicate2 = new Supplier { Name = duplicate1.Name, City = duplicate1.City, IsActive = true };
        db.Suppliers.AddRange(duplicate1, duplicate2);
        var inactive = new Product { Name = prefix + " inactive", Sku = prefix + "-INACTIVE", BaseUnitId = unit.Id, IsActive = false };
        db.Products.Add(inactive);
        db.ProductUnits.Add(new ProductUnit { ProductId = inactive.Id, UnitId = unit.Id, FactorToBaseUnit = 1m,
            IsActive = false, CanPurchase = true, CanSell = true, IsDefaultPurchaseUnit = true, IsDefaultSaleUnit = true });
        db.Suppliers.Add(new Supplier { Name = prefix + " inactive", IsActive = false });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reads = scope.ServiceProvider.GetRequiredService<IPurchaseCatalogReadService>();
        var found = await reads.SearchAsync(new PurchaseCatalogPageQuery(Search: products[^1].Sku), default);
        Assert.Equal(2, found.Items.Count);
        Assert.All(found.Items, x => { Assert.Equal(products[^1].Id, x.ProductId); Assert.Equal(73.25m, x.SellableStock); Assert.Equal(19.75m, x.ReferenceCost); });
        var management = scope.ServiceProvider.GetRequiredService<IProductManagementReadService>();
        Assert.Equal(products[^1].Id, Assert.Single(await management.GetProductsPageAsync(
            new ProductManagementPageQuery(Search: products[^1].Sku), default)).ProductId);
        var selected = await reads.SearchAsync(new PurchaseCatalogPageQuery(ProductId: products[^1].Id), default);
        Assert.All(selected.Items, x => Assert.Equal(73.25m, x.SellableStock));
        var visitedProducts = new List<Guid>();
        var visitedUnits = new List<Guid>();
        string? name = null; Guid? id = null;
        do
        {
            var page = await reads.SearchAsync(new PurchaseCatalogPageQuery(prefix, 17, name, id), default);
            Assert.InRange(page.Items.Select(x => x.ProductId).Distinct().Count(), 1, 17);
            Assert.All(page.Items.GroupBy(x => x.ProductId), x => Assert.Equal(2, x.Count()));
            visitedProducts.AddRange(page.Items.Select(x => x.ProductId).Distinct());
            visitedUnits.AddRange(page.Items.Select(x => x.ProductUnitId));
            name = page.NextName; id = page.NextProductId;
        } while (id.HasValue);
        Assert.Equal(501, visitedProducts.Count);
        Assert.Equal(501, visitedProducts.Distinct().Count());
        Assert.Equal(products.Select(x => x.Id).Order(), visitedProducts.Order());
        Assert.Equal(1002, visitedUnits.Distinct().Count());
        Assert.Empty((await reads.SearchAsync(new PurchaseCatalogPageQuery(ProductId: inactive.Id), default)).Items);
        Assert.Single((await reads.SearchAsync(new PurchaseCatalogPageQuery(ProductId: inactive.Id, IncludeInactive: true), default)).Items);

        var parties = scope.ServiceProvider.GetRequiredService<IPartyDirectoryReadService>();
        Assert.Equal(suppliers[^1].Id, Assert.Single(await parties.GetSuppliersAsync(suppliers[^1].Name.ToLowerInvariant(), 50)).SupplierId);
        var duplicatePage1 = Assert.Single(await parties.GetSuppliersAsync(duplicate1.Name, 1));
        var duplicatePage2 = Assert.Single(await parties.GetSuppliersAsync(duplicate1.Name, 1, default,
            duplicatePage1.Name, duplicatePage1.SupplierId));
        Assert.NotEqual(duplicatePage1.SupplierId, duplicatePage2.SupplierId);
        Assert.Equal(new[] { duplicate1.Id, duplicate2.Id }.Order(), new[] { duplicatePage1.SupplierId, duplicatePage2.SupplierId }.Order());
        var supplierIds = new List<Guid>(); name = null; id = null;
        while (true)
        {
            var page = await parties.GetSuppliersAsync(prefix, 19, default, name, id);
            supplierIds.AddRange(page.Select(x => x.SupplierId));
            if (page.Count < 19)
            {
                break;
            }
            name = page[^1].Name; id = page[^1].SupplierId;
        }
        Assert.Equal(203, supplierIds.Count);
        Assert.Equal(203, supplierIds.Distinct().Count());
    }
}
