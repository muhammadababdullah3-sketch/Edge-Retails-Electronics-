using System.Text.Json;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5R1ExactWarrantyMatrixPostgresTests
{
    // NEW_COVERAGE: canonical purchases, exact provenance, plural disposition and economics.
    public static IEnumerable<object[]> Outcomes()
    {
        foreach (var mode in new[] { TrackingMode.Serialized, TrackingMode.IndividualPiece })
        {
            foreach (var outcome in new[] { WarrantyResolutionType.Repaired, WarrantyResolutionType.Rejected,
                         WarrantyResolutionType.Scrapped, WarrantyResolutionType.Credited, WarrantyResolutionType.Replaced })
            {
                yield return [mode, outcome];
            }
        }
    }

    public static IEnumerable<object[]> Sequences()
    {
        foreach (var row in Outcomes())
        {
            foreach (var order in new[] { 0, 1, 2 }) // one spanning operation; A then B; B then A
            {
                yield return [row[0], row[1], order];
            }
        }
    }

    public static IEnumerable<object[]> GraphGuards()
    {
        foreach (var mode in new[] { TrackingMode.Serialized, TrackingMode.IndividualPiece })
        {
            foreach (var table in new[] { "inventory.movements", "inventory.movement_units", "inventory.movement_effects",
                         "inventory.units", "inventory.lots", "purchasing.purchase_items", "purchasing.purchases", "catalog.supplier_products" })
            {
                foreach (var delete in new[] { false, true })
                {
                    yield return [mode, table, delete];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(GraphGuards))]
    public async Task CommittedExactGraphRejectsRawUpdateAndDelete(TrackingMode mode, string table, bool delete)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        await ReceiveSuccessAsync(provider, Command(f, WarrantyResolutionType.Repaired, f.Ids));
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var send = await db.ShopWarrantySendAllocations.SingleAsync(x => x.CaseId == f.Case && x.OriginalInventoryLotId == f.Lots[0]);
        var id = table switch
        {
            "inventory.movements" => send.SendMovementId,
            "inventory.movement_units" => (await db.InventoryMovementUnits.SingleAsync(x => x.MovementId == send.SendMovementId && x.InventoryUnitId == f.Ids[0])).Id,
            "inventory.movement_effects" => (await db.InventoryMovementEffects.SingleAsync(x => x.MovementId == send.SendMovementId && x.StockBucket == InventoryBucket.WithSupplier)).Id,
            "inventory.units" => f.Ids[0],
            "inventory.lots" => f.Lots[0],
            "purchasing.purchase_items" => f.Items[0],
            "purchasing.purchases" => (await db.PurchaseItems.SingleAsync(x => x.Id == f.Items[0])).PurchaseId,
            "catalog.supplier_products" => (await db.InventoryUnits.SingleAsync(x => x.Id == f.Ids[0])).SupplierProductId!.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        var update = table switch
        {
            "inventory.movements" => "reason='hostile rewrite'",
            "inventory.movement_units" => "to_status=5",
            "inventory.movement_effects" => "quantity_delta=quantity_delta+1",
            "inventory.units" => "acquisition_cost=acquisition_cost+1",
            "inventory.lots" => "purchase_item_id=@replacement",
            "purchasing.purchase_items" => "product_id=@replacement",
            "purchasing.purchases" => "supplier_id=@replacement",
            "catalog.supplier_products" => "supplier_id=@replacement",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        var before = await SnapshotAsync(provider);
        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var sourceBefore = await ReadRawRowAsync(connection, table, id);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await using var command = new NpgsqlCommand(delete ? $"DELETE FROM {table} WHERE id=@id" :
                $"UPDATE {table} SET {update} WHERE id=@id", connection, (NpgsqlTransaction)transaction.GetDbTransaction());
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("replacement", Guid.NewGuid());
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("PASS5_PROVENANCE_APPEND_ONLY: exact shop warranty graph provenance cannot be changed", error.MessageText);
            await transaction.RollbackAsync();
        }
        Assert.Equal(sourceBefore, await ReadRawRowAsync(connection, table, id));
        Assert.Equal(before, await SnapshotAsync(provider));
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, "movements")]
    [InlineData(TrackingMode.Serialized, "movement_units")]
    [InlineData(TrackingMode.Serialized, "movement_effects")]
    [InlineData(TrackingMode.IndividualPiece, "movements")]
    [InlineData(TrackingMode.IndividualPiece, "movement_units")]
    [InlineData(TrackingMode.IndividualPiece, "movement_effects")]
    public async Task RawGraphInsertIsDeferredToCallerCommitAndFullyRollsBack(TrackingMode mode, string table)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var unsent = f.Ids[0];
        if (table == "movement_units")
        {
            await using var receipt = provider.CreateAsyncScope();
            var receiptDb = receipt.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            // A distinct unsent physical ID avoids the immediate unique link constraint.
            // This proof specifically reaches the deferred graph validator at COMMIT.
            unsent = await PurchaseOneAsync(receipt.ServiceProvider, receiptDb, f.Product,
                f.ProductUnit, f.Supplier, f.Actor, mode, 90m);
        }
        var before = await SnapshotAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var send = await db.ShopWarrantySendAllocations.FirstAsync(x => x.CaseId == f.Case);
            await using var transaction = await db.Database.BeginTransactionAsync();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var sql = table switch
            {
                "movements" => "INSERT INTO inventory.movements SELECT (jsonb_populate_record(NULL::inventory.movements, to_jsonb(m)||jsonb_build_object('id',@id))).* FROM inventory.movements m WHERE m.id=@movement",
                "movement_units" => "INSERT INTO inventory.movement_units(id,movement_id,inventory_unit_id,from_status,to_status) VALUES(@id,@movement,@unit,4,6)",
                "movement_effects" => "INSERT INTO inventory.movement_effects(id,movement_id,stock_bucket,quantity_delta,quantity_before,quantity_after) VALUES(@id,@movement,4,1,0,1)",
                _ => throw new ArgumentOutOfRangeException(nameof(table))
            };
            await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("movement", send.SendMovementId);
            command.Parameters.AddWithValue("unit", unsent);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.StartsWith("PASS5_PROVENANCE_REQUIRED:", error.MessageText);
        }
        Assert.Equal(before, await SnapshotAsync(provider));
    }

    [Theory]
    [InlineData(TrackingMode.Serialized)]
    [InlineData(TrackingMode.IndividualPiece)]
    public async Task SamePhysicalIdentityCannotFundSecondCaseButLegitimateLaterResendCan(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var before = await SnapshotAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var refused = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new(f.Product, InventoryBucket.Damaged, 1m, f.Supplier, null, "same physical source",
                    f.Actor, Guid.NewGuid(), [f.Ids[0]]), default);
            Assert.False(refused.IsSuccess);
        }
        Assert.Equal(before, await SnapshotAsync(provider));
        await ReceiveSuccessAsync(provider, Command(f, WarrantyResolutionType.Repaired, [f.Ids[0]]));
        await using (var scope = provider.CreateAsyncScope())
        {
            var damage = await scope.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
                new(f.Product, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, f.Actor,
                    "Legitimate later defect", InventoryUnitIds: [f.Ids[0]]), default);
            Assert.True(damage.IsSuccess, damage.Error?.Message);
            var send = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new(f.Product, InventoryBucket.Damaged, 1m, f.Supplier, null, "later source case",
                    f.Actor, Guid.NewGuid(), [f.Ids[0]]), default);
            Assert.True(send.IsSuccess, send.Error?.Message);
            Assert.NotEqual(f.Case, send.Value);
        }
        // First case remaining selection must stay scoped to its immutable source identities.
        await ReceiveSuccessAsync(provider, Command(f, WarrantyResolutionType.Repaired, [f.Ids[1]]));
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(ShopWarrantyCaseStatus.Closed, (await db.ShopStockWarrantyCases.SingleAsync(x => x.Id == f.Case)).Status);
        Assert.Equal(InventoryUnitStatus.WithSupplier, (await db.InventoryUnits.SingleAsync(x => x.Id == f.Ids[0])).Status);
        Assert.Equal(1m, (await db.StockBalances.SingleAsync(x => x.ProductId == f.Product)).WithSupplierQty);
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task ClosedAndWrittenOffCasesReplayWithoutMutationAndRejectChangedPayload(
        TrackingMode mode, WarrantyResolutionType outcome)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var command = Command(f, outcome, f.Ids);
        await ReceiveSuccessAsync(provider, command);
        await AssertStateAsync(provider, f, outcome, f.Ids);
        var before = await SnapshotAsync(provider);
        await ReceiveSuccessAsync(provider, command);
        Assert.Equal(before, await SnapshotAsync(provider));
        await using var scope = provider.CreateAsyncScope();
        var mismatch = await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
            command with { OriginalInventoryUnitIds = [f.Ids[0]], ResolvedQuantity = 1m }, default);
        Assert.False(mismatch.IsSuccess);
        Assert.Equal(before, await SnapshotAsync(provider));
    }

    private static async Task<string> ReadRawRowAsync(NpgsqlConnection connection, string table, Guid id)
    {
        await using var command = new NpgsqlCommand($"SELECT row_to_json(t)::text FROM {table} t WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task SupportedLegacyGraphOnlyCaseResolvesPartiallyWithoutBackfillOrSourceOverwrite(
        TrackingMode mode, WarrantyResolutionType outcome)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedLegacyGraphAsync(provider, mode);
        var resolved = new List<Guid>();
        foreach (var id in f.Ids)
        {
            await ReceiveSuccessAsync(provider, Command(f, outcome, [id]));
            resolved.Add(id);
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var c = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == f.Case);
            Assert.Equal(f.Items[0], c.SourcePurchaseItemId);
            Assert.Empty(await db.ShopWarrantySendAllocations.Where(x => x.CaseId == f.Case).ToArrayAsync());
            var movements = await db.InventoryMovements.Where(x => x.ReferenceType == "SHOP_WARRANTY" &&
                x.ReferenceId == f.Case).ToArrayAsync();
            var movementIds = movements.Select(x => x.Id).ToArray();
            Assert.Empty(await db.ShopWarrantyResolutionAllocations.Where(x => movementIds.Contains(x.ResolutionMovementId)).ToArrayAsync());
            var resolutionMovementIds = movements.Where(x => x.MovementType != InventoryMovementType.SendToSupplierWarranty)
                .Select(x => x.Id).ToArray();
            var links = await db.InventoryMovementUnits.Where(x => resolutionMovementIds.Contains(x.MovementId) &&
                x.FromStatus == InventoryUnitStatus.WithSupplier).ToArrayAsync();
            Assert.Equal(resolved.Order(), links.Select(x => x.InventoryUnitId).Order());
            Assert.Equal(resolved.Count < 2 ? ShopWarrantyCaseStatus.WithSupplier :
                outcome == WarrantyResolutionType.Scrapped ? ShopWarrantyCaseStatus.WrittenOff : ShopWarrantyCaseStatus.Closed, c.Status);
            Assert.Equal(resolved.Count == 2, c.ClosedAt.HasValue);
            var removed = resolved.Sum(unit => unit == f.Ids[0] ? 100m : 140m);
            var cost = await db.ProductCostStates.SingleAsync(x => x.ProductId == f.Product);
            var removal = outcome is WarrantyResolutionType.Scrapped or WarrantyResolutionType.Credited;
            Assert.Equal(removal ? 240m - removed : 240m, cost.TotalInventoryCost);
            Assert.Equal(2m - resolved.Count, (await db.StockBalances.SingleAsync(x => x.ProductId == f.Product)).WithSupplierQty);
            if (outcome == WarrantyResolutionType.Credited)
            {
                Assert.Equal(removed, c.InventoryCarryingCostResolved);
                Assert.Equal(resolved.Count * 160m - removed, c.RecoveryDifference);
                Assert.Equal(resolved.Count * 160m, await AssertCanonicalCreditEntriesAsync(db, f));
            }
            foreach (var selected in resolved)
            {
                var index = Array.IndexOf(f.Ids, selected);
                var original = await db.InventoryUnits.SingleAsync(x => x.Id == selected);
                Assert.Equal(f.Lots[index], original.InventoryLotId);
                Assert.Equal(f.Items[index], original.SourcePurchaseItemId);
                Assert.Equal(outcome switch
                {
                    WarrantyResolutionType.Repaired => InventoryUnitStatus.InStock,
                    WarrantyResolutionType.Rejected => InventoryUnitStatus.Defective,
                    WarrantyResolutionType.Scrapped => InventoryUnitStatus.Scrapped,
                    _ => InventoryUnitStatus.SupplierReturned
                }, original.Status);
            }
        }
    }

    private static async Task<Fixture> SeedLegacyGraphAsync(ServiceProvider provider, TrackingMode mode)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode;
        product.SerialTrackingEnabled = mode == TrackingMode.Serialized;
        await db.SaveChangesAsync();
        var ids = new[]
        {
            await PurchaseOneAsync(scope.ServiceProvider, db, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, mode, 100m),
            await PurchaseOneAsync(scope.ServiceProvider, db, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, mode, 140m)
        };
        var units = await db.InventoryUnits.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync();
        var lots = ids.Select(id => units.Single(x => x.Id == id).InventoryLotId!.Value).ToArray();
        var items = ids.Select(id => units.Single(x => x.Id == id).SourcePurchaseItemId!.Value).ToArray();
        var damage = await scope.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 2m, seed.ActorId,
                "Legacy graph fixture", InventoryUnitIds: ids), default);
        Assert.True(damage.IsSuccess, damage.Error?.Message);
        // Model the established old writer with a genuine canonical movement graph.
        // Never remove typed allocations or synthesize historical immutable facts.
        var c = new ShopStockWarrantyCase
        {
            CaseNumber = "LEGACY-H02-" + Guid.NewGuid().ToString("N"), ProductId = seed.ProductId,
            SupplierId = seed.SupplierId, SourcePurchaseItemId = items[0], BaseQuantity = 2m,
            FaultDescription = "Supported old exact writer", CreatedBy = seed.ActorId,
            CreatedAt = DateTimeOffset.UtcNow, Status = ShopWarrantyCaseStatus.Open
        };
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.ShopStockWarrantyCases.Add(c);
        await db.SaveChangesAsync();
        var transfer = await scope.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new(seed.ProductId, InventoryBucket.Damaged, InventoryBucket.WithSupplier, 2m, seed.ActorId,
                "Legacy exact send", ReferenceType: "SHOP_WARRANTY", ReferenceId: c.Id,
                InventoryUnitIds: ids, MovementTypeOverride: InventoryMovementType.SendToSupplierWarranty), default);
        Assert.True(transfer.IsSuccess, transfer.Error?.Message);
        c.Status = ShopWarrantyCaseStatus.WithSupplier;
        c.SentAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        Assert.Empty(await db.ShopWarrantySendAllocations.Where(x => x.CaseId == c.Id).ToArrayAsync());
        return new(seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, c.Id, mode, ids, lots, items);
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public async Task FiveOutcomesPreserveEachOriginalSourceAndTrueAggregateRemaining(
        TrackingMode mode, WarrantyResolutionType outcome, int order)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var sendsBefore = await SendFactsAsync(provider, f);
        var selections = order == 0 ? new[] { f.Ids } : order == 1
            ? new[] { new[] { f.Ids[0] }, new[] { f.Ids[1] } }
            : new[] { new[] { f.Ids[1] }, new[] { f.Ids[0] } };
        var resolved = new List<Guid>();
        foreach (var selected in selections)
        {
            var command = Command(f, outcome, selected);
            await ReceiveSuccessAsync(provider, command);
            resolved.AddRange(selected);
            await AssertStateAsync(provider, f, outcome, resolved);
            Assert.Equal(sendsBefore, await SendFactsAsync(provider, f));
        }
    }

    [Theory]
    [InlineData(TrackingMode.Serialized)]
    [InlineData(TrackingMode.IndividualPiece)]
    public async Task DuplicateNotSentWrongProductWrongSupplierAndQuantityRefuseWithoutMutation(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var other = await SeedAsync(provider, mode);
        Guid notSent;
        Guid wrongSupplierUnit;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            notSent = await PurchaseOneAsync(scope.ServiceProvider, db, f.Product, f.ProductUnit,
                f.Supplier, f.Actor, mode, 90m);
            wrongSupplierUnit = await PurchaseOneAsync(scope.ServiceProvider, db, f.Product, f.ProductUnit,
                other.Supplier, f.Actor, mode, 80m);
            var damage = await scope.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
                new(f.Product, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, f.Actor,
                    "Wrong supplier source fixture", InventoryUnitIds: [wrongSupplierUnit]), default);
            Assert.True(damage.IsSuccess, damage.Error?.Message);
        }
        foreach (var command in new[]
        {
            Command(f, WarrantyResolutionType.Repaired, [f.Ids[0], f.Ids[0]]),
            Command(f, WarrantyResolutionType.Repaired, [notSent]),
            Command(f, WarrantyResolutionType.Repaired, [other.Ids[0]]),
            Command(f, WarrantyResolutionType.Repaired, [Guid.NewGuid()]),
            Command(f, WarrantyResolutionType.Repaired, [f.Ids[0]]) with { ResolvedQuantity = 2m },
            Command(f, WarrantyResolutionType.Repaired, f.Ids) with { ResolvedQuantity = 3m },
            Command(f, WarrantyResolutionType.Repaired, [f.Ids[0]]) with { ResolvedQuantity = 1.00001m }
        })
        {
            var before = await SnapshotAsync(provider);
            await using var scope = provider.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(command, default);
            Assert.False(result.IsSuccess);
            Assert.Equal(before, await SnapshotAsync(provider));
        }
        // Canonical source supplier is enforced even when physical IDs and product are otherwise valid.
        var beforeWrongSupplier = await SnapshotAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new(f.Product, InventoryBucket.Damaged, 1m, f.Supplier, null, "wrong supplier",
                    f.Actor, Guid.NewGuid(), [wrongSupplierUnit]), default);
            Assert.False(result.IsSuccess);
        }
        Assert.Equal(beforeWrongSupplier, await SnapshotAsync(provider));
        await ReceiveSuccessAsync(provider, Command(f, WarrantyResolutionType.Repaired, [f.Ids[0]]));
        var beforeDuplicate = await SnapshotAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
                Command(f, WarrantyResolutionType.Rejected, [f.Ids[0]]), default);
            Assert.False(result.IsSuccess);
        }
        Assert.Equal(beforeDuplicate, await SnapshotAsync(provider));
        await using var verify = provider.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(ShopWarrantyCaseStatus.WithSupplier,
            (await verifyDb.ShopStockWarrantyCases.SingleAsync(x => x.Id == f.Case)).Status);
        Assert.Equal(InventoryUnitStatus.WithSupplier,
            (await verifyDb.InventoryUnits.SingleAsync(x => x.Id == f.Ids[1])).Status);
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task CommittedReplayAndPayloadMismatchKeepOneImmutableDisposition(
        TrackingMode mode, WarrantyResolutionType outcome)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var command = Command(f, outcome, [f.Ids[0]]);
        await ReceiveSuccessAsync(provider, command);
        var committed = await SnapshotAsync(provider);
        await ReceiveSuccessAsync(provider, command);
        Assert.Equal(committed, await SnapshotAsync(provider));
        await using var scope = provider.CreateAsyncScope();
        var mismatch = await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
            command with { OriginalInventoryUnitIds = [f.Ids[1]] }, default);
        Assert.False(mismatch.IsSuccess);
        Assert.Equal(committed, await SnapshotAsync(provider));
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task CallerOwnedRollbackRestoresCustodyAllocationsEconomicsAuditAndOutcome(
        TrackingMode mode, WarrantyResolutionType outcome)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var before = await SnapshotAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var result = await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
                Command(f, outcome, f.Ids), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(2m, await db.ShopWarrantyResolutionAllocations
                .Where(x => db.ShopWarrantySendAllocations.Where(a => a.CaseId == f.Case).Select(a => a.Id).Contains(x.SendAllocationId))
                .SumAsync(x => x.ResolvedBaseQuantity));
            await transaction.RollbackAsync();
        }
        Assert.Equal(before, await SnapshotAsync(provider));
    }

    [Theory]
    [InlineData(TrackingMode.Serialized, false)]
    [InlineData(TrackingMode.Serialized, true)]
    [InlineData(TrackingMode.IndividualPiece, false)]
    [InlineData(TrackingMode.IndividualPiece, true)]
    public async Task ConcurrentSameOrDisjointIdentitiesRespectRemainingCapacity(TrackingMode mode, bool disjoint)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var arrivals = 0;
        async Task<bool> ResolveAsync(Guid id)
        {
            await using var scope = provider.CreateAsyncScope();
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                gate.SetResult();
            }
            await gate.Task.WaitAsync(deadline.Token);
            var result = await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(Command(f, WarrantyResolutionType.Repaired, [id]), deadline.Token);
            return result.IsSuccess;
        }
        // Await both tasks to completion even if either fails. The shared deadline cancels
        // pending locks/commands; no timeout wrapper can abandon a running task or scope.
        var results = await Task.WhenAll(ResolveAsync(f.Ids[0]), ResolveAsync(f.Ids[disjoint ? 1 : 0]));
        Assert.Equal(disjoint ? 2 : 1, results.Count(x => x));
        await AssertStateAsync(provider, f, WarrantyResolutionType.Repaired,
            disjoint ? f.Ids : [f.Ids[0]]);
    }

    private sealed record Fixture(Guid Product, Guid ProductUnit, Guid Supplier, Guid Actor,
        Guid Case, TrackingMode Mode, Guid[] Ids, Guid[] Lots, Guid[] Items);

    private static async Task<Fixture> SeedAsync(ServiceProvider provider, TrackingMode mode)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode;
        product.SerialTrackingEnabled = mode == TrackingMode.Serialized;
        await db.SaveChangesAsync();
        var ids = new[]
        {
            await PurchaseOneAsync(scope.ServiceProvider, db, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, mode, 100m),
            await PurchaseOneAsync(scope.ServiceProvider, db, seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, mode, 140m)
        };
        var units = await db.InventoryUnits.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync();
        var lots = ids.Select(id => units.Single(x => x.Id == id).InventoryLotId!.Value).ToArray();
        var items = ids.Select(id => units.Single(x => x.Id == id).SourcePurchaseItemId!.Value).ToArray();
        Assert.Equal(2, lots.Distinct().Count());
        Assert.Equal(2, items.Distinct().Count());
        var damage = await scope.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 2m, seed.ActorId,
                "H02 exact matrix", InventoryUnitIds: ids), default);
        Assert.True(damage.IsSuccess, damage.Error?.Message);
        var send = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
            new(seed.ProductId, InventoryBucket.Damaged, 2m, seed.SupplierId, null, "H02 exact matrix",
                seed.ActorId, Guid.NewGuid(), ids), default);
        Assert.True(send.IsSuccess, send.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Null((await db.ShopStockWarrantyCases.SingleAsync(x => x.Id == send.Value)).SourcePurchaseItemId);
        var allocations = await db.ShopWarrantySendAllocations.Where(x => x.CaseId == send.Value).ToArrayAsync();
        Assert.Equal(2, allocations.Length);
        Assert.All(allocations, x => Assert.Equal(1m, x.BaseQuantity));
        Assert.Equal(lots.Order(), allocations.Select(x => x.OriginalInventoryLotId).Order());
        Assert.Equal(240m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == seed.ProductId)).TotalInventoryCost);
        return new(seed.ProductId, seed.ProductUnitId, seed.SupplierId, seed.ActorId, send.Value, mode, ids, lots, items);
    }

    private static async Task<Guid> PurchaseOneAsync(IServiceProvider services, EdgeRetailsDbContext db,
        Guid product, Guid productUnit, Guid supplier, Guid actor, TrackingMode mode, decimal cost)
    {
        var identities = mode == TrackingMode.Serialized
            ? new[] { new SerializedIdentityInput("H02-" + Guid.NewGuid().ToString("N")) }
            : Array.Empty<SerializedIdentityInput>();
        var result = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new(
            supplier, "H02-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, actor, Guid.NewGuid(),
            [new CreatePurchaseLineInput(product, productUnit, 1m, cost, 5000m, identities)]), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        db.ChangeTracker.Clear();
        var item = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == result.Value!.PurchaseId);
        return (await db.InventoryUnits.SingleAsync(x => x.SourcePurchaseItemId == item.Id)).Id;
    }

    private static ReceiveShopStockWarrantyCommand Command(Fixture f, WarrantyResolutionType outcome, Guid[] ids) =>
        new(f.Case, outcome, f.Actor, ids, outcome == WarrantyResolutionType.Replaced
                ? ids.Select(_ => new ReplacementSerializedUnitInput(f.Mode == TrackingMode.Serialized
                    ? "H02-R-" + Guid.NewGuid().ToString("N") : null, null, null)).ToArray() : null,
            "H02 exact disposition", Guid.NewGuid(),
            SupplierCreditAmount: outcome == WarrantyResolutionType.Credited ? ids.Length * 160m : null,
            ResolvedQuantity: ids.Length);

    private static async Task ReceiveSuccessAsync(ServiceProvider provider, ReceiveShopStockWarrantyCommand command)
    {
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(command, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    private static async Task AssertStateAsync(ServiceProvider provider, Fixture f, WarrantyResolutionType outcome,
        IReadOnlyCollection<Guid> resolved)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var sends = await db.ShopWarrantySendAllocations.AsNoTracking().Where(x => x.CaseId == f.Case).ToArrayAsync();
        var sendIds = sends.Select(x => x.Id).ToArray();
        var allocations = await db.ShopWarrantyResolutionAllocations.AsNoTracking()
            .Where(x => sendIds.Contains(x.SendAllocationId)).ToArrayAsync();
        Assert.Equal((decimal)resolved.Count, allocations.Sum(x => x.ResolvedBaseQuantity));
        foreach (var send in sends)
        {
            var index = Array.IndexOf(f.Lots, send.OriginalInventoryLotId);
            var done = resolved.Contains(f.Ids[index]);
            var rows = allocations.Where(x => x.SendAllocationId == send.Id).ToArray();
            Assert.Equal(done ? 1 : 0, rows.Length);
            if (done)
            {
                var row = Assert.Single(rows);
                Assert.Equal(1m, row.ResolvedBaseQuantity);
                Assert.Equal(outcome, row.ResolutionOutcome);
                Assert.Equal(outcome is WarrantyResolutionType.Scrapped or WarrantyResolutionType.Credited
                    ? index == 0 ? 100m : 140m : 0m, row.ActualResolvedCarryingValue);
                var links = await db.InventoryMovementUnits.AsNoTracking()
                    .Where(x => x.MovementId == row.ResolutionMovementId && x.FromStatus == InventoryUnitStatus.WithSupplier)
                    .ToArrayAsync();
                Assert.Contains(links, x => x.InventoryUnitId == f.Ids[index]);
            }
            var unit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == f.Ids[index]);
            Assert.Equal(f.Lots[index], unit.InventoryLotId);
            Assert.Equal(f.Items[index], unit.SourcePurchaseItemId);
            Assert.Equal(done ? outcome switch
            {
                WarrantyResolutionType.Repaired => InventoryUnitStatus.InStock,
                WarrantyResolutionType.Rejected => InventoryUnitStatus.Defective,
                WarrantyResolutionType.Scrapped => InventoryUnitStatus.Scrapped,
                _ => InventoryUnitStatus.SupplierReturned
            } : InventoryUnitStatus.WithSupplier, unit.Status);
            Assert.Equal(done ? 0m : 1m, await db.InventoryLotBucketBalances.Where(x =>
                x.LotId == f.Lots[index] && x.StockBucket == InventoryBucket.WithSupplier).SumAsync(x => x.Quantity));
        }
        var c = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == f.Case);
        Assert.Equal(resolved.Count < 2 ? ShopWarrantyCaseStatus.WithSupplier :
            outcome == WarrantyResolutionType.Scrapped ? ShopWarrantyCaseStatus.WrittenOff : ShopWarrantyCaseStatus.Closed, c.Status);
        Assert.Equal(resolved.Count == 2, c.ClosedAt.HasValue);
        var removed = resolved.Sum(id => id == f.Ids[0] ? 100m : 140m);
        var economicRemoval = outcome is WarrantyResolutionType.Scrapped or WarrantyResolutionType.Credited;
        var cost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
        Assert.Equal(economicRemoval ? 240m - removed : 240m, cost.TotalInventoryCost);
        Assert.Equal(economicRemoval ? 2m - resolved.Count : 2m, cost.CostedQty);
        var stock = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
        Assert.Equal(2m - resolved.Count, stock.WithSupplierQty);
        if (outcome == WarrantyResolutionType.Credited)
        {
            Assert.Equal(resolved.Count * 160m, allocations.Sum(x => x.SupplierCreditAmount ?? 0m));
            Assert.Equal(resolved.Count * 160m, await AssertCanonicalCreditEntriesAsync(db, f));
            Assert.Equal(removed, c.InventoryCarryingCostResolved);
            Assert.Equal(resolved.Count * 160m - removed, c.RecoveryDifference);
        }
        else
        {
            Assert.Equal(0m, await AssertCanonicalCreditEntriesAsync(db, f));
        }
        if (outcome == WarrantyResolutionType.Replaced)
        {
            var replacements = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == f.Product &&
                x.OriginType == InventoryUnitOriginType.WarrantyReplacement).ToArrayAsync();
            Assert.Equal(resolved.Count, replacements.Length);
            foreach (var id in resolved)
            {
                var index = Array.IndexOf(f.Ids, id);
                var replacement = Assert.Single(replacements, x => x.InventoryLotId == f.Lots[index]);
                Assert.Equal(index == 0 ? 100m : 140m, replacement.AcquisitionCost);
                Assert.Equal(InventoryUnitStatus.InStock, replacement.Status);
            }
        }
        Assert.Empty(await db.SaleItems.Where(x => x.ProductId == f.Product).ToArrayAsync());
    }

    private static async Task<string> SendFactsAsync(ServiceProvider provider, Fixture f)
    {
        await using var scope = provider.CreateAsyncScope();
        return JsonSerializer.Serialize(await scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>()
            .ShopWarrantySendAllocations.AsNoTracking().Where(x => x.CaseId == f.Case).OrderBy(x => x.Id).ToArrayAsync());
    }

    // ASSERTION_CHANGE: canonical §216.1/219.4 reference is the immutable resolution
    // movement, not its case. Preserve totals and require a bijection plus operation lineage.
    private static async Task<decimal> AssertCanonicalCreditEntriesAsync(EdgeRetailsDbContext db, Fixture f)
    {
        var movements = await db.InventoryMovements.AsNoTracking().Where(x =>
            x.ReferenceType == "SHOP_WARRANTY" && x.ReferenceId == f.Case).ToArrayAsync();
        var movementIds = movements.Select(x => x.Id).ToArray();
        var operations = movements.Select(x => x.CorrelationId).ToArray();
        var credits = movements.Where(x => x.MovementType == InventoryMovementType.WarrantyCreditResolution).ToArray();
        var entries = await db.SupplierAccountEntries.AsNoTracking().Where(x =>
            x.ReferenceId == f.Case || (x.ReferenceId.HasValue && movementIds.Contains(x.ReferenceId.Value)) ||
            (x.ClientOperationId.HasValue && operations.Contains(x.ClientOperationId.Value))).ToArrayAsync();
        Assert.Equal(credits.Length, entries.Length);
        Assert.DoesNotContain(entries, x => x.ReferenceId == f.Case);
        foreach (var credit in credits)
        {
            var entry = Assert.Single(entries, x => x.ReferenceId == credit.Id);
            Assert.Equal("WarrantyResolution", entry.ReferenceType);
            Assert.Equal(SupplierAccountEntryType.WarrantyCredit, entry.EntryType);
            Assert.Equal(SupplierAccountDirection.DecreasePayable, entry.Direction);
            Assert.Equal(f.Supplier, entry.SupplierId);
            Assert.Equal(credit.ActorId, entry.ActorId);
            Assert.Equal(credit.CorrelationId, entry.ClientOperationId);
            var physicalCount = await db.InventoryMovementUnits.CountAsync(x =>
                x.MovementId == credit.Id && x.FromStatus == InventoryUnitStatus.WithSupplier);
            Assert.Equal(physicalCount * 160m, entry.Amount);
            var allocations = await db.ShopWarrantyResolutionAllocations.Where(x =>
                x.ResolutionMovementId == credit.Id).ToArrayAsync();
            if (allocations.Length > 0)
            {
                Assert.Equal(entry.Amount, allocations.Sum(x => x.SupplierCreditAmount ?? 0m));
                Assert.All(allocations, x => Assert.Equal(entry.ClientOperationId, x.ClientOperationId));
            }
        }
        return entries.Sum(x => x.Amount);
    }

    private static async Task<string> SnapshotAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        return JsonSerializer.Serialize(new
        {
            Units = await db.InventoryUnits.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Cases = await db.ShopStockWarrantyCases.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Sends = await db.ShopWarrantySendAllocations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Resolutions = await db.ShopWarrantyResolutionAllocations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Stock = await db.StockBalances.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Costs = await db.ProductCostStates.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Lots = await db.InventoryLots.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            LotBalances = await db.InventoryLotBucketBalances.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Consumptions = await db.InventoryLotConsumptions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Movements = await db.InventoryMovements.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Effects = await db.InventoryMovementEffects.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Links = await db.InventoryMovementUnits.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            SupplierEntries = await db.SupplierAccountEntries.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await db.BusinessAuditEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Outcomes = await db.OperationOutcomes.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Outbox = await db.OutboxMessages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
        });
    }
}
