using System.Text.Json;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

// Narrow public read-contract regression, not migration or inventory certification.
// BuildProvider attests the owned cluster; no operational fallback is permitted.
[Collection("Phase2PostgresIntegration")]
public sealed class Phase234KhataTimestampPostgresTests
{
    private static readonly DateTimeOffset Timestamp =
        new DateTimeOffset(2026, 10, 8, 13, 17, 29, TimeSpan.Zero).AddTicks(6543210);

    [Fact]
    public async Task EmptyDetail_InNonUtcReadOnlyTransaction_ReturnsEmptyListsWithoutMutation()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var project = await SeedProjectAsync(db, seed.ActorId);
        var before = await SnapshotAsync(db, project.Id);
        var detail = await ReadAsync(scope, project.Id);
        Assert.Equal(project.Id, detail.Project.ProjectId);
        Assert.Empty(detail.Materials);
        Assert.Empty(detail.Payments);
        Assert.Equal(0m, detail.Project.MaterialValue);
        Assert.Equal(0m, detail.Project.Paid);
        Assert.Equal(0m, detail.Project.Balance);
        Assert.Equal(before, await SnapshotAsync(db, project.Id));
    }

    [Fact]
    public async Task MaterialAndPayment_PublicDapperMappingPreservesMicrosecondsInNonUtcReadOnlySession()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var project = await SeedProjectAsync(db, seed.ActorId);
        var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            new(seed.SupplierId, "P234-" + Guid.NewGuid(), new DateOnly(2026, 10, 8), null,
                0m, PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new(seed.ProductId, seed.ProductUnitId, 2m, 10m, 150m, [])]), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Code + ":" + purchase.Error?.Message);
        var issued = await scope.ServiceProvider.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(
            new(Guid.NewGuid(), project.Id, seed.ActorId, "Timestamp read fixture",
                [new(seed.ProductId, seed.ProductUnitId, 1m, 150m, [])]), default);
        Assert.True(issued.IsSuccess, issued.Error?.Code + ":" + issued.Error?.Message);
        var issueId = issued.Value!.MaterialIssueId;
        (await db.ThakaMaterialIssues.SingleAsync(x => x.Id == issueId)).IssuedAt = Timestamp;
        var payment = new ThakaPayment
        {
            ProjectId = project.Id, ReceiptNumber = "P234R-" + Guid.NewGuid().ToString("N"),
            ClientOperationId = Guid.NewGuid(), Amount = 12.34m, PaymentMethod = ThakaPaymentMethod.Bank,
            RecordedBy = seed.ActorId, RecordedAt = Timestamp.AddTicks(10), Reference = "Timestamp fixture"
        };
        db.ThakaPayments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db, project.Id);
        var detail = await ReadAsync(scope, project.Id);
        var material = Assert.Single(detail.Materials);
        var paid = Assert.Single(detail.Payments);
        Assert.Equal(issueId, material.MaterialIssueId);
        Assert.Equal(Timestamp.Ticks, material.IssuedAt.Ticks);
        Assert.Equal(TimeSpan.Zero, material.IssuedAt.Offset);
        Assert.Equal(payment.Id, paid.PaymentId);
        Assert.Equal(Timestamp.AddTicks(10).Ticks, paid.RecordedAt.Ticks);
        Assert.Equal(TimeSpan.Zero, paid.RecordedAt.Offset);
        Assert.Equal(150m, material.LineCharge);
        Assert.Equal(12.34m, paid.Amount);
        Assert.Equal(150m, detail.Project.MaterialValue);
        Assert.Equal(12.34m, detail.Project.Paid);
        Assert.Equal(137.66m, detail.Project.Balance);
        Assert.Equal(before, await SnapshotAsync(db, project.Id));
    }

    private static async Task<ThakaProject> SeedProjectAsync(EdgeRetailsDbContext db, Guid actorId)
    {
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "Phase234 timestamp");
        var project = new ThakaProject
        {
            CustomerId = customer.Id, ProjectNumber = "P234-" + Guid.NewGuid().ToString("N"),
            ProjectName = "Isolated read fixture", StartedOn = new DateOnly(2026, 10, 8),
            CreatedBy = actorId, CreatedAt = Timestamp, Status = ThakaProjectStatus.Active
        };
        db.ThakaProjects.Add(project);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return project;
    }

    private static async Task<ThakaProjectDetailDto> ReadAsync(AsyncServiceScope scope, Guid projectId)
    {
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using (var command = new NpgsqlCommand(
                "SET TRANSACTION READ ONLY; SET LOCAL TIME ZONE 'Asia/Karachi';", connection))
            {
                await command.ExecuteNonQueryAsync();
            }
            await using (var command = new NpgsqlCommand(
                "SELECT current_setting('transaction_read_only'), current_setting('TimeZone')", connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("on", reader.GetString(0));
                Assert.Equal("Asia/Karachi", reader.GetString(1));
            }
            var detail = await scope.ServiceProvider.GetRequiredService<IThakaReadService>()
                .GetProjectAsync(projectId, default);
            Assert.NotNull(detail);
            await transaction.CommitAsync();
            return detail;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string> SnapshotAsync(EdgeRetailsDbContext db, Guid projectId)
    {
        var issues = await db.ThakaMaterialIssues.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Id).ToListAsync();
        var ids = issues.Select(x => x.Id).ToArray();
        return JsonSerializer.Serialize(new
        {
            project = await db.ThakaProjects.AsNoTracking().SingleAsync(x => x.Id == projectId),
            issues,
            items = await db.ThakaMaterialIssueItems.AsNoTracking().Where(x => ids.Contains(x.MaterialIssueId))
                .OrderBy(x => x.Id).ToListAsync(),
            payments = await db.ThakaPayments.AsNoTracking().Where(x => x.ProjectId == projectId)
                .OrderBy(x => x.Id).ToListAsync()
        });
    }
}
