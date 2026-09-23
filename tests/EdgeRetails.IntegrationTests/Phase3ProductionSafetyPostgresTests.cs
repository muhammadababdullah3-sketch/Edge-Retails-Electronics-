using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Application.Production.Startup;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase3ProductionSafetyPostgresTests
{
    [Fact]
    public async Task PostgresReadinessProbe_ReportsReady_OnLivePostgres18()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("EDGE_RETAILS_TEST_DB is not configured.");
        }

        var probe = new NpgsqlDatabaseReadinessProbe(connectionString);
        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(DatabaseReadinessCode.Ready, result.Code);
        Assert.NotNull(result.PostgreSqlVersion);
        Assert.StartsWith("18.", result.PostgreSqlVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.DatabaseName));
    }

    [Fact]
    public async Task OutboxRepository_PersistsAndTransitionsMessages_InPostgres()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var messageId = Guid.NewGuid();
        var idempotencyKey = $"test_idemp_{Guid.NewGuid():N}";
        var msg = new OutboxMessage
        {
            Id = messageId,
            EffectType = "PrintDocument",
            SourceType = "Sale",
            SourceId = Guid.NewGuid().ToString(),
            PayloadJson = "{\"test\": true}",
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 0,
            NextAttemptAt = null,
            Status = OutboxMessageStatus.Pending
        };

        repo.Enqueue(msg);
        await db.SaveChangesAsync(CancellationToken.None);

        // Verify message is written to PostgreSQL
        var inDb = await repo.GetByIdAsync(messageId, CancellationToken.None);
        Assert.NotNull(inDb);
        Assert.Equal(OutboxMessageStatus.Pending, inDb.Status);
        Assert.Equal(idempotencyKey, inDb.IdempotencyKey);

        // Verify pending batch retrieval
        var pending = await repo.GetPendingMessagesAsync(10, CancellationToken.None);
        Assert.Contains(pending, x => x.Id == messageId);

        // Mark failed with next attempt
        var nextAttempt = DateTimeOffset.UtcNow.AddMinutes(5);
        await repo.MarkFailedAsync(messageId, "Transient print error", nextAttempt, CancellationToken.None);

        var afterFail = await repo.GetByIdAsync(messageId, CancellationToken.None);
        Assert.NotNull(afterFail);
        Assert.Equal(OutboxMessageStatus.Pending, afterFail.Status);
        Assert.Equal(1, afterFail.AttemptCount);
        Assert.Contains("Transient print error", afterFail.LastError);

        // Mark completed
        var completedAt = DateTimeOffset.UtcNow;
        await repo.MarkCompletedAsync(messageId, completedAt, CancellationToken.None);

        var afterComplete = await repo.GetByIdAsync(messageId, CancellationToken.None);
        Assert.NotNull(afterComplete);
        Assert.Equal(OutboxMessageStatus.Completed, afterComplete.Status);
        Assert.Equal(2, afterComplete.AttemptCount);
        Assert.NotNull(afterComplete.CompletedAt);
    }

    [Fact]
    public async Task OutboxRepository_EnforcesUniqueIdempotencyKeyConstraint_InPostgres()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var sharedKey = $"duplicate_key_{Guid.NewGuid():N}";

        var msg1 = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EffectType = "PrintDocument",
            SourceType = "Sale",
            SourceId = "source_1",
            PayloadJson = "{}",
            IdempotencyKey = sharedKey,
            CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 0,
            Status = OutboxMessageStatus.Pending
        };

        repo.Enqueue(msg1);
        await db.SaveChangesAsync(CancellationToken.None);

        using var scope2 = provider.CreateScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var db2 = scope2.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var msg2 = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EffectType = "PrintDocument",
            SourceType = "Sale",
            SourceId = "source_2",
            PayloadJson = "{}",
            IdempotencyKey = sharedKey,
            CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 0,
            Status = OutboxMessageStatus.Pending
        };

        repo2.Enqueue(msg2);

        // Expect unique constraint violation in PostgreSQL
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db2.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OutboxMessage_RollsBackAtomically_WhenTransactionFails()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var messageId = Guid.NewGuid();
        var sharedKey = $"rollback_key_{Guid.NewGuid():N}";

        await using (var tx = await db.Database.BeginTransactionAsync(CancellationToken.None))
        {
            var msg = new OutboxMessage
            {
                Id = messageId,
                EffectType = "PrintDocument",
                SourceType = "Sale",
                SourceId = "source_tx",
                PayloadJson = "{}",
                IdempotencyKey = sharedKey,
                CreatedAt = DateTimeOffset.UtcNow,
                AttemptCount = 0,
                Status = OutboxMessageStatus.Pending
            };

            repo.Enqueue(msg);
            await db.SaveChangesAsync(CancellationToken.None);

            // Explicitly roll back the transaction
            await tx.RollbackAsync(CancellationToken.None);
        }

        // Verify message was NOT committed to PostgreSQL
        using var verifyScope = provider.CreateScope();
        var verifyRepo = verifyScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var found = await verifyRepo.GetByIdAsync(messageId, CancellationToken.None);
        Assert.Null(found);
    }

    [Fact]
    public async Task PosSaleReceiptKindSource_LoadsCanonicalDocumentFromPostgres()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);

        var saleId = Guid.NewGuid();
        var movement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = fixture.ProductId,
            MovementType = InventoryMovementType.SaleOut,
            ReferenceType = "Sale",
            ReferenceId = saleId,
            ActorId = fixture.ActorId,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(movement);

        var sale = new Sale
        {
            Id = saleId,
            InvoiceNumber = $"INV-P3-{Guid.NewGuid():N}"[..16],
            CashierUserId = fixture.ActorId,
            CompletedAt = DateTimeOffset.UtcNow,
            Subtotal = 500m,
            InvoiceDiscount = 0m,
            GrandTotal = 500m,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            ReceiptTemplateSnapshot = "{\"shopName\":\"Edge Retails Test Shop\",\"header\":\"Thank you\",\"footer\":\"Visit Again\"}",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var item = new SaleItem
        {
            Id = Guid.NewGuid(),
            SaleId = saleId,
            InventoryMovementId = movement.Id,
            ProductId = fixture.ProductId,
            ProductUnitId = fixture.ProductUnitId,
            ProductNameSnapshot = "Test Product Safety Item",
            EnteredQuantity = 2m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 2m,
            UnitPrice = 250m,
            GrossLineTotal = 500m,
            AllocatedInvoiceDiscount = 0m,
            NetLineTotal = 500m,
            UnitCostSnapshot = 100m,
            TotalCostSnapshot = 200m,
            GrossProfitSnapshot = 300m
        };

        db.Sales.Add(sale);
        db.SaleItems.Add(item);
        await db.SaveChangesAsync(CancellationToken.None);

        var router = scope.ServiceProvider.GetRequiredService<IProductionDocumentSource>();
        var doc = await router.LoadAsync(ProductionDocumentKind.PosSaleReceipt, saleId, CancellationToken.None);

        Assert.NotNull(doc);
        Assert.Equal(ProductionDocumentKind.PosSaleReceipt, doc.Kind);
        Assert.Equal(sale.InvoiceNumber, doc.DocumentNumber);
        Assert.Equal("Edge Retails Test Shop", doc.ShopName);
        Assert.Single(doc.Lines);
        Assert.Equal("Test Product Safety Item", doc.Lines[0].Description);
        Assert.Equal(2m, doc.Lines[0].Quantity);
        Assert.Equal(250m, doc.Lines[0].UnitPrice);
        Assert.Equal(500m, doc.Lines[0].LineTotal);
    }
}
