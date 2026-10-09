using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("TrackingManufacturerIdentityPg")]
public sealed class TrackingMasterRacePostgresTests
{
    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.IndividualPiece)]
    public async Task ReceiptWinsFirst_TrackingPolicyMutationWaitsAndFailsAfterHistory(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var receiptScope = provider.CreateAsyncScope();
        var db = receiptScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode;
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var receipt = await receiptScope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "RECEIPT-FIRST-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 150m, [])]), default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Message);
        await using var editScope = provider.CreateAsyncScope();
        var editDb = editScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var applicationName = await TagConnectionAsync(editDb);
        var editTask = editScope.ServiceProvider.GetRequiredService<UpdateProductHandler>().HandleAsync(
            new UpdateProductCommand(seed.ActorId, product.Id, product.Version,
                new ProductCatalogInput(product.Name, product.Sku, BaseUnitId: seed.UnitId,
                    TrackingMode: mode == TrackingMode.Quantity ? TrackingMode.IndividualPiece : TrackingMode.Quantity)), default);
        try
        {
            await AssertDatabaseLockWaitAsync(applicationName);
            Assert.False(editTask.IsCompleted);
        }
        finally
        {
            await transaction.CommitAsync();
        }
        var result = await editTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("catalog.tracking_policy_locked", result.Error?.Code);
        db.ChangeTracker.Clear();
        Assert.Equal(mode, (await db.Products.SingleAsync(x => x.Id == product.Id)).TrackingMode);
        Assert.Equal(mode == TrackingMode.Quantity ? 0 : 1, await db.InventoryUnits.CountAsync(x => x.ProductId == product.Id));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TrackingModeMutationWinsFirst_ReceiptUsesLockedCurrentPolicy(bool purchase, bool becomesPhysical)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var editScope = provider.CreateAsyncScope();
        var editDb = editScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(editDb);
        var product = await editDb.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = becomesPhysical ? TrackingMode.Quantity : TrackingMode.IndividualPiece;
        await editDb.SaveChangesAsync();
        await using var transaction = await editDb.Database.BeginTransactionAsync();
        var newMode = becomesPhysical ? TrackingMode.IndividualPiece : TrackingMode.Quantity;
        var edited = await editScope.ServiceProvider.GetRequiredService<UpdateProductHandler>().HandleAsync(
            new UpdateProductCommand(seed.ActorId, product.Id, product.Version,
                new ProductCatalogInput(product.Name, product.Sku, BaseUnitId: seed.UnitId, TrackingMode: newMode)), default);
        Assert.True(edited.IsSuccess, edited.Error?.Message);
        await using var creation = provider.CreateAsyncScope();
        var db = creation.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.NotEqual(newMode, (await db.Products.SingleAsync(x => x.Id == product.Id)).TrackingMode);
        var applicationName = await TagConnectionAsync(db);
        async Task<bool> CreateAsync()
        {
            if (purchase)
            {
                var result = await creation.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                    seed.SupplierId, "MODE-RACE-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                    PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                    [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 150m, [])]), default);
                Assert.True(result.IsSuccess, result.Error?.Message);
                return result.IsSuccess;
            }
            var adjustment = await creation.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
                    [new StockAdjustmentItemCommand(product.Id, seed.ProductUnitId, StockAdjustmentDirection.Increase,
                        InventoryBucket.Sellable, 1m, 100m, seed.SupplierId)], seed.ActorId, Guid.NewGuid()), default);
            Assert.True(adjustment.IsSuccess, adjustment.Error?.Message);
            return adjustment.IsSuccess;
        }
        var createTask = CreateAsync();
        try
        {
            await AssertDatabaseLockWaitAsync(applicationName);
        }
        finally
        {
            await transaction.CommitAsync();
        }
        Assert.True(await createTask.WaitAsync(TimeSpan.FromSeconds(30)));
        db.ChangeTracker.Clear();
        Assert.Equal(newMode, (await db.Products.SingleAsync(x => x.Id == product.Id)).TrackingMode);
        Assert.Equal(becomesPhysical ? 1 : 0, await db.InventoryUnits.CountAsync(x => x.ProductId == product.Id));
        Assert.Equal(1m, (await db.StockBalances.SingleAsync(x => x.ProductId == product.Id)).SellableQty);
        Assert.Equal(100m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == product.Id)).TotalInventoryCost);
    }

    [Fact]
    public async Task SkuMutationWinsFirst_PhysicalCreationRefreshesMasterAndSnapshotsCommittedSku()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var editScope = provider.CreateAsyncScope();
        var editDb = editScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(editDb);
        var product = await editDb.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = TrackingMode.IndividualPiece;
        await editDb.SaveChangesAsync();
        await using var transaction = await editDb.Database.BeginTransactionAsync();
        var sku = "CHANGED-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var edited = await editScope.ServiceProvider.GetRequiredService<UpdateProductHandler>().HandleAsync(
            new UpdateProductCommand(seed.ActorId, product.Id, product.Version,
                new ProductCatalogInput(product.Name, sku, BaseUnitId: seed.UnitId, TrackingMode: product.TrackingMode)), default);
        Assert.True(edited.IsSuccess, edited.Error?.Message);
        await using var creation = provider.CreateAsyncScope();
        var creationDb = creation.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        // Intentionally track the committed OLD SKU while the edit transaction is still uncommitted.
        Assert.NotEqual(sku, (await creationDb.Products.SingleAsync(x => x.Id == product.Id)).Sku);
        var applicationName = await TagConnectionAsync(creationDb);
        var createTask = creation.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
                [new StockAdjustmentItemCommand(product.Id, seed.ProductUnitId, StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable, 1m, 100m, seed.SupplierId)], seed.ActorId, Guid.NewGuid()), default);
        try
        {
            await AssertDatabaseLockWaitAsync(applicationName);
            Assert.False(createTask.IsCompleted);
        }
        finally
        {
            await transaction.CommitAsync();
        }
        var result = await createTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.IsSuccess, result.Error?.Message);
        creationDb.ChangeTracker.Clear();
        var unit = await creationDb.InventoryUnits.SingleAsync(x => x.ProductId == product.Id);
        Assert.Equal(sku, unit.ProductSkuSnapshot);
        Assert.Contains("-" + sku + "-", unit.TrackingCode);
        Assert.Equal(sku, (await creationDb.Products.SingleAsync(x => x.Id == product.Id)).Sku);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstPhysicalCreation_IdentityCompanyOrCategoryEditWaitsAndCannotCommit(bool companyEdit)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var creation = provider.CreateAsyncScope();
        var db = creation.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var companyName = "Maker-" + Guid.NewGuid();
        var companyCodes = await db.Companies.Select(x => x.Code).ToListAsync();
        var company = new Company { Name = companyName, Code = TraceabilityCodeRules.SuggestCompanyCode(companyName, companyCodes.Contains) };
        var category = new Category { Name = "Category-" + Guid.NewGuid(), IdentitySymbol = "Q" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant() };
        db.Companies.Add(company);
        db.Categories.Add(category);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.CompanyId = company.Id;
        product.CategoryId = category.Id;
        product.TrackingMode = TrackingMode.IndividualPiece;
        await db.SaveChangesAsync();
        var adjustment = new StockAdjustment { AdjustmentNumber = "MASTER-" + Guid.NewGuid(), ActorId = seed.ActorId,
            CorrelationId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, OccurredAt = DateTimeOffset.UtcNow };
        var item = new StockAdjustmentItem { StockAdjustmentId = adjustment.Id, ProductId = product.Id, BaseQuantity = 1m };
        db.StockAdjustments.Add(adjustment);
        db.StockAdjustmentItems.Add(item);
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var physical = await creation.ServiceProvider.GetRequiredService<IPhysicalUnitCreationAuthority>().CreateAsync(seed.SupplierId, product.Id,
            [new PhysicalUnitCreationEntry(null, null, null, InventoryUnitStatus.InStock, 100m, null,
                InventoryUnitOriginType.StockAdjustment, SourceStockAdjustmentItemId: item.Id)], default);
        Assert.True(physical.IsSuccess, physical.Error?.Message);
        await db.SaveChangesAsync();
        await using var editScope = provider.CreateAsyncScope();
        var editDb = editScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var applicationName = await TagConnectionAsync(editDb);
        var editTask = companyEdit
            ? editScope.ServiceProvider.GetRequiredService<SaveCompanyHandler>().HandleAsync(
                new SaveCompanyCommand(seed.ActorId, company.Id, company.Name, "ZZ"), default)
            : editScope.ServiceProvider.GetRequiredService<SaveCategoryHandler>().HandleAsync(
                new SaveCategoryCommand(seed.ActorId, category.Id, category.Name, "ZZ"), default);
        try
        {
            await AssertDatabaseLockWaitAsync(applicationName);
            Assert.False(editTask.IsCompleted);
        }
        finally
        {
            await transaction.CommitAsync();
        }
        var result = await editTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.IsSuccess);
        Assert.Equal(companyEdit ? "catalog.company_code_immutable" : "catalog.category_symbol_immutable", result.Error?.Code);
        db.ChangeTracker.Clear();
        Assert.Equal(company.Code, (await db.Companies.SingleAsync(x => x.Id == company.Id)).Code);
        Assert.Equal(category.IdentitySymbol, (await db.Categories.SingleAsync(x => x.Id == category.Id)).IdentitySymbol);
        Assert.Equal(product.Sku, (await db.InventoryUnits.SingleAsync(x => x.ProductId == product.Id)).ProductSkuSnapshot);
    }

    private static async Task<string> TagConnectionAsync(EdgeRetailsDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        var name = "tracking-master-" + Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {name}, false)");
        return name;
    }

    private static async Task AssertDatabaseLockWaitAsync(string applicationName)
    {
        await using var observer = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name=@name AND wait_event_type='Lock')", observer);
            query.Parameters.AddWithValue("name", applicationName);
            if ((bool)(await query.ExecuteScalarAsync(timeout.Token))!)
            {
                return;
            }
            await Task.Delay(50, timeout.Token);
        }
    }
}
