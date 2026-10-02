using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Server.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase3ServerPostgresOperationalTraceTests
{
    [Fact]
    public async Task AuthenticatedServer_PostgresOperationalTrace_CoversReceivingSalesReturnsWarrantyKhataPrintingStocktakeAndBackup()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        Assert.False(string.IsNullOrWhiteSpace(connectionString),
            "This operational trace requires EDGE_RETAILS_TEST_DB to point to the disposable PostgreSQL rehearsal database.");

        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var highWaterService = provider.GetRequiredService<ISequenceHighWaterService>();
        using var factory = new ProductionServerFactory(connectionString!, highWaterService);
        using var client = factory.CreateClient();
        var terminalId = Guid.CreateVersion7();
        var terminalSecret = "phase3-pg-trace-" + Guid.NewGuid().ToString("N");
        Guid actorId;
        Guid sessionId;
        QuantityProductFixture product;
        string quantityProductName;
        string stocktakeNote = "Phase 3 PostgreSQL operational trace " + Guid.NewGuid().ToString("N");
        Guid? stocktakeId = null;
        Guid? cashSessionId = null;

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);
            quantityProductName = await db.Products
                .Where(x => x.Id == product.ProductId)
                .Select(x => x.Name)
                .SingleAsync();
            actorId = product.ActorId;
            cashSessionId = (await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, actorId)).Id;

            var now = DateTimeOffset.UtcNow;
            db.Terminals.Add(new Terminal
            {
                Id = terminalId,
                TerminalCode = "P3-" + Guid.NewGuid().ToString("N")[..10],
                Name = "Phase 3 PostgreSQL trace",
                Status = TerminalStatus.Active,
                AuthSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terminalSecret))),
                RegisteredAt = now,
                LastSeenAt = now,
                ProtocolVersion = TerminalProtocol.CurrentProtocolVersion
            });
            var session = new UserSession
            {
                UserId = actorId,
                ClientSessionId = Guid.CreateVersion7(),
                StartedAt = now,
                IsRevoked = false
            };
            db.UserSessions.Add(session);
            await db.SaveChangesAsync();
            sessionId = session.Id;
        }

        SetAuthentication(client, terminalId, terminalSecret, sessionId);
        try
        {
            var purchaseCommand = new CreatePurchaseCommand(
                product.SupplierId,
                "P3-TRACE-" + Guid.NewGuid().ToString("N")[..10],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Server-to-PostgreSQL operational trace",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(product.ProductId, product.ProductUnitId, 3m, 75m, 150m, [])],
                InitialPaymentAmount: 0m);
            using var purchaseResponse = await client.PostAsJsonAsync("/api/purchasing/create", purchaseCommand);
            var purchaseBody = await purchaseResponse.Content.ReadAsStringAsync();
            Assert.True(
                purchaseResponse.StatusCode == HttpStatusCode.OK,
                $"Purchase endpoint returned {(int)purchaseResponse.StatusCode}: {purchaseBody}");
            using var purchaseJson = JsonDocument.Parse(purchaseBody);
            var purchaseId = purchaseJson.RootElement.GetProperty("purchaseId").GetGuid();

            var deferredPurchaseCommand = new CreatePurchaseCommand(
                product.SupplierId,
                "P3-INTAKE-" + Guid.NewGuid().ToString("N")[..10],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Deferred receiving trace",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(product.ProductId, product.ProductUnitId, 2m, 75m, 150m, [])],
                InitialPaymentAmount: 0m,
                ReceiveStockImmediately: false);
            using var deferredPurchaseResponse = await client.PostAsJsonAsync("/api/purchasing/create", deferredPurchaseCommand);
            Assert.Equal(HttpStatusCode.OK, deferredPurchaseResponse.StatusCode);
            using var deferredPurchaseJson = JsonDocument.Parse(await deferredPurchaseResponse.Content.ReadAsStringAsync());
            var deferredPurchaseId = deferredPurchaseJson.RootElement.GetProperty("purchaseId").GetGuid();

            var intakeCommand = new ReceiveProductIntakeCommand(
                deferredPurchaseId,
                product.ProductId,
                product.ProductUnitId,
                2m,
                75m,
                [],
                actorId,
                Guid.CreateVersion7(),
                "Physical receiving trace");
            using var intakeResponse = await client.PostAsJsonAsync("/api/purchasing/intake", intakeCommand);
            var intakeBody = await intakeResponse.Content.ReadAsStringAsync();
            Assert.True(
                intakeResponse.StatusCode == HttpStatusCode.OK,
                $"Physical intake endpoint returned {(int)intakeResponse.StatusCode}: {intakeBody}");
            using var intakeJson = JsonDocument.Parse(intakeBody);
            Assert.Equal(2m, intakeJson.RootElement.GetProperty("receivedQuantity").GetDecimal());

            using var supplierWorkspaceResponse = await client.GetAsync($"/api/finance/suppliers/{product.SupplierId:D}/workspace");
            Assert.Equal(HttpStatusCode.OK, supplierWorkspaceResponse.StatusCode);
            var paymentCommand = new CreateSupplierPaymentCommand(
                product.SupplierId,
                25m,
                SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.External,
                actorId,
                Guid.CreateVersion7(),
                "P3-TRACE-SETTLEMENT",
                "Supplier khata trace");
            using var paymentResponse = await client.PostAsJsonAsync("/api/finance/supplier-payment", paymentCommand);
            var paymentBody = await paymentResponse.Content.ReadAsStringAsync();
            Assert.True(
                paymentResponse.StatusCode == HttpStatusCode.OK,
                $"Supplier payment endpoint returned {(int)paymentResponse.StatusCode}: {paymentBody}");

            using var catalogResponse = await client.GetAsync($"/api/sales/catalog?search={Uri.EscapeDataString(quantityProductName)}");
            Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
            using var catalogJson = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
            Assert.Contains(catalogJson.RootElement.EnumerateArray(),
                row => row.GetProperty("productId").GetGuid() == product.ProductId);

            var saleCommand = new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                actorId,
                sessionId,
                0m,
                SalePaymentMethod.Cash,
                200m,
                null,
                null,
                [new CompleteSaleLineInput(product.ProductId, product.ProductUnitId, 1m, 150m, [])]);
            using var saleResponse = await client.PostAsJsonAsync("/api/sales/complete", saleCommand);
            Assert.Equal(HttpStatusCode.OK, saleResponse.StatusCode);
            using var saleJson = JsonDocument.Parse(await saleResponse.Content.ReadAsStringAsync());
            var saleId = saleJson.RootElement.GetProperty("saleId").GetGuid();
            Assert.False(string.IsNullOrWhiteSpace(saleJson.RootElement.GetProperty("invoiceNumber").GetString()));

            Guid saleItemId;
            await using (var readProvider = Phase2PostgresTestHarness.BuildProvider())
            await using (var readScope = readProvider.CreateAsyncScope())
            {
                var readDb = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                saleItemId = await readDb.SaleItems.Where(x => x.SaleId == saleId).Select(x => x.Id).SingleAsync();
            }

            var returnCommand = new CreateSaleReturnCommand(
                saleId,
                "CUSTOMER_RETURN",
                "Phase 3 route-backed sale return trace",
                RefundMethod.Bank,
                actorId,
                Guid.CreateVersion7(),
                [new SaleReturnLineInput(saleItemId, 1m, SaleReturnDisposition.RestockSellable, [])]);
            using var returnResponse = await client.PostAsJsonAsync("/api/sales/return", returnCommand);
            Assert.Equal(HttpStatusCode.OK, returnResponse.StatusCode);
            using var returnJson = JsonDocument.Parse(await returnResponse.Content.ReadAsStringAsync());
            var saleReturnId = returnJson.RootElement.GetProperty("saleReturnId").GetGuid();
            using var returnReceiptResponse = await client.GetAsync($"/api/printing/documents/SaleReturnReceipt/{saleReturnId:D}");
            Assert.Equal(HttpStatusCode.OK, returnReceiptResponse.StatusCode);

            var serializedProduct = await SeedSerializedProductAsync();
            var originalSerial = "P3ORIG" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var replacementSerial = "P3REPL" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var serializedPurchaseCommand = new CreatePurchaseCommand(
                serializedProduct.SupplierId,
                "P3-WARRANTY-" + Guid.NewGuid().ToString("N")[..10],
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Warranty replacement trace",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(serializedProduct.ProductId, serializedProduct.ProductUnitId, 1m, 500m, 800m,
                    [new SerializedIdentityInput(originalSerial)])]);
            using var serializedPurchaseResponse = await client.PostAsJsonAsync("/api/purchasing/create", serializedPurchaseCommand);
            Assert.Equal(HttpStatusCode.OK, serializedPurchaseResponse.StatusCode);

            using var inventoryUnitsResponse = await client.GetAsync(
                $"/api/inventory/exact-units?productId={serializedProduct.ProductId:D}");
            Assert.Equal(HttpStatusCode.OK, inventoryUnitsResponse.StatusCode);
            using var inventoryUnitsJson = JsonDocument.Parse(
                await inventoryUnitsResponse.Content.ReadAsStringAsync());
            Assert.Contains(inventoryUnitsJson.RootElement.EnumerateArray(),
                row => row.GetProperty("serialNumber").GetString() == originalSerial);

            using var exactUnitsResponse = await client.GetAsync($"/api/sales/exact-units?productId={serializedProduct.ProductId:D}");
            Assert.Equal(HttpStatusCode.OK, exactUnitsResponse.StatusCode);
            using var exactUnitsJson = JsonDocument.Parse(await exactUnitsResponse.Content.ReadAsStringAsync());
            Assert.Contains(exactUnitsJson.RootElement.EnumerateArray(),
                row => row.GetProperty("serialNumber").GetString() == originalSerial);
            using var scanResponse = await client.GetAsync($"/api/sales/scan?code={Uri.EscapeDataString(originalSerial)}");
            Assert.Equal(HttpStatusCode.OK, scanResponse.StatusCode);
            using var scanJson = JsonDocument.Parse(await scanResponse.Content.ReadAsStringAsync());
            Assert.Contains(scanJson.RootElement.EnumerateArray(),
                row => row.GetProperty("serialNumber").GetString() == originalSerial);

            Guid originalUnitId;
            var warrantyCustomer = await SeedCustomerAsync();
            await using (var readProvider = Phase2PostgresTestHarness.BuildProvider())
            await using (var readScope = readProvider.CreateAsyncScope())
            {
                var readDb = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                originalUnitId = await readDb.InventoryUnits.Where(x => x.SerialNumber == originalSerial).Select(x => x.Id).SingleAsync();
            }

            var serializedSaleCommand = new CompleteSaleCommand(
                Guid.CreateVersion7(),
                warrantyCustomer.Id,
                actorId,
                sessionId,
                0m,
                SalePaymentMethod.Bank,
                800m,
                "P3-WARRANTY-BANK",
                null,
                [new CompleteSaleLineInput(serializedProduct.ProductId, serializedProduct.ProductUnitId, 1m, 800m, [originalUnitId])]);
            using var serializedSaleResponse = await client.PostAsJsonAsync("/api/sales/complete", serializedSaleCommand);
            Assert.Equal(HttpStatusCode.OK, serializedSaleResponse.StatusCode);
            using var serializedSaleJson = JsonDocument.Parse(await serializedSaleResponse.Content.ReadAsStringAsync());
            var serializedSaleId = serializedSaleJson.RootElement.GetProperty("saleId").GetGuid();

            Guid serializedSaleItemId;
            await using (var readProvider = Phase2PostgresTestHarness.BuildProvider())
            await using (var readScope = readProvider.CreateAsyncScope())
            {
                var readDb = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                serializedSaleItemId = await readDb.SaleItems.Where(x => x.SaleId == serializedSaleId).Select(x => x.Id).SingleAsync();
            }

            var createClaim = new CreateWarrantyClaimCommand(
                warrantyCustomer.Id,
                serializedSaleId,
                serializedProduct.SupplierId,
                actorId,
                [new WarrantyClaimItemInput(
                    serializedProduct.ProductId,
                    1m,
                    "Phase 3 operational warranty trace",
                    serializedSaleItemId,
                    DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                    [new WarrantyClaimUnitInput(originalUnitId, originalSerial)])],
                Guid.CreateVersion7());
            using var claimResponse = await client.PostAsJsonAsync("/api/warranty/claims", createClaim);
            Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
            using var claimJson = JsonDocument.Parse(await claimResponse.Content.ReadAsStringAsync());
            var claimId = claimJson.RootElement.GetGuid();

            foreach (var (route, note) in new[]
                     {
                         ("review", "Claim reviewed"),
                         ("send-to-supplier", "Claim sent to supplier"),
                         ("supplier-processing", "Supplier processing")
                     })
            {
                using var actionResponse = await client.PostAsJsonAsync(
                    $"/api/warranty/claims/{claimId:D}/{route}",
                    new WarrantyClaimActionRequest(Guid.CreateVersion7(), note));
                Assert.Equal(HttpStatusCode.OK, actionResponse.StatusCode);
            }

            Guid claimItemUnitId;
            await using (var readProvider = Phase2PostgresTestHarness.BuildProvider())
            await using (var readScope = readProvider.CreateAsyncScope())
            {
                var readDb = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                claimItemUnitId = await readDb.WarrantyClaimItemUnits
                    .Where(x => x.OriginalInventoryUnitId == originalUnitId)
                    .Select(x => x.Id)
                    .SingleAsync();
            }

            using var replacementResponse = await client.PostAsJsonAsync(
                $"/api/warranty/claims/{claimId:D}/replacement-receipt",
                new CustomerWarrantyReplacementRequest(
                    Guid.CreateVersion7(),
                    [new CustomerWarrantyReplacementUnitInput(claimItemUnitId, replacementSerial, null, null)],
                    "Replacement received through the authenticated server"));
            Assert.Equal(HttpStatusCode.OK, replacementResponse.StatusCode);
            using var handoverResponse = await client.PostAsJsonAsync(
                $"/api/warranty/claims/{claimId:D}/handover",
                new WarrantyClaimActionRequest(Guid.CreateVersion7(), "Replacement handed to customer"));
            Assert.Equal(HttpStatusCode.OK, handoverResponse.StatusCode);

            using var historyResponse = await client.GetAsync("/api/sales?pageSize=20");
            Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
            var history = await historyResponse.Content.ReadAsStringAsync();
            Assert.Contains(saleId.ToString("D"), history, StringComparison.OrdinalIgnoreCase);

            using var receiptResponse = await client.GetAsync($"/api/printing/documents/PosSaleReceipt/{saleId:D}");
            Assert.Equal(HttpStatusCode.OK, receiptResponse.StatusCode);
            using var receiptJson = JsonDocument.Parse(await receiptResponse.Content.ReadAsStringAsync());
            Assert.Equal(saleId, receiptJson.RootElement.GetProperty("businessDocumentId").GetGuid());
            Assert.Equal("PosSaleReceipt", receiptJson.RootElement.GetProperty("kind").GetString());
            Assert.True(receiptJson.RootElement.GetProperty("lines").GetArrayLength() > 0);

            using var stocktakeResponse = await client.PostAsJsonAsync("/api/inventory/stocktake",
                new CreateStocktakeCommand(StocktakeScope.FullShop, null, actorId, stocktakeNote, Guid.CreateVersion7()));
            Assert.Equal(HttpStatusCode.OK, stocktakeResponse.StatusCode);

            using var openStocktakeResponse = await client.GetAsync("/api/inventory/stocktake/open");
            Assert.Equal(HttpStatusCode.OK, openStocktakeResponse.StatusCode);
            using var openStocktakeJson = JsonDocument.Parse(await openStocktakeResponse.Content.ReadAsStringAsync());
            var openStocktake = openStocktakeJson.RootElement.GetProperty("stocktake");
            Assert.Equal(stocktakeNote, openStocktake.GetProperty("note").GetString());
            stocktakeId = openStocktake.GetProperty("stocktakeId").GetGuid();

            using var cancelResponse = await client.PostAsJsonAsync(
                $"/api/inventory/stocktake/{stocktakeId.Value:D}/cancel",
                new StocktakeActionApiRequest(Guid.CreateVersion7()));
            Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

            await using var verificationProvider = Phase2PostgresTestHarness.BuildProvider();
            await using var verificationScope = verificationProvider.CreateAsyncScope();
            var verifyDb = verificationScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.True(await verifyDb.Purchases.AnyAsync(x => x.Id == purchaseId));
            Assert.True(await verifyDb.Sales.AnyAsync(x => x.Id == saleId));
            Assert.Equal(5m, (await verifyDb.StockBalances.SingleAsync(x => x.ProductId == product.ProductId)).SellableQty);
            Assert.True(await verifyDb.SaleReturns.AnyAsync(x => x.Id == saleReturnId));
            Assert.Equal(WarrantyClaimStatus.Closed,
                await verifyDb.WarrantyClaims.Where(x => x.Id == claimId).Select(x => x.Status).SingleAsync());
            Assert.Equal(InventoryUnitStatus.WarrantyCustomerHandedOver,
                await verifyDb.InventoryUnits.Where(x => x.SerialNumber == replacementSerial).Select(x => x.Status).SingleAsync());
            Assert.Equal(350m, await verifyDb.SupplierAccountEntries
                .Where(x => x.SupplierId == product.SupplierId)
                .SumAsync(x => x.Direction == SupplierAccountDirection.IncreasePayable ? x.Amount : -x.Amount));
            Assert.Equal(StocktakeStatus.Cancelled,
                await verifyDb.Stocktakes.Where(x => x.Id == stocktakeId).Select(x => x.Status).SingleAsync());

            var backupCorrelationId = Guid.CreateVersion7();
            using var backupResponse = await client.PostAsJsonAsync("/api/backups", new CreateBackupApiRequest(backupCorrelationId));
            Assert.Equal(HttpStatusCode.OK, backupResponse.StatusCode);
            using var backupJson = JsonDocument.Parse(await backupResponse.Content.ReadAsStringAsync());
            Assert.True(backupJson.RootElement.GetProperty("backup").GetProperty("sizeBytes").GetInt64() > 0);
            using var backupHistoryResponse = await client.GetAsync("/api/backups");
            Assert.Equal(HttpStatusCode.OK, backupHistoryResponse.StatusCode);
            Assert.Contains(backupJson.RootElement.GetProperty("backup").GetProperty("backupId").GetGuid().ToString("D"),
                await backupHistoryResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var cleanupProvider = Phase2PostgresTestHarness.BuildProvider();
            await using var cleanupScope = cleanupProvider.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            if (cashSessionId is Guid ownedCashSessionId)
            {
                await cleanupDb.CashSessions
                    .Where(x => x.Id == ownedCashSessionId && x.Status == CashSessionStatus.Open)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, CashSessionStatus.Closed));
            }
            await cleanupDb.Stocktakes
                .Where(x => x.Note == stocktakeNote && x.Status != StocktakeStatus.Posted)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, StocktakeStatus.Cancelled));
        }
    }

    [Fact]
    public async Task Installed5ProductGoldenTrace_FullPhysicalAndFinancialLifecycle_OnRealPostgreSql()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        Assert.False(string.IsNullOrWhiteSpace(connectionString),
            "This operational trace requires EDGE_RETAILS_TEST_DB to point to the disposable PostgreSQL rehearsal database.");

        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var highWaterService = provider.GetRequiredService<ISequenceHighWaterService>();
        using var factory = new ProductionServerFactory(connectionString!, highWaterService);
        using var client = factory.CreateClient();

        var terminalId = Guid.CreateVersion7();
        var terminalSecret = "gt5-secret-" + Guid.NewGuid().ToString("N");
        Guid actorId;
        Guid sessionId;
        Guid? cashSessionId = null;

        QuantityProductFixture bulkProduct;
        QuantityProductFixture serviceProduct;
        SerializedProductFixture pieceProduct;
        SerializedProductFixture serializedProduct;
        SerializedProductFixture imeiProduct;
        EdgeRetails.Domain.Parties.Customer customer;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serialNum = ("GT-SN-" + suffix).ToUpperInvariant();
        var replacementSerialNum = ("GT-SN-REPL-" + suffix).ToUpperInvariant();
        var imei1 = "35693803" + suffix[..7];
        var imei2 = "35693803" + suffix[1..8];

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

            bulkProduct = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 60m);
            actorId = bulkProduct.ActorId;
            var supplierId = bulkProduct.SupplierId;

            pieceProduct = await SeedPieceProductAsync(db, supplierId, actorId, suffix);
            serializedProduct = await SeedSerializedCustomProductAsync(db, supplierId, actorId, suffix);
            imeiProduct = await SeedImeiDualSimProductAsync(db, supplierId, actorId, suffix);
            serviceProduct = await SeedServiceCustomProductAsync(db, supplierId, actorId, suffix);
            customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "GT-Customer-" + suffix);

            cashSessionId = (await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, actorId)).Id;

            var now = DateTimeOffset.UtcNow;
            db.Terminals.Add(new Terminal
            {
                Id = terminalId,
                TerminalCode = "GT-" + suffix,
                Name = "Phase 3 5-Product Golden Trace Terminal",
                Status = TerminalStatus.Active,
                AuthSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terminalSecret))),
                RegisteredAt = now,
                LastSeenAt = now,
                ProtocolVersion = TerminalProtocol.CurrentProtocolVersion
            });
            var session = new UserSession
            {
                UserId = actorId,
                ClientSessionId = Guid.CreateVersion7(),
                StartedAt = now,
                IsRevoked = false
            };
            db.UserSessions.Add(session);
            await db.SaveChangesAsync();
            sessionId = session.Id;
        }

        SetAuthentication(client, terminalId, terminalSecret, sessionId);
        try
        {
            // STEP 1: Purchasing
            // Purchase A: Immediate intake for Bulk Quantity (10 units @ 30) and Service (5 units @ 100)
            var immediatePurchaseCommand = new CreatePurchaseCommand(
                bulkProduct.SupplierId,
                "GT-INV-BULK-" + suffix,
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Golden Trace Bulk and Service Intake",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(bulkProduct.ProductId, bulkProduct.ProductUnitId, 10m, 30m, 60m, []),
                    new CreatePurchaseLineInput(serviceProduct.ProductId, serviceProduct.ProductUnitId, 5m, 100m, 500m, [])
                ],
                InitialPaymentAmount: 500m,
                InitialPaymentMethod: SupplierSettlementMethod.External,
                InitialPaymentExternalReference: "GT-PAY-001",
                ReceiveStockImmediately: true);

            using var immPurchaseResp = await client.PostAsJsonAsync("/api/purchasing/create", immediatePurchaseCommand);
            Assert.True(immPurchaseResp.StatusCode == HttpStatusCode.OK,
                $"Immediate purchase failed: {await immPurchaseResp.Content.ReadAsStringAsync()}");

            // Purchase B: Deferred intake for Piece-Tracked, Serialized, and Dual-SIM IMEI
            var deferredPurchaseCommand = new CreatePurchaseCommand(
                bulkProduct.SupplierId,
                "GT-INV-DEFERRED-" + suffix,
                DateOnly.FromDateTime(DateTime.UtcNow),
                "Golden Trace Tracked Physical Receiving",
                0m,
                PurchaseSettlementMode.External,
                actorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(pieceProduct.ProductId, pieceProduct.ProductUnitId, 2m, 50m, 100m, []),
                    new CreatePurchaseLineInput(serializedProduct.ProductId, serializedProduct.ProductUnitId, 1m, 1200m, 2000m, [new SerializedIdentityInput(serialNum)]),
                    new CreatePurchaseLineInput(imeiProduct.ProductId, imeiProduct.ProductUnitId, 1m, 15000m, 25000m, [new SerializedIdentityInput(null, imei1, imei2)])
                ],
                InitialPaymentAmount: 0m,
                ReceiveStockImmediately: false);

            using var defPurchaseResp = await client.PostAsJsonAsync("/api/purchasing/create", deferredPurchaseCommand);
            Assert.Equal(HttpStatusCode.OK, defPurchaseResp.StatusCode);
            using var defPurchaseJson = JsonDocument.Parse(await defPurchaseResp.Content.ReadAsStringAsync());
            var deferredPurchaseId = defPurchaseJson.RootElement.GetProperty("purchaseId").GetGuid();

            // STEP 2: Physical Intake of Tracked Units
            // 2a. Piece-Tracked Intake (2 units) -> Backend generates TrackingCodes
            var pieceIntakeCommand = new ReceiveProductIntakeCommand(
                deferredPurchaseId,
                pieceProduct.ProductId,
                pieceProduct.ProductUnitId,
                2m,
                50m,
                [],
                actorId,
                Guid.CreateVersion7(),
                "Piece intake");
            using var pieceIntakeResp = await client.PostAsJsonAsync("/api/purchasing/intake", pieceIntakeCommand);
            Assert.Equal(HttpStatusCode.OK, pieceIntakeResp.StatusCode);
            using var pieceIntakeJson = JsonDocument.Parse(await pieceIntakeResp.Content.ReadAsStringAsync());
            var pieceCommittedUnits = pieceIntakeJson.RootElement.GetProperty("committedUnits").EnumerateArray().ToArray();
            Assert.Equal(2, pieceCommittedUnits.Length);
            var pieceUnitId1 = pieceCommittedUnits[0].GetProperty("id").GetGuid();
            var pieceTrackingCode1 = pieceCommittedUnits[0].GetProperty("trackingCode").GetString()!;
            var pieceTrackingCode2 = pieceCommittedUnits[1].GetProperty("trackingCode").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(pieceTrackingCode1));
            Assert.False(string.IsNullOrWhiteSpace(pieceTrackingCode2));
            Assert.NotEqual(pieceTrackingCode1, pieceTrackingCode2);

            // 2b. Serialized Intake (1 unit) -> Backend stores Serial and generates TrackingCode
            var serialIntakeCommand = new ReceiveProductIntakeCommand(
                deferredPurchaseId,
                serializedProduct.ProductId,
                serializedProduct.ProductUnitId,
                1m,
                1200m,
                [new SerializedIdentityInput(serialNum)],
                actorId,
                Guid.CreateVersion7(),
                "Serialized intake");
            using var serialIntakeResp = await client.PostAsJsonAsync("/api/purchasing/intake", serialIntakeCommand);
            Assert.Equal(HttpStatusCode.OK, serialIntakeResp.StatusCode);
            using var serialIntakeJson = JsonDocument.Parse(await serialIntakeResp.Content.ReadAsStringAsync());
            var serialCommittedUnit = serialIntakeJson.RootElement.GetProperty("committedUnits").EnumerateArray().Single();
            var serializedUnitId = serialCommittedUnit.GetProperty("id").GetGuid();
            var serialTrackingCode = serialCommittedUnit.GetProperty("trackingCode").GetString()!;
            Assert.Equal(serialNum, serialCommittedUnit.GetProperty("serialNumber").GetString());
            Assert.False(string.IsNullOrWhiteSpace(serialTrackingCode));

            // 2c. Dual-SIM IMEI Intake (1 unit) -> Backend stores IMEI1, IMEI2 and generates TrackingCode
            var imeiIntakeCommand = new ReceiveProductIntakeCommand(
                deferredPurchaseId,
                imeiProduct.ProductId,
                imeiProduct.ProductUnitId,
                1m,
                15000m,
                [new SerializedIdentityInput(null, imei1, imei2)],
                actorId,
                Guid.CreateVersion7(),
                "IMEI intake");
            using var imeiIntakeResp = await client.PostAsJsonAsync("/api/purchasing/intake", imeiIntakeCommand);
            Assert.Equal(HttpStatusCode.OK, imeiIntakeResp.StatusCode);
            using var imeiIntakeJson = JsonDocument.Parse(await imeiIntakeResp.Content.ReadAsStringAsync());
            var imeiCommittedUnit = imeiIntakeJson.RootElement.GetProperty("committedUnits").EnumerateArray().Single();
            var imeiUnitId = imeiCommittedUnit.GetProperty("id").GetGuid();
            var imeiTrackingCode = imeiCommittedUnit.GetProperty("trackingCode").GetString()!;
            Assert.Equal(imei1, imeiCommittedUnit.GetProperty("imei1").GetString());
            Assert.Equal(imei2, imeiCommittedUnit.GetProperty("imei2").GetString());
            Assert.False(string.IsNullOrWhiteSpace(imeiTrackingCode));

            // STEP 3: Physical Sticker / Barcode Document Generation
            using var stickerResp = await client.GetAsync($"/api/printing/stickers/{pieceUnitId1:D}");
            Assert.Equal(HttpStatusCode.OK, stickerResp.StatusCode);
            using var stickerJson = JsonDocument.Parse(await stickerResp.Content.ReadAsStringAsync());
            Assert.Equal(pieceTrackingCode1, stickerJson.RootElement.GetProperty("trackingCode").GetString());

            // STEP 4: POS Universal Scan & Non-Mutating Price Check
            using var scanPieceResp = await client.GetAsync($"/api/sales/scan?code={Uri.EscapeDataString(pieceTrackingCode1)}");
            Assert.Equal(HttpStatusCode.OK, scanPieceResp.StatusCode);
            using var scanSerialResp = await client.GetAsync($"/api/sales/scan?code={Uri.EscapeDataString(serialNum)}");
            Assert.Equal(HttpStatusCode.OK, scanSerialResp.StatusCode);
            using var scanImeiResp = await client.GetAsync($"/api/sales/scan?code={Uri.EscapeDataString(imei1)}");
            Assert.Equal(HttpStatusCode.OK, scanImeiResp.StatusCode);

            using var priceCheckResp = await client.GetAsync($"/api/sales/catalog?search={Uri.EscapeDataString(pieceProduct.ProductId.ToString())}");
            Assert.Equal(HttpStatusCode.OK, priceCheckResp.StatusCode);

            // STEP 5: POS Draft Hold & Cancel
            var draftCommand = new SavePosDraftCommand(
                null,
                null,
                customer.Id,
                actorId,
                terminalId.ToString("D"),
                "GT Draft Note",
                [new SavePosDraftItemInput(bulkProduct.ProductId, bulkProduct.ProductUnitId, 2m)],
                Guid.CreateVersion7(),
                terminalId,
                sessionId);
            using var saveDraftResp = await client.PostAsJsonAsync("/api/sales/drafts", draftCommand);
            Assert.Equal(HttpStatusCode.OK, saveDraftResp.StatusCode);
            using var saveDraftJson = JsonDocument.Parse(await saveDraftResp.Content.ReadAsStringAsync());
            var draftId = saveDraftJson.RootElement.GetProperty("draftId").GetGuid();

            using var listDraftsResp = await client.GetAsync("/api/sales/drafts");
            Assert.Equal(HttpStatusCode.OK, listDraftsResp.StatusCode);
            Assert.Contains(draftId.ToString("D"), await listDraftsResp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            using var cancelDraftResp = await client.PostAsJsonAsync($"/api/sales/drafts/{draftId:D}/cancel", new CancelPosDraftRequest(null, actorId));
            Assert.Equal(HttpStatusCode.OK, cancelDraftResp.StatusCode);

            // STEP 6: Complete Sale Across All 5 Canonical Products
            var saleCommand = new CompleteSaleCommand(
                Guid.CreateVersion7(),
                customer.Id,
                actorId,
                sessionId,
                0m,
                SalePaymentMethod.Cash,
                30000m,
                null,
                null,
                [
                    new CompleteSaleLineInput(pieceProduct.ProductId, pieceProduct.ProductUnitId, 1m, 100m, []),
                    new CompleteSaleLineInput(serializedProduct.ProductId, serializedProduct.ProductUnitId, 1m, 2000m, [serializedUnitId]),
                    new CompleteSaleLineInput(imeiProduct.ProductId, imeiProduct.ProductUnitId, 1m, 25000m, [imeiUnitId]),
                    new CompleteSaleLineInput(bulkProduct.ProductId, bulkProduct.ProductUnitId, 2m, 60m, []),
                    new CompleteSaleLineInput(serviceProduct.ProductId, serviceProduct.ProductUnitId, 1m, 500m, [])
                ]);

            using var saleResp = await client.PostAsJsonAsync("/api/sales/complete", saleCommand);
            Assert.True(saleResp.StatusCode == HttpStatusCode.OK,
                $"5-product sale failed: {await saleResp.Content.ReadAsStringAsync()}");
            using var saleJson = JsonDocument.Parse(await saleResp.Content.ReadAsStringAsync());
            var saleId = saleJson.RootElement.GetProperty("saleId").GetGuid();
            var invoiceNumber = saleJson.RootElement.GetProperty("invoiceNumber").GetString();
            Assert.False(string.IsNullOrWhiteSpace(invoiceNumber));

            // STEP 7: POS Sale Receipt
            using var receiptResp = await client.GetAsync($"/api/printing/documents/PosSaleReceipt/{saleId:D}");
            Assert.Equal(HttpStatusCode.OK, receiptResp.StatusCode);
            using var receiptJson = JsonDocument.Parse(await receiptResp.Content.ReadAsStringAsync());
            Assert.Equal("PosSaleReceipt", receiptJson.RootElement.GetProperty("kind").GetString());
            Assert.Equal(5, receiptJson.RootElement.GetProperty("lines").GetArrayLength());

            // STEP 8: Sale Return for Dual-SIM IMEI Item
            Guid serializedSaleItemId;
            Guid imeiSaleItemId;
            await using (var readProvider = Phase2PostgresTestHarness.BuildProvider())
            await using (var readScope = readProvider.CreateAsyncScope())
            {
                var readDb = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                serializedSaleItemId = await readDb.SaleItems.Where(x => x.SaleId == saleId && x.ProductId == serializedProduct.ProductId).Select(x => x.Id).SingleAsync();
                imeiSaleItemId = await readDb.SaleItems.Where(x => x.SaleId == saleId && x.ProductId == imeiProduct.ProductId).Select(x => x.Id).SingleAsync();
            }

            var returnCommand = new CreateSaleReturnCommand(
                saleId,
                "CUSTOMER_EXCHANGE",
                "GT 5-product IMEI return",
                RefundMethod.Cash,
                actorId,
                Guid.CreateVersion7(),
                [new SaleReturnLineInput(imeiSaleItemId, 1m, SaleReturnDisposition.RestockSellable, [imeiUnitId])]);
            using var returnResp = await client.PostAsJsonAsync("/api/sales/return", returnCommand);
            Assert.Equal(HttpStatusCode.OK, returnResp.StatusCode);
            using var returnJson = JsonDocument.Parse(await returnResp.Content.ReadAsStringAsync());
            var saleReturnId = returnJson.RootElement.GetProperty("saleReturnId").GetGuid();

            using var returnReceiptResp = await client.GetAsync($"/api/printing/documents/SaleReturnReceipt/{saleReturnId:D}");
            Assert.Equal(HttpStatusCode.OK, returnReceiptResp.StatusCode);

            // STEP 9: Customer Warranty Lifecycle for Serialized Product
            var claimCommand = new CreateWarrantyClaimCommand(
                customer.Id,
                saleId,
                bulkProduct.SupplierId,
                actorId,
                [new WarrantyClaimItemInput(
                    serializedProduct.ProductId,
                    1m,
                    "GT warranty trace",
                    serializedSaleItemId,
                    DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                    [new WarrantyClaimUnitInput(serializedUnitId, serialNum)])],
                Guid.CreateVersion7());
            using var claimResp = await client.PostAsJsonAsync("/api/warranty/claims", claimCommand);
            Assert.Equal(HttpStatusCode.OK, claimResp.StatusCode);
            using var claimJson = JsonDocument.Parse(await claimResp.Content.ReadAsStringAsync());
            var claimId = claimJson.RootElement.GetGuid();

            foreach (var route in new[] { "review", "send-to-supplier", "supplier-processing" })
            {
                using var actResp = await client.PostAsJsonAsync(
                    $"/api/warranty/claims/{claimId:D}/{route}",
                    new WarrantyClaimActionRequest(Guid.CreateVersion7(), $"Action {route}"));
                Assert.Equal(HttpStatusCode.OK, actResp.StatusCode);
            }

            Guid claimItemUnitId;
            await using (var readProvider = Phase2PostgresTestHarness.BuildProvider())
            await using (var readScope = readProvider.CreateAsyncScope())
            {
                var readDb = readScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                claimItemUnitId = await readDb.WarrantyClaimItemUnits
                    .Where(x => x.OriginalInventoryUnitId == serializedUnitId)
                    .Select(x => x.Id)
                    .SingleAsync();
            }

            using var replResp = await client.PostAsJsonAsync(
                $"/api/warranty/claims/{claimId:D}/replacement-receipt",
                new CustomerWarrantyReplacementRequest(
                    Guid.CreateVersion7(),
                    [new CustomerWarrantyReplacementUnitInput(claimItemUnitId, replacementSerialNum, null, null)],
                    "GT replacement received"));
            Assert.Equal(HttpStatusCode.OK, replResp.StatusCode);

            using var handoverResp = await client.PostAsJsonAsync(
                $"/api/warranty/claims/{claimId:D}/handover",
                new WarrantyClaimActionRequest(Guid.CreateVersion7(), "GT replacement handed over"));
            Assert.Equal(HttpStatusCode.OK, handoverResp.StatusCode);

            // STEP 10: Supplier Khata Settlement
            var supplierPayCommand = new CreateSupplierPaymentCommand(
                bulkProduct.SupplierId,
                1000m,
                SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.External,
                actorId,
                Guid.CreateVersion7(),
                "GT-SUPP-PAY",
                "GT supplier khata settlement");
            using var suppPayResp = await client.PostAsJsonAsync("/api/finance/supplier-payment", supplierPayCommand);
            Assert.Equal(HttpStatusCode.OK, suppPayResp.StatusCode);

            using var suppWorkspaceResp = await client.GetAsync($"/api/finance/suppliers/{bulkProduct.SupplierId:D}/workspace");
            Assert.Equal(HttpStatusCode.OK, suppWorkspaceResp.StatusCode);

            // STEP 11: Final State & Database Assertions
            await using var verifyProvider = Phase2PostgresTestHarness.BuildProvider();
            await using var verifyScope = verifyProvider.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

            Assert.Equal(1m, (await verifyDb.StockBalances.SingleAsync(x => x.ProductId == pieceProduct.ProductId)).SellableQty);

            Assert.Equal(0m, (await verifyDb.StockBalances.SingleAsync(x => x.ProductId == serializedProduct.ProductId)).SellableQty);
            Assert.Equal(WarrantyClaimStatus.Closed, await verifyDb.WarrantyClaims.Where(x => x.Id == claimId).Select(x => x.Status).SingleAsync());
            Assert.Equal(InventoryUnitStatus.WarrantyCustomerHandedOver,
                await verifyDb.InventoryUnits.Where(x => x.SerialNumber == replacementSerialNum).Select(x => x.Status).SingleAsync());

            Assert.Equal(1m, (await verifyDb.StockBalances.SingleAsync(x => x.ProductId == imeiProduct.ProductId)).SellableQty);
            Assert.Equal(InventoryUnitStatus.InStock, (await verifyDb.InventoryUnits.SingleAsync(x => x.Id == imeiUnitId)).Status);

            Assert.Equal(8m, (await verifyDb.StockBalances.SingleAsync(x => x.ProductId == bulkProduct.ProductId)).SellableQty);
            Assert.Equal(4m, (await verifyDb.StockBalances.SingleAsync(x => x.ProductId == serviceProduct.ProductId)).SellableQty);
        }
        finally
        {
            await using var cleanupProvider = Phase2PostgresTestHarness.BuildProvider();
            await using var cleanupScope = cleanupProvider.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            if (cashSessionId is Guid ownedCashSessionId)
            {
                await cleanupDb.CashSessions
                    .Where(x => x.Id == ownedCashSessionId && x.Status == CashSessionStatus.Open)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, CashSessionStatus.Closed));
            }
        }
    }

    private static async Task<SerializedProductFixture> SeedPieceProductAsync(
        EdgeRetailsDbContext db, Guid supplierId, Guid actorId, string suffix)
    {
        var unit = new Unit { Name = "Piece-" + suffix, Symbol = "pc-" + suffix[..6], DisplayDecimalPlaces = 0 };
        var product = new Product
        {
            Name = "GT Piece Cable " + suffix,
            Sku = ("SKU-PIECE-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.IndividualPiece,
            SerialTrackingEnabled = false,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = 100m,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        await db.SaveChangesAsync();
        return new SerializedProductFixture(product.Id, productUnit.Id, supplierId, actorId, unit.Id);
    }

    private static async Task<SerializedProductFixture> SeedSerializedCustomProductAsync(
        EdgeRetailsDbContext db, Guid supplierId, Guid actorId, string suffix)
    {
        var unit = new Unit { Name = "Serialized-" + suffix, Symbol = "sn-" + suffix[..6], DisplayDecimalPlaces = 0 };
        var product = new Product
        {
            Name = "GT Serialized Scanner " + suffix,
            Sku = ("SKU-SERIAL-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = 2000m,
            DefaultWarrantyMonths = 12,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        await db.SaveChangesAsync();
        return new SerializedProductFixture(product.Id, productUnit.Id, supplierId, actorId, unit.Id);
    }

    private static async Task<SerializedProductFixture> SeedImeiDualSimProductAsync(
        EdgeRetailsDbContext db, Guid supplierId, Guid actorId, string suffix)
    {
        var unit = new Unit { Name = "Phone-" + suffix, Symbol = "ph-" + suffix[..6], DisplayDecimalPlaces = 0 };
        var product = new Product
        {
            Name = "GT Dual-SIM Smartphone " + suffix,
            Sku = ("SKU-IMEI-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = false,
            ImeiTrackingEnabled = true,
            DefaultSalePrice = 25000m,
            DefaultWarrantyMonths = 12,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        await db.SaveChangesAsync();
        return new SerializedProductFixture(product.Id, productUnit.Id, supplierId, actorId, unit.Id);
    }

    private static async Task<QuantityProductFixture> SeedServiceCustomProductAsync(
        EdgeRetailsDbContext db, Guid supplierId, Guid actorId, string suffix)
    {
        var unit = new Unit { Name = "Service-" + suffix, Symbol = "sv-" + suffix[..6], DisplayDecimalPlaces = 0 };
        var product = new Product
        {
            Name = "GT Installation Service " + suffix,
            Sku = ("SKU-SRV-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = 500m,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        await db.SaveChangesAsync();
        return new QuantityProductFixture(product.Id, productUnit.Id, supplierId, actorId, unit.Id);
    }

    private static void SetAuthentication(HttpClient client, Guid terminalId, string secret, Guid sessionId)
    {
        client.DefaultRequestHeaders.Add("X-Terminal-Id", terminalId.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Terminal-Secret", secret);
        client.DefaultRequestHeaders.Add("X-Session-Id", sessionId.ToString("D"));
    }

    private static async Task<SerializedProductFixture> SeedSerializedProductAsync()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        return await Phase2PostgresTestHarness.SeedSerializedProductAsync(
            scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(),
            defaultSalePrice: 800m,
            defaultWarrantyMonths: 12);
    }

    private static async Task<EdgeRetails.Domain.Parties.Customer> SeedCustomerAsync()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        return await Phase2PostgresTestHarness.SeedCustomerAsync(
            scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(),
            "Phase 3 Warranty Customer");
    }

    private sealed class ProductionServerFactory(string connectionString, ISequenceHighWaterService highWaterService) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISequenceHighWaterService>();
                services.AddSingleton(highWaterService);
            });
        }
    }
}
