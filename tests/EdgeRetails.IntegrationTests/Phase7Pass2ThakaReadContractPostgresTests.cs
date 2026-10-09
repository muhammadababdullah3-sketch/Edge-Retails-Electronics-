using System.Text.Json;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

// NEW_COVERAGE: the existing harness attests the owned PostgreSQL cluster before
// any fixture is created. Public detail assertions use production DI and Dapper.
[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass2ThakaReadContractPostgresTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset FixedUtc =
        new DateTimeOffset(2026, 9, 26, 18, 42, 31, TimeSpan.Zero).AddTicks(1234560);

    [Fact]
    public async Task SchemaProviderAuthority_ActualColumnsAndNativeNpgsqlPreserveUtcMicroseconds()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        var issue = await IssueAsync(provider, fixture, fixture.Units);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await SetIssuedAtAsync(db, issue.MaterialIssueId, FixedUtc);
        var payment = await SeedPaymentReadFixtureAsync(db, fixture);
        Assert.Equal(typeof(DateTimeOffset), db.Model.FindEntityType(typeof(ThakaMaterialIssue))!
            .FindProperty(nameof(ThakaMaterialIssue.IssuedAt))!.ClrType);
        Assert.Equal(typeof(DateTimeOffset), db.Model.FindEntityType(typeof(ThakaPayment))!
            .FindProperty(nameof(ThakaPayment.RecordedAt))!.ClrType);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using (var version = new NpgsqlCommand("SHOW server_version", connection))
            {
                var serverVersion = Assert.IsType<string>(await version.ExecuteScalarAsync());
                Assert.StartsWith("18.6", serverVersion);
                output.WriteLine($"Owned harness server_version={serverVersion}; Npgsql={typeof(NpgsqlConnection).Assembly.GetName().Version}");
            }
            await using (var versionNumber = new NpgsqlCommand("SHOW server_version_num", connection))
            {
                Assert.Equal("180006", await versionNumber.ExecuteScalarAsync());
                output.WriteLine("Owned harness server_version_num=180006.");
            }
            await using (var schema = new NpgsqlCommand("""
                SELECT table_name, column_name, data_type, udt_name, column_default, is_nullable, datetime_precision
                FROM information_schema.columns
                WHERE table_schema = 'thaka'
                  AND ((table_name = 'material_issues' AND column_name = 'issued_at')
                    OR (table_name = 'payments' AND column_name = 'recorded_at'))
                ORDER BY table_name;
                """, connection))
            await using (var reader = await schema.ExecuteReaderAsync())
            {
                var count = 0;
                while (await reader.ReadAsync())
                {
                    Assert.Equal("timestamp with time zone", reader.GetString(2));
                    Assert.Equal("timestamptz", reader.GetString(3));
                    Assert.True(reader.IsDBNull(4));
                    Assert.Equal("NO", reader.GetString(5));
                    Assert.Equal(6, reader.GetInt32(6));
                    output.WriteLine($"thaka.{reader.GetString(0)}.{reader.GetString(1)}: timestamptz; no default; NOT NULL; precision6; EF CLR DateTimeOffset.");
                    count++;
                }
                Assert.Equal(2, count);
            }
            await using (var timezone = new NpgsqlCommand("SET TIME ZONE 'Asia/Karachi'", connection))
            {
                await timezone.ExecuteNonQueryAsync();
            }
            await AssertNativeTimestampAsync(connection,
                "SELECT pg_typeof(issued_at)::text, issued_at FROM thaka.material_issues WHERE id = @id",
                issue.MaterialIssueId);
            await AssertNativeTimestampAsync(connection,
                "SELECT pg_typeof(recorded_at)::text, recorded_at FROM thaka.payments WHERE id = @id",
                payment.Id);
        }
        finally
        {
            await using var reset = new NpgsqlCommand("RESET TIME ZONE", connection);
            await reset.ExecuteNonQueryAsync();
            await connection.CloseAsync();
        }
    }

    [Fact]
    public async Task EmptyDetail_ReturnsCanonicalProjectAndEmptyMaterialAndPaymentLists()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var before = await PersistedStateAsync(db, fixture);
        var detail = await ReadInNonUtcSessionAsync(scope, fixture.ProjectId);
        Assert.Equal(fixture.ProjectId, detail.Project.ProjectId);
        Assert.Equal(ThakaProjectStatus.Active, detail.Project.Status);
        Assert.Equal(0m, detail.Project.MaterialValue);
        Assert.Equal(0m, detail.Project.Paid);
        Assert.Equal(0m, detail.Project.Balance);
        Assert.Empty(detail.Materials);
        Assert.Empty(detail.Payments);
        Assert.Equal(before, await PersistedStateAsync(db, fixture));
        await AssertInventoryAsync(db, fixture, 100m, 22000m);
    }

    [Fact]
    public async Task OneMaterialRow_DetailPreservesUtcTimestampAndExactContainerChargeAndCost()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        var issue = await IssueAsync(provider, fixture, fixture.Units);
        Assert.Equal(50000m, issue.TotalCharge);
        Assert.Equal(22000m, issue.TotalCost);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await SetIssuedAtAsync(db, issue.MaterialIssueId, FixedUtc);
        var before = await PersistedStateAsync(db, fixture);
        var detail = await ReadInNonUtcSessionAsync(scope, fixture.ProjectId);
        var row = Assert.Single(detail.Materials);
        await AssertMaterialAsync(db, fixture, row, issue.MaterialIssueId, FixedUtc, 2m, 22000m, false);
        Assert.Equal(issue.ChallanNumber, row.ChallanNumber);
        Assert.Equal(50000m, detail.Project.MaterialValue);
        Assert.Equal(50000m, detail.Project.Balance);
        Assert.Equal(0m, detail.Project.Paid);
        Assert.Empty(detail.Payments);
        Assert.Equal(FixedUtc, JsonSerializer.Deserialize<ThakaMaterialLedgerRowDto>(JsonSerializer.Serialize(row))!.IssuedAt);
        Assert.Equal(before, await PersistedStateAsync(db, fixture));
        await AssertInventoryAsync(db, fixture, 0m, 0m);
        Assert.All(await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == fixture.ProductId).ToListAsync(),
            unit => Assert.Equal(InventoryUnitStatus.IssuedThaka, unit.Status));
    }

    [Fact]
    public async Task MultipleMaterialRows_ReversalDetailRetainsOriginalUtcRowsAndRestoredExactValue()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        var first = await IssueAsync(provider, fixture, [fixture.Units[0]]);
        var second = await IssueAsync(provider, fixture, [fixture.Units[1]]);
        var later = FixedUtc.AddTicks(10);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await SetIssuedAtAsync(db, first.MaterialIssueId, FixedUtc);
        await SetIssuedAtAsync(db, second.MaterialIssueId, later);
        var issuedBefore = await PersistedStateAsync(db, fixture);
        var issued = await ReadInNonUtcSessionAsync(scope, fixture.ProjectId);
        Assert.Equal(2, issued.Materials.Count);
        Assert.Equal(second.MaterialIssueId, issued.Materials[0].MaterialIssueId);
        Assert.Equal(first.MaterialIssueId, issued.Materials[1].MaterialIssueId);
        await AssertMaterialAsync(db, fixture, issued.Materials[0], second.MaterialIssueId, later, 1m, 12000m, false);
        await AssertMaterialAsync(db, fixture, issued.Materials[1], first.MaterialIssueId, FixedUtc, 1m, 10000m, false);
        Assert.Equal(50000m, issued.Project.MaterialValue);
        Assert.Equal(issuedBefore, await PersistedStateAsync(db, fixture));

        var reversal = await scope.ServiceProvider.GetRequiredService<ReverseThakaMaterialHandler>().HandleAsync(
            new(Guid.NewGuid(), fixture.ProjectId, first.MaterialIssueId, "Timestamp contract reversal", fixture.ActorId), default);
        Assert.True(reversal.IsSuccess, reversal.Error?.Code + ":" + reversal.Error?.Message);
        Assert.Equal(25000m, reversal.Value!.ReversedCharge);
        Assert.Equal(10000m, reversal.Value.RestoredCost);
        db.ChangeTracker.Clear();
        var reversedBefore = await PersistedStateAsync(db, fixture);
        var reversed = await ReadInNonUtcSessionAsync(scope, fixture.ProjectId);
        Assert.Equal(2, reversed.Materials.Count);
        await AssertMaterialAsync(db, fixture, reversed.Materials[0], second.MaterialIssueId, later, 1m, 12000m, false);
        await AssertMaterialAsync(db, fixture, reversed.Materials[1], first.MaterialIssueId, FixedUtc, 1m, 10000m, true);
        Assert.Equal(25000m, reversed.Project.MaterialValue);
        Assert.Equal(25000m, reversed.Project.Balance);
        Assert.Equal(0m, reversed.Project.Paid);
        Assert.Empty(reversed.Payments);
        var savedReversal = await db.ThakaMaterialReversals.AsNoTracking().SingleAsync(x => x.MaterialIssueId == first.MaterialIssueId);
        Assert.Equal(25000m, savedReversal.ReversedCharge);
        Assert.Equal(10000m, savedReversal.RestoredCost);
        await AssertInventoryAsync(db, fixture, 50m, 10000m);
        Assert.Equal(InventoryUnitStatus.InStock, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == fixture.Units[0].Id)).Status);
        Assert.Equal(InventoryUnitStatus.IssuedThaka, (await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.Id == fixture.Units[1].Id)).Status);
        Assert.Equal(reversedBefore, await PersistedStateAsync(db, fixture));
    }

    [Fact]
    public async Task PaymentOnlyDetail_PreservesTypedUtcMicrosecondsAndPersistedPaymentFields()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        // Narrow read-only fixture: isolates the payment timestamp projection so a
        // material projection failure cannot hide this independent provider boundary.
        var payment = await SeedPaymentReadFixtureAsync(db, fixture);
        var before = await PersistedStateAsync(db, fixture);
        var detail = await ReadInNonUtcSessionAsync(scope, fixture.ProjectId);
        Assert.Empty(detail.Materials);
        var row = Assert.Single(detail.Payments);
        Assert.Equal(payment.Id, row.PaymentId);
        Assert.Equal(payment.ReceiptNumber, row.ReceiptNumber);
        Assert.Equal(FixedUtc, row.RecordedAt);
        Assert.Equal(TimeSpan.Zero, row.RecordedAt.Offset);
        Assert.Equal(FixedUtc.Ticks, row.RecordedAt.Ticks);
        Assert.Equal(ThakaPaymentMethod.Bank, row.PaymentMethod);
        Assert.Equal(1234.56m, row.Amount);
        Assert.Equal(fixture.ActorId, row.RecordedBy);
        Assert.Equal("UTC provider contract", row.Reference);
        Assert.False(row.IsReversed);
        Assert.Equal(1234.56m, detail.Project.Paid);
        Assert.Equal(0m, detail.Project.MaterialValue);
        Assert.Equal(0m, detail.Project.Balance);
        Assert.Equal(FixedUtc, JsonSerializer.Deserialize<ThakaPaymentLedgerRowDto>(JsonSerializer.Serialize(row))!.RecordedAt);
        Assert.Equal(before, await PersistedStateAsync(db, fixture));
    }

    private async Task AssertNativeTimestampAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("timestamp with time zone", reader.GetString(0));
        Assert.Equal(typeof(DateTime), reader.GetFieldType(1));
        var defaultValue = Assert.IsType<DateTime>(reader.GetValue(1));
        var nativeDateTime = reader.GetDateTime(1);
        Assert.Equal(DateTimeKind.Utc, defaultValue.Kind);
        Assert.Equal(DateTimeKind.Utc, nativeDateTime.Kind);
        Assert.Equal(FixedUtc.UtcDateTime, defaultValue);
        Assert.Equal(FixedUtc.UtcDateTime, nativeDateTime);
        var offsetValue = reader.GetFieldValue<DateTimeOffset>(1);
        Assert.Equal(FixedUtc, offsetValue);
        Assert.Equal(TimeSpan.Zero, offsetValue.Offset);
        Assert.Equal(FixedUtc.Ticks, offsetValue.Ticks);
        Assert.False(await reader.ReadAsync());
        output.WriteLine($"pg_typeof=timestamptz; default GetValue/GetFieldType=DateTime; Kind=Utc; typed DateTimeOffset={offsetValue:O}; ticks={offsetValue.Ticks}; session Asia/Karachi.");
    }

    private static async Task<ThakaProjectDetailDto> ReadInNonUtcSessionAsync(AsyncServiceScope scope, Guid projectId)
    {
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var timezone = new NpgsqlCommand("SET TIME ZONE 'Asia/Karachi'", connection);
            await timezone.ExecuteNonQueryAsync();
            await using var current = new NpgsqlCommand("SHOW TIME ZONE", connection);
            Assert.Equal("Asia/Karachi", await current.ExecuteScalarAsync());
            var detail = await scope.ServiceProvider.GetRequiredService<IThakaReadService>().GetProjectAsync(projectId, default);
            Assert.NotNull(detail);
            return detail;
        }
        finally
        {
            await using var reset = new NpgsqlCommand("RESET TIME ZONE", connection);
            await reset.ExecuteNonQueryAsync();
            await connection.CloseAsync();
        }
    }

    private static async Task AssertMaterialAsync(EdgeRetailsDbContext db, Fixture fixture,
        ThakaMaterialLedgerRowDto row, Guid issueId, DateTimeOffset timestamp, decimal entered, decimal cost, bool reversed)
    {
        Assert.Equal(issueId, row.MaterialIssueId);
        Assert.Equal(fixture.ProductId, row.ProductId);
        Assert.Equal(fixture.ProductUnitId, row.ProductUnitId);
        Assert.Equal(fixture.ProductName, row.ProductName);
        Assert.Equal(fixture.UnitSymbol, row.UnitSymbol);
        Assert.Equal(timestamp, row.IssuedAt);
        Assert.Equal(timestamp.Ticks, row.IssuedAt.Ticks);
        Assert.Equal(TimeSpan.Zero, row.IssuedAt.Offset);
        Assert.Equal(entered, row.EnteredQuantity);
        Assert.Equal(500m, row.UnitCharge);
        Assert.Equal(entered * 50m * 500m, row.LineCharge);
        Assert.Equal(reversed, row.IsReversed);
        var header = await db.ThakaMaterialIssues.AsNoTracking().SingleAsync(x => x.Id == issueId);
        var item = await db.ThakaMaterialIssueItems.AsNoTracking().SingleAsync(x => x.MaterialIssueId == issueId);
        Assert.Equal(timestamp, header.IssuedAt);
        Assert.Equal(row.LineCharge, header.TotalCharge);
        Assert.Equal(cost, header.TotalCost);
        Assert.Equal(entered, item.EnteredQuantity);
        Assert.Equal(50m, item.FactorToBaseSnapshot);
        Assert.Equal(entered * 50m, item.BaseQuantity);
        Assert.Equal(500m, item.UnitCharge);
        Assert.Equal(row.LineCharge, item.LineCharge);
        Assert.Equal(cost, item.TotalCostSnapshot);
        Assert.Equal(cost / (entered * 50m), item.UnitCostSnapshot);
        Assert.Equal(row.LineCharge - cost, item.GrossProfitSnapshot);
    }

    private static async Task<Fixture> SeedAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, 500m);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = TrackingMode.Container;
        var productUnit = await db.ProductUnits.SingleAsync(x => x.Id == seed.ProductUnitId);
        productUnit.FactorToBaseUnit = 50m;
        var symbol = (await db.Units.SingleAsync(x => x.Id == seed.UnitId)).Symbol;
        await db.SaveChangesAsync();
        foreach (var cost in new[] { 10000m, 12000m })
        {
            var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new(seed.SupplierId, "P7READ-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null,
                    0m, PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                    [new(seed.ProductId, seed.ProductUnitId, 1m, cost, 500m,
                        [new SerializedIdentityInput("P7READ-" + Guid.NewGuid().ToString("N"))])]), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Code + ":" + purchase.Error?.Message);
        }
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "Timestamp read customer");
        var project = await scope.ServiceProvider.GetRequiredService<CreateThakaProjectHandler>().HandleAsync(
            new(customer.Id, "Timestamp read " + Guid.NewGuid(), null, "NEW_COVERAGE",
                new DateOnly(2026, 9, 26), seed.ActorId, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.True(project.IsSuccess, project.Error?.Code + ":" + project.Error?.Message);
        db.ChangeTracker.Clear();
        var units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == seed.ProductId)
            .OrderBy(x => x.ItemSequence).ToArrayAsync();
        Assert.Equal(2, units.Length);
        return new(seed.ProductId, seed.ProductUnitId, seed.ActorId, project.Value, product.Name, symbol, units);
    }

    private static async Task<IssueThakaMaterialResult> IssueAsync(ServiceProvider provider, Fixture fixture, InventoryUnit[] units)
    {
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(
            new(Guid.NewGuid(), fixture.ProjectId, fixture.ActorId, "Timestamp read contract",
                [new(fixture.ProductId, fixture.ProductUnitId, units.Length, 500m, units.Select(x => x.Id).ToArray())]), default);
        Assert.True(result.IsSuccess, result.Error?.Code + ":" + result.Error?.Message);
        return result.Value!;
    }

    private static async Task SetIssuedAtAsync(EdgeRetailsDbContext db, Guid issueId, DateTimeOffset timestamp)
    {
        // Only the strongly typed persisted timestamp is made deterministic after
        // the canonical handler has produced every stock/accounting snapshot.
        (await db.ThakaMaterialIssues.SingleAsync(x => x.Id == issueId)).IssuedAt = timestamp;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<ThakaPayment> SeedPaymentReadFixtureAsync(EdgeRetailsDbContext db, Fixture fixture)
    {
        var payment = new ThakaPayment
        {
            ProjectId = fixture.ProjectId, ReceiptNumber = "P7RP-" + Guid.NewGuid().ToString("N"),
            ClientOperationId = Guid.NewGuid(), Amount = 1234.56m, PaymentMethod = ThakaPaymentMethod.Bank,
            RecordedBy = fixture.ActorId, RecordedAt = FixedUtc, Reference = "UTC provider contract"
        };
        db.ThakaPayments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return payment;
    }

    private static async Task AssertInventoryAsync(EdgeRetailsDbContext db, Fixture fixture, decimal quantity, decimal cost)
    {
        Assert.Equal(quantity, (await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId)).SellableQty);
        var pool = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(quantity, pool.CostedQty);
        Assert.Equal(cost, pool.TotalInventoryCost);
        Assert.Equal(quantity, await (from bucket in db.InventoryLotBucketBalances
            join lot in db.InventoryLots on bucket.LotId equals lot.Id
            where lot.ProductId == fixture.ProductId && bucket.StockBucket == InventoryBucket.Sellable
            select bucket.Quantity).SumAsync());
    }

    private static async Task<string> PersistedStateAsync(EdgeRetailsDbContext db, Fixture fixture)
    {
        var issues = await db.ThakaMaterialIssues.AsNoTracking().Where(x => x.ProjectId == fixture.ProjectId).OrderBy(x => x.Id).ToListAsync();
        var issueIds = issues.Select(x => x.Id).ToArray();
        return JsonSerializer.Serialize(new
        {
            project = await db.ThakaProjects.AsNoTracking().SingleAsync(x => x.Id == fixture.ProjectId),
            issues,
            items = await db.ThakaMaterialIssueItems.AsNoTracking().Where(x => issueIds.Contains(x.MaterialIssueId)).OrderBy(x => x.Id).ToListAsync(),
            reversals = await db.ThakaMaterialReversals.AsNoTracking().Where(x => x.ProjectId == fixture.ProjectId).OrderBy(x => x.Id).ToListAsync(),
            payments = await db.ThakaPayments.AsNoTracking().Where(x => x.ProjectId == fixture.ProjectId).OrderBy(x => x.Id).ToListAsync(),
            stock = await db.StockBalances.AsNoTracking().Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToListAsync(),
            cost = await db.ProductCostStates.AsNoTracking().Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToListAsync(),
            units = await db.InventoryUnits.AsNoTracking().Where(x => x.ProductId == fixture.ProductId).OrderBy(x => x.Id).ToListAsync()
        });
    }

    private sealed record Fixture(Guid ProductId, Guid ProductUnitId, Guid ActorId, Guid ProjectId,
        string ProductName, string UnitSymbol, InventoryUnit[] Units);
}
