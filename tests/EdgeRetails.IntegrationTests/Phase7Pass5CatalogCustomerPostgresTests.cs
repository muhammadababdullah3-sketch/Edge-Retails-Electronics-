using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Audit;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5CatalogCustomerPostgresTests
{
    // NEW_COVERAGE. Only the attested PostgreSQL runner owns these fixtures.
    // These assertions require the existing canonical transactional audit writer.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R19_SignificantIdentityChangePersistsActualBeforeAfterAudit(bool aggregate)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var before = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seed.ProductId);
        var incoming = Input(before) with { Sku = "R19-NEW-" + Guid.NewGuid().ToString("N").ToUpperInvariant(), ModelCode = "R19MODEL" };
        var operation = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        if (aggregate)
        {
            var result = await services.GetRequiredService<SaveProductAggregateHandler>().HandleAsync(
                Aggregate(seed.ActorId, before, incoming, [], operation), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        else
        {
            var result = await services.GetRequiredService<UpdateProductHandler>().HandleAsync(
                new(seed.ActorId, seed.ProductId, before.Version, incoming), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        db.ChangeTracker.Clear();
        var after = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seed.ProductId);
        Assert.Equal(incoming.Sku, after.Sku); Assert.Equal(incoming.ModelCode, after.ModelCode);
        var audit = Assert.Single(await db.BusinessAuditEvents.AsNoTracking()
            .Where(x => x.EntityId == seed.ProductId && x.Action == "PRODUCT_IDENTITY_CHANGED").ToArrayAsync());
        AuditAuthority(audit, seed.ActorId, seed.ProductId, "PRODUCT", started,
            aggregate ? operation : null);
        using var summary = JsonDocument.Parse(audit.Summary!);
        Assert.Equal(aggregate ? "SaveProductAggregate" : "UpdateProduct", summary.RootElement.GetProperty("Context").GetString());
        Assert.Equal(before.Sku, summary.RootElement.GetProperty("Before").GetProperty("Sku").GetString());
        Assert.Equal(after.Sku, summary.RootElement.GetProperty("After").GetProperty("Sku").GetString());
        Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("Before").GetProperty("ModelCode").ValueKind);
        Assert.Equal(after.ModelCode, summary.RootElement.GetProperty("After").GetProperty("ModelCode").GetString());
        Assert.False(await db.InventoryUnits.AnyAsync(x => x.ProductId == seed.ProductId));
        Assert.False(await db.InventoryMovements.AnyAsync(x => x.ProductId == seed.ProductId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R19_PairChangesAuditActualStateAndPreservePhysicalIdentityAndHighwater(bool aggregate)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, 300m);
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            new(seed.SupplierId, "R19-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new(seed.ProductId, seed.ProductUnitId, 1m, 100m, 300m,
                    [new SerializedIdentityInput("R19-SERIAL-" + Guid.NewGuid())])], InitialPaymentAmount: 0m), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seed.ProductId);
        var pair = await db.SupplierProducts.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId && x.SupplierId == seed.SupplierId);
        var otherSupplier = await Phase2PostgresTestHarness.SeedSupplierAsync(db, "R19 new pair supplier", "RZ");
        var physicalBefore = await PhysicalStateAsync(db, seed.ProductId);
        var highwaterBefore = await HighwaterAsync();
        var started = DateTimeOffset.UtcNow;
        var operation = Guid.NewGuid();
        if (aggregate)
        {
            var command = Aggregate(seed.ActorId, product, Input(product), [otherSupplier.Id], operation);
            var handler = services.GetRequiredService<SaveProductAggregateHandler>();
            var changed = await handler.HandleAsync(command, default);
            Assert.True(changed.IsSuccess, changed.Error?.Message);
            var replay = await handler.HandleAsync(command, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
        }
        else
        {
            var changed = await services.GetRequiredService<SetSupplierProductActiveHandler>().HandleAsync(
                new(seed.ActorId, seed.ProductId, seed.SupplierId, false, pair.Version), default);
            Assert.True(changed.IsSuccess, changed.Error?.Message);
            var created = await services.GetRequiredService<SetSupplierProductActiveHandler>().HandleAsync(
                new(seed.ActorId, seed.ProductId, otherSupplier.Id, true), default);
            Assert.True(created.IsSuccess, created.Error?.Message);
        }
        db.ChangeTracker.Clear();
        var oldPair = await db.SupplierProducts.AsNoTracking().SingleAsync(x => x.Id == pair.Id);
        var newPair = await db.SupplierProducts.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId && x.SupplierId == otherSupplier.Id);
        Assert.False(oldPair.IsActive); Assert.True(newPair.IsActive);
        Assert.Equal(pair.NextItemSequence, oldPair.NextItemSequence); Assert.Equal(1L, newPair.NextItemSequence);
        var oldAudit = Assert.Single(await db.BusinessAuditEvents.AsNoTracking()
            .Where(x => x.EntityId == pair.Id && x.Action == "SUPPLIER_PRODUCT_DEACTIVATED").ToArrayAsync());
        AuditAuthority(oldAudit, seed.ActorId, pair.Id, "SUPPLIER_PRODUCT", started, aggregate ? operation : null);
        PairSummary(oldAudit, aggregate ? "SaveProductAggregate" : "SetSupplierProductActive", true, false, pair.NextItemSequence);
        var createAudit = Assert.Single(await db.BusinessAuditEvents.AsNoTracking()
            .Where(x => x.EntityId == newPair.Id && x.Action == "SUPPLIER_PRODUCT_CREATED").ToArrayAsync());
        AuditAuthority(createAudit, seed.ActorId, newPair.Id, "SUPPLIER_PRODUCT", started, aggregate ? operation : null);
        using (var summary = JsonDocument.Parse(createAudit.Summary!))
        {
            Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("Before").ValueKind);
            Assert.True(summary.RootElement.GetProperty("After").GetProperty("IsActive").GetBoolean());
            Assert.Equal(seed.ProductId, summary.RootElement.GetProperty("After").GetProperty("ProductId").GetGuid());
            Assert.Equal(otherSupplier.Id, summary.RootElement.GetProperty("After").GetProperty("SupplierId").GetGuid());
        }
        Assert.Equal(physicalBefore, await PhysicalStateAsync(db, seed.ProductId));
        Assert.Equal(highwaterBefore, await HighwaterAsync());

        var activationStarted = DateTimeOffset.UtcNow;
        var activationOperation = Guid.NewGuid();
        if (aggregate)
        {
            product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == seed.ProductId);
            var result = await services.GetRequiredService<SaveProductAggregateHandler>().HandleAsync(
                Aggregate(seed.ActorId, product, Input(product), [seed.SupplierId, otherSupplier.Id], activationOperation), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        else
        {
            var result = await services.GetRequiredService<SetSupplierProductActiveHandler>().HandleAsync(
                new(seed.ActorId, seed.ProductId, seed.SupplierId, true, oldPair.Version), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        db.ChangeTracker.Clear();
        var activatedAudit = Assert.Single(await db.BusinessAuditEvents.AsNoTracking()
            .Where(x => x.EntityId == pair.Id && x.Action == "SUPPLIER_PRODUCT_ACTIVATED").ToArrayAsync());
        AuditAuthority(activatedAudit, seed.ActorId, pair.Id, "SUPPLIER_PRODUCT", activationStarted,
            aggregate ? activationOperation : null);
        PairSummary(activatedAudit, aggregate ? "SaveProductAggregate" : "SetSupplierProductActive", false, true, pair.NextItemSequence);
        Assert.Equal(physicalBefore, await PhysicalStateAsync(db, seed.ProductId));
        Assert.Equal(highwaterBefore, await HighwaterAsync());
        Assert.Equal(pair.NextItemSequence, (await db.SupplierProducts.AsNoTracking().SingleAsync(x => x.Id == pair.Id)).NextItemSequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R19_PairAuditAndMutationRollbackTogetherBeforeAndAfterActualSqlFlush(bool flush)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var pair = new SupplierProduct
        {
            ProductId = seed.ProductId, SupplierId = seed.SupplierId, IsActive = true,
            NextItemSequence = 7, Version = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SupplierProducts.Add(pair); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = JsonSerializer.Serialize(await db.SupplierProducts.AsNoTracking().SingleAsync(x => x.Id == pair.Id));
        var failing = new AuditFlushFailure(db, pair.Id, flush);
        var handler = ActivatorUtilities.CreateInstance<SetSupplierProductActiveHandler>(services, failing);
        await Assert.ThrowsAsync<OwnedAuditFailure>(() => handler.HandleAsync(new(seed.ActorId, seed.ProductId, seed.SupplierId, false, 1), default));
        Assert.True(failing.ObservedMutation); Assert.True(failing.ObservedAudit);
        Assert.Equal(flush, failing.Flushed);
        await using var verify = provider.CreateAsyncScope();
        var persisted = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(before, JsonSerializer.Serialize(await persisted.SupplierProducts.AsNoTracking().SingleAsync(x => x.Id == pair.Id)));
        Assert.False(await persisted.BusinessAuditEvents.AnyAsync(x => x.EntityId == pair.Id));
    }

    private static ProductCatalogInput Input(Product product) => new(product.Name, product.Sku,
        product.Brand, product.Model, product.CategoryId, product.CompanyId, product.ModelCode,
        product.BaseUnitId, product.TrackingMode, product.SerialTrackingEnabled, product.ImeiTrackingEnabled,
        product.ReferencePurchaseCost, product.DefaultSalePrice, product.MinimumStockLevel,
        product.DefaultWarrantyMonths, product.AttributesJson, product.AttributesSchemaVersion);

    private static SaveProductAggregateCommand Aggregate(Guid actor, Product product, ProductCatalogInput input,
        IReadOnlyList<Guid> suppliers, Guid operation) => new(actor, product.Id, product.Version, input,
            [new ProductUnitInput(product.BaseUnitId, 1m, true, true, true, true, true)], suppliers, operation);

    private static void AuditAuthority(BusinessAuditEvent audit, Guid actor, Guid entity, string entityType,
        DateTimeOffset started, Guid? operation)
    {
        Assert.Equal(actor, audit.ActorId); Assert.Equal(entity, audit.EntityId); Assert.Equal(entityType, audit.EntityType);
        Assert.NotEqual(Guid.Empty, audit.CorrelationId);
        if (operation.HasValue)
        {
            Assert.Equal(operation.Value, audit.CorrelationId);
        }
        Assert.InRange(audit.OccurredAt, started.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.False(string.IsNullOrWhiteSpace(audit.Summary));
    }

    private static void PairSummary(BusinessAuditEvent audit, string context, bool before, bool after, long sequence)
    {
        using var summary = JsonDocument.Parse(audit.Summary!);
        Assert.Equal(context, summary.RootElement.GetProperty("Context").GetString());
        Assert.Equal(before, summary.RootElement.GetProperty("Before").GetProperty("IsActive").GetBoolean());
        Assert.Equal(after, summary.RootElement.GetProperty("After").GetProperty("IsActive").GetBoolean());
        Assert.Equal(sequence, summary.RootElement.GetProperty("Before").GetProperty("NextItemSequence").GetInt64());
        Assert.Equal(sequence, summary.RootElement.GetProperty("After").GetProperty("NextItemSequence").GetInt64());
    }

    private static async Task<string> PhysicalStateAsync(EdgeRetailsDbContext db, Guid productId)
    {
        var units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync();
        var ids = units.Select(x => x.Id).ToArray();
        return JsonSerializer.Serialize(new
        {
            Units = units,
            Claims = await db.InventoryUnitIdentityClaims.AsNoTracking().Where(x => ids.Contains(x.InventoryUnitId)).OrderBy(x => x.Id).ToArrayAsync(),
            Lots = await db.InventoryLots.AsNoTracking().Where(x => x.ProductId == productId).OrderBy(x => x.Id).ToArrayAsync(),
            Balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == productId),
            Cost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == productId)
        });
    }

    private static Task<string> HighwaterAsync() => File.ReadAllTextAsync(Path.Combine(
        Environment.GetEnvironmentVariable("EDGE_RETAILS_MASTER_PG_RUN_ROOT")
            ?? throw new InvalidOperationException("Owned PostgreSQL root is required."), "harness-highwater", "highwater.manifest"));

    private sealed class OwnedAuditFailure() : Exception("Owned R19 failure before transaction commit");

    private sealed class AuditFlushFailure(EdgeRetailsDbContext db, Guid pairId, bool flush) : IUnitOfWork
    {
        public bool ObservedMutation { get; private set; }
        public bool ObservedAudit { get; private set; }
        public bool Flushed { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            ObservedMutation = db.SupplierProducts.Local.Any(x => x.Id == pairId && !x.IsActive);
            ObservedAudit = db.BusinessAuditEvents.Local.Any(x => x.EntityId == pairId && x.Action == "SUPPLIER_PRODUCT_DEACTIVATED");
            if (flush)
            {
                await db.SaveChangesAsync(cancellationToken); Flushed = true;
                ObservedMutation = await db.SupplierProducts.AsNoTracking().AnyAsync(x => x.Id == pairId && !x.IsActive, cancellationToken);
                ObservedAudit = await db.BusinessAuditEvents.AsNoTracking().AnyAsync(x => x.EntityId == pairId && x.Action == "SUPPLIER_PRODUCT_DEACTIVATED", cancellationToken);
            }
            throw new OwnedAuditFailure();
        }
    }

    [Fact]
    public async Task Gap3_01_BrandFilter_PostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var brandA = "BrandA-" + suffix;
        var brandB = "BrandB-" + suffix;

        var (p1, _) = await SeedCatalogProductAsync(db, "ProductA-" + suffix, brand: brandA);
        var (p2, _) = await SeedCatalogProductAsync(db, "ProductB-" + suffix, brand: brandB);
        var (p3, _) = await SeedCatalogProductAsync(db, "ProductC-" + suffix, brand: null);

        var resultsA = await reader.GetSellableCatalogAsync(null, null, brandA, 100, null, null, default);
        Assert.Contains(resultsA, x => x.ProductId == p1.Id && x.Brand == brandA);
        Assert.DoesNotContain(resultsA, x => x.ProductId == p2.Id);
        Assert.DoesNotContain(resultsA, x => x.ProductId == p3.Id);

        var resultsUnbranded = await reader.GetSellableCatalogAsync(suffix, null, "Unbranded", 100, null, null, default);
        Assert.Contains(resultsUnbranded, x => x.ProductId == p3.Id);
        Assert.DoesNotContain(resultsUnbranded, x => x.ProductId == p1.Id);
    }

    [Fact]
    public async Task Gap3_02_CategoryFilter_PostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var catName = "Cat-" + suffix;
        var category = new Category { Name = catName, IdentitySymbol = "C" + suffix.ToUpperInvariant(), IsActive = true };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var (p1, _) = await SeedCatalogProductAsync(db, "ProductCat-" + suffix, categoryId: category.Id);
        var (p2, _) = await SeedCatalogProductAsync(db, "ProductNoCat-" + suffix, categoryId: null);

        var resultsCat = await reader.GetSellableCatalogAsync(null, catName, null, 100, null, null, default);
        Assert.Contains(resultsCat, x => x.ProductId == p1.Id && x.Category == catName);
        Assert.DoesNotContain(resultsCat, x => x.ProductId == p2.Id);

        var resultsUncat = await reader.GetSellableCatalogAsync(suffix, "Uncategorized", null, 100, null, null, default);
        Assert.Contains(resultsUncat, x => x.ProductId == p2.Id && x.Category == "Uncategorized");
        Assert.DoesNotContain(resultsUncat, x => x.ProductId == p1.Id);
    }

    [Fact]
    public async Task Gap3_03_CombinedBrandCategorySearch_PostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var brand = "BrandCombo-" + suffix;
        var catName = "CatCombo-" + suffix;
        var category = new Category { Name = catName, IdentitySymbol = "K" + suffix.ToUpperInvariant(), IsActive = true };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var (target, _) = await SeedCatalogProductAsync(db, "TargetSpecial-" + suffix, brand: brand, categoryId: category.Id);
        var (wrongBrand, _) = await SeedCatalogProductAsync(db, "TargetSpecial-WB-" + suffix, brand: "OtherBrand", categoryId: category.Id);
        var (wrongCat, _) = await SeedCatalogProductAsync(db, "TargetSpecial-WC-" + suffix, brand: brand, categoryId: null);
        var (wrongSearch, _) = await SeedCatalogProductAsync(db, "OtherProduct-" + suffix, brand: brand, categoryId: category.Id);

        var results = await reader.GetSellableCatalogAsync("TargetSpecial-" + suffix, catName, brand, 100, null, null, default);
        var item = Assert.Single(results);
        Assert.Equal(target.Id, item.ProductId);
    }

    [Fact]
    public async Task Gap3_04_DeterministicOrdering_NameAsc_IdAsc_PostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < 10; i++)
        {
            await SeedCatalogProductAsync(db, $"OrderingProd_{10 - i:D2}_{suffix}");
        }

        var results = await reader.GetSellableCatalogAsync($"OrderingProd_", null, null, 100, null, null, default);
        var matching = results.Where(x => x.Name.Contains(suffix)).ToList();
        Assert.Equal(10, matching.Count);

        for (int i = 0; i < matching.Count - 1; i++)
        {
            var cmp = string.Compare(matching[i].Name, matching[i + 1].Name, StringComparison.Ordinal);
            if (cmp == 0)
            {
                Assert.True(matching[i].ProductId.CompareTo(matching[i + 1].ProductId) < 0);
            }
            else
            {
                Assert.True(cmp < 0);
            }
        }
    }

    [Fact]
    public async Task Gap3_05_PagingPast200_ContinuousNonOverlapping_PostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var prefix = $"Paging200_{suffix}_";
        for (int i = 1; i <= 210; i++)
        {
            await SeedCatalogProductAsync(db, $"{prefix}{i:D4}");
        }

        var page1 = await reader.GetSellableCatalogAsync(prefix, null, null, 150, null, null, default);
        Assert.Equal(150, page1.Count);

        var last = page1[^1];
        var page2 = await reader.GetSellableCatalogAsync(prefix, null, null, 150, last.Name, last.ProductId, default);
        Assert.Equal(60, page2.Count);

        var combined = page1.Concat(page2).ToList();
        Assert.Equal(210, combined.Count);
        Assert.Equal(210, combined.Select(x => x.ProductId).Distinct().Count());

        Assert.True(string.Compare(page2[0].Name, last.Name, StringComparison.Ordinal) > 0 ||
            (page2[0].Name == last.Name && page2[0].ProductId.CompareTo(last.ProductId) > 0));
    }

    [Fact]
    public async Task Gap3_06_IdenticalNames_DisambiguatedByIdAsc_PostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var identicalName = $"Identical_{suffix}";
        for (int i = 0; i < 10; i++)
        {
            await SeedCatalogProductAsync(db, identicalName);
        }

        var page1 = await reader.GetSellableCatalogAsync(identicalName, null, null, 6, null, null, default);
        Assert.Equal(6, page1.Count);

        var last = page1[^1];
        var page2 = await reader.GetSellableCatalogAsync(identicalName, null, null, 6, last.Name, last.ProductId, default);
        Assert.Equal(4, page2.Count);

        var all = page1.Concat(page2).ToList();
        Assert.Equal(10, all.Count);
        Assert.Equal(10, all.Select(x => x.ProductId).Distinct().Count());

        for (int i = 0; i < all.Count - 1; i++)
        {
            Assert.True(all[i].ProductId.CompareTo(all[i + 1].ProductId) < 0);
        }
    }

    [Fact]
    public async Task Gap3_07_IncompleteCursorRejection_PostgreSql()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.GetSellableCatalogAsync(null, null, null, 50, "afterNameOnly", null, default));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.GetSellableCatalogAsync(null, null, null, 50, null, Guid.NewGuid(), default));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.GetSellableCatalogAsync(null, null, null, 50, "   ", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Gap3_08_SellableStockCalculation_AccurateAgainstRealPostgreSqlInventory()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (withStock, _) = await SeedCatalogProductAsync(db, $"StockTestA_{suffix}", sellableStock: 75.5m);
        var (withoutStock, _) = await SeedCatalogProductAsync(db, $"StockTestB_{suffix}", sellableStock: null);

        var resA = await reader.GetSellableCatalogAsync($"StockTestA_{suffix}", null, null, 10, null, null, default);
        var itemA = Assert.Single(resA);
        Assert.Equal(75.5m, itemA.SellableStock);

        var resB = await reader.GetSellableCatalogAsync($"StockTestB_{suffix}", null, null, 10, null, null, default);
        var itemB = Assert.Single(resB);
        Assert.Equal(0m, itemB.SellableStock);
    }

    [Fact]
    public async Task Gap3_09_CatalogQuery_IsReadOnly_NoMutationsOrExclusiveLocks()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedCatalogProductAsync(db, $"ReadOnlyTest_{suffix}", sellableStock: 10m);

        await using var readOnly = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var productCountBefore = await db.Products.CountAsync();
        var stockCountBefore = await db.StockBalances.CountAsync();

        _ = await reader.GetSellableCatalogAsync($"ReadOnlyTest_{suffix}", null, null, 10, null, null, default);
        _ = await reader.GetSellableCatalogAsync(null, "All", "All", 50, null, null, default);

        var productCountAfter = await db.Products.CountAsync();
        var stockCountAfter = await db.StockBalances.CountAsync();

        Assert.Equal(productCountBefore, productCountAfter);
        Assert.Equal(stockCountBefore, stockCountAfter);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private static async Task<(Product product, ProductUnit unit)> SeedCatalogProductAsync(
        EdgeRetailsDbContext db,
        string name,
        string? brand = null,
        Guid? categoryId = null,
        decimal price = 100m,
        decimal? sellableStock = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var unit = new Unit
        {
            Name = "Unit-" + suffix,
            Symbol = "pc-" + suffix,
            DisplayDecimalPlaces = 0
        };
        db.Units.Add(unit);

        var product = new Product
        {
            Name = name,
            Sku = "SKU-" + suffix.ToUpperInvariant(),
            Brand = brand,
            CategoryId = categoryId,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = price,
            IsActive = true
        };
        db.Products.Add(product);

        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            IsDefaultSaleUnit = true,
            IsDefaultPurchaseUnit = true,
            IsActive = true
        };
        db.ProductUnits.Add(productUnit);

        if (sellableStock.HasValue)
        {
            db.StockBalances.Add(new StockBalance
            {
                ProductId = product.Id,
                SellableQty = sellableStock.Value,
                DamagedQty = 0m,
                DefectiveQty = 0m,
                WithSupplierQty = 0m,
                ScrapQty = 0m,
                Version = 1
            });
        }

        await db.SaveChangesAsync();
        return (product, productUnit);
    }
}
