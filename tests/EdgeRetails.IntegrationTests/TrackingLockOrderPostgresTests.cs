using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class TrackingLockOrderPostgresTests
{
    [Fact]
    public async Task Complete_serialized_purchase_acquires_every_logical_family_before_rows()
    {
        var trace = new LockTrace();
        await using var provider = Phase2PostgresTestHarness.BuildProvider(configure: services =>
        {
            services.AddDbContext<EdgeRetailsDbContext>(options => options.AddInterceptors(trace));
            services.RemoveAll<IOperationLock>();
            services.RemoveAll<IResourceLock>();
            services.AddScoped(sp => new RecordingLock(sp.GetRequiredService<PostgresOperationLock>(),
                sp.GetRequiredService<EdgeRetailsDbContext>(), trace));
            services.AddScoped<IOperationLock>(sp => sp.GetRequiredService<RecordingLock>());
            services.AddScoped<IResourceLock>(sp => sp.GetRequiredService<RecordingLock>());
        });
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var second = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var operation = Guid.NewGuid();
        var invoice = "LOCK-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var serial = "ORDER-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var secondSerial = "ORDER-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        trace.Expect("operation:" + operation.ToString("D"), 1);
        trace.Expect($"resource:supplier-invoice:{seed.SupplierId:D}:{invoice}", 3);
        trace.Expect($"resource:product:{seed.ProductId:D}", 4);
        trace.Expect($"resource:product:{second.ProductId:D}", 4);
        trace.Expect($"resource:supplier-product:{seed.SupplierId:D}:{seed.ProductId:D}", 5);
        trace.Expect($"resource:supplier-product:{seed.SupplierId:D}:{second.ProductId:D}", 5);
        trace.Expect($"resource:supplier-account:{seed.SupplierId:D}", 6);
        trace.Expect($"resource:inventory-identity:SERIAL:{serial}", 8);
        trace.Expect($"resource:inventory-identity:SERIAL:{secondSerial}", 8);
        trace.Start();

        var lines = Purchase(seed, operation, invoice, serial).Lines
            .Concat(Purchase(second, operation, invoice, secondSerial).Lines)
            .OrderByDescending(x => x.ProductId).ToArray();
        var result = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            Purchase(seed, operation, invoice, serial) with { Lines = lines }, default);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var ranks = trace.Ranks.ToArray();
        foreach (var rank in new[] { 1, 3, 4, 5, 6, 8, 9 })
        {
            Assert.Contains(rank, ranks);
        }
        Assert.Equal(ranks.OrderBy(x => x).ToArray(), ranks);
        Assert.Empty(trace.UnknownAdvisoryKeys);
        Assert.Equal(2, ranks.Count(x => x == 4));
        Assert.Equal(2, ranks.Count(x => x == 5));
        Assert.Equal(2, ranks.Count(x => x == 8));
        var expectedKeys = new[] { "operation:" + operation.ToString("D"),
            $"resource:supplier-invoice:{seed.SupplierId:D}:{invoice}" }
            .Concat(new[] { seed.ProductId, second.ProductId }.OrderBy(x => x).Select(x => $"resource:product:{x:D}"))
            .Concat(new[] { seed.ProductId, second.ProductId }.OrderBy(x => x).Select(x => $"resource:supplier-product:{seed.SupplierId:D}:{x:D}"))
            .Append($"resource:supplier-account:{seed.SupplierId:D}")
            .Concat(new[] { serial, secondSerial }.OrderBy(x => x, StringComparer.Ordinal).Select(x => $"resource:inventory-identity:SERIAL:{x}"));
        Assert.Equal(expectedKeys.ToArray(), trace.AcquiredKeys.ToArray());
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(1, await db.InventoryUnits.CountAsync(x => x.ProductId == second.ProductId));
    }

    [Fact]
    public async Task Identity_wait_does_not_hold_product_row_and_purchase_finishes_atomically()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var serial = "WAIT-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var operation = Guid.NewGuid();
        await db.Database.OpenConnectionAsync();
        var application = "lock-order-" + Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");

        await using var blocker = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", blocker, blockerTransaction))
        {
            hold.Parameters.AddWithValue("key", LockTrace.Key($"resource:inventory-identity:SERIAL:{serial}"));
            await hold.ExecuteNonQueryAsync();
        }
        using var operationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var identityKey = LockTrace.Key($"resource:inventory-identity:SERIAL:{serial}");
        var operationTask = scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            Purchase(seed, operation, "WAIT-INV-" + Guid.NewGuid().ToString("N"), serial), operationCancellation.Token);
        try
        {
            await AssertLockWaitAsync(application, identityKey);
            Assert.False(operationTask.IsCompleted);
            await using var sentinel = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
            await sentinel.OpenAsync();
            await using var sentinelTransaction = await sentinel.BeginTransactionAsync();
            await using var row = new NpgsqlCommand("SELECT id FROM catalog.products WHERE id=@id FOR UPDATE NOWAIT", sentinel, sentinelTransaction);
            row.Parameters.AddWithValue("id", seed.ProductId);
            try
            {
                Assert.Equal(seed.ProductId, await row.ExecuteScalarAsync());
            }
            finally
            {
                await sentinelTransaction.RollbackAsync();
            }
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
            // Always give the business command a terminal result, even if the
            // sentinel assertion fails. A queued task is never assumed successful.
            try
            {
                await operationTask.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                operationCancellation.Cancel();
                await operationTask.WaitAsync(TimeSpan.FromSeconds(10));
                throw;
            }
        }
        var result = await operationTask;
        Assert.True(result.IsSuccess, result.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(serial, unit.SerialNumber);
        Assert.Equal(1, unit.ItemSequence);
        Assert.Equal(1, await db.InventoryUnitIdentityClaims.CountAsync(x => x.InventoryUnitId == unit.Id));
        var claim = await db.InventoryUnitIdentityClaims.SingleAsync(x => x.InventoryUnitId == unit.Id);
        Assert.Equal(claim.Id, (await db.InventoryUnitIdentityOwnerships.SingleAsync(x => x.NormalizedValue == serial)).InventoryUnitIdentityClaimId);
        Assert.Equal(2, (await db.SupplierProducts.SingleAsync(x => x.ProductId == seed.ProductId)).NextItemSequence);
        Assert.Equal(1m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        Assert.Equal(1, await db.Purchases.CountAsync(x => x.ClientOperationId == operation));
    }

    [Fact]
    public async Task Physical_intake_identity_wait_holds_neither_purchase_nor_product_row()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var serial = "INTAKE-LOCK-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var order = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            Purchase(seed, Guid.NewGuid(), "LOCK-ORDER-" + Guid.NewGuid().ToString("N"), serial) with
            {
                ReceiveStockImmediately = false,
                Lines = [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m, [])]
            }, default);
        Assert.True(order.IsSuccess, order.Error?.Message);
        var purchaseId = order.Value!.PurchaseId;
        await db.Database.OpenConnectionAsync();
        var application = "intake-lock-order-" + Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");
        var identityKey = LockTrace.Key($"resource:inventory-identity:SERIAL:{serial}");
        await using var blocker = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await blocker.OpenAsync();
        await using var block = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", blocker, block))
        {
            hold.Parameters.AddWithValue("key", identityKey);
            await hold.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var intakeTask = services.GetRequiredService<ReceiveProductIntakeHandler>().HandleAsync(
            new ReceiveProductIntakeCommand(purchaseId, seed.ProductId, seed.ProductUnitId, 1m, null,
                [new SerializedIdentityInput(serial)], seed.ActorId, Guid.NewGuid()), cancellation.Token);
        try
        {
            await AssertLockWaitAsync(application, identityKey);
            Assert.False(intakeTask.IsCompleted);
            await using var sentinel = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
            await sentinel.OpenAsync();
            await using var sentinelTransaction = await sentinel.BeginTransactionAsync();
            try
            {
                await using var purchaseRow = new NpgsqlCommand("SELECT id FROM purchasing.purchases WHERE id=@id FOR UPDATE NOWAIT", sentinel, sentinelTransaction);
                purchaseRow.Parameters.AddWithValue("id", purchaseId);
                Assert.Equal(purchaseId, await purchaseRow.ExecuteScalarAsync());
                await using var productRow = new NpgsqlCommand("SELECT id FROM catalog.products WHERE id=@id FOR UPDATE NOWAIT", sentinel, sentinelTransaction);
                productRow.Parameters.AddWithValue("id", seed.ProductId);
                Assert.Equal(seed.ProductId, await productRow.ExecuteScalarAsync());
            }
            finally
            {
                await sentinelTransaction.RollbackAsync();
            }
        }
        finally
        {
            await block.RollbackAsync();
            try
            {
                await intakeTask.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                cancellation.Cancel();
                await intakeTask.WaitAsync(TimeSpan.FromSeconds(10));
                throw;
            }
        }
        var intake = await intakeTask;
        Assert.True(intake.IsSuccess, intake.Error?.Message);
        Assert.Single(intake.Value!.CommittedUnits);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        Assert.Equal(serial, unit.SerialNumber);
        Assert.Equal(100m, unit.AcquisitionCost);
        Assert.Equal(1m, (await db.StockBalances.SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        Assert.Equal(2, (await db.SupplierProducts.SingleAsync(x => x.ProductId == seed.ProductId)).NextItemSequence);
    }

    [Fact]
    public async Task Physical_adjustment_identity_wait_holds_no_rows_and_complete_trace_is_ordered()
    {
        var trace = new LockTrace();
        await using var provider = Phase2PostgresTestHarness.BuildProvider(configure: services =>
        {
            services.AddDbContext<EdgeRetailsDbContext>(options => options.AddInterceptors(trace));
            services.RemoveAll<IOperationLock>();
            services.RemoveAll<IResourceLock>();
            services.AddScoped(sp => new RecordingLock(sp.GetRequiredService<PostgresOperationLock>(),
                sp.GetRequiredService<EdgeRetailsDbContext>(), trace));
            services.AddScoped<IOperationLock>(sp => sp.GetRequiredService<RecordingLock>());
            services.AddScoped<IResourceLock>(sp => sp.GetRequiredService<RecordingLock>());
        });
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var second = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        db.StockBalances.AddRange(new StockBalance { ProductId = seed.ProductId }, new StockBalance { ProductId = second.ProductId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var operation = Guid.NewGuid();
        var serial = "ADJ-A-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var secondSerial = "ADJ-Z-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        trace.Expect($"operation:{operation:D}", 1);
        foreach (var productId in new[] { seed.ProductId, second.ProductId })
        {
            trace.Expect($"resource:product:{productId:D}", 4);
            trace.Expect($"resource:supplier-product:{seed.SupplierId:D}:{productId:D}", 5);
        }
        trace.Expect($"resource:inventory-identity:SERIAL:{serial}", 8);
        trace.Expect($"resource:inventory-identity:SERIAL:{secondSerial}", 8);
        await db.Database.OpenConnectionAsync();
        var application = "adjustment-lock-" + Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");
        var identityKey = LockTrace.Key($"resource:inventory-identity:SERIAL:{serial}");
        await using var blocker = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await blocker.OpenAsync();
        await using var block = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", blocker, block))
        {
            hold.Parameters.AddWithValue("key", identityKey);
            await hold.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var items = new[]
        {
            new StockAdjustmentItemCommand(seed.ProductId, seed.ProductUnitId, StockAdjustmentDirection.Increase,
                InventoryBucket.Sellable, 1m, 100m, seed.SupplierId, [new(serial)]),
            new StockAdjustmentItemCommand(second.ProductId, second.ProductUnitId, StockAdjustmentDirection.Increase,
                InventoryBucket.Sellable, 1m, 200m, seed.SupplierId, [new(secondSerial)])
        }.OrderByDescending(x => x.ProductId).ToArray();
        trace.Start();
        var task = scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            new(StockAdjustmentMode.Delta, StockAdjustmentReason.Other, items, seed.ActorId, operation), cancellation.Token);
        try
        {
            await AssertLockWaitAsync(application, identityKey);
            Assert.False(task.IsCompleted);
            await AssertProductAndBalanceRowsAvailableAsync(seed.ProductId);
            await AssertProductAndBalanceRowsAvailableAsync(second.ProductId);
        }
        finally
        {
            await block.RollbackAsync();
            try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                cancellation.Cancel();
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                throw;
            }
        }
        var result = await task;
        Assert.True(result.IsSuccess, result.Error?.Message);
        var ranks = trace.Ranks.ToArray();
        Assert.Contains(9, ranks);
        Assert.Equal(ranks.OrderBy(x => x).ToArray(), ranks);
        Assert.Empty(trace.UnknownAdvisoryKeys);
        var expectedKeys = new[] { $"operation:{operation:D}" }
            .Concat(new[] { seed.ProductId, second.ProductId }.OrderBy(x => x).Select(x => $"resource:product:{x:D}"))
            .Concat(new[] { seed.ProductId, second.ProductId }.OrderBy(x => x).Select(x => $"resource:supplier-product:{seed.SupplierId:D}:{x:D}"))
            .Concat(new[] { serial, secondSerial }.OrderBy(x => x, StringComparer.Ordinal).Select(x => $"resource:inventory-identity:SERIAL:{x}"));
        Assert.Equal(expectedKeys.ToArray(), trace.AcquiredKeys.ToArray());
        db.ChangeTracker.Clear();
        foreach (var productId in new[] { seed.ProductId, second.ProductId })
        {
            var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == productId);
            var claim = await db.InventoryUnitIdentityClaims.SingleAsync(x => x.InventoryUnitId == unit.Id);
            Assert.Equal(claim.Id, (await db.InventoryUnitIdentityOwnerships.SingleAsync(x => x.NormalizedValue == claim.NormalizedValue)).InventoryUnitIdentityClaimId);
            Assert.Equal(1m, (await db.StockBalances.SingleAsync(x => x.ProductId == productId)).SellableQty);
            Assert.Equal(2, (await db.SupplierProducts.SingleAsync(x => x.ProductId == productId)).NextItemSequence);
        }
        Assert.Equal(2, await db.StockAdjustmentItems.CountAsync(x => x.StockAdjustmentId == result.Value));
    }

    [Fact]
    public async Task Physical_count_waits_for_unit_logical_key_before_product_and_balance_rows()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var seedScope = provider.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(seedDb);
        var serial = "COUNT-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var receipt = await seedScope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            Purchase(seed, Guid.NewGuid(), "COUNT-INV-" + Guid.NewGuid().ToString("N"), serial) with
            {
                Lines = [new(seed.ProductId, seed.ProductUnitId, 2m, 100m, 5000m,
                    [new(serial), new(serial + "-2")])]
            }, default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Message);
        seedDb.ChangeTracker.Clear();
        var selectedId = await seedDb.InventoryUnits.Where(x => x.ProductId == seed.ProductId).OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await db.Database.OpenConnectionAsync();
        var application = "count-lock-" + Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");
        var unitKey = LockTrace.Key($"resource:inventory-unit:{selectedId:D}");
        await using var blocker = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await blocker.OpenAsync();
        await using var block = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", blocker, block))
        {
            hold.Parameters.AddWithValue("key", unitKey);
            await hold.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var operation = Guid.NewGuid();
        var task = scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            new(StockAdjustmentMode.SetPhysicalCount, StockAdjustmentReason.Lost,
                [new(seed.ProductId, seed.ProductUnitId, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable,
                    1m, null, InventoryUnitIds: [selectedId])], seed.ActorId, operation), cancellation.Token);
        try
        {
            await AssertLockWaitAsync(application, unitKey);
            Assert.False(task.IsCompleted);
            await AssertProductAndBalanceRowsAvailableAsync(seed.ProductId);
        }
        finally
        {
            await block.RollbackAsync();
            try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                cancellation.Cancel();
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                throw;
            }
        }
        var result = await task;
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1m, (await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId)).SellableQty);
        Assert.Equal(100m, (await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        Assert.Equal(InventoryUnitStatus.Missing, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == selectedId)).Status);
        Assert.Equal(100m, (await db.InventoryMovements.AsNoTracking().SingleAsync(x => x.CorrelationId == operation)).RecognizedLossAmount);
        Assert.Equal(2, await db.InventoryUnitIdentityOwnerships.CountAsync(x => x.NormalizedValue == serial || x.NormalizedValue == serial + "-2"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Warranty_replacement_identity_wait_holds_no_context_or_product_rows(bool shop)
    {
        var trace = new LockTrace();
        await using var provider = Phase2PostgresTestHarness.BuildProvider(configure: services =>
        {
            services.AddDbContext<EdgeRetailsDbContext>(options => options.AddInterceptors(trace));
            services.RemoveAll<IOperationLock>();
            services.RemoveAll<IResourceLock>();
            services.AddScoped(sp => new RecordingLock(sp.GetRequiredService<PostgresOperationLock>(),
                sp.GetRequiredService<EdgeRetailsDbContext>(), trace));
            services.AddScoped<IOperationLock>(sp => sp.GetRequiredService<RecordingLock>());
            services.AddScoped<IResourceLock>(sp => sp.GetRequiredService<RecordingLock>());
        });
        await using var setup = provider.CreateAsyncScope();
        var services = setup.ServiceProvider;
        var seedDb = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(seedDb);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(seedDb);
        var originalSerial = "WLOCK-ORIGINAL-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var receipt = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            Purchase(seed, Guid.NewGuid(), "WLOCK-INV-" + Guid.NewGuid().ToString("N"), originalSerial), default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Message);
        seedDb.ChangeTracker.Clear();
        var original = await seedDb.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        Guid target;
        var claimUnitId = Guid.Empty;
        if (shop)
        {
            var damaged = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
                new(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, seed.ActorId,
                    "Lock-order fixture", InventoryUnitIds: [original.Id]), default);
            Assert.True(damaged.IsSuccess, damaged.Error?.Message);
            var sent = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new(seed.ProductId, InventoryBucket.Damaged, 1m, seed.SupplierId, original.SourcePurchaseItemId,
                    "Fault", seed.ActorId, Guid.NewGuid(), [original.Id]), default);
            Assert.True(sent.IsSuccess, sent.Error?.Message);
            target = sent.Value;
        }
        else
        {
            var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(seedDb);
            var sale = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(
                new(Guid.NewGuid(), customer.Id, seed.ActorId, null, 0m, SalePaymentMethod.Bank, 5000m, "BANK", null,
                    [new(seed.ProductId, seed.ProductUnitId, 1m, 5000m, [original.Id])]), default);
            Assert.True(sale.IsSuccess, sale.Error?.Message);
            seedDb.ChangeTracker.Clear();
            var saleItem = await seedDb.SaleItems.SingleAsync(x => x.SaleId == sale.Value!.SaleId);
            var claim = await services.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
                new(customer.Id, sale.Value!.SaleId, seed.SupplierId, seed.ActorId,
                    [new(seed.ProductId, 1m, "Fault", saleItem.Id, saleItem.WarrantyValidUntil,
                        [new(original.Id, originalSerial)])], Guid.NewGuid()), default);
            Assert.True(claim.IsSuccess, claim.Error?.Message);
            target = claim.Value;
            Assert.True((await services.GetRequiredService<BeginWarrantyClaimReviewHandler>().HandleAsync(
                new(target, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
            Assert.True((await services.GetRequiredService<SendWarrantyClaimToSupplierHandler>().HandleAsync(
                new(target, seed.ActorId, Guid.NewGuid()), default)).IsSuccess);
            claimUnitId = await seedDb.WarrantyClaimItemUnits.Where(x => x.OriginalInventoryUnitId == original.Id).Select(x => x.Id).SingleAsync();
        }
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var operation = Guid.NewGuid();
        var serial = "WLOCK-NEW-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        trace.Expect($"operation:{operation:D}", 1);
        trace.Expect($"resource:{(shop ? "warranty-case" : "warranty-claim")}:{target:D}", 2);
        trace.Expect($"resource:product:{seed.ProductId:D}", 4);
        trace.Expect($"resource:supplier-product:{seed.SupplierId:D}:{seed.ProductId:D}", 5);
        trace.Expect($"resource:warranty-unit:{original.Id:D}", 7);
        trace.Expect($"resource:inventory-identity:SERIAL:{serial}", 8);
        await db.Database.OpenConnectionAsync();
        var application = "warranty-lock-" + Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('application_name', {application}, false)");
        var key = LockTrace.Key($"resource:inventory-identity:SERIAL:{serial}");
        await using var blocker = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await blocker.OpenAsync();
        await using var block = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", blocker, block))
        {
            hold.Parameters.AddWithValue("key", key);
            await hold.ExecuteNonQueryAsync();
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        trace.Start();
        var task = shop
            ? scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
                new(target, WarrantyResolutionType.Replaced, seed.ActorId, [original.Id], [new(serial, null, null)], null, operation), cancellation.Token)
            : scope.ServiceProvider.GetRequiredService<ReceiveCustomerWarrantyReplacementHandler>().HandleAsync(
                new(target, seed.ActorId, operation, [new(claimUnitId, serial, null, null)], null), cancellation.Token);
        try
        {
            await AssertLockWaitAsync(application, key);
            Assert.False(task.IsCompleted);
            await AssertProductAndBalanceRowsAvailableAsync(seed.ProductId);
            await using var sentinel = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
            await sentinel.OpenAsync();
            await using var transaction = await sentinel.BeginTransactionAsync();
            try
            {
                var table = shop ? "warranty.shop_stock_cases" : "warranty.claims";
                await using var row = new NpgsqlCommand($"SELECT id FROM {table} WHERE id=@id FOR UPDATE NOWAIT", sentinel, transaction);
                row.Parameters.AddWithValue("id", target);
                Assert.Equal(target, await row.ExecuteScalarAsync());
            }
            finally { await transaction.RollbackAsync(); }
        }
        finally
        {
            await block.RollbackAsync();
            try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                cancellation.Cancel();
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                throw;
            }
        }
        var result = await task;
        Assert.True(result.IsSuccess, result.Error?.Message);
        var ranks = trace.Ranks.ToArray();
        Assert.Equal(ranks.OrderBy(x => x).ToArray(), ranks);
        Assert.Contains(9, ranks);
        Assert.Empty(trace.UnknownAdvisoryKeys);
        Assert.Equal(new[] { 1, 2, 4, 5, 7, 8 }, ranks.Where(x => x != 9).ToArray());
        db.ChangeTracker.Clear();
        var replacement = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId && x.Id != original.Id);
        Assert.Equal(serial, replacement.SerialNumber);
        Assert.Equal(100m, replacement.AcquisitionCost);
        Assert.Equal(shop ? InventoryUnitStatus.InStock : InventoryUnitStatus.WarrantyCustomerHeld, replacement.Status);
        Assert.Equal(3, (await db.SupplierProducts.SingleAsync(x => x.ProductId == seed.ProductId)).NextItemSequence);
        Assert.Single(await db.WarrantyOperations.Where(x => x.ClientOperationId == operation).ToArrayAsync());
        Assert.Equal(2, await db.InventoryUnitIdentityOwnerships.CountAsync(x => x.NormalizedValue == serial || x.NormalizedValue == originalSerial));
    }

    private static async Task AssertProductAndBalanceRowsAvailableAsync(Guid productId)
    {
        await using var sentinel = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await sentinel.OpenAsync();
        await using var transaction = await sentinel.BeginTransactionAsync();
        try
        {
            await using var product = new NpgsqlCommand("SELECT id FROM catalog.products WHERE id=@id FOR UPDATE NOWAIT", sentinel, transaction);
            product.Parameters.AddWithValue("id", productId);
            Assert.Equal(productId, await product.ExecuteScalarAsync());
            await using var balance = new NpgsqlCommand("SELECT product_id FROM inventory.stock_balances WHERE product_id=@id FOR UPDATE NOWAIT", sentinel, transaction);
            balance.Parameters.AddWithValue("id", productId);
            Assert.Equal(productId, await balance.ExecuteScalarAsync());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static CreatePurchaseCommand Purchase(SerializedProductFixture seed, Guid operation, string invoice, string serial) =>
        new(seed.SupplierId, invoice, DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, operation,
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m,
                [new SerializedIdentityInput(serial)])]);

    private static async Task AssertLockWaitAsync(string application, long identityKey)
    {
        await using var observer = new NpgsqlConnection(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB"));
        await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var query = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity a JOIN pg_locks l ON l.pid=a.pid
                WHERE a.application_name=@name AND a.wait_event_type='Lock' AND a.wait_event='advisory'
                AND l.locktype='advisory' AND NOT l.granted AND l.objsubid=1
                AND l.classid::bigint=@high AND l.objid::bigint=@low)
                """, observer);
            query.Parameters.AddWithValue("name", application);
            var bits = unchecked((ulong)identityKey);
            query.Parameters.AddWithValue("high", (long)(bits >> 32));
            query.Parameters.AddWithValue("low", (long)(bits & uint.MaxValue));
            if ((bool)(await query.ExecuteScalarAsync(timeout.Token))!)
            {
                return;
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class LockTrace : DbCommandInterceptor
    {
        private readonly Dictionary<long, int> _expected = [];
        private bool _recording;
        public ConcurrentQueue<int> Ranks { get; } = new();
        public ConcurrentQueue<string> AcquiredKeys { get; } = new();
        public ConcurrentQueue<long> UnknownAdvisoryKeys { get; } = new();
        public static long Key(string scopedKey) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(scopedKey)), 0);
        public void Expect(string scopedKey, int rank) => _expected.Add(Key(scopedKey), rank);
        public void Start() => _recording = true;
        public void Acquired(string scopedKey)
        {
            if (!_recording)
            {
                return;
            }
            AcquiredKeys.Enqueue(scopedKey);
            var key = Key(scopedKey);
            if (_expected.TryGetValue(key, out var rank))
            {
                Ranks.Enqueue(rank);
            }
            else
            {
                UnknownAdvisoryKeys.Enqueue(key);
            }
        }
        private void Record(DbCommand command)
        {
            if (!_recording)
            {
                return;
            }
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
            {
                var key = (long)command.Parameters[0].Value!;
                if (_expected.TryGetValue(key, out var rank))
                {
                    Ranks.Enqueue(rank);
                }
                else
                {
                    UnknownAdvisoryKeys.Enqueue(key);
                }
            }
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) ||
                command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal))
            {
                Ranks.Enqueue(9);
            }
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // The production lock executes its DbCommand directly, outside EF's
    // interceptor. Record successful first acquisitions through its interfaces;
    // the real PostgreSQL implementation remains the delegated authority.
    private sealed class RecordingLock(PostgresOperationLock inner, EdgeRetailsDbContext db, LockTrace trace)
        : IOperationLock, IResourceLock
    {
        private Guid? _transaction;
        private readonly HashSet<string> _held = new(StringComparer.Ordinal);
        public async Task AcquireAsync(Guid operation, CancellationToken cancellationToken)
        {
            await inner.AcquireAsync(operation, cancellationToken);
            Record("operation:" + operation.ToString("D"));
        }
        public Task AcquireAsync(string type, Guid id, CancellationToken cancellationToken) =>
            AcquireAsync(type, id.ToString("D"), cancellationToken);
        public async Task AcquireAsync(string type, string key, CancellationToken cancellationToken)
        {
            await inner.AcquireAsync(type, key, cancellationToken);
            Record($"resource:{type.Trim().ToLowerInvariant()}:{key.Trim()}");
        }
        private void Record(string key)
        {
            var transaction = db.Database.CurrentTransaction!.TransactionId;
            if (_transaction != transaction)
            {
                _held.Clear();
                _transaction = transaction;
            }
            if (_held.Add(key))
            {
                trace.Acquired(key);
            }
        }
    }
}
