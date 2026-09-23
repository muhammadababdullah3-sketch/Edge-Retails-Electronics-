using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase3UnknownOutcomeReplayIntegrationTests
{
    [Fact]
    public async Task IDEMP_01_SaleCommitted_ResponseLost_ReplayRecoversCommittedSaleWithoutDuplication()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var handler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
        var purchasing = scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);

        // Stock product via purchase first
        var purchaseOpId = Guid.NewGuid();
        var purchaseResult = await purchasing.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            $"INV-SUPP-{purchaseOpId:N}"[..16],
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Initial stock",
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            purchaseOpId,
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 10m, 100m, 150m, [])],
            0m), CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);

        // Now initiate a Sale
        var saleOpId = Guid.NewGuid();
        var customer = new Customer
        {
            Name = "Replay Test Customer",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var saleCommand = new CompleteSaleCommand(
            saleOpId,
            customer.Id,
            fixture.ActorId,
            null,
            0m,
            SalePaymentMethod.Bank,
            300m,
            "BANK-REF",
            null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 150m, [])]);

        // 1. First execution succeeds and commits to PostgreSQL
        var firstResult = await handler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess);
        Assert.NotNull(firstResult.Value);
        Assert.False(firstResult.Value.WasExisting);
        var originalInvoiceNumber = firstResult.Value.InvoiceNumber;
        var originalSaleId = firstResult.Value.SaleId;

        // 2. Caller simulates unknown outcome / response loss, then replays identical request
        using var replayScope = provider.CreateScope();
        var replayHandler = replayScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();

        var replayResult = await replayHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(replayResult.IsSuccess);
        Assert.NotNull(replayResult.Value);
        Assert.True(replayResult.Value.WasExisting, "Handler must flag replayed unknown-outcome operation as WasExisting=true.");
        Assert.Equal(originalSaleId, replayResult.Value.SaleId);
        Assert.Equal(originalInvoiceNumber, replayResult.Value.InvoiceNumber);
        Assert.Equal(300m, replayResult.Value.GrandTotal);

        // 3. Inspect PostgreSQL database: exactly 1 Sale, 1 Item, 1 Movement, 1 Outbox
        using var verifyScope = provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var salesCount = await verifyDb.Sales.CountAsync(s => s.ClientOperationId == saleOpId);
        Assert.Equal(1, salesCount);

        var itemsCount = await verifyDb.SaleItems.CountAsync(i => verifyDb.Sales.Any(s => s.Id == i.SaleId && s.ClientOperationId == saleOpId));
        Assert.Equal(1, itemsCount);

        var movementsCount = await verifyDb.InventoryMovements.CountAsync(m => m.ReferenceType == "SALE" && m.ReferenceId == originalSaleId);
        Assert.Equal(1, movementsCount);

        var paymentsCount = await verifyDb.SalePayments.CountAsync(p => p.SaleId == originalSaleId);
        Assert.Equal(1, paymentsCount);
    }

    [Fact]
    public async Task IDEMP_02_SameClientOperationId_DifferentPayload_RejectsWithPayloadMismatch()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var handler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
        var purchasing = scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);

        // Stock product
        var purchaseOpId = Guid.NewGuid();
        await purchasing.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            $"INV-SUPP-{purchaseOpId:N}"[..16],
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Initial stock",
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            purchaseOpId,
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 5m, 100m, 150m, [])],
            0m), CancellationToken.None);

        var saleOpId = Guid.NewGuid();
        var customer1 = new Customer { Name = "Customer A", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
        var customer2 = new Customer { Name = "Customer B", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
        db.Customers.AddRange(customer1, customer2);
        await db.SaveChangesAsync();

        var initialCommand = new CompleteSaleCommand(
            saleOpId,
            customer1.Id,
            fixture.ActorId,
            null,
            0m,
            SalePaymentMethod.Bank,
            150m,
            "BANK-REF",
            null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 150m, [])]);

        var firstResult = await handler.HandleAsync(initialCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);

        // Replay with SAME ClientOperationId but DIFFERENT customer
        var conflictingCommand = new CompleteSaleCommand(
            saleOpId,
            customer2.Id,
            fixture.ActorId,
            null,
            0m,
            SalePaymentMethod.Bank,
            150m,
            "BANK-REF",
            null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 150m, [])]);

        var mismatchResult = await handler.HandleAsync(conflictingCommand, CancellationToken.None);
        Assert.False(mismatchResult.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", mismatchResult.Error!.Code);
    }
}
