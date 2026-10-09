using System.Data.Common;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("TrackingManufacturerIdentityPg")]
public sealed class TrackingWorkflowPostgresTests
{
    [Fact]
    public async Task Scanner_UsesActiveClaimAuthorityAndRejectsClearingImmutableRawSlots()
    {
        var scannerSql = new ScannerSqlTrace();
        await using var provider = Phase2PostgresTestHarness.BuildProvider(configure: services =>
            services.AddDbContext<EdgeRetailsDbContext>(options => options.AddInterceptors(scannerSql)));
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var serial = "CLAIM-" + Guid.NewGuid().ToString("N");
        var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "SCANNER-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m, [new SerializedIdentityInput(serial)])]), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        // Approved immutable history forbids manufacturing an inconsistent raw
        // slot. Retain the attempted bypass and prove the database rejects it.
        var rejected = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inventory.units SET serial_number=NULL WHERE id={unit.Id}"));
        Assert.Equal("P0001", rejected.SqlState);
        Assert.Equal("Manufacturer identity on an inventory unit is immutable after first assignment.", rejected.MessageText);
        db.ChangeTracker.Clear();
        Assert.Equal(unit.SerialNumber, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == unit.Id)).SerialNumber);
        var reads = scope.ServiceProvider.GetRequiredService<IPhase4WorkflowReadService>();
        scannerSql.Commands.Clear();
        var claimMatch = Assert.Single(await reads.ResolveScannerAsync(serial.ToLowerInvariant(), default));
        var authorityCommand = Assert.Single(scannerSql.Commands, x => x.Contains("unit_identity_ownership", StringComparison.Ordinal));
        Assert.Contains("unit_identity_claims", authorityCommand, StringComparison.Ordinal);
        Assert.Contains("JOIN", authorityCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("serial_number", authorityCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("imei1", authorityCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("imei2", authorityCommand, StringComparison.Ordinal);
        Assert.Equal(unit.Id, claimMatch.InventoryUnitId);
        Assert.Equal(ScannerResolutionNamespace.SerialNumber, claimMatch.Namespace);
        Assert.Equal(ScannerResolutionNamespace.TrackingCode, Assert.Single(await reads.ResolveScannerAsync(unit.TrackingCode!, default)).Namespace);
    }

    private sealed class ScannerSqlTrace : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task ConcurrentPurchaseAndAdjustment_SameSupplierProductKeepDistinctSequences()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var seed = await SeedAsync(provider, TrackingMode.IndividualPiece);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task SubmitAsync(bool purchase)
        {
            await using var scope = provider.CreateAsyncScope();
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                barrier.SetResult();
            }
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (purchase)
            {
                var result = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                    seed.SupplierId, "PAIR-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                    PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                    [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 150m, [])]), default);
                Assert.True(result.IsSuccess, result.Error?.Message);
            }
            else
            {
                var result = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(new CreateStockAdjustmentCommand(
                    StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
                    [new StockAdjustmentItemCommand(seed.ProductId, seed.ProductUnitId, StockAdjustmentDirection.Increase,
                        InventoryBucket.Sellable, 1m, 100m, seed.SupplierId)], seed.ActorId, Guid.NewGuid()), default);
                Assert.True(result.IsSuccess, result.Error?.Message);
            }
        }
        await Task.WhenAll(SubmitAsync(true), SubmitAsync(false)).WaitAsync(TimeSpan.FromSeconds(45));
        await using var verify = provider.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var units = await db.InventoryUnits.Where(x => x.ProductId == seed.ProductId).OrderBy(x => x.ItemSequence).ToListAsync();
        Assert.Equal(new long?[] { 1, 2 }, units.Select(x => x.ItemSequence));
        Assert.Equal(2, units.Select(x => x.TrackingCode).Distinct().Count());
        Assert.Equal(3, (await db.SupplierProducts.SingleAsync(x => x.ProductId == seed.ProductId)).NextItemSequence);
        Assert.Equal(2m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        Assert.Contains(units, x => x.OriginType == InventoryUnitOriginType.Purchase);
        Assert.Contains(units, x => x.OriginType == InventoryUnitOriginType.StockAdjustment);
    }

    [Theory]
    [InlineData(false, TrackingMode.Serialized, 1)]
    [InlineData(true, TrackingMode.Serialized, 1)]
    [InlineData(false, TrackingMode.IndividualPiece, 1)]
    [InlineData(true, TrackingMode.IndividualPiece, 1)]
    [InlineData(false, TrackingMode.Container, 1)]
    [InlineData(true, TrackingMode.Container, 1)]
    [InlineData(false, TrackingMode.Container, 2)]
    [InlineData(true, TrackingMode.Container, 2)]
    public async Task WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(bool shopStock, TrackingMode mode, int factor)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setup = provider.CreateAsyncScope();
        var services = setup.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var warrantyProduct = await db.Products.SingleAsync(x => x.Id == fixture.ProductId);
        warrantyProduct.ImeiTrackingEnabled = true;
        warrantyProduct.TrackingMode = mode;
        (await db.ProductUnits.SingleAsync(x => x.Id == fixture.ProductUnitId)).FactorToBaseUnit = factor;
        await db.SaveChangesAsync();
        var originalSerial = "ORIGINAL-" + Guid.NewGuid().ToString("N");
        var originalImei = "86" + Random.Shared.NextInt64(100000000000, 999999999999).ToString();
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId, "WARRANTY-" + Guid.NewGuid().ToString("N"), DateOnly.FromDateTime(DateTime.UtcNow), null,
            0m, PurchaseSettlementMode.External, fixture.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 100m, 5000m,
                [new SerializedIdentityInput(originalSerial, originalImei)])]), CancellationToken.None);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var original = await db.InventoryUnits.SingleAsync(x => x.ProductId == fixture.ProductId);
        var tracking = original.TrackingCode;
        Guid target;
        Guid claimUnitId = Guid.Empty;
        if (shopStock)
        {
            var damaged = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
                new TransferInventoryConditionCommand(fixture.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged,
                    factor, fixture.ActorId, "Warranty fixture defect", InventoryUnitIds: [original.Id]), CancellationToken.None);
            Assert.True(damaged.IsSuccess, damaged.Error?.Message);
            var sent = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new SendShopStockToSupplierWarrantyCommand(fixture.ProductId, InventoryBucket.Damaged, factor,
                    fixture.SupplierId, original.SourcePurchaseItemId, "Fault", fixture.ActorId, Guid.NewGuid(), [original.Id]), CancellationToken.None);
            Assert.True(sent.IsSuccess, sent.Error?.Message);
            target = sent.Value;
        }
        else
        {
            var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);
            var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
                Guid.NewGuid(), customer.Id, fixture.ActorId, null, 0m, SalePaymentMethod.Bank, 5000m * factor, "BANK", null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 5000m * factor, [original.Id])]), CancellationToken.None);
            Assert.True(sale.IsSuccess, sale.Error?.Message);
            db.ChangeTracker.Clear();
            var saleItem = await db.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
            var claim = await services.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(new CreateWarrantyClaimCommand(
                customer.Id, sale.Value!.SaleId, fixture.SupplierId, fixture.ActorId,
                [new WarrantyClaimItemInput(fixture.ProductId, factor, "Fault", saleItem.Id, saleItem.WarrantyValidUntil,
                    [new WarrantyClaimUnitInput(original.Id, originalSerial)])], Guid.NewGuid()), CancellationToken.None);
            Assert.True(claim.IsSuccess, claim.Error?.Message);
            target = claim.Value;
            Assert.True((await services.GetRequiredService<BeginWarrantyClaimReviewHandler>().HandleAsync(
                new BeginWarrantyClaimReviewCommand(target, fixture.ActorId, Guid.NewGuid()), CancellationToken.None)).IsSuccess);
            Assert.True((await services.GetRequiredService<SendWarrantyClaimToSupplierHandler>().HandleAsync(
                new SendWarrantyClaimToSupplierCommand(target, fixture.ActorId, Guid.NewGuid()), CancellationToken.None)).IsSuccess);
            claimUnitId = await db.WarrantyClaimItemUnits.Where(x => x.OriginalInventoryUnitId == original.Id).Select(x => x.Id).SingleAsync();
        }
        var operationId = Guid.NewGuid();
        var serial = "replacement-" + Guid.NewGuid().ToString("N");
        var imei = "86" + Random.Shared.NextInt64(100000000000, 999999999999).ToString();
        async Task ReceiveAsync(string rawSerial, string rawImei)
        {
            await using var scope = provider.CreateAsyncScope();
            var result = shopStock
                ? await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
                    new ReceiveShopStockWarrantyCommand(target, WarrantyResolutionType.Replaced, fixture.ActorId,
                        [original.Id], [new ReplacementSerializedUnitInput(rawSerial, rawImei, null)], null, operationId), CancellationToken.None)
                : await scope.ServiceProvider.GetRequiredService<ReceiveCustomerWarrantyReplacementHandler>().HandleAsync(
                    new ReceiveCustomerWarrantyReplacementCommand(target, fixture.ActorId, operationId,
                        [new CustomerWarrantyReplacementUnitInput(claimUnitId, rawSerial, rawImei, null)], null), CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        await ReceiveAsync(serial, imei);
        var fullWidth = new string(serial.Select(c => c is >= 'a' and <= 'z' ? (char)(c + 0xFEE0) : c).ToArray());
        await ReceiveAsync(" " + fullWidth + " ", imei[..3] + "-" + imei[3..]);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.InventoryUnits.CountAsync(x => x.ProductId == fixture.ProductId));
        Assert.Equal(3, (await db.SupplierProducts.SingleAsync(x => x.ProductId == fixture.ProductId)).NextItemSequence);
        Assert.Equal(tracking, (await db.InventoryUnits.SingleAsync(x => x.Id == original.Id)).TrackingCode);
        Assert.Equal(1, await db.WarrantyOperations.CountAsync(x => x.ClientOperationId == operationId));
        Assert.Equal(1, await db.InventoryUnitIdentityClaims.CountAsync(x => x.NormalizedValue == imei));
        var replacement = await db.InventoryUnits.SingleAsync(x => x.ProductId == fixture.ProductId && x.Id != original.Id);
        Assert.Equal(InventoryUnitOriginType.WarrantyReplacement, replacement.OriginType);
        Assert.NotEqual(tracking, replacement.TrackingCode);
        if (shopStock)
        {
            Assert.Equal(InventoryUnitStatus.SupplierReturned, (await db.InventoryUnits.SingleAsync(x => x.Id == original.Id)).Status);
            Assert.Equal(target, replacement.SourceWarrantyCaseId);
        }
        else
        {
            var relationship = await db.WarrantyClaimItemUnits.SingleAsync(x => x.Id == claimUnitId);
            Assert.Equal(replacement.Id, relationship.ReplacementInventoryUnitId);
            Assert.Equal(original.Id, relationship.OriginalInventoryUnitId);
        }
    }

    [Theory]
    [InlineData(TrackingMode.IndividualPiece)]
    [InlineData(TrackingMode.Container)]
    public async Task ConcurrentIdentitylessAdjustmentReplay_CommitsOneUnitMovementAndSequence(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider, mode);
        var command = new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
            [new StockAdjustmentItemCommand(fixture.ProductId, fixture.ProductUnitId, StockAdjustmentDirection.Increase,
                InventoryBucket.Sellable, 1m, 100m, fixture.SupplierId)], fixture.ActorId, Guid.NewGuid());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<EdgeRetails.Application.Common.Result<Guid>> SubmitAsync()
        {
            await using var scope = provider.CreateAsyncScope();
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                ready.SetResult();
            }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>()
                .HandleAsync(command, CancellationToken.None);
        }
        var results = await Task.WhenAll(SubmitAsync(), SubmitAsync());
        Assert.All(results, x => Assert.True(x.IsSuccess, x.Error?.Message));
        Assert.Equal(results[0].Value, results[1].Value);
        await using var check = provider.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(1, unit.ItemSequence);
        Assert.False(string.IsNullOrWhiteSpace(unit.TrackingCode));
        Assert.Equal(1, await db.InventoryMovements.CountAsync(x => x.CorrelationId == command.CorrelationId));
        Assert.Equal(2, (await db.SupplierProducts.SingleAsync(x => x.ProductId == fixture.ProductId)).NextItemSequence);
        var mismatch = await check.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            command with { Items = [command.Items[0] with { BaseQuantity = 2m }] }, CancellationToken.None);
        Assert.Equal("idempotency.payload_mismatch", mismatch.Error?.Code);
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == fixture.ProductId));
    }

    [Fact]
    public async Task FirstPhysicalCreation_BlocksIdentityMasterMutationUntilHistoryIsCommitted()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider, TrackingMode.IndividualPiece);
        await using var creationScope = provider.CreateAsyncScope();
        var db = creationScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.False(await creationScope.ServiceProvider.GetRequiredService<IProductCatalogSafetyReadService>()
            .HasStockOrHistoryAsync(fixture.ProductId, CancellationToken.None));
        var item = await SeedAdjustmentSourceAsync(db, fixture);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var units = await creationScope.ServiceProvider.GetRequiredService<IPhysicalUnitCreationAuthority>().CreateAsync(
            fixture.SupplierId, fixture.ProductId,
            [new PhysicalUnitCreationEntry(null, null, null, InventoryUnitStatus.InStock, 100m, null,
                InventoryUnitOriginType.StockAdjustment, SourceStockAdjustmentItemId: item.Id)], CancellationToken.None);
        Assert.True(units.IsSuccess, units.Error?.Message);
        await db.SaveChangesAsync();

        await using var editScope = provider.CreateAsyncScope();
        var editDb = editScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await editDb.Database.OpenConnectionAsync();
        var name = "tracking-edit-" + Guid.NewGuid().ToString("N");
        await editDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {name}, false)");
        var product = await editDb.Products.AsNoTracking().SingleAsync(x => x.Id == fixture.ProductId);
        var edit = editScope.ServiceProvider.GetRequiredService<UpdateProductHandler>().HandleAsync(
            new UpdateProductCommand(fixture.ActorId, product.Id, product.Version,
                new ProductCatalogInput(product.Name, "CHANGED-" + Guid.NewGuid().ToString("N")[..8],
                    BaseUnitId: fixture.UnitId, TrackingMode: product.TrackingMode)), CancellationToken.None);
        try
        {
            await WaitForDatabaseLockAsync(name);
            Assert.False(edit.IsCompleted);
        }
        finally
        {
            // Release the transaction even if the lock assertion fails.
            await transaction.CommitAsync();
        }
        var result = await edit.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.sku_immutable", result.Error?.Code);
        Assert.Equal(product.Sku, units.Value!.Single().ProductSkuSnapshot);
    }

    private static async Task WaitForDatabaseLockAsync(string applicationName)
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

    private static async Task<QuantityProductFixture> SeedAsync(ServiceProvider provider, TrackingMode mode)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == fixture.ProductId)).TrackingMode = mode;
        await db.SaveChangesAsync();
        return fixture;
    }

    private static async Task<StockAdjustmentItem> SeedAdjustmentSourceAsync(EdgeRetailsDbContext db, QuantityProductFixture fixture)
    {
        var adjustment = new StockAdjustment { AdjustmentNumber = "FIRST-" + Guid.NewGuid().ToString("N"),
            ActorId = fixture.ActorId, CorrelationId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow };
        var item = new StockAdjustmentItem { StockAdjustmentId = adjustment.Id, ProductId = fixture.ProductId,
            SupplierId = fixture.SupplierId, BaseQuantity = 1m, UnitCostSnapshot = 100m };
        db.StockAdjustments.Add(adjustment);
        db.StockAdjustmentItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }
}
