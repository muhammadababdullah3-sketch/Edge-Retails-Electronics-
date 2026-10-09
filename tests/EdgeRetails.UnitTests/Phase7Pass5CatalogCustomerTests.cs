using System.Text.Json;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Parties;

namespace EdgeRetails.UnitTests;

public sealed class Phase7Pass5CatalogCustomerTests
{
    [Theory]
    [InlineData("sku")]
    [InlineData("name")]
    public async Task R19_SignificantIdentityWithoutAuditRefusesBeforeMutation(string field)
    {
        var (doubles, product, _) = Seed();
        var before = JsonSerializer.Serialize(product);
        var input = Input(product) with { Sku = field == "sku" ? "R19-NEW" : product.Sku, Name = field == "name" ? "New identity name" : product.Name };
        var handler = new UpdateProductHandler(doubles.Catalog, doubles.Authorization,
            new Phase3CatalogSafetyReadService(false), doubles.Transactions, doubles.UnitOfWork);
        var result = await handler.HandleAsync(new(Guid.NewGuid(), product.Id, product.Version, input), default);
        Assert.False(result.IsSuccess); Assert.Equal("catalog.audit_unavailable", result.Error?.Code);
        Assert.Equal(before, JsonSerializer.Serialize(product)); Assert.Empty(doubles.Audit.Records);
    }

    [Fact]
    public async Task R19_IdentityAuditFreezesActualSnapshotsAndCallerAuthority()
    {
        var (doubles, product, _) = Seed();
        var actor = Guid.NewGuid();
        var oldSku = product.Sku;
        var handler = new UpdateProductHandler(doubles.Catalog, doubles.Authorization,
            new Phase3CatalogSafetyReadService(false), doubles.Transactions, doubles.UnitOfWork, doubles.Audit);
        var result = await handler.HandleAsync(new(actor, product.Id, product.Version, Input(product) with { Sku = "R19-NEW" }), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var audit = Assert.Single(doubles.Audit.Records);
        Assert.Equal("PRODUCT_IDENTITY_CHANGED", audit.Action); Assert.Equal("PRODUCT", audit.EntityType);
        Assert.Equal(product.Id, audit.EntityId); Assert.Equal(actor, audit.ActorId); Assert.NotEqual(Guid.Empty, audit.CorrelationId);
        product.Sku = "LATER-STATE";
        using var summary = JsonDocument.Parse(audit.Summary!);
        Assert.Equal(oldSku, summary.RootElement.GetProperty("Before").GetProperty("Sku").GetString());
        Assert.Equal("R19-NEW", summary.RootElement.GetProperty("After").GetProperty("Sku").GetString());
        Assert.Equal("UpdateProduct", summary.RootElement.GetProperty("Context").GetString());
    }

    [Fact]
    public async Task R19_PriceOnlyEditDoesNotInventSignificantIdentityAudit()
    {
        var (doubles, product, _) = Seed();
        var handler = new UpdateProductHandler(doubles.Catalog, doubles.Authorization,
            new Phase3CatalogSafetyReadService(false), doubles.Transactions, doubles.UnitOfWork);
        var result = await handler.HandleAsync(new(Guid.NewGuid(), product.Id, product.Version,
            Input(product) with { DefaultSalePrice = 19m }), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(19m, product.DefaultSalePrice); Assert.Empty(doubles.Audit.Records);
    }

    [Fact]
    public async Task R19_ExplicitEmptyAggregateOperationPreservesExistingRefusalWithoutAudit()
    {
        var (doubles, product, _) = Seed();
        var before = JsonSerializer.Serialize(product);
        var handler = new SaveProductAggregateHandler(doubles.Catalog, doubles.Traceability, doubles.Parties,
            doubles.Authorization, doubles.Transactions, doubles.UnitOfWork, doubles.Clock,
            operationLock: doubles.OperationLock, outcomeLedger: doubles.OutcomeLedger, audit: doubles.Audit);
        var result = await handler.HandleAsync(new(Guid.NewGuid(), product.Id, product.Version,
            Input(product) with { Sku = "R19-NEW" }, [], [], Guid.Empty), default);
        Assert.False(result.IsSuccess); Assert.Equal("operation.ledger_unavailable", result.Error?.Code);
        Assert.Equal(before, JsonSerializer.Serialize(product)); Assert.Empty(doubles.Audit.Records);
    }

    [Fact]
    public async Task R19_PairChangeWithoutAuditRefusesBeforeMutation()
    {
        var (doubles, product, supplier) = Seed();
        var pair = Pair(doubles, product, supplier);
        var before = JsonSerializer.Serialize(pair);
        var handler = new SetSupplierProductActiveHandler(doubles.Catalog, doubles.Traceability, doubles.Parties,
            doubles.Authorization, doubles.Transactions, doubles.UnitOfWork, doubles.Clock, doubles.ResourceLock);
        var result = await handler.HandleAsync(new(Guid.NewGuid(), product.Id, supplier.Id, false, pair.Version), default);
        Assert.False(result.IsSuccess); Assert.Equal("catalog.audit_unavailable", result.Error?.Code);
        Assert.Equal(before, JsonSerializer.Serialize(pair)); Assert.Empty(doubles.Audit.Records);
    }

    [Fact]
    public async Task R19_AggregatePairChangeWithoutAuditRefusesBeforeCatalogMutation()
    {
        var (doubles, product, supplier) = Seed();
        var before = JsonSerializer.Serialize(product);
        var handler = new SaveProductAggregateHandler(doubles.Catalog, doubles.Traceability, doubles.Parties,
            doubles.Authorization, doubles.Transactions, doubles.UnitOfWork, doubles.Clock,
            safety: new Phase3CatalogSafetyReadService(false));
        var result = await handler.HandleAsync(new(Guid.NewGuid(), product.Id, product.Version, Input(product),
            [new ProductUnitInput(product.BaseUnitId, 1m, true, true, true, true, true)], [supplier.Id]), default);
        Assert.False(result.IsSuccess); Assert.Equal("catalog.audit_unavailable", result.Error?.Code);
        Assert.Equal(before, JsonSerializer.Serialize(product)); Assert.Empty(doubles.Audit.Records);
    }

    [Fact]
    public async Task R19_PairNoOpPreservesExistingVersionBehaviorWithoutFalseSignificantAudit()
    {
        var (doubles, product, supplier) = Seed();
        var pair = Pair(doubles, product, supplier);
        var handler = new SetSupplierProductActiveHandler(doubles.Catalog, doubles.Traceability, doubles.Parties,
            doubles.Authorization, doubles.Transactions, doubles.UnitOfWork, doubles.Clock, doubles.ResourceLock, doubles.Audit);
        var first = await handler.HandleAsync(new(Guid.NewGuid(), product.Id, supplier.Id, false, pair.Version), default);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Single(doubles.Audit.Records);
        var version = pair.Version;
        var noop = await handler.HandleAsync(new(Guid.NewGuid(), product.Id, supplier.Id, false, version), default);
        Assert.True(noop.IsSuccess, noop.Error?.Message); Assert.Equal(version + 1, pair.Version);
        Assert.Equal(9L, pair.NextItemSequence); Assert.Single(doubles.Audit.Records);
        var withoutAudit = new SetSupplierProductActiveHandler(doubles.Catalog, doubles.Traceability, doubles.Parties,
            doubles.Authorization, doubles.Transactions, doubles.UnitOfWork, doubles.Clock, doubles.ResourceLock);
        Assert.True((await withoutAudit.HandleAsync(new(Guid.NewGuid(), product.Id, supplier.Id, false, pair.Version), default)).IsSuccess);
        Assert.Single(doubles.Audit.Records);
    }

    private static (Phase2TestDoubles Doubles, Product Product, Supplier Supplier) Seed()
    {
        var doubles = new Phase2TestDoubles();
        var unit = new Unit { Name = "Piece", Symbol = "PC" }; doubles.Catalog.AddUnit(unit);
        var product = new Product { Name = "R19 product", Sku = "R19-OLD", BaseUnitId = unit.Id, Version = 1, IsActive = true };
        doubles.Catalog.AddProduct(product);
        var supplier = new Supplier { Name = "R19 supplier", DealerCode = "R19-1", IsActive = true };
        doubles.Parties.AddSupplier(supplier);
        return (doubles, product, supplier);
    }

    private static SupplierProduct Pair(Phase2TestDoubles doubles, Product product, Supplier supplier)
    {
        var pair = new SupplierProduct { ProductId = product.Id, SupplierId = supplier.Id, IsActive = true, NextItemSequence = 9, Version = 1 };
        doubles.Traceability.AddSupplierProduct(pair); return pair;
    }

    private static ProductCatalogInput Input(Product product) => new(product.Name, product.Sku,
        BaseUnitId: product.BaseUnitId, TrackingMode: product.TrackingMode);
}
