using System.Text.Json;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Server.Controllers;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase12Wave2PostgresTests
{
    static Phase12Wave2PostgresTests()
    {
        var connStr = Phase2PostgresTestHarness.ResolveConnectionString();
        Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(connStr, performLiveProbe: true);
    }

    private static async Task<(QuantityProductFixture Product, Guid TerminalId)> SetupStockAndTerminalAsync(ServiceProvider provider, decimal initialStock = 10m)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        // Seed stock via purchase
        var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            new CreatePurchaseCommand(
                product.SupplierId,
                "PURCHASE-" + Guid.NewGuid().ToString("N"),
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                product.ActorId,
                Guid.NewGuid(),
                [new(product.ProductId, product.ProductUnitId, initialStock, 100m, 150m, [])]),
            default);

        Assert.True(purchase.IsSuccess, purchase.Error?.ToString());

        // Create registered terminal
        var terminal = new Terminal
        {
            Name = "POS-T12-" + Guid.NewGuid().ToString("N")[..8],
            TerminalCode = "T12-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            Status = TerminalStatus.Active,
            RegisteredAt = DateTimeOffset.UtcNow
        };
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync();

        return (product, terminal.Id);
    }

    // =========================================================================
    // P12-W2-002 & P12-W2-003: Universal Operation Status Authority & Finding F06
    // =========================================================================

    [Fact]
    public async Task Postgres_StatusQuery_AuthorizedActorAndTerminal_ReturnsCanonicalOutcomeWithOriginalResponse()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider);

        var clientOpId = Guid.NewGuid();
        var cashierId = product.ActorId;
        var saleCommand = new CompleteSaleCommand(
            ClientOperationId: clientOpId,
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: "Cash Sale",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 2m, 150m, [])],
            RegisteredTerminalId: terminalId);

        // Execute sale
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var saleResult = await handler.HandleAsync(saleCommand, default);
            Assert.True(saleResult.IsSuccess, saleResult.Error?.ToString());
        }

        // Query status with authorized context (ActorId + TerminalId)
        await using (var queryScope = provider.CreateAsyncScope())
        {
            var statusHandler = queryScope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: clientOpId,
                ActorId: cashierId,
                TerminalId: terminalId,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var result = await statusHandler.HandleAsync(query, default);
            Assert.True(result.IsSuccess, result.Error?.ToString());

            var status = result.Value!;
            Assert.True(status.Found);
            Assert.Equal("Succeeded", status.Status);
            Assert.Equal("Sale", status.OperationType);
            Assert.True(status.WasCommitted);
            Assert.NotNull(status.EntityId);
            Assert.NotNull(status.DocumentNumber);
            Assert.Equal(200, status.ResponseStatusCode);
            Assert.Equal("application/json", status.ResponseContentType);
            Assert.False(string.IsNullOrWhiteSpace(status.ResponsePayload));

            var cachedSale = JsonSerializer.Deserialize<CompleteSaleResult>(status.ResponsePayload);
            Assert.NotNull(cachedSale);
            Assert.Equal(status.EntityId, cachedSale.SaleId);
            Assert.Equal(status.DocumentNumber, cachedSale.InvoiceNumber);
        }
    }

    [Fact]
    public async Task Postgres_StatusQuery_MismatchedActor_ReturnsForbidden_F06Remediation()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider);

        var clientOpId = Guid.NewGuid();
        var cashierId = product.ActorId;
        var saleCommand = new CompleteSaleCommand(
            ClientOperationId: clientOpId,
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 150m,
            PaymentReference: "Context Guard Test",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 150m, [])],
            RegisteredTerminalId: terminalId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var saleResult = await scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>().HandleAsync(saleCommand, default);
            Assert.True(saleResult.IsSuccess, saleResult.Error?.ToString());
        }

        // Rogue actor queries the operation
        await using (var queryScope = provider.CreateAsyncScope())
        {
            var statusHandler = queryScope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var rogueActorId = Guid.NewGuid();

            var query = new OperationStatusQuery(
                ClientOperationId: clientOpId,
                ActorId: rogueActorId, // Mismatched!
                TerminalId: terminalId,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var result = await statusHandler.HandleAsync(query, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("authorization.forbidden", result.Error?.Code);
        }
    }

    [Fact]
    public async Task Postgres_StatusQuery_MismatchedTerminal_ReturnsForbidden_F06Remediation()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider);

        var clientOpId = Guid.NewGuid();
        var cashierId = product.ActorId;
        var saleCommand = new CompleteSaleCommand(
            ClientOperationId: clientOpId,
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 150m,
            PaymentReference: "Terminal Guard Test",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 150m, [])],
            RegisteredTerminalId: terminalId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var saleResult = await scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>().HandleAsync(saleCommand, default);
            Assert.True(saleResult.IsSuccess, saleResult.Error?.ToString());
        }

        // Rogue terminal queries the operation
        await using (var queryScope = provider.CreateAsyncScope())
        {
            var statusHandler = queryScope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var rogueTerminalId = Guid.NewGuid();

            var query = new OperationStatusQuery(
                ClientOperationId: clientOpId,
                ActorId: cashierId,
                TerminalId: rogueTerminalId, // Mismatched!
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var result = await statusHandler.HandleAsync(query, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("authorization.forbidden", result.Error?.Code);
        }
    }

    [Fact]
    public async Task Postgres_StatusQuery_TamperedPayloadFingerprint_ReturnsPayloadMismatch()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider);

        var clientOpId = Guid.NewGuid();
        var cashierId = product.ActorId;
        var saleCommand = new CompleteSaleCommand(
            ClientOperationId: clientOpId,
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 150m,
            PaymentReference: "Fingerprint Tamper Test",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 150m, [])],
            RegisteredTerminalId: terminalId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var saleResult = await scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>().HandleAsync(saleCommand, default);
            Assert.True(saleResult.IsSuccess, saleResult.Error?.ToString());
        }

        // Query with forged fingerprint
        await using (var queryScope = provider.CreateAsyncScope())
        {
            var statusHandler = queryScope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: clientOpId,
                ActorId: cashierId,
                TerminalId: terminalId,
                PayloadFingerprint: "sha256:tampered" + Guid.NewGuid().ToString("N"));

            var result = await statusHandler.HandleAsync(query, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }
    }

    [Fact]
    public async Task Postgres_StatusQuery_NotFound_ReturnsNotFoundStatus_WithoutPermittingFreshIdForSameIntent()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var queryScope = provider.CreateAsyncScope();
        var statusHandler = queryScope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();

        var unrecordedId = Guid.NewGuid();
        var query = new OperationStatusQuery(ClientOperationId: unrecordedId);

        var result = await statusHandler.HandleAsync(query, default);
        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Found);
        Assert.Equal("NotFound", result.Value.Status);
    }

    [Fact]
    public async Task Postgres_OperationsController_Endpoints_HonorsHeaderAuthAndStatusMapping()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider);

        var clientOpId = Guid.NewGuid();
        var cashierId = product.ActorId;
        var saleCommand = new CompleteSaleCommand(
            ClientOperationId: clientOpId,
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 150m,
            PaymentReference: "API Controller Test",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 150m, [])],
            RegisteredTerminalId: terminalId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var saleResult = await scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>().HandleAsync(saleCommand, default);
            Assert.True(saleResult.IsSuccess);
        }

        await using (var controllerScope = provider.CreateAsyncScope())
        {
            var statusHandler = controllerScope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var controller = new OperationsController(statusHandler);

            // 1. Missing terminal -> 401 Unauthorized
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            var unauthResult1 = await controller.GetOperationStatus(clientOpId, default);
            var obj1 = Assert.IsType<ObjectResult>(unauthResult1);
            Assert.Equal(StatusCodes.Status401Unauthorized, obj1.StatusCode);

            // 2. Missing actor -> 401 Unauthorized
            var httpContext2 = new DefaultHttpContext();
            httpContext2.Items["CurrentTerminal"] = new Terminal { Id = terminalId, Name = "POS-1", TerminalCode = "T1" };
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext2 };
            var unauthResult2 = await controller.GetOperationStatus(clientOpId, default);
            var obj2 = Assert.IsType<ObjectResult>(unauthResult2);
            Assert.Equal(StatusCodes.Status401Unauthorized, obj2.StatusCode);

            // 3. Authorized request -> 200 OK
            var httpContext3 = new DefaultHttpContext();
            httpContext3.Items["CurrentTerminal"] = new Terminal { Id = terminalId, Name = "POS-1", TerminalCode = "T1" };
            httpContext3.Items["ActorContext"] = new ActorContext(cashierId, Guid.NewGuid(), "Cashier 1", Guid.NewGuid(), "Cashier", new HashSet<string>());
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext3 };
            var okResult = await controller.GetOperationStatus(clientOpId, default);
            var okObj = Assert.IsType<OkObjectResult>(okResult);
            var statusResult = Assert.IsType<OperationStatusResult>(okObj.Value);
            Assert.Equal("Succeeded", statusResult.Status);
            Assert.Equal(clientOpId, statusResult.ClientOperationId);

            // 4. Mismatched terminal -> 403 Forbidden
            var httpContext4 = new DefaultHttpContext();
            httpContext4.Items["CurrentTerminal"] = new Terminal { Id = Guid.NewGuid(), Name = "Other POS", TerminalCode = "T2" };
            httpContext4.Items["ActorContext"] = new ActorContext(cashierId, Guid.NewGuid(), "Cashier 1", Guid.NewGuid(), "Cashier", new HashSet<string>());
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext4 };
            var forbiddenResult = await controller.GetOperationStatus(clientOpId, default);
            var forbiddenObj = Assert.IsType<ObjectResult>(forbiddenResult);
            Assert.Equal(StatusCodes.Status403Forbidden, forbiddenObj.StatusCode);
        }
    }

    // =========================================================================
    // P12-W2-004: Real Committed Sale Lost-Response Recovery (Area 12F)
    // =========================================================================

    [Fact]
    public async Task Postgres_LostResponseRecovery_Sale_ReplayReturnsOriginalResponse_WithoutDuplicateDomainEffects()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        var clientOpId = Guid.NewGuid();
        var cashierId = product.ActorId;
        var saleCommand = new CompleteSaleCommand(
            ClientOperationId: clientOpId,
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: "Initial Lost Response Test",
            QuotationId: null,
            Lines: [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 2m, 150m, [])],
            RegisteredTerminalId: terminalId);

        // 1. Initial attempt completes and commits
        Guid originalSaleId;
        string originalInvoice;
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>().HandleAsync(saleCommand, default);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.False(result.Value!.WasExisting);
            originalSaleId = result.Value.SaleId;
            originalInvoice = result.Value.InvoiceNumber;
        }

        // 2. Client simulates lost response / network disconnection and replays SAME command
        await using (var replayScope = provider.CreateAsyncScope())
        {
            var replayResult = await replayScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>().HandleAsync(saleCommand, default);
            Assert.True(replayResult.IsSuccess, replayResult.Error?.ToString());
            Assert.True(replayResult.Value!.WasExisting); // Replay recovery!
            Assert.Equal(originalSaleId, replayResult.Value.SaleId);
            Assert.Equal(originalInvoice, replayResult.Value.InvoiceNumber);
            Assert.Equal(300m, replayResult.Value.GrandTotal);
        }

        // 3. Database assertions: verify ZERO duplicate domain effects
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

            // Sale table has exactly 1 row for this clientOperationId
            var sales = await db.Sales.Where(x => x.ClientOperationId == clientOpId).ToListAsync();
            Assert.Single(sales);
            Assert.Equal(originalSaleId, sales[0].Id);

            // SalePayment table has exactly 1 row
            var payments = await db.SalePayments.Where(x => x.SaleId == originalSaleId).ToListAsync();
            Assert.Single(payments);
            Assert.Equal(300m, payments[0].AmountTendered);

            // Sellable stock is reduced exactly by 2 (10 - 2 = 8), NOT by 4
            var balance = await db.StockBalances.SingleAsync(x => x.ProductId == product.ProductId);
            Assert.Equal(8m, balance.SellableQty);

            // Outcome table has exactly 1 record with Succeeded
            var outcome = await db.OperationOutcomes.SingleAsync(x => x.ClientOperationId == clientOpId);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
            Assert.True(outcome.WasCommitted);
            Assert.Equal(originalSaleId, outcome.ResultEntityId);
        }
    }

    // =========================================================================
    // P12-W2-005: Same-ID Converted-Draft Replay Before Mutable-State Validation (Area 12G / F07 / H01)
    // =========================================================================

    [Fact]
    public async Task Postgres_DraftReplay_SameOperationId_RecoversCommittedSale_WasExistingTrue()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        // 1. Create and save POS draft
        Guid draftId;
        long draftVersion;
        await using (var scope = provider.CreateAsyncScope())
        {
            var saveDraftResult = await scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>().HandleAsync(
                new SavePosDraftCommand(
                    DraftId: null,
                    ExpectedVersion: null,
                    CustomerId: null,
                    ActorId: product.ActorId,
                    TerminalId: "T12",
                    Note: "Draft for replay test",
                    Items: [new SavePosDraftItemInput(product.ProductId, product.ProductUnitId, 2m)]),
                default);

            Assert.True(saveDraftResult.IsSuccess, saveDraftResult.Error?.ToString());
            draftId = saveDraftResult.Value!.DraftId;
            draftVersion = saveDraftResult.Value.Version;
        }

        var clientOpId = Guid.NewGuid();
        var completeDraftCommand = new CompletePosDraftCommand(
            DraftId: draftId,
            ExpectedVersion: draftVersion,
            ClientOperationId: clientOpId,
            CashierUserId: product.ActorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: "Draft Completion",
            RegisteredTerminalId: terminalId);

        // 2. Complete draft initially
        Guid committedSaleId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var completeResult = await scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(completeDraftCommand, default);
            Assert.True(completeResult.IsSuccess, completeResult.Error?.ToString());
            Assert.False(completeResult.Value!.WasExisting);
            committedSaleId = completeResult.Value.SaleId;
        }

        // Verify draft is Converted
        await using (var checkScope = provider.CreateAsyncScope())
        {
            var db = checkScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var draft = await db.PosDrafts.AsNoTracking().SingleAsync(x => x.Id == draftId);
            Assert.Equal(PosDraftStatus.Converted, draft.Status);
        }

        // 3. Same-ID replay executes BEFORE draft mutable state validation and recovers original sale
        await using (var replayScope = provider.CreateAsyncScope())
        {
            var replayResult = await replayScope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(completeDraftCommand, default);
            Assert.True(replayResult.IsSuccess, replayResult.Error?.ToString());
            Assert.True(replayResult.Value!.WasExisting);
            Assert.Equal(committedSaleId, replayResult.Value.SaleId);
        }

        // 4. Verify stock and sale row counts are preserved
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Single(await db.Sales.Where(x => x.ClientOperationId == clientOpId).ToListAsync());
            var balance = await db.StockBalances.SingleAsync(x => x.ProductId == product.ProductId);
            Assert.Equal(8m, balance.SellableQty);
        }
    }

    [Fact]
    public async Task Postgres_DraftReplay_NewOperationIdOnConvertedDraft_FailsWithDraftAlreadyConverted()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        Guid draftId;
        long draftVersion;
        await using (var scope = provider.CreateAsyncScope())
        {
            var saveDraftResult = await scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>().HandleAsync(
                new SavePosDraftCommand(
                    DraftId: null,
                    ExpectedVersion: null,
                    CustomerId: null,
                    ActorId: product.ActorId,
                    TerminalId: "T12",
                    Note: "Draft for converted test",
                    Items: [new SavePosDraftItemInput(product.ProductId, product.ProductUnitId, 2m)]),
                default);

            draftId = saveDraftResult.Value!.DraftId;
            draftVersion = saveDraftResult.Value.Version;
        }

        var firstOpId = Guid.NewGuid();
        var completeCommand1 = new CompletePosDraftCommand(
            DraftId: draftId,
            ExpectedVersion: draftVersion,
            ClientOperationId: firstOpId,
            CashierUserId: product.ActorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: "First Op",
            RegisteredTerminalId: terminalId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(completeCommand1, default);
            Assert.True(result.IsSuccess);
        }

        // Attempt to convert the same draft with a NEW ClientOperationId
        var freshOpId = Guid.NewGuid();
        var completeCommand2 = completeCommand1 with { ClientOperationId = freshOpId };

        await using (var scope = provider.CreateAsyncScope())
        {
            var rejected = await scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(completeCommand2, default);
            Assert.False(rejected.IsSuccess);
            Assert.Equal("sales.draft_already_converted", rejected.Error?.Code);
        }

        // Verify zero fallthrough mutations: no sale for freshOpId and stock balance unaltered
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var salesCount = await db.Sales.CountAsync(x => x.ClientOperationId == freshOpId);
            Assert.Equal(0, salesCount);
            var balance = await db.StockBalances.SingleAsync(x => x.ProductId == product.ProductId);
            Assert.Equal(8m, balance.SellableQty);
        }
    }

    [Fact]
    public async Task Postgres_DraftReplay_AlteredPayload_FailsWithPayloadMismatch()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        Guid draftId;
        long draftVersion;
        await using (var scope = provider.CreateAsyncScope())
        {
            var saveDraftResult = await scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>().HandleAsync(
                new SavePosDraftCommand(
                    DraftId: null,
                    ExpectedVersion: null,
                    CustomerId: null,
                    ActorId: product.ActorId,
                    TerminalId: "T12",
                    Note: "Draft for payload mismatch test",
                    Items: [new SavePosDraftItemInput(product.ProductId, product.ProductUnitId, 2m)]),
                default);

            draftId = saveDraftResult.Value!.DraftId;
            draftVersion = saveDraftResult.Value.Version;
        }

        var clientOpId = Guid.NewGuid();
        var completeCommand = new CompletePosDraftCommand(
            DraftId: draftId,
            ExpectedVersion: draftVersion,
            ClientOperationId: clientOpId,
            CashierUserId: product.ActorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: "Original Payment",
            RegisteredTerminalId: terminalId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(completeCommand, default);
            Assert.True(result.IsSuccess);
        }

        // Replay with altered AmountTendered fails closed with payload_mismatch
        var alteredCommand = completeCommand with { AmountTendered = 500m };

        await using (var scope = provider.CreateAsyncScope())
        {
            var rejected = await scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>().HandleAsync(alteredCommand, default);
            Assert.False(rejected.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", rejected.Error?.Code);
        }

        // Verify zero fallthrough mutations: exactly 1 sale for clientOpId and stock balance deducted exactly once
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var salesCount = await db.Sales.CountAsync(x => x.ClientOperationId == clientOpId);
            Assert.Equal(1, salesCount);
            var balance = await db.StockBalances.SingleAsync(x => x.ProductId == product.ProductId);
            Assert.Equal(8m, balance.SellableQty);
        }
    }

    [Fact]
    public async Task Postgres_DraftConversion_ConcurrentDuplicates_ExactOneCommittedEffect()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        Guid draftId;
        long draftVersion;
        await using (var scope = provider.CreateAsyncScope())
        {
            var saveDraftResult = await scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>().HandleAsync(
                new SavePosDraftCommand(
                    DraftId: null,
                    ExpectedVersion: null,
                    CustomerId: null,
                    ActorId: product.ActorId,
                    TerminalId: "T12",
                    Note: "Concurrent duplicate draft conversion",
                    Items: [new SavePosDraftItemInput(product.ProductId, product.ProductUnitId, 2m)]),
                default);

            draftId = saveDraftResult.Value!.DraftId;
            draftVersion = saveDraftResult.Value.Version;
        }

        var clientOpId = Guid.NewGuid();
        var completeCommand = new CompletePosDraftCommand(
            DraftId: draftId,
            ExpectedVersion: draftVersion,
            ClientOperationId: clientOpId,
            CashierUserId: product.ActorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: "Concurrent Replay",
            RegisteredTerminalId: terminalId);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>();
            await barrier.Task;
            return await handler.HandleAsync(completeCommand, default);
        }).ToArray();

        barrier.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.True(results.All(r => r.IsSuccess));
        Assert.Single(results, r => !r.Value!.WasExisting);
        Assert.Single(results, r => r.Value!.WasExisting);

        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sales = await db.Sales.Where(x => x.ClientOperationId == clientOpId).ToListAsync();
            Assert.Single(sales);
            var balance = await db.StockBalances.SingleAsync(x => x.ProductId == product.ProductId);
            Assert.Equal(8m, balance.SellableQty);
        }
    }

    [Fact]
    public async Task Postgres_DraftConversion_ControlledInterleaving_SameIntent_RecoversExistingSale()
    {
        var coordinator = new ControlledOutcomeCoordinator();
        await using var provider = Phase2PostgresTestHarness.BuildProvider(configure: services =>
        {
            var descriptor = services.First(d => d.ServiceType == typeof(IOperationOutcomeLedger));
            services.Remove(descriptor);
            services.AddScoped<IOperationOutcomeLedger>(sp =>
            {
                var inner = new EfOperationOutcomeLedger(sp.GetRequiredService<EdgeRetailsDbContext>());
                return new ControlledOutcomeLedger(inner, coordinator);
            });
        });

        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        Guid draftId;
        long draftVersion;
        await using (var scope = provider.CreateAsyncScope())
        {
            var saveDraftResult = await scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>().HandleAsync(
                new SavePosDraftCommand(
                    DraftId: null,
                    ExpectedVersion: null,
                    CustomerId: null,
                    ActorId: product.ActorId,
                    TerminalId: "T12",
                    Note: "Controlled interleaving same-intent draft conversion",
                    Items: [new SavePosDraftItemInput(product.ProductId, product.ProductUnitId, 2m)]),
                default);

            draftId = saveDraftResult.Value!.DraftId;
            draftVersion = saveDraftResult.Value.Version;
        }

        var clientOpId = Guid.NewGuid();
        coordinator.TargetOperationId = clientOpId;
        var completeCommand = new CompletePosDraftCommand(
            DraftId: draftId,
            ExpectedVersion: draftVersion,
            ClientOperationId: clientOpId,
            CashierUserId: product.ActorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: "Controlled Interleaving Same Intent",
            RegisteredTerminalId: terminalId);

        // Caller 1 (Winner) and Caller 2 (Waiter) both start:
        // Winner performs lookup 1 (sees null), Waiter performs lookup 2 (sees null).
        // Winner acquires lock and performs lookup 3 (sees null), then completes and commits sale.
        // Waiter unblocks on lock, performs lookup 4 (sees Winner's committed outcome), and recovers existing sale.
        var winnerTask = Task.Run(async () =>
        {
            ControlledOutcomeLedger.IsWinnerContext.Value = true;
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>();
            return await handler.HandleAsync(completeCommand, default);
        });

        var waiterTask = Task.Run(async () =>
        {
            ControlledOutcomeLedger.IsWinnerContext.Value = false;
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>();
            return await handler.HandleAsync(completeCommand, default);
        });

        var results = await Task.WhenAll(winnerTask, waiterTask);
        var winnerResult = results[0];
        var waiterResult = results[1];

        Assert.True(winnerResult.IsSuccess, $"Winner failed: {winnerResult.Error?.Code}: {winnerResult.Error?.Message}");
        Assert.False(winnerResult.Value!.WasExisting);

        Assert.True(waiterResult.IsSuccess, $"Waiter failed: {waiterResult.Error?.Code}: {waiterResult.Error?.Message}");
        Assert.True(waiterResult.Value!.WasExisting);

        Assert.Equal(winnerResult.Value.SaleId, waiterResult.Value.SaleId);
        Assert.Equal(winnerResult.Value.InvoiceNumber, waiterResult.Value.InvoiceNumber);

        // Explicitly assert that both initial lookups observed real null from the DB,
        // and waiter performed its second lookup post-lock and observed the committed winner outcome!
        Assert.Null(coordinator.WinnerInitialOutcome);
        Assert.Null(coordinator.WaiterInitialOutcome);
        Assert.NotNull(coordinator.WaiterPostLockOutcome);
        Assert.Equal(OperationOutcomeState.Succeeded, coordinator.WaiterPostLockOutcome.State);
        Assert.True(coordinator.WaiterPostLockOutcome.WasCommitted);
        Assert.Equal(winnerResult.Value.SaleId, coordinator.WaiterPostLockOutcome.EntityId);
        Assert.Equal(3, coordinator.WinnerCallCount);
        Assert.Equal(2, coordinator.WaiterCallCount);

        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sales = await db.Sales.Where(x => x.ClientOperationId == clientOpId).ToListAsync();
            Assert.Single(sales);
            var balance = await db.StockBalances.SingleAsync(x => x.ProductId == product.ProductId);
            Assert.Equal(8m, balance.SellableQty);
        }
    }

    [Theory]
    [InlineData("actor_mismatch")]
    [InlineData("terminal_mismatch")]
    [InlineData("draft_mismatch")]
    [InlineData("payment_mismatch")]
    public async Task Postgres_DraftConversion_ControlledInterleaving_ChangedContextOrPayload_RejectsWithZeroSecondEffects(string variant)
    {
        var coordinator = new ControlledOutcomeCoordinator();
        await using var provider = Phase2PostgresTestHarness.BuildProvider(configure: services =>
        {
            var descriptor = services.First(d => d.ServiceType == typeof(IOperationOutcomeLedger));
            services.Remove(descriptor);
            services.AddScoped<IOperationOutcomeLedger>(sp =>
            {
                var inner = new EfOperationOutcomeLedger(sp.GetRequiredService<EdgeRetailsDbContext>());
                return new ControlledOutcomeLedger(inner, coordinator);
            });
        });

        var (product, terminalId) = await SetupStockAndTerminalAsync(provider, initialStock: 10m);

        Guid draftId1;
        long draftVersion1;
        Guid draftId2;
        long draftVersion2;

        await using (var scope = provider.CreateAsyncScope())
        {
            var saveDraft1 = await scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>().HandleAsync(
                new SavePosDraftCommand(
                    DraftId: null,
                    ExpectedVersion: null,
                    CustomerId: null,
                    ActorId: product.ActorId,
                    TerminalId: "T12",
                    Note: "Controlled interleaving draft 1",
                    Items: [new SavePosDraftItemInput(product.ProductId, product.ProductUnitId, 2m)]),
                default);
            draftId1 = saveDraft1.Value!.DraftId;
            draftVersion1 = saveDraft1.Value.Version;

            var saveDraft2 = await scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>().HandleAsync(
                new SavePosDraftCommand(
                    DraftId: null,
                    ExpectedVersion: null,
                    CustomerId: null,
                    ActorId: product.ActorId,
                    TerminalId: "T12",
                    Note: "Controlled interleaving draft 2 (different items)",
                    Items: [new SavePosDraftItemInput(product.ProductId, product.ProductUnitId, 1m)]),
                default);
            draftId2 = saveDraft2.Value!.DraftId;
            draftVersion2 = saveDraft2.Value.Version;
        }

        Guid otherActorId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            otherActorId = await IntegrationIdentitySeeder.CreateActorAsync(db);
        }

        var clientOpId = Guid.NewGuid();
        coordinator.TargetOperationId = clientOpId;

        var winnerCommand = new CompletePosDraftCommand(
            DraftId: draftId1,
            ExpectedVersion: draftVersion1,
            ClientOperationId: clientOpId,
            CashierUserId: product.ActorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: "Winner Original Intent",
            RegisteredTerminalId: terminalId);

        var waiterCommand = variant switch
        {
            "actor_mismatch" => winnerCommand with { CashierUserId = otherActorId },
            "terminal_mismatch" => winnerCommand with { RegisteredTerminalId = Guid.NewGuid() },
            "draft_mismatch" => winnerCommand with { DraftId = draftId2, ExpectedVersion = draftVersion2 },
            "payment_mismatch" => winnerCommand with { PaymentMethod = SalePaymentMethod.Bank },
            _ => throw new ArgumentException($"Unknown variant: {variant}")
        };

        var winnerTask = Task.Run(async () =>
        {
            ControlledOutcomeLedger.IsWinnerContext.Value = true;
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>();
            return await handler.HandleAsync(winnerCommand, default);
        });

        var waiterTask = Task.Run(async () =>
        {
            ControlledOutcomeLedger.IsWinnerContext.Value = false;
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>();
            return await handler.HandleAsync(waiterCommand, default);
        });

        var results = await Task.WhenAll(winnerTask, waiterTask);
        var winnerResult = results[0];
        var waiterResult = results[1];

        // Winner must succeed as the original canonical transaction
        Assert.True(winnerResult.IsSuccess, $"Winner failed: {winnerResult.Error?.Code}: {winnerResult.Error?.Message}");
        Assert.False(winnerResult.Value!.WasExisting);

        // Waiter must be rejected post-lock with canonical failure code matching the modified context/payload
        Assert.False(waiterResult.IsSuccess, "Waiter should have been rejected post-lock with canonical mismatch.");

        if (variant == "actor_mismatch" || variant == "terminal_mismatch")
        {
            Assert.Equal("idempotency.context_mismatch", waiterResult.Error?.Code);
        }
        else
        {
            Assert.Equal("idempotency.payload_mismatch", waiterResult.Error?.Code);
        }

        // Explicitly assert that both initial lookups observed real null from the DB,
        // and waiter performed its second lookup post-lock and observed the committed winner outcome!
        Assert.Null(coordinator.WinnerInitialOutcome);
        Assert.Null(coordinator.WaiterInitialOutcome);
        Assert.NotNull(coordinator.WaiterPostLockOutcome);
        Assert.Equal(OperationOutcomeState.Succeeded, coordinator.WaiterPostLockOutcome.State);
        Assert.True(coordinator.WaiterPostLockOutcome.WasCommitted);
        Assert.Equal(winnerResult.Value.SaleId, coordinator.WaiterPostLockOutcome.EntityId);
        Assert.Equal(3, coordinator.WinnerCallCount);
        Assert.Equal(2, coordinator.WaiterCallCount);

        // Verify ZERO second effects:
        // Exactly 1 sale created in Postgres
        // Exactly 1 payment created
        // Stock decremented only once (8m sellable remaining, draft 2 was NOT converted)
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sales = await db.Sales.Where(x => x.ClientOperationId == clientOpId).ToListAsync();
            Assert.Single(sales);
            Assert.Equal(winnerResult.Value.SaleId, sales[0].Id);

            var payments = await db.SalePayments.Where(x => x.SaleId == winnerResult.Value.SaleId).ToListAsync();
            Assert.Single(payments);
            Assert.Equal(SalePaymentMethod.Cash, payments[0].Method);

            var balance = await db.StockBalances.SingleAsync(x => x.ProductId == product.ProductId);
            Assert.Equal(8m, balance.SellableQty);
        }
    }

    [Fact]
    public async Task Postgres_DraftConversion_OperationTypeMismatch_RejectsWithCanonicalError()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider);

        Guid draftId;
        long draftVersion;
        await using (var scope = provider.CreateAsyncScope())
        {
            var saveDraft = await scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>().HandleAsync(
                new SavePosDraftCommand(
                    DraftId: null,
                    ExpectedVersion: null,
                    CustomerId: null,
                    ActorId: product.ActorId,
                    TerminalId: "T12",
                    Note: "Draft for operation type mismatch test",
                    Items: [new SavePosDraftItemInput(product.ProductId, product.ProductUnitId, 1m)]),
                default);
            draftId = saveDraft.Value!.DraftId;
            draftVersion = saveDraft.Value.Version;
        }

        var clientOpId = Guid.NewGuid();

        // Pre-register an outcome with a different operation type ("Purchase") at the authoritative boundary
        await using (var scope = provider.CreateAsyncScope())
        {
            var outcomeLedger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            await outcomeLedger.ClaimAsync(clientOpId, "Purchase", "purchase-fingerprint-payload", product.ActorId, terminalId, null, default);
        }

        // Attempt CompletePosDraftCommand using the same clientOpId
        var command = new CompletePosDraftCommand(
            DraftId: draftId,
            ExpectedVersion: draftVersion,
            ClientOperationId: clientOpId,
            CashierUserId: product.ActorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 150m,
            PaymentReference: "OpType Mismatch Test",
            RegisteredTerminalId: terminalId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>();
            var result = await handler.HandleAsync(command, default);

            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.operation_type_mismatch", result.Error?.Code);
            Assert.Contains("'Purchase', not 'Sale'", result.Error?.Message);
        }

        // Verify ZERO second effects: no sale created
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sales = await db.Sales.Where(x => x.ClientOperationId == clientOpId).ToListAsync();
            Assert.Empty(sales);
        }
    }

    private sealed class ControlledOutcomeCoordinator
    {
        public Guid TargetOperationId { get; set; }

        public readonly TaskCompletionSource WinnerInitialReadDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource WaiterInitialReadDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource WinnerAcquiredLock = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationOutcomeRecord? WinnerInitialOutcome { get; set; }
        public OperationOutcomeRecord? WaiterInitialOutcome { get; set; }
        public OperationOutcomeRecord? WaiterPostLockOutcome { get; set; }

        public int WinnerCallCount => _winnerCallCount;
        public int WaiterCallCount => _waiterCallCount;

        private int _winnerCallCount = 0;
        private int _waiterCallCount = 0;

        public async Task<OperationOutcomeRecord?> CoordinateWinnerLookupAsync(Func<Task<OperationOutcomeRecord?>> innerRead, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _winnerCallCount);
            if (call == 1)
            {
                // Winner executes its real initial inner read against PostgreSQL BEFORE continuing
                var initialResult = await innerRead();
                WinnerInitialOutcome = initialResult;
                WinnerInitialReadDone.TrySetResult();

                // Wait until waiter has ALSO performed its real initial inner read and captured null
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                await WaiterInitialReadDone.Task.WaitAsync(linked.Token);

                return initialResult;
            }
            else
            {
                // Winner post-lock lookup inside transaction: Winner has acquired both operation and draft locks.
                // Signal waiter that locks are held so waiter can safely proceed into transaction lock acquisition.
                WinnerAcquiredLock.TrySetResult();
                return await innerRead();
            }
        }

        public async Task<OperationOutcomeRecord?> CoordinateWaiterLookupAsync(Func<Task<OperationOutcomeRecord?>> innerRead, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _waiterCallCount);
            if (call == 1)
            {
                // Waiter pre-lock: wait until winner has performed its initial inner read
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                await WinnerInitialReadDone.Task.WaitAsync(linked.Token);

                // Waiter executes its real initial inner read while winner is held waiting on WaiterInitialReadDone!
                // Since winner has not yet acquired the lock or committed, this real DB read is guaranteed to observe null.
                var initialResult = await innerRead();
                WaiterInitialOutcome = initialResult;
                WaiterInitialReadDone.TrySetResult();

                // Now wait until Winner has acquired the operation lock inside its transaction before returning null to handler.
                // This guarantees that when Waiter proceeds to handler's _transactions.ExecuteAsync,
                // Waiter will strictly block on _operationLock.AcquireAsync behind Winner in PostgreSQL.
                await WinnerAcquiredLock.Task.WaitAsync(linked.Token);

                return initialResult;
            }
            else
            {
                // Waiter post-lock lookup: Waiter unblocked and acquired the lock AFTER winner committed!
                // Execute real inner read against PostgreSQL to observe winner's committed outcome.
                var postLockResult = await innerRead();
                WaiterPostLockOutcome = postLockResult;
                return postLockResult;
            }
        }
    }

    private sealed class ControlledOutcomeLedger : IOperationOutcomeLedger
    {
        public static readonly AsyncLocal<bool> IsWinnerContext = new();

        private readonly IOperationOutcomeLedger _inner;
        private readonly ControlledOutcomeCoordinator _coordinator;

        public ControlledOutcomeLedger(IOperationOutcomeLedger inner, ControlledOutcomeCoordinator coordinator)
        {
            _inner = inner;
            _coordinator = coordinator;
        }

        public async Task<OperationOutcomeRecord?> GetOutcomeAsync(Guid clientOperationId, CancellationToken cancellationToken = default)
        {
            if (_coordinator.TargetOperationId != Guid.Empty && clientOperationId == _coordinator.TargetOperationId)
            {
                if (IsWinnerContext.Value)
                {
                    return await _coordinator.CoordinateWinnerLookupAsync(() => _inner.GetOutcomeAsync(clientOperationId, cancellationToken), cancellationToken);
                }
                else
                {
                    return await _coordinator.CoordinateWaiterLookupAsync(() => _inner.GetOutcomeAsync(clientOperationId, cancellationToken), cancellationToken);
                }
            }
            return await _inner.GetOutcomeAsync(clientOperationId, cancellationToken);
        }

        public Task<OperationClaimResult> ClaimAsync(Guid clientOperationId, string operationType, string payloadFingerprint, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, CancellationToken cancellationToken = default) =>
            _inner.ClaimAsync(clientOperationId, operationType, payloadFingerprint, actorId, terminalId, sessionId, cancellationToken);

        public Task RecordPendingAsync(Guid clientOperationId, string operationType, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) =>
            _inner.RecordPendingAsync(clientOperationId, operationType, actorId, terminalId, sessionId, payloadFingerprint, cancellationToken);

        public Task RecordSuccessAsync(Guid clientOperationId, string operationType, Guid entityId, string? documentNumber = null, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) =>
            _inner.RecordSuccessAsync(clientOperationId, operationType, entityId, documentNumber, actorId, terminalId, sessionId, payloadFingerprint, cancellationToken);

        public Task RecordSuccessWithResponseAsync(Guid clientOperationId, string operationType, Guid entityId, string? responsePayload, int responseStatusCode = 200, string responseContentType = "application/json", string? documentNumber = null, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) =>
            _inner.RecordSuccessWithResponseAsync(clientOperationId, operationType, entityId, responsePayload, responseStatusCode, responseContentType, documentNumber, actorId, terminalId, sessionId, payloadFingerprint, cancellationToken);

        public Task RecordFailureAsync(Guid clientOperationId, string operationType, string errorCode, string errorMessage, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) =>
            _inner.RecordFailureAsync(clientOperationId, operationType, errorCode, errorMessage, actorId, terminalId, sessionId, payloadFingerprint, cancellationToken);

        public Task RecordFailureWithResponseAsync(Guid clientOperationId, string operationType, string errorCode, string errorMessage, string? responsePayload = null, int responseStatusCode = 400, string responseContentType = "application/problem+json", Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) =>
            _inner.RecordFailureWithResponseAsync(clientOperationId, operationType, errorCode, errorMessage, responsePayload, responseStatusCode, responseContentType, actorId, terminalId, sessionId, payloadFingerprint, cancellationToken);

        public Task RecordOutcomeUnknownAsync(Guid clientOperationId, string operationType, Guid? actorId = null, Guid? terminalId = null, Guid? sessionId = null, string? payloadFingerprint = null, CancellationToken cancellationToken = default) =>
            _inner.RecordOutcomeUnknownAsync(clientOperationId, operationType, actorId, terminalId, sessionId, payloadFingerprint, cancellationToken);
    }

    // =========================================================================
    // P12-W2-011: Validated Final Rejection Policy & Outcome Integrity
    // =========================================================================

    [Fact]
    public async Task Postgres_FinalRejection_QueryReturnsFailedStatusAndCachedErrorDetails()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var (product, terminalId) = await SetupStockAndTerminalAsync(provider);

        var clientOpId = Guid.NewGuid();
        var inactiveSupplierId = Guid.NewGuid(); // Non-existent supplier

        var purchaseCommand = new CreatePurchaseCommand(
            SupplierId: inactiveSupplierId,
            SupplierInvoiceNumber: "FAIL-TEST-" + Guid.NewGuid().ToString("N"),
            PurchaseDate: DateOnly.FromDateTime(DateTime.UtcNow),
            Note: "Expected failure",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: product.ActorId,
            ClientOperationId: clientOpId,
            Lines: [new(product.ProductId, product.ProductUnitId, 1m, 10m, 15m, [])]);

        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(purchaseCommand, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("purchasing.supplier_not_active", result.Error?.Code);
        }

        // Query status for the failed operation
        await using (var queryScope = provider.CreateAsyncScope())
        {
            var statusHandler = queryScope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: clientOpId,
                ActorId: product.ActorId,
                RequireIdentityScope: false,
                RequireCanonicalOutcome: true);

            var statusResult = await statusHandler.HandleAsync(query, default);
            Assert.True(statusResult.IsSuccess);

            var status = statusResult.Value!;
            Assert.True(status.Found);
            Assert.Equal("Failed", status.Status);
            Assert.False(status.WasCommitted);
            Assert.Equal("purchasing.supplier_not_active", status.ErrorCode);
            Assert.NotNull(status.ErrorMessage);
        }
    }
}
