using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Common;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.UnitTests;

public sealed class TrackingMasterIdentityGovernanceTests
{
    [Theory]
    [InlineData("company", "catalog.company_immutable")]
    [InlineData("category", "catalog.category_immutable")]
    [InlineData("model_clear", "catalog.model_code_immutable")]
    [InlineData("model_assign", "catalog.model_code_immutable")]
    [InlineData("derived_sku", "catalog.sku_immutable")]
    public async Task ProductIdentityFields_cannot_be_cleared_or_assigned_after_history(string field, string error)
    {
        var doubles = new Phase2TestDoubles();
        var company = new Company { Name = "Maker", Code = "MK", IsActive = true };
        var category = new Category { Name = "Category", IdentitySymbol = "C", IsActive = true };
        var unit = new Unit { Name = "Piece", Symbol = "PC", IsActive = true };
        doubles.Catalog.AddCompany(company);
        doubles.Catalog.AddCategory(category);
        doubles.Catalog.AddUnit(unit);
        var product = new Product
        {
            Name = "Historic",
            Sku = field == "derived_sku" ? "LEGACY" : "MKC-HIST",
            ModelCode = field == "model_assign" ? null : "HIST",
            CompanyId = company.Id,
            CategoryId = category.Id,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.IndividualPiece,
            IsActive = true
        };
        doubles.Catalog.AddProduct(product);
        var input = new ProductCatalogInput(product.Name, product.Sku,
            CategoryId: field == "category" ? null : category.Id,
            CompanyId: field == "company" ? null : company.Id,
            ModelCode: field == "model_clear" ? null : "HIST",
            BaseUnitId: unit.Id, TrackingMode: product.TrackingMode);
        var before = (product.Sku, product.ModelCode, product.CompanyId, product.CategoryId, product.Version);
        var handler = new UpdateProductHandler(doubles.Catalog, doubles.Authorization,
            new FixedHistorySafety(product.Id), doubles.Transactions, doubles.UnitOfWork, new FakeBusinessAuditWriter());
        var result = await handler.HandleAsync(new UpdateProductCommand(Guid.NewGuid(), product.Id, product.Version, input), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error?.Code);
        Assert.Equal(before, (product.Sku, product.ModelCode, product.CompanyId, product.CategoryId, product.Version));
    }

    [Theory]
    [InlineData("SU2")]
    [InlineData(null)]
    public async Task AssignedDealerCode_is_permanent_even_without_current_stock(string? replacement)
    {
        await using var db = new EdgeRetailsDbContext(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var supplier = new Supplier { Name = "Supplier", DealerCode = "SU1" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        supplier.DealerCode = replacement;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => db.SaveChangesAsync());
        Assert.Equal("parties.dealer_code_immutable", ex.Code);
        db.ChangeTracker.Clear();
        Assert.Equal("SU1", (await db.Suppliers.SingleAsync()).DealerCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignedDealerCode_boolSaveOverloadsCannotBypassPermanence(bool asynchronous)
    {
        await using var db = new EdgeRetailsDbContext(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var supplier = new Supplier { Name = "Supplier", DealerCode = "SU1" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        supplier.DealerCode = "SU2";
        if (asynchronous)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => db.SaveChangesAsync(true, default));
            Assert.Equal("parties.dealer_code_immutable", ex.Code);
        }
        else
        {
            var ex = Assert.Throws<BusinessRuleException>(() => db.SaveChanges(true));
            Assert.Equal("parties.dealer_code_immutable", ex.Code);
        }
        db.ChangeTracker.Clear();
        Assert.Equal("SU1", (await db.Suppliers.SingleAsync()).DealerCode);
    }

    [Fact]
    public async Task CompanyCode_cannot_change_when_only_inactive_product_has_history()
    {
        var doubles = new Phase2TestDoubles();
        var company = new Company { Name = "Maker", Code = "MK", IsActive = true };
        doubles.Catalog.AddCompany(company);
        var product = new Product
        {
            Name = "Historic Product",
            CompanyId = company.Id,
            Sku = "MKE-HIST",
            ModelCode = "HIST",
            IsActive = false
        };
        doubles.Catalog.AddProduct(product);
        var safety = new FixedHistorySafety(product.Id);
        var handler = new SaveCompanyHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork, safety);

        var result = await handler.HandleAsync(
            new SaveCompanyCommand(Guid.NewGuid(), company.Id, company.Name, "NW"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.company_code_immutable", result.Error?.Code);
        Assert.Equal("MK", company.Code);
    }

    [Fact]
    public async Task CategorySymbol_cannot_change_when_only_inactive_product_has_history()
    {
        var doubles = new Phase2TestDoubles();
        var category = new Category { Name = "Historic Category", IdentitySymbol = "HC", IsActive = true };
        doubles.Catalog.AddCategory(category);
        var product = new Product
        {
            Name = "Inactive Historic Product",
            CategoryId = category.Id,
            Sku = "HCI-HIST",
            ModelCode = "HIST",
            IsActive = false
        };
        doubles.Catalog.AddProduct(product);
        var safety = new FixedHistorySafety(product.Id);
        var handler = new SaveCategoryHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork, safety);

        var result = await handler.HandleAsync(
            new SaveCategoryCommand(Guid.NewGuid(), category.Id, category.Name, "N"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.category_symbol_immutable", result.Error?.Code);
        Assert.Equal("HC", category.IdentitySymbol);
    }

    private sealed class FixedHistorySafety(Guid productId) : IProductCatalogSafetyReadService
    {
        public Task<bool> HasStockOrHistoryAsync(Guid candidate, CancellationToken cancellationToken) =>
            Task.FromResult(candidate == productId);
    }
}
