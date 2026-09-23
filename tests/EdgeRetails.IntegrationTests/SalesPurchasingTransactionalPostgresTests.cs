using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

public sealed class SalesPurchasingTransactionalPostgresTests
{
    [Fact]
    public async Task Purchase_Sale_Retry_And_Void_Consumption_Are_Atomic()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await EnsureReceiptConfigurationAsync(db);
        var fixture = await SeedQuantityProductAsync(db, defaultSalePrice: 150m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var purchaseOperation = Guid.CreateVersion7();
        var purchase = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                " INV-" + Guid.NewGuid().ToString("N")[..8] + " ",
                DateOnly.FromDateTime(DateTime.UtcNow),
                "integration purchase",
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                purchaseOperation,
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        10m,
                        100m,
                        150m,
                        Array.Empty<SerializedIdentityInput>())
                ]),
            CancellationToken.None);

        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        Assert.NotNull(purchase.Value);
        Assert.False(purchase.Value!.WasExisting);

        db.ChangeTracker.Clear();

        var stockAfterPurchase = await db.StockBalances
            .AsNoTracking()
            .SingleAsync(x => x.ProductId == fixture.ProductId);
        var costAfterPurchase = await db.ProductCostStates
            .AsNoTracking()
            .SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(10m, stockAfterPurchase.SellableQty);
        Assert.Equal(10m, costAfterPurchase.CostedQty);
        Assert.Equal(1000m, costAfterPurchase.TotalInventoryCost);
        Assert.Equal(100m, costAfterPurchase.LastPurchaseCost);

        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var saleOperation = Guid.CreateVersion7();
        var saleCommand = new CompleteSaleCommand(
            saleOperation,
            null,
            fixture.ActorId,
            null,
            0m,
            SalePaymentMethod.Bank,
            300m,
            "BANK-TEST",
            null,
            [
                new CompleteSaleLineInput(
                    fixture.ProductId,
                    fixture.ProductUnitId,
                    2m,
                    150m,
                    Array.Empty<Guid>())
            ]);

        var sale = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        Assert.NotNull(sale.Value);
        Assert.False(sale.Value!.WasExisting);
        Assert.Equal(300m, sale.Value.GrandTotal);

        db.ChangeTracker.Clear();

        var stockAfterSale = await db.StockBalances
            .AsNoTracking()
            .SingleAsync(x => x.ProductId == fixture.ProductId);
        var consumption = await db.InventoryLotConsumptions
            .AsNoTracking()
            .Where(x => x.Quantity > 0)
            .ToListAsync();
        Assert.Equal(8m, stockAfterSale.SellableQty);
        Assert.Contains(consumption, x => x.Quantity == 2m && x.TotalCostSnapshot == 200m);

        var provenanceReads = services.GetRequiredService<IInventoryProvenanceReadService>();
        var purchaseProvenance = await provenanceReads.GetProductPurchaseProvenanceAsync(
            new GetProductPurchaseProvenanceQuery(fixture.ProductId),
            CancellationToken.None);
        Assert.Contains(
            purchaseProvenance,
            x => x.PurchaseId == purchase.Value.PurchaseId &&
                 x.ReceivedQuantity == 10m &&
                 x.ConsumedQuantity == 2m &&
                 x.SellableQuantity == 8m);

        var productSales = await provenanceReads.GetProductSaleHistoryAsync(
            new GetProductSaleHistoryQuery(fixture.ProductId),
            CancellationToken.None);
        Assert.Contains(
            productSales,
            x => x.SaleId == sale.Value.SaleId &&
                 x.BaseQuantity == 2m &&
                 x.TotalCostSnapshot == 200m);

        var saleCountBeforeRetry = await db.Sales.CountAsync();
        var retry = await saleHandler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(retry.IsSuccess, retry.Error?.Message);
        Assert.True(retry.Value!.WasExisting);
        Assert.Equal(sale.Value.SaleId, retry.Value.SaleId);
        Assert.Equal(saleCountBeforeRetry, await db.Sales.CountAsync());

        var voidHandler = ActivatorUtilities.CreateInstance<VoidPurchaseHandler>(services);
        var voidResult = await voidHandler.HandleAsync(
            new VoidPurchaseCommand(
                purchase.Value.PurchaseId,
                Guid.CreateVersion7(),
                fixture.ActorId,
                "mistaken receipt"),
            CancellationToken.None);

        Assert.False(voidResult.IsSuccess);
        Assert.Equal("purchasing.void_origin_consumed", voidResult.Error?.Code);

        db.ChangeTracker.Clear();
        var purchaseEntity = await db.Purchases
            .AsNoTracking()
            .SingleAsync(x => x.Id == purchase.Value.PurchaseId);
        Assert.Equal(PurchaseStatus.Completed, purchaseEntity.Status);
    }

    [Fact]
    public async Task Discounted_Partial_Returns_Close_To_Exact_Net_Line_Total()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await EnsureReceiptConfigurationAsync(db);
        var fixture = await SeedQuantityProductAsync(db, defaultSalePrice: 100m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var purchase = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "RET-" + Guid.NewGuid().ToString("N")[..10],
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        3m,
                        40m,
                        100m,
                        Array.Empty<SerializedIdentityInput>())
                ]),
            CancellationToken.None);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);

        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var sale = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                null,
                1m,
                SalePaymentMethod.Bank,
                299m,
                "BANK-RETURN",
                null,
                [
                    new CompleteSaleLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        3m,
                        100m,
                        Array.Empty<Guid>())
                ]),
            CancellationToken.None);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
        Assert.NotNull(sale.Value);
        var saleId = sale.Value!.SaleId;

        db.ChangeTracker.Clear();
        var saleItem = await db.SaleItems
            .AsNoTracking()
            .SingleAsync(x => x.SaleId == saleId);
        Assert.Equal(299m, saleItem.NetLineTotal);

        var returnHandler = ActivatorUtilities.CreateInstance<CreateSaleReturnHandler>(services);
        var refundAmounts = new List<decimal>();

        for (var index = 0; index < 3; index++)
        {
            var result = await returnHandler.HandleAsync(
                new CreateSaleReturnCommand(
                    saleId,
                    "CUSTOMER_CHANGED_MIND",
                    null,
                    RefundMethod.Bank,
                    fixture.ActorId,
                    Guid.CreateVersion7(),
                    [
                        new SaleReturnLineInput(
                            saleItem.Id,
                            1m,
                            SaleReturnDisposition.RestockSellable,
                            Array.Empty<Guid>())
                    ]),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            refundAmounts.Add(result.Value!.RefundAmount);
            db.ChangeTracker.Clear();
        }

        Assert.Equal([99.67m, 99.66m, 99.67m], refundAmounts);
        Assert.Equal(299m, refundAmounts.Sum());

        var restoredStock = await db.StockBalances
            .AsNoTracking()
            .SingleAsync(x => x.ProductId == fixture.ProductId);
        var restoredCost = await db.ProductCostStates
            .AsNoTracking()
            .SingleAsync(x => x.ProductId == fixture.ProductId);

        Assert.Equal(3m, restoredStock.SellableQty);
        Assert.Equal(3m, restoredCost.CostedQty);
        Assert.Equal(120m, restoredCost.TotalInventoryCost);
    }


    [Fact]
    public async Task Purchase_Return_Separates_Supplier_Value_From_Inventory_Cost()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        var fixture = await SeedQuantityProductAsync(db, defaultSalePrice: 160m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var purchase = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "PR-" + Guid.NewGuid().ToString("N")[..10],
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                200m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        10m,
                        100m,
                        160m,
                        Array.Empty<SerializedIdentityInput>())
                ]),
            CancellationToken.None);

        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        Assert.NotNull(purchase.Value);

        db.ChangeTracker.Clear();

        var purchaseItem = await db.PurchaseItems
            .AsNoTracking()
            .SingleAsync(x => x.PurchaseId == purchase.Value!.PurchaseId);
        Assert.Equal(120m, purchaseItem.EffectiveBaseUnitCost);

        var returnHandler =
            ActivatorUtilities.CreateInstance<CreatePurchaseReturnHandler>(services);
        var purchaseReturn = await returnHandler.HandleAsync(
            new CreatePurchaseReturnCommand(
                purchase.Value.PurchaseId,
                "SUPPLIER_RETURN",
                "two pieces returned",
                PurchaseReturnSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new PurchaseReturnLineInput(
                        purchaseItem.Id,
                        2m,
                        100m,
                        Array.Empty<Guid>())
                ]),
            CancellationToken.None);

        Assert.True(purchaseReturn.IsSuccess, purchaseReturn.Error?.Message);
        Assert.NotNull(purchaseReturn.Value);
        Assert.Equal(200m, purchaseReturn.Value!.SupplierReturnValue);
        Assert.Equal(240m, purchaseReturn.Value.InventoryCostRemoved);

        db.ChangeTracker.Clear();

        var stock = await db.StockBalances
            .AsNoTracking()
            .SingleAsync(x => x.ProductId == fixture.ProductId);
        var costState = await db.ProductCostStates
            .AsNoTracking()
            .SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(8m, stock.SellableQty);
        Assert.Equal(8m, costState.CostedQty);
        Assert.Equal(960m, costState.TotalInventoryCost);

        var reads = services.GetRequiredService<IPurchasingReadService>();
        var returns = await reads.GetReturnHistoryAsync(
            new GetPurchaseReturnHistoryQuery(
                PurchaseId: purchase.Value.PurchaseId,
                SupplierId: fixture.SupplierId),
            CancellationToken.None);

        var returnRow = Assert.Single(returns);
        Assert.Equal(200m, returnRow.SupplierReturnValue);
        Assert.Equal(240m, returnRow.InventoryCostRemoved);

        var supplierHistory = await reads.GetHistoryAsync(
            new GetPurchaseHistoryQuery(
                SupplierId: fixture.SupplierId,
                PageSize: 10),
            CancellationToken.None);

        var purchaseRow = Assert.Single(
            supplierHistory,
            x => x.PurchaseId == purchase.Value.PurchaseId);
        Assert.Equal(200m, purchaseRow.SupplierReturnValue);
    }

    private static ServiceProvider BuildProvider()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "EDGE_RETAILS_TEST_DB must point to an isolated PostgreSQL integration-test database.");
        }

        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);
        return services.BuildServiceProvider();
    }

    private static async Task EnsureReceiptConfigurationAsync(
        EdgeRetailsDbContext db)
    {
        if (!await db.ShopProfiles.AnyAsync(x => x.ProfileKey == "PRIMARY"))
        {
            db.ShopProfiles.Add(new ShopProfile
            {
                ProfileKey = "PRIMARY",
                ShopName = "Edge Retails Integration Shop",
                Phone = "000",
                Address = "Integration Test",
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        if (!await db.ReceiptTemplateSettings.AnyAsync(x => x.TemplateKey == "PRIMARY"))
        {
            db.ReceiptTemplateSettings.Add(new ReceiptTemplateSettings
            {
                TemplateKey = "PRIMARY",
                Header = "Integration",
                Footer = "Test",
                ShowCustomer = true,
                ShowCashier = true,
                LogoBehavior = "NONE",
                TemplateVersion = 1,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<Fixture> SeedQuantityProductAsync(
        EdgeRetailsDbContext db,
        decimal defaultSalePrice)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unit = new Unit
        {
            Name = "Piece-" + suffix,
            Symbol = "pc-" + suffix[..8],
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = "Product-" + suffix,
            Sku = "SKU-" + suffix[..12],
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = defaultSalePrice,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        var supplier = new Supplier
        {
            Name = "Supplier-" + suffix,
            DealerCode = TraceabilityCodeRules.BuildDealerCode("SU", Random.Shared.Next(1000, 999999)),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);
        db.ChangeTracker.Clear();

        return new Fixture(
            product.Id,
            productUnit.Id,
            supplier.Id,
            actorId);
    }

    private sealed record Fixture(
        Guid ProductId,
        Guid ProductUnitId,
        Guid SupplierId,
        Guid ActorId);
}

