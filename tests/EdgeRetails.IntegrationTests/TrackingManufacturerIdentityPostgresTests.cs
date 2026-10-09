using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[CollectionDefinition("TrackingManufacturerIdentityPg", DisableParallelization = true)]
public sealed class TrackingManufacturerIdentityPgCollection { }

[Collection("TrackingManufacturerIdentityPg")]
public sealed class TrackingManufacturerIdentityPostgresTests
{
    [Fact]
    public async Task CrossSlotImeiConcurrency_AllowsExactlyOneActiveIdentityOwner()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (unitA, unitB) = await CreateTwoUnidentifiedPhysicalUnitsAsync(provider);
        var imei = MakeValidImei();

        var outcomes = await RaceClaimsAsync(
            unitA, ManufacturerIdentifierType.Imei, ManufacturerIdentifierSlot.Imei1, imei,
            unitB, ManufacturerIdentifierType.Imei, ManufacturerIdentifierSlot.Imei2, imei);

        Assert.Equal(1, outcomes.Count(x => x));
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(1, await db.InventoryUnitIdentityClaims.CountAsync(x =>
            x.IdentifierType == ManufacturerIdentifierType.Imei && x.NormalizedValue == imei));
        Assert.Equal(1, await db.InventoryUnitIdentityOwnerships.CountAsync(x => x.NormalizedValue == imei));
    }

    [Theory]
    [InlineData(ManufacturerIdentifierSlot.Imei1)]
    [InlineData(ManufacturerIdentifierSlot.Imei2)]
    public async Task SerialVersusImeiCrossTypeConcurrency_AllowsExactlyOneActiveOwner(
        ManufacturerIdentifierSlot imeiSlot)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (unitA, unitB) = await CreateTwoUnidentifiedPhysicalUnitsAsync(provider);
        var sharedValue = MakeValidImei();
        var outcomes = await RaceClaimsAsync(
            unitA, ManufacturerIdentifierType.Serial, ManufacturerIdentifierSlot.Serial, sharedValue,
            unitB, ManufacturerIdentifierType.Imei, imeiSlot, sharedValue);

        Assert.Equal(1, outcomes.Count(x => x));
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(1, await db.InventoryUnitIdentityOwnerships.CountAsync(x => x.NormalizedValue == sharedValue));
    }

    [Fact]
    public async Task NormalizedSerialConcurrency_AllowsExactlyOneLogicalSerialClaim()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (unitA, unitB) = await CreateTwoUnidentifiedPhysicalUnitsAsync(provider);
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var normalized = "ABC-" + suffix;

        var outcomes = await RaceClaimsAsync(
            unitA, ManufacturerIdentifierType.Serial, ManufacturerIdentifierSlot.Serial, normalized.ToLowerInvariant(),
            unitB, ManufacturerIdentifierType.Serial, ManufacturerIdentifierSlot.Serial, normalized);

        Assert.Equal(1, outcomes.Count(x => x));
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(1, await db.InventoryUnitIdentityClaims.CountAsync(x =>
            x.IdentifierType == ManufacturerIdentifierType.Serial && x.NormalizedValue == normalized));
    }

    [Fact]
    public async Task ReceiptVoid_RetainsHistoryReleasesActiveOwnershipAndAllowsGovernedReintake()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.ImeiTrackingEnabled = true;
        await db.SaveChangesAsync();

        var serial = "GOVERNED-" + Guid.NewGuid().ToString("N");
        var imei1 = MakeValidImei();
        var imei2 = MakeValidImei();
        var originalPurchase = await CreateSerializedPurchaseAsync(
            services, seed, "VOID-" + Guid.NewGuid().ToString("N"), serial, imei1, imei2);
        Assert.True(originalPurchase.IsSuccess, originalPurchase.Error?.Message);
        db.ChangeTracker.Clear();
        var original = await db.InventoryUnits.SingleAsync(x => x.SourcePurchaseItemId != null && x.ProductId == seed.ProductId);

        var voidResult = await services.GetRequiredService<VoidPurchaseHandler>().HandleAsync(
            new VoidPurchaseCommand(originalPurchase.Value!.PurchaseId, Guid.NewGuid(), seed.ActorId, "Receipt entered in error"),
            CancellationToken.None);
        Assert.True(voidResult.IsSuccess, voidResult.Error?.Message);
        db.ChangeTracker.Clear();
        var voided = await db.InventoryUnits.SingleAsync(x => x.Id == original.Id);
        Assert.Equal(InventoryUnitStatus.ReceiptVoided, voided.Status);
        Assert.Equal((original.SerialNumber, original.Imei1, original.Imei2, original.TrackingCode, original.ItemSequence),
            (voided.SerialNumber, voided.Imei1, voided.Imei2, voided.TrackingCode, voided.ItemSequence));
        Assert.Equal(3, await db.InventoryUnitIdentityClaims.CountAsync(x => x.InventoryUnitId == original.Id));
        await Assert.ThrowsAsync<PostgresException>(() => db.InventoryUnitIdentityClaims
            .Where(x => x.InventoryUnitId == original.Id)
            .ExecuteDeleteAsync());
        await Assert.ThrowsAsync<PostgresException>(() => db.InventoryUnitIdentityClaims
            .Where(x => x.InventoryUnitId == original.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RawValue, "ALTERED")));
        Assert.Empty(await db.InventoryUnitIdentityOwnerships
            .Where(x => x.NormalizedValue == IdentityNormalizationRules.NormalizeSerialNumber(serial) ||
                        x.NormalizedValue == IdentityNormalizationRules.NormalizeImeiIdentity(imei1) ||
                        x.NormalizedValue == IdentityNormalizationRules.NormalizeImeiIdentity(imei2))
            .ToArrayAsync());

        var reintakeOperationId = Guid.NewGuid();
        var reintakeInvoice = "REINTAKE-" + Guid.NewGuid().ToString("N");
        var reintake = await CreateSerializedPurchaseAsync(
            services, seed, reintakeInvoice, serial, imei1, imei2, reintakeOperationId);
        Assert.True(reintake.IsSuccess, reintake.Error?.Message);
        db.ChangeTracker.Clear();
        var units = await db.InventoryUnits.Where(x => x.ProductId == seed.ProductId)
            .OrderBy(x => x.ItemSequence).ToArrayAsync();
        Assert.Equal(2, units.Length);
        var replacement = Assert.Single(units, x => x.Status == InventoryUnitStatus.InStock);
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.NotEqual(original.TrackingCode, replacement.TrackingCode);
        Assert.NotEqual(original.ItemSequence, replacement.ItemSequence);
        Assert.Equal((original.SerialNumber, original.Imei1, original.Imei2),
            (voided.SerialNumber, voided.Imei1, voided.Imei2));
        Assert.Equal(3, await db.InventoryUnitIdentityClaims.CountAsync(x => x.InventoryUnitId == replacement.Id));
        Assert.Equal(3, await db.InventoryUnitIdentityOwnerships.CountAsync(x =>
            x.NormalizedValue == IdentityNormalizationRules.NormalizeSerialNumber(serial) ||
            x.NormalizedValue == IdentityNormalizationRules.NormalizeImeiIdentity(imei1) ||
            x.NormalizedValue == IdentityNormalizationRules.NormalizeImeiIdentity(imei2)));

        var replay = await CreateSerializedPurchaseAsync(
            services, seed, reintakeInvoice, serial, imei1, imei2, reintakeOperationId);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.True(replay.Value!.WasExisting);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(6, await db.InventoryUnitIdentityClaims.CountAsync(x =>
            x.InventoryUnitId == original.Id || x.InventoryUnitId == replacement.Id));
        Assert.Equal(3, await db.InventoryUnitIdentityOwnerships.CountAsync(x =>
            x.NormalizedValue == IdentityNormalizationRules.NormalizeSerialNumber(serial) ||
            x.NormalizedValue == IdentityNormalizationRules.NormalizeImeiIdentity(imei1) ||
            x.NormalizedValue == IdentityNormalizationRules.NormalizeImeiIdentity(imei2)));

        var scanner = services.GetRequiredService<EdgeRetails.Application.Features.Inventory.IPhase4WorkflowReadService>();
        var serialMatch = Assert.Single(await scanner.ResolveScannerAsync(serial, CancellationToken.None));
        Assert.Equal(replacement.Id, serialMatch.InventoryUnitId);

        // An arbitrary deletion cannot release a non-ReceiptVoided identity.
        var ownership = await db.InventoryUnitIdentityOwnerships.SingleAsync(
            x => x.NormalizedValue == IdentityNormalizationRules.NormalizeSerialNumber(serial));
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await db.InventoryUnitIdentityOwnerships.Where(x => x.NormalizedValue == ownership.NormalizedValue)
                .ExecuteDeleteAsync());
        Assert.True(await db.InventoryUnitIdentityOwnerships.AnyAsync(x => x.NormalizedValue == ownership.NormalizedValue));
    }

    [Fact]
    public async Task ReceiptVoidRollback_RestoresUnitAndActiveOwnershipTogether()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var serial = "ROLLBACK-" + Guid.NewGuid().ToString("N");
        var purchase = await CreateSerializedPurchaseAsync(
            scope.ServiceProvider, seed, "ROLLBACK-" + Guid.NewGuid().ToString("N"), serial, null, null);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        var normalized = IdentityNormalizationRules.NormalizeSerialNumber(serial);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            unit.Status = InventoryUnitStatus.ReceiptVoided;
            await db.SaveChangesAsync();
            await db.InventoryUnitIdentityOwnerships.Where(x => x.NormalizedValue == normalized)
                .ExecuteDeleteAsync();
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal(InventoryUnitStatus.InStock,
            (await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id)).Status);
        Assert.True(await db.InventoryUnitIdentityOwnerships.AnyAsync(x => x.NormalizedValue == normalized));
        Assert.Single(await db.InventoryUnitIdentityClaims.Where(x => x.InventoryUnitId == unit.Id).ToArrayAsync());
    }

    [Fact]
    public async Task PurchaseReturn_RetainsActiveManufacturerIdentityOwnership()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var serial = "RETURNED-" + Guid.NewGuid().ToString("N");
        var purchase = await CreateSerializedPurchaseAsync(
            services, seed, "RETURN-" + Guid.NewGuid().ToString("N"), serial, null, null);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        var item = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == purchase.Value!.PurchaseId);

        var result = await services.GetRequiredService<CreatePurchaseReturnHandler>().HandleAsync(
            new CreatePurchaseReturnCommand(
                purchase.Value!.PurchaseId,
                "Supplier return",
                null,
                PurchaseReturnSettlementMode.External,
                seed.ActorId,
                Guid.NewGuid(),
                [new PurchaseReturnLineInput(item.Id, 1m, 5000m, [unit.Id])]),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(InventoryUnitStatus.SupplierReturned,
            (await db.InventoryUnits.SingleAsync(x => x.Id == unit.Id)).Status);
        Assert.Single(await db.InventoryUnitIdentityClaims.Where(x => x.InventoryUnitId == unit.Id).ToArrayAsync());
        Assert.True(await db.InventoryUnitIdentityOwnerships.AnyAsync(x =>
            x.NormalizedValue == IdentityNormalizationRules.NormalizeSerialNumber(serial)));
    }

    [Fact]
    public async Task DirectStatusChangeAndOwnerDelete_WithoutPurchaseVoidProvenanceAreRejected()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db);
        var serial = "FORGED-" + Guid.NewGuid().ToString("N");
        var purchase = await CreateSerializedPurchaseAsync(
            scope.ServiceProvider, seed, "FORGED-" + Guid.NewGuid().ToString("N"), serial, null, null);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var unit = await db.InventoryUnits.SingleAsync(x => x.ProductId == seed.ProductId);
        var normalized = IdentityNormalizationRules.NormalizeSerialNumber(serial);

        await using var transaction = await db.Database.BeginTransactionAsync();
        unit.Status = InventoryUnitStatus.ReceiptVoided;
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.InventoryUnitIdentityOwnerships
            .Where(x => x.NormalizedValue == normalized)
            .ExecuteDeleteAsync());
        await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
    }

    [Fact]
    public async Task DirectInventoryUnitInsert_WithManufacturerIdentityButWithoutClaimAndOwnerIsRejected()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var (unitId, _) = await CreateTwoUnidentifiedPhysicalUnitsAsync(provider);
        var source = await db.InventoryUnits.SingleAsync(x => x.Id == unitId);
        var bypass = new InventoryUnit
        {
            ProductId = source.ProductId,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = source.SourcePurchaseItemId,
            SerialNumber = "BYPASS-" + Guid.NewGuid().ToString("N"),
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1m,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        db.InventoryUnits.Add(bypass);

        var saveFailure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(saveFailure.InnerException);
        db.ChangeTracker.Clear();
        Assert.False(await db.InventoryUnits.AnyAsync(x => x.Id == bypass.Id));

        var fabricatedValue = "FABRICATED-" + Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory.unit_identity_claims
                (id, inventory_unit_id, identifier_type, identifier_slot, raw_value,
                 normalized_value, normalization_version, created_at)
            VALUES ({Guid.NewGuid()}, {unitId}, 1, 1, {fabricatedValue}, {fabricatedValue}, 1, now())
            """));
        Assert.Empty(await db.InventoryUnitIdentityClaims
            .Where(x => x.InventoryUnitId == unitId)
            .ToArrayAsync());
    }

    private static Task<EdgeRetails.Application.Common.Result<CreatePurchaseResult>> CreateSerializedPurchaseAsync(
        IServiceProvider services,
        SerializedProductFixture seed,
        string invoice,
        string? serial,
        string? imei1,
        string? imei2,
        Guid? clientOperationId = null) =>
        services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, invoice, DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, clientOperationId ?? Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 5000m,
                [new SerializedIdentityInput(serial, imei1, imei2)])]), CancellationToken.None);

    private static async Task<(Guid A, Guid B)> CreateTwoUnidentifiedPhysicalUnitsAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var first = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var second = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        foreach (var productId in new[] { first.ProductId, second.ProductId })
        {
            var product = await db.Products.SingleAsync(x => x.Id == productId);
            product.TrackingMode = TrackingMode.IndividualPiece;
        }
        await db.SaveChangesAsync();
        var ids = new List<Guid>();
        foreach (var fixture in new[] { first, second })
        {
            var purchase = await ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(scope.ServiceProvider)
                .HandleAsync(new CreatePurchaseCommand(
                    fixture.SupplierId, "TRACKING-" + Guid.NewGuid().ToString("N"),
                    DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                    PurchaseSettlementMode.External, fixture.ActorId, Guid.NewGuid(),
                    [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 100m, 150m, [])]),
                    CancellationToken.None);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
            ids.Add(await db.InventoryUnits.Where(x => x.ProductId == fixture.ProductId)
                .Select(x => x.Id).SingleAsync());
        }
        return (ids[0], ids[1]);
    }

    private static string MakeValidImei() =>
        "86" + Random.Shared.NextInt64(100000000000, 999999999999).ToString();

    // Deliberately bypass application duplicate prechecks: PostgreSQL's unique
    // active-owner key must settle the race across identity slots and types.
    private static async Task<bool[]> RaceClaimsAsync(
        Guid unitA, ManufacturerIdentifierType typeA, ManufacturerIdentifierSlot slotA, string rawA,
        Guid unitB, ManufacturerIdentifierType typeB, ManufacturerIdentifierSlot slotB, string rawB)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var claimIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")!;
        async Task<bool> InsertAsync(Guid unit, ManufacturerIdentifierType type,
            ManufacturerIdentifierSlot slot, string raw, Guid claimId)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                ready.SetResult();
            }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var normalized = type == ManufacturerIdentifierType.Serial
                ? IdentityNormalizationRules.NormalizeSerialNumber(raw)
                : IdentityNormalizationRules.NormalizeImeiIdentity(raw);
            await using var transaction = await connection.BeginTransactionAsync();
            var slotColumn = slot switch
            {
                ManufacturerIdentifierSlot.Serial => "serial_number",
                ManufacturerIdentifierSlot.Imei1 => "imei1",
                ManufacturerIdentifierSlot.Imei2 => "imei2",
                _ => throw new ArgumentOutOfRangeException(nameof(slot))
            };
            await using (var setIdentity = new NpgsqlCommand(
                $"UPDATE inventory.units SET {slotColumn} = @normalized WHERE id = @unit",
                connection,
                transaction))
            {
                setIdentity.Parameters.AddWithValue("normalized", normalized);
                setIdentity.Parameters.AddWithValue("unit", unit);
                Assert.Equal(1, await setIdentity.ExecuteNonQueryAsync());
            }
            await using var insert = new NpgsqlCommand("""
                INSERT INTO inventory.unit_identity_claims
                (id, inventory_unit_id, identifier_type, identifier_slot, raw_value,
                 normalized_value, normalization_version, created_at)
                VALUES (@id, @unit, @type, @slot, @raw, @normalized, 1, now())
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", claimId);
            insert.Parameters.AddWithValue("unit", unit);
            insert.Parameters.AddWithValue("type", (int)type);
            insert.Parameters.AddWithValue("slot", (int)slot);
            insert.Parameters.AddWithValue("raw", raw);
            insert.Parameters.AddWithValue("normalized", normalized);
            try
            {
                await insert.ExecuteNonQueryAsync();
                await using var ownership = new NpgsqlCommand("""
                    INSERT INTO inventory.unit_identity_ownership
                    (normalized_value, inventory_unit_identity_claim_id)
                    VALUES (@normalized, @id)
                    """, connection, transaction);
                ownership.Parameters.AddWithValue("normalized", normalized);
                ownership.Parameters.AddWithValue("id", claimId);
                await ownership.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await transaction.RollbackAsync();
                Assert.Equal("pk_unit_identity_ownership", ex.ConstraintName);
                return false;
            }
        }
        // The runner owns the isolated database and removes it after the suite;
        // retain these successful claims for each test's persisted assertions.
        return await Task.WhenAll(
            InsertAsync(unitA, typeA, slotA, rawA, claimIds[0]),
            InsertAsync(unitB, typeB, slotB, rawB, claimIds[1]));
    }
}
