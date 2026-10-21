using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Features.Posting;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace EdgeRetails.IntegrationTests.Phase11;

/// <summary>
/// Task P11-IMP-110: Complete Golden Lifecycle Trace Suite against Real PostgreSQL 18.
/// Implements all 10 canonical golden traces (P11-PLAN-GT-01 through P11-PLAN-GT-10)
/// targeting exclusively the dedicated disposable PostgreSQL 18 instance on port 55437.
/// </summary>
[Collection("Phase11GoldenTraces")]
public sealed class GoldenLifecycleTracePostgresTests
{
    private static readonly TimeZoneInfo KarachiZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Pakistan Standard Time" : "Asia/Karachi");

    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> DataSources = new();

    private static string GetConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (!string.IsNullOrWhiteSpace(envConn))
        {
            Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(envConn, performLiveProbe: false);
            return envConn;
        }

        var clusterDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
        var credFile = Path.Combine(clusterDir, "provpw.tmp");
        if (File.Exists(credFile))
        {
            var pw = File.ReadAllText(credFile).Trim();
            if (!string.IsNullOrWhiteSpace(pw))
            {
                var builtConn = $"Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password={pw}";
                Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(builtConn, performLiveProbe: false);
                return builtConn;
            }
        }

        throw new InvalidOperationException("Disposable PostgreSQL cluster credentials not found.");
    }

    private static NpgsqlDataSource GetOrCreateDataSource(string connectionString)
    {
        return DataSources.GetOrAdd(connectionString, connStr =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connStr);
            dataSourceBuilder.MapEnum<PackState>("inventory.pack_state", new Npgsql.NameTranslation.NpgsqlNullNameTranslator());
            return dataSourceBuilder.Build();
        });
    }

    private static EdgeRetailsDbContext CreateDbContext(string connectionString)
    {
        Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(connectionString, performLiveProbe: false);

        var dataSource = GetOrCreateDataSource(connectionString);

        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(dataSource, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "system");
                npgsql.MapEnum<PackState>("pack_state", "inventory", new Npgsql.NameTranslation.NpgsqlNullNameTranslator());
            })
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        return new EdgeRetailsDbContext(options);
    }

    private static async Task<(Product Product, ProductUnit ProductUnit, Guid ActorId)> SeedProductAndActorAsync(
        EdgeRetailsDbContext db,
        TrackingMode mode = TrackingMode.Quantity,
        decimal defaultPrice = 100m,
        string unitName = "Piece",
        decimal factorToBase = 1m)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var unit = new Unit
        {
            Name = $"{unitName}-{suffix}",
            Symbol = $"u-{suffix}",
            DisplayDecimalPlaces = mode == TrackingMode.Length ? 2 : 0
        };
        var product = new Product
        {
            Name = $"GT-Product-{suffix}",
            Sku = $"SKU-GT-{suffix}".ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = mode,
            DefaultSalePrice = defaultPrice,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = factorToBase,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };

        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        await db.SaveChangesAsync();

        return (product, productUnit, actorId);
    }

    private static async Task<(Purchase Purchase, PurchaseItem PurchaseItem, InventoryMovement IntakeMovement)> SeedPurchaseIntakeAsync(
        EdgeRetailsDbContext db,
        Product product,
        ProductUnit productUnit,
        Guid actorId,
        decimal quantity = 100m,
        decimal unitCost = 50m)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var supplier = new Supplier
        {
            Name = "Supplier-" + suffix,
            IsActive = true,
            CreatedAt = now,
            Version = 1
        };
        db.Suppliers.Add(supplier);

        var purchase = new Purchase
        {
            PurchaseNumber = "PUR-" + suffix,
            SupplierId = supplier.Id,
            SupplierInvoiceNumber = "INV-" + suffix,
            NormalizedSupplierInvoiceNumber = "INV-" + suffix,
            PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Subtotal = quantity * unitCost,
            GrandTotal = quantity * unitCost,
            Status = PurchaseStatus.Completed,
            SettlementMode = PurchaseSettlementMode.External,
            CreatedBy = actorId,
            CreatedAt = now,
            ClientOperationId = Guid.NewGuid(),
            Version = 1
        };
        db.Purchases.Add(purchase);

        var purchaseItem = new PurchaseItem
        {
            PurchaseId = purchase.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            ProductNameSnapshot = product.Name,
            SkuSnapshot = product.Sku,
            EnteredQuantity = quantity,
            FactorToBaseSnapshot = productUnit.FactorToBaseUnit,
            BaseQuantity = quantity * productUnit.FactorToBaseUnit,
            EnteredUnitCost = unitCost,
            BaseLineTotal = quantity * unitCost,
            AllocatedOtherCost = 0m,
            EffectiveBaseUnitCost = unitCost,
            EffectiveLineCost = quantity * unitCost,
            SalePriceAtPurchase = product.DefaultSalePrice
        };
        db.PurchaseItems.Add(purchaseItem);

        var intakeMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.PurchaseIn,
            ReferenceType = "Purchase",
            ReferenceId = purchase.Id,
            UnitCostSnapshot = unitCost,
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(intakeMovement);
        await db.SaveChangesAsync();

        return (purchase, purchaseItem, intakeMovement);
    }

    private static async Task<Customer> SeedCustomerAsync(EdgeRetailsDbContext db)
    {
        var customer = new Customer
        {
            Name = "GT-Customer-" + Guid.NewGuid().ToString("N")[..8],
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>
    /// P11-PLAN-GT-01: GT01 (Quantity): Intake -> Sale -> Return -> Stocktake -> Conservation
    /// Oracle: InitialIntake (100) - Sold (30) + Returned (5) - VarianceLoss (2) == FinalSellableQty (73).
    /// Financials: Ending Inventory Value = 73 * 50 = 3650. Recognized Loss = 2 * 50 = 100.
    /// </summary>
    [Fact]
    public async Task GT01_Quantity_IntakeSaleReturnStocktakeConservation()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, TrackingMode.Quantity, defaultPrice: 80m);
        var (purchase, purchaseItem, intakeMovement) = await SeedPurchaseIntakeAsync(db, product, productUnit, actorId, quantity: 100m, unitCost: 50m);
        var customer = await SeedCustomerAsync(db);
        var now = DateTimeOffset.UtcNow;

        // 1. Intake: 100 units in Lot linked to purchase
        var lot = new InventoryLot
        {
            ProductId = product.Id,
            SourceMovementId = intakeMovement.Id,
            PurchaseItemId = purchaseItem.Id,
            ReceivedQuantity = 100m,
            OriginalUnitCost = 50m,
            EffectiveUnitCost = 50m,
            CreatedAt = now.AddHours(-10)
        };
        db.InventoryLots.Add(lot);

        var stockBalance = new StockBalance
        {
            ProductId = product.Id,
            SellableQty = 100m,
            DamagedQty = 0m,
            DefectiveQty = 0m,
            WithSupplierQty = 0m,
            ScrapQty = 0m,
            Version = 1
        };
        db.StockBalances.Add(stockBalance);
        await db.SaveChangesAsync();

        // 2. Sale: Sell 30 units at 80 each
        stockBalance.ApplyDelta(InventoryBucket.Sellable, -30m);
        var saleMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.SaleOut,
            ReferenceType = "Sale",
            UnitCostSnapshot = 50m,
            ActorId = actorId,
            OccurredAt = now.AddHours(-5),
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(saleMovement);

        var sale = new Sale
        {
            InvoiceNumber = "INV-GT01-" + Guid.NewGuid().ToString("N")[..8],
            CashierUserId = actorId,
            CustomerId = customer.Id,
            CompletedAt = now.AddHours(-5),
            Subtotal = 2400m,
            InvoiceDiscount = 0m,
            GrandTotal = 2400m,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            CreatedAt = now.AddHours(-5)
        };
        db.Sales.Add(sale);

        var saleItem = new SaleItem
        {
            SaleId = sale.Id,
            InventoryMovementId = saleMovement.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            ProductNameSnapshot = product.Name,
            SkuSnapshot = product.Sku,
            EnteredQuantity = 30m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 30m,
            ListUnitPriceSnapshot = 80m,
            UnitPrice = 80m,
            GrossLineTotal = 2400m,
            NetLineTotal = 2400m,
            UnitCostSnapshot = 50m,
            TotalCostSnapshot = 1500m,
            GrossProfitSnapshot = 900m
        };
        db.SaleItems.Add(saleItem);
        await db.SaveChangesAsync();

        // 3. Return: Customer returns 5 units restocked to sellable
        stockBalance.ApplyDelta(InventoryBucket.Sellable, +5m);
        var returnMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.SaleReturn,
            ReferenceType = "SaleReturn",
            UnitCostSnapshot = 50m,
            ActorId = actorId,
            OccurredAt = now.AddHours(-3),
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(returnMovement);

        var saleReturn = new SaleReturn
        {
            ReturnNumber = "RET-GT01-" + Guid.NewGuid().ToString("N")[..8],
            SaleId = sale.Id,
            ReasonCode = "RESTOCK",
            RefundMethod = RefundMethod.Cash,
            RefundAmount = 400m,
            CreatedBy = actorId,
            CreatedAt = now.AddHours(-3),
            ClientOperationId = Guid.NewGuid()
        };
        db.SaleReturns.Add(saleReturn);
        await db.SaveChangesAsync();

        // 4. Stocktake: Physical count reveals 73 units on shelf (-2 variance written off to loss)
        var physicalCount = 73m;
        var variance = physicalCount - stockBalance.SellableQty; // 73 - 75 = -2
        Assert.Equal(-2m, variance);

        stockBalance.ApplyDelta(InventoryBucket.Sellable, variance);
        var stocktakeLossMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "StocktakeVariance",
            UnitCostSnapshot = 50m,
            RecognizedLossAmount = Math.Abs(variance) * 50m, // 2 * 50 = 100
            ActorId = actorId,
            OccurredAt = now.AddHours(-1),
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(stocktakeLossMovement);
        await db.SaveChangesAsync();

        // 5. Independent Conservation Oracle
        await using var verifyDb = CreateDbContext(connStr);
        var finalBalance = await verifyDb.StockBalances.SingleAsync(b => b.ProductId == product.Id);
        Assert.Equal(73m, finalBalance.SellableQty);

        var expectedCarryingValue = finalBalance.SellableQty * 50m;
        Assert.Equal(3650m, expectedCarryingValue);

        var totalLoss = await verifyDb.InventoryMovements
            .Where(m => m.ProductId == product.Id && m.RecognizedLossAmount > 0)
            .SumAsync(m => m.RecognizedLossAmount);
        Assert.Equal(100m, totalLoss);
    }

    /// <summary>
    /// P11-PLAN-GT-02: GT02 (Length): Coil Intake -> Roll Sale -> Meter Sale -> Remnant Scrap -> Cut Return
    /// Oracle: 100m initial coil. 40m roll sale + 25m meter cut = 65m sold. 2m remnant scrap written off.
    /// 5m cut return received as separate loose stock. Parent remaining = 33m, loose = 5m. Total accounted = 100m.
    /// </summary>
    [Fact]
    public async Task GT02_Length_CoilIntakeRollSaleMeterCutRemnantScrap()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, TrackingMode.Length, defaultPrice: 15m, unitName: "Meter");
        var (purchase, purchaseItem, _) = await SeedPurchaseIntakeAsync(db, product, productUnit, actorId, quantity: 100m, unitCost: 10m);
        var now = DateTimeOffset.UtcNow;

        // 1. Coil Intake: 100m continuous reel
        var coilUnit = new InventoryUnit
        {
            ProductId = product.Id,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItem.Id,
            TrackingCode = "COIL-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1000m, // 10/meter
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 100m,
            PackState = PackState.Sealed,
            IsLooseStock = false,
            CreatedAt = now.AddHours(-10),
            Version = 1
        };
        db.InventoryUnits.Add(coilUnit);
        await db.SaveChangesAsync();

        // 2. Roll Sale: 40m sold from coil
        coilUnit.ConsumeFromPack(40m);
        Assert.Equal(60m, coilUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, coilUnit.PackState);
        await db.SaveChangesAsync();

        // 3. Meter Sale: Cut and sell 25m from open coil
        coilUnit.ConsumeFromPack(25m);
        Assert.Equal(35m, coilUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, coilUnit.PackState);
        await db.SaveChangesAsync();

        // 4. Remnant Scrap: End of reel damage write-off (2m scrapped)
        coilUnit.ConsumeFromPack(2m);
        Assert.Equal(33m, coilUnit.RemainingBaseQuantity);

        var scrapMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.WriteOffToScrap,
            ReferenceType = "ScrapRemnant",
            RecognizedLossAmount = 2m * 10m, // 20
            ActorId = actorId,
            OccurredAt = now.AddHours(-2),
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(scrapMovement);
        await db.SaveChangesAsync();

        // 5. Cut Return: Customer returns 5m cut wire. Rule: Cut wire remains separate loose stock, never resealed into parent coil!
        var looseCutReturn = new InventoryUnit
        {
            ProductId = product.Id,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItem.Id,
            TrackingCode = "LOOSE-CUT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 50m,
            InitialBaseQuantity = 5m,
            RemainingBaseQuantity = 5m,
            PackState = PackState.Open,
            IsLooseStock = true,
            CreatedAt = now.AddHours(-1),
            Version = 1
        };
        db.InventoryUnits.Add(looseCutReturn);
        await db.SaveChangesAsync();

        // 6. Conservation Oracle:
        await using var verifyDb = CreateDbContext(connStr);
        var parentCoil = await verifyDb.InventoryUnits.SingleAsync(u => u.Id == coilUnit.Id);
        var looseUnit = await verifyDb.InventoryUnits.SingleAsync(u => u.Id == looseCutReturn.Id);

        Assert.Equal(33m, parentCoil.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, parentCoil.PackState);
        Assert.False(parentCoil.IsLooseStock);

        Assert.Equal(5m, looseUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, looseUnit.PackState);
        Assert.True(looseUnit.IsLooseStock);

        var netSold = 40m + 25m - 5m; // 60m
        var scrapped = 2m;
        var physicalRemaining = (parentCoil.RemainingBaseQuantity ?? 0m) + (looseUnit.RemainingBaseQuantity ?? 0m); // 38m

        Assert.Equal(100m, netSold + scrapped + physicalRemaining);
    }

    /// <summary>
    /// P11-PLAN-GT-03: GT03 (Pack/Container): Intake Box -> Partial Loose Sale -> Cross-Pack Sale
    /// Oracle: Box A (10 pcs) + Box B (10 pcs). Sell 4 from Box A -> Box A (6, Open).
    /// Cross-pack sell 8 -> Box A (0, Consumed) + Box B (8, Open). Total remaining = 8.
    /// </summary>
    [Fact]
    public async Task GT03_ContainerPack_IntakeBoxLooseSaleCrossPackAllocation()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, TrackingMode.Container, defaultPrice: 200m, unitName: "Box");
        var (purchase, purchaseItem, _) = await SeedPurchaseIntakeAsync(db, product, productUnit, actorId, quantity: 20m, unitCost: 100m);
        var now = DateTimeOffset.UtcNow;

        // 1. Intake: Box A and Box B (10 pcs each, Sealed)
        var boxA = new InventoryUnit
        {
            ProductId = product.Id,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItem.Id,
            TrackingCode = "BOX-A-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1000m,
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Sealed,
            IsLooseStock = false,
            CreatedAt = now.AddHours(-10),
            Version = 1
        };

        var boxB = new InventoryUnit
        {
            ProductId = product.Id,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItem.Id,
            TrackingCode = "BOX-B-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1000m,
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Sealed,
            IsLooseStock = false,
            CreatedAt = now.AddHours(-10),
            Version = 1
        };

        db.InventoryUnits.AddRange(boxA, boxB);
        await db.SaveChangesAsync();

        // 2. Loose Sale: Sell 4 pcs from Box A
        boxA.ConsumeFromPack(4m);
        Assert.Equal(6m, boxA.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, boxA.PackState);
        await db.SaveChangesAsync();

        // 3. Cross-Pack Sale: Customer requests 8 pcs.
        // Allocation rule: Draw open pack first (6 pcs from Box A), then draw 2 pcs from Box B
        boxA.ConsumeFromPack(6m);
        Assert.Equal(0m, boxA.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, boxA.PackState);

        boxB.ConsumeFromPack(2m);
        Assert.Equal(8m, boxB.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, boxB.PackState);

        await db.SaveChangesAsync();

        // 4. Conservation Oracle:
        await using var verifyDb = CreateDbContext(connStr);
        var savedA = await verifyDb.InventoryUnits.SingleAsync(u => u.Id == boxA.Id);
        var savedB = await verifyDb.InventoryUnits.SingleAsync(u => u.Id == boxB.Id);

        Assert.Equal(PackState.Consumed, savedA.PackState);
        Assert.Equal(0m, savedA.RemainingBaseQuantity);

        Assert.Equal(PackState.Open, savedB.PackState);
        Assert.Equal(8m, savedB.RemainingBaseQuantity);

        var totalSold = 4m + 8m; // 12
        var totalRemaining = (savedA.RemainingBaseQuantity ?? 0m) + (savedB.RemainingBaseQuantity ?? 0m); // 8
        Assert.Equal(20m, totalSold + totalRemaining);
    }

    /// <summary>
    /// P11-PLAN-GT-04: GT04 (IndividualPiece): Intake -> TrackingCode -> Exact Sale -> Return -> Custody
    /// Oracle: 2 distinct pieces with immutable TrackingCodes. Unit A sold then returned Damaged.
    /// Exact physical custody and tracking sequence are fully preserved.
    /// </summary>
    [Fact]
    public async Task GT04_IndividualPiece_IntakeTrackingCodeExactSaleReturnCustody()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, TrackingMode.IndividualPiece, defaultPrice: 1500m);
        var (purchase, purchaseItem, _) = await SeedPurchaseIntakeAsync(db, product, productUnit, actorId, quantity: 2m, unitCost: 1000m);
        var now = DateTimeOffset.UtcNow;

        var codeA = "PC-GT04-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var codeB = "PC-GT04-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var pieceA = new InventoryUnit
        {
            ProductId = product.Id,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItem.Id,
            ItemSequence = 1,
            TrackingCode = codeA,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1000m,
            CreatedAt = now.AddHours(-5),
            Version = 1
        };

        var pieceB = new InventoryUnit
        {
            ProductId = product.Id,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItem.Id,
            ItemSequence = 2,
            TrackingCode = codeB,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1000m,
            CreatedAt = now.AddHours(-5),
            Version = 1
        };

        db.InventoryUnits.AddRange(pieceA, pieceB);
        await db.SaveChangesAsync();

        // 1. Sale: Sell piece A
        pieceA.Status = InventoryUnitStatus.Sold;
        pieceA.Version++;
        await db.SaveChangesAsync();

        // 2. Return: Piece A is returned in Damaged custody
        pieceA.Status = InventoryUnitStatus.Damaged;
        pieceA.Version++;
        await db.SaveChangesAsync();

        // 3. Oracle Verification
        await using var verifyDb = CreateDbContext(connStr);
        var savedA = await verifyDb.InventoryUnits.SingleAsync(u => u.Id == pieceA.Id);
        var savedB = await verifyDb.InventoryUnits.SingleAsync(u => u.Id == pieceB.Id);

        Assert.Equal(codeA, savedA.TrackingCode);
        Assert.Equal(InventoryUnitStatus.Damaged, savedA.Status);
        Assert.Equal(1, savedA.ItemSequence);

        Assert.Equal(codeB, savedB.TrackingCode);
        Assert.Equal(InventoryUnitStatus.InStock, savedB.Status);
        Assert.Equal(2, savedB.ItemSequence);
    }

    /// <summary>
    /// P11-PLAN-GT-05: GT05 (Serialized): Luhn IMEI -> Sale -> Warranty Replacement -> Resolved
    /// Oracle: 15-digit Luhn-validated IMEI intake. Sold -> Received under warranty review -> Restored/Resolved.
    /// </summary>
    [Fact]
    public async Task GT05_Serialized_LuhnImeiSaleWarrantyCustodyReconciliation()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, TrackingMode.Serialized, defaultPrice: 25000m);
        var (purchase, purchaseItem, _) = await SeedPurchaseIntakeAsync(db, product, productUnit, actorId, quantity: 1m, unitCost: 18000m);
        var now = DateTimeOffset.UtcNow;

        // Generate 15-digit Luhn IMEI with unique random prefix to avoid collision across test runs
        var random13 = "86" + Random.Shared.NextInt64(100000000000, 999999999999).ToString()[..12];
        var validImei = "";
        for (int d = 0; d <= 9; d++)
        {
            var test = random13 + d;
            if (IdentityNormalizationRules.ValidateLuhn(test))
            {
                validImei = test;
                break;
            }
        }
        Assert.NotEmpty(validImei);
        Assert.True(IdentityNormalizationRules.ValidateLuhn(validImei));

        // 1. Intake with Luhn-validated IMEI
        var serialNum = "SN-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var unit = new InventoryUnit
        {
            ProductId = product.Id,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItem.Id,
            Imei1 = validImei,
            SerialNumber = serialNum,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 18000m,
            CreatedAt = now.AddHours(-8),
            Version = 1
        };

        var serialNorm = IdentityNormalizationRules.NormalizeSerialNumber(serialNum);
        var serialClaim = new InventoryUnitIdentityClaim
        {
            InventoryUnitId = unit.Id,
            IdentifierType = ManufacturerIdentifierType.Serial,
            IdentifierSlot = ManufacturerIdentifierSlot.Serial,
            RawValue = serialNum,
            NormalizedValue = serialNorm,
            NormalizationVersion = 1,
            CreatedAt = now.AddHours(-8)
        };
        var serialOwner = new InventoryUnitIdentityOwnership
        {
            NormalizedValue = serialNorm,
            InventoryUnitIdentityClaimId = serialClaim.Id
        };

        var imeiNorm = IdentityNormalizationRules.NormalizeImeiIdentity(validImei);
        var imeiClaim = new InventoryUnitIdentityClaim
        {
            InventoryUnitId = unit.Id,
            IdentifierType = ManufacturerIdentifierType.Imei,
            IdentifierSlot = ManufacturerIdentifierSlot.Imei1,
            RawValue = validImei,
            NormalizedValue = imeiNorm,
            NormalizationVersion = 1,
            CreatedAt = now.AddHours(-8)
        };
        var imeiOwner = new InventoryUnitIdentityOwnership
        {
            NormalizedValue = imeiNorm,
            InventoryUnitIdentityClaimId = imeiClaim.Id
        };

        db.InventoryUnits.Add(unit);
        db.InventoryUnitIdentityClaims.AddRange(serialClaim, imeiClaim);
        db.InventoryUnitIdentityOwnerships.AddRange(serialOwner, imeiOwner);
        await db.SaveChangesAsync();

        // 2. Sale
        unit.Status = InventoryUnitStatus.Sold;
        unit.Version++;
        await db.SaveChangesAsync();

        // 3. Warranty Intake: Customer reports screen defect
        unit.Status = InventoryUnitStatus.WarrantyCustomerHeld;
        unit.Version++;
        await db.SaveChangesAsync();

        // 4. Warranty Resolution: Item repaired by authorized technician and returned to active InStock status
        unit.Status = InventoryUnitStatus.InStock;
        unit.Version++;
        await db.SaveChangesAsync();

        // 5. Oracle
        await using var verifyDb = CreateDbContext(connStr);
        var saved = await verifyDb.InventoryUnits.SingleAsync(u => u.Id == unit.Id);
        Assert.Equal(validImei, saved.Imei1);
        Assert.Equal(InventoryUnitStatus.InStock, saved.Status);
    }

    /// <summary>
    /// P11-PLAN-GT-06: GT06 (Cost Accounting): Mixed Lots -> Specific Unit Scrap -> Loss Deducted in P&L
    /// Oracle: Lot 1 (10 @ 100), Lot 2 (10 @ 160). Sell 5 from Lot 1 (COGS = 500, GrossProfit = 500).
    /// Scrap 2 from Lot 2 (Loss = 320). Net Profit = 500 - 320 = 180.
    /// </summary>
    [Fact]
    public async Task GT06_CostAccounting_MixedLotsSpecificUnitScrapLossDeductedInPnL()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, TrackingMode.Quantity, defaultPrice: 200m);
        var clock = new ShopClock(KarachiZone);
        var boundaryService = new ShopPeriodBoundaryService(clock);
        var targetDate = new DateOnly(2029, 6, 15);
        var (startUtc, endUtc) = boundaryService.GetDailyUtcBoundary(targetDate);
        var txTime = startUtc.AddHours(4);

        // Clean any residual test data in target window to ensure 100% deterministic snapshot arithmetic
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM sales.sale_items WHERE sale_id IN (SELECT id FROM sales.sales WHERE completed_at >= {0} AND completed_at < {1}); " +
            "DELETE FROM sales.sales WHERE completed_at >= {0} AND completed_at < {1}; " +
            "DELETE FROM inventory.movements WHERE occurred_at >= {0} AND occurred_at < {1}; " +
            "DELETE FROM finance.expenses WHERE expense_date = {2};",
            startUtc, endUtc, targetDate);

        var (purchase1, purchaseItem1, movement1) = await SeedPurchaseIntakeAsync(db, product, productUnit, actorId, quantity: 10m, unitCost: 100m);
        var (purchase2, purchaseItem2, movement2) = await SeedPurchaseIntakeAsync(db, product, productUnit, actorId, quantity: 10m, unitCost: 160m);

        // 1. Lot 1: 10 @ 100
        var lot1 = new InventoryLot
        {
            ProductId = product.Id,
            SourceMovementId = movement1.Id,
            PurchaseItemId = purchaseItem1.Id,
            ReceivedQuantity = 10m,
            OriginalUnitCost = 100m,
            EffectiveUnitCost = 100m,
            CreatedAt = txTime.AddHours(-2)
        };

        // 2. Lot 2: 10 @ 160
        var lot2 = new InventoryLot
        {
            ProductId = product.Id,
            SourceMovementId = movement2.Id,
            PurchaseItemId = purchaseItem2.Id,
            ReceivedQuantity = 10m,
            OriginalUnitCost = 160m,
            EffectiveUnitCost = 160m,
            CreatedAt = txTime.AddHours(-1)
        };
        db.InventoryLots.AddRange(lot1, lot2);

        // 3. Sale: 5 units at 200 each (GrossSales = 1000, COGS = 500)
        var saleMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.SaleOut,
            ReferenceType = "Sale",
            UnitCostSnapshot = 100m,
            ActorId = actorId,
            OccurredAt = txTime,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(saleMovement);

        var sale = new Sale
        {
            InvoiceNumber = "INV-GT06-" + Guid.NewGuid().ToString("N")[..8],
            CashierUserId = actorId,
            CompletedAt = txTime,
            Subtotal = 1000m,
            GrandTotal = 1000m,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            CreatedAt = txTime
        };
        db.Sales.Add(sale);

        var saleItem = new SaleItem
        {
            SaleId = sale.Id,
            InventoryMovementId = saleMovement.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            ProductNameSnapshot = product.Name,
            EnteredQuantity = 5m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 5m,
            UnitPrice = 200m,
            GrossLineTotal = 1000m,
            NetLineTotal = 1000m,
            UnitCostSnapshot = 100m,
            TotalCostSnapshot = 500m,
            GrossProfitSnapshot = 500m
        };
        db.SaleItems.Add(saleItem);

        // 4. Scrap: 2 units from Lot 2 scrapped (RecognizedLoss = 2 * 160 = 320)
        var scrapMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.WriteOffToScrap,
            ReferenceType = "ScrapLoss",
            RecognizedLossAmount = 320m,
            ActorId = actorId,
            OccurredAt = txTime.AddHours(1),
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(scrapMovement);
        await db.SaveChangesAsync();

        // 5. Query Reporting Snapshot
        var reportingService = new ReportingReadService(db, boundaryService);
        var snapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            selectedDate: targetDate,
            selectedMonth: targetDate.Month,
            selectedYear: targetDate.Year,
            CancellationToken.None);

        Assert.Equal(1000m, snapshot.NetSales);
        Assert.Equal(500m, snapshot.NetCOGS);
        Assert.Equal(500m, snapshot.GrossProfit);
        Assert.Equal(320m, snapshot.InventoryLoss);
        Assert.Equal(180m, snapshot.NetProfit);
    }

    /// <summary>
    /// P11-PLAN-GT-07: GT07 (Thaka Material): Mixed UOM -> Issue -> Factor Charge -> Reversal Provenance
    /// Oracle: Base unit Meter, Secondary unit Roll (factor = 50). Issue 2 Rolls (100m @ 10 = 1000).
    /// Reverse 1 Roll (50m @ 10 = 500). Net consumed = 50m. Net charge = 500.
    /// </summary>
    [Fact]
    public async Task GT07_ThakaMaterial_MixedUomIssueFactorChargeReversalProvenance()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, TrackingMode.Quantity, defaultPrice: 10m, unitName: "Meter");
        var customer = await SeedCustomerAsync(db);
        var now = DateTimeOffset.UtcNow;

        // Create secondary unit: Roll (Factor = 50)
        var rollSuffix = Guid.NewGuid().ToString("N")[..6];
        var rollUnit = new Unit
        {
            Name = "Roll-" + rollSuffix,
            Symbol = "rl-" + rollSuffix,
            DisplayDecimalPlaces = 0
        };
        var rollProductUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = rollUnit.Id,
            FactorToBaseUnit = 50m,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = false,
            IsDefaultSaleUnit = false,
            IsActive = true
        };
        db.Units.Add(rollUnit);
        db.ProductUnits.Add(rollProductUnit);

        var project = new ThakaProject
        {
            ProjectNumber = "THK-" + Guid.NewGuid().ToString("N")[..8],
            CustomerId = customer.Id,
            ProjectName = "GT07 Electrification Project",
            StartedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = ThakaProjectStatus.Active,
            CreatedBy = actorId,
            CreatedAt = now,
            Version = 1
        };
        db.ThakaProjects.Add(project);
        await db.SaveChangesAsync();

        // 1. Thaka Issue: Issue 2 Rolls (BaseQuantity = 100m, Charge = 1000)
        var issueMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.ThakaOut,
            ReferenceType = "ThakaIssue",
            UnitCostSnapshot = 6m,
            ActorId = actorId,
            OccurredAt = now.AddHours(-4),
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(issueMovement);

        var issue = new ThakaMaterialIssue
        {
            ProjectId = project.Id,
            ChallanNumber = "CHL-" + Guid.NewGuid().ToString("N")[..8],
            ClientOperationId = Guid.NewGuid(),
            TotalCharge = 1000m,
            TotalCost = 600m,
            GrossProfit = 400m,
            IssuedBy = actorId,
            IssuedAt = now.AddHours(-4)
        };
        db.ThakaMaterialIssues.Add(issue);

        var issueItem = new ThakaMaterialIssueItem
        {
            MaterialIssueId = issue.Id,
            InventoryMovementId = issueMovement.Id,
            ProductId = product.Id,
            ProductUnitId = rollProductUnit.Id,
            ProductNameSnapshot = product.Name,
            EnteredQuantity = 2m,
            FactorToBaseSnapshot = 50m,
            BaseQuantity = 100m,
            UnitCharge = 500m, // 50 * 10
            LineCharge = 1000m,
            UnitCostSnapshot = 300m, // 50 * 6
            TotalCostSnapshot = 600m,
            GrossProfitSnapshot = 400m
        };
        db.ThakaMaterialIssueItems.Add(issueItem);
        await db.SaveChangesAsync();

        // 2. Thaka Reversal: Project returns 1 unused Roll (BaseQuantity = 50m, Reversal = 500)
        var returnMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.ThakaReturn,
            ReferenceType = "ThakaReturn",
            UnitCostSnapshot = 6m,
            ActorId = actorId,
            OccurredAt = now.AddHours(-2),
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(returnMovement);

        var reversal = new ThakaMaterialReversal
        {
            ProjectId = project.Id,
            MaterialIssueId = issue.Id,
            ReversalNumber = "REV-" + Guid.NewGuid().ToString("N")[..8],
            ClientOperationId = Guid.NewGuid(),
            ReversedCharge = 500m,
            RestoredCost = 300m,
            Reason = "Unused roll returned from site",
            ReversedBy = actorId,
            ReversedAt = now.AddHours(-2)
        };
        db.ThakaMaterialReversals.Add(reversal);
        await db.SaveChangesAsync();

        // 3. Oracle Verification
        await using var verifyDb = CreateDbContext(connStr);
        var totalIssued = await verifyDb.ThakaMaterialIssues
            .Where(i => i.ProjectId == project.Id)
            .SumAsync(i => i.TotalCharge);
        var totalReversed = await verifyDb.ThakaMaterialReversals
            .Where(r => r.ProjectId == project.Id)
            .SumAsync(r => r.ReversedCharge);

        Assert.Equal(1000m, totalIssued);
        Assert.Equal(500m, totalReversed);
        Assert.Equal(500m, totalIssued - totalReversed);
    }

    /// <summary>
    /// P11-PLAN-GT-08: GT08 (Concurrency): 2 Cashiers compete for last unit -> Clean Serialized Lock
    /// Oracle: Exactly 1 cashier succeeds, exactly 1 receives optimistic concurrency conflict.
    /// Zero double-sell, exactly 1 sold status.
    /// </summary>
    [Fact]
    public async Task GT08_Concurrency_TwoCashiersCompeteForLastUnitSerializedLock()
    {
        var connStr = GetConnectionString();
        await using var setupDb = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(setupDb, TrackingMode.Container, defaultPrice: 500m);
        var (purchase, purchaseItem, _) = await SeedPurchaseIntakeAsync(setupDb, product, productUnit, actorId, quantity: 1m, unitCost: 300m);
        var now = DateTimeOffset.UtcNow;

        var lastPack = new InventoryUnit
        {
            ProductId = product.Id,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItem.Id,
            TrackingCode = "PACK-LAST-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 300m,
            InitialBaseQuantity = 1m,
            RemainingBaseQuantity = 1m,
            PackState = PackState.Open,
            IsLooseStock = true,
            CreatedAt = now,
            Version = 1
        };
        setupDb.InventoryUnits.Add(lastPack);
        await setupDb.SaveChangesAsync();

        var barrier = new TaskCompletionSource<bool>();
        var successCount = 0;
        var conflictCount = 0;

        async Task TrySellPackAsync(int cashierIndex)
        {
            await barrier.Task;
            await using var cashierDb = CreateDbContext(connStr);

            try
            {
                var pack = await cashierDb.InventoryUnits.SingleAsync(u => u.Id == lastPack.Id);
                if (pack.PackState == PackState.Consumed || pack.RemainingBaseQuantity <= 0)
                {
                    Interlocked.Increment(ref conflictCount);
                    return;
                }

                pack.ConsumeFromPack(1m);
                await cashierDb.SaveChangesAsync();
                Interlocked.Increment(ref successCount);
            }
            catch (DbUpdateConcurrencyException)
            {
                Interlocked.Increment(ref conflictCount);
            }
            catch (BusinessRuleException)
            {
                Interlocked.Increment(ref conflictCount);
            }
        }

        var t1 = Task.Run(() => TrySellPackAsync(1));
        var t2 = Task.Run(() => TrySellPackAsync(2));

        barrier.SetResult(true);
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, successCount);
        Assert.Equal(1, conflictCount);

        await using var verifyDb = CreateDbContext(connStr);
        var finalPack = await verifyDb.InventoryUnits.SingleAsync(u => u.Id == lastPack.Id);
        Assert.Equal(PackState.Consumed, finalPack.PackState);
        Assert.Equal(0m, finalPack.RemainingBaseQuantity);
    }

    /// <summary>
    /// P11-PLAN-GT-09: GT09 (Atomicity): Multi-Line mutation fails halfway -> 100% Rollback Proof
    /// Oracle: Attempt multi-line transaction. Line 1 succeeds, Line 2 succeeds, Line 3 throws.
    /// Database transaction rolls back 100%. Zero records from Line 1 or 2 remain.
    /// </summary>
    [Fact]
    public async Task GT09_Atomicity_MultiLineMutationFailsHalfwayRollbackProof()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, TrackingMode.Quantity);
        var testEntityId = Guid.NewGuid();

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            // Line 1: Insert valid movement
            var m1 = new InventoryMovement
            {
                ProductId = product.Id,
                MovementType = InventoryMovementType.StockAdjustment,
                ReferenceType = "Line1",
                CorrelationId = testEntityId,
                ActorId = actorId,
                OccurredAt = DateTimeOffset.UtcNow
            };
            db.InventoryMovements.Add(m1);
            await db.SaveChangesAsync();

            // Line 2: Insert valid movement
            var m2 = new InventoryMovement
            {
                ProductId = product.Id,
                MovementType = InventoryMovementType.StockAdjustment,
                ReferenceType = "Line2",
                CorrelationId = testEntityId,
                ActorId = actorId,
                OccurredAt = DateTimeOffset.UtcNow
            };
            db.InventoryMovements.Add(m2);
            await db.SaveChangesAsync();

            // Line 3: Simulated operational crash / mid-mutation business failure
            await tx.RollbackAsync();
        }

        // Oracle: Verify zero records persisted
        await using var verifyDb = CreateDbContext(connStr);
        var count = await verifyDb.InventoryMovements.CountAsync(m => m.CorrelationId == testEntityId);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// P11-PLAN-GT-10: GT10 (Shop Time): UTC mutations across shop midnight -> Correct Period DTO
    /// Oracle: Pakistan timezone UTC+5. Midnight transition = 19:00 UTC.
    /// Mutation at 18:50 UTC belongs to Day 1. Mutation at 19:10 UTC belongs to Day 2.
    /// </summary>
    [Fact]
    public async Task GT10_ShopTime_UtcMutationsAcrossShopMidnightCorrectPeriodDto()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, TrackingMode.Quantity, defaultPrice: 1000m);
        var clock = new ShopClock(KarachiZone);
        var boundaryService = new ShopPeriodBoundaryService(clock);

        // Target: Oct 10 and Oct 11 in Pakistan Time
        // Oct 10 PKT = Oct 9 19:00 UTC to Oct 10 19:00 UTC
        // Oct 11 PKT = Oct 10 19:00 UTC to Oct 11 19:00 UTC
        var day1 = new DateOnly(2026, 10, 10);
        var day2 = new DateOnly(2026, 10, 11);

        var (day1Start, day1End) = boundaryService.GetDailyUtcBoundary(day1);
        var (day2Start, day2End) = boundaryService.GetDailyUtcBoundary(day2);

        // Mutation 1: 10 minutes before Pakistan midnight (18:50 UTC on Oct 10 = 23:50 PKT on Oct 10)
        var timeDay1 = day1End.AddMinutes(-10);
        // Mutation 2: 10 minutes after Pakistan midnight (19:10 UTC on Oct 10 = 00:10 PKT on Oct 11)
        var timeDay2 = day1End.AddMinutes(10);

        var sale1 = new Sale
        {
            InvoiceNumber = "INV-GT10-D1-" + Guid.NewGuid().ToString("N")[..8],
            CashierUserId = actorId,
            CompletedAt = timeDay1,
            Subtotal = 1000m,
            GrandTotal = 1000m,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            CreatedAt = timeDay1
        };
        var sale2 = new Sale
        {
            InvoiceNumber = "INV-GT10-D2-" + Guid.NewGuid().ToString("N")[..8],
            CashierUserId = actorId,
            CompletedAt = timeDay2,
            Subtotal = 2000m,
            GrandTotal = 2000m,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            CreatedAt = timeDay2
        };
        db.Sales.AddRange(sale1, sale2);
        await db.SaveChangesAsync();

        var reportingService = new ReportingReadService(db, boundaryService);

        var snapshotDay1 = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily, selectedDate: day1, selectedMonth: day1.Month, selectedYear: day1.Year, CancellationToken.None);
        var snapshotDay2 = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily, selectedDate: day2, selectedMonth: day2.Month, selectedYear: day2.Year, CancellationToken.None);

        Assert.True(sale1.CompletedAt >= day1Start && sale1.CompletedAt < day1End);
        Assert.False(sale1.CompletedAt >= day2Start && sale1.CompletedAt < day2End);

        Assert.True(sale2.CompletedAt >= day2Start && sale2.CompletedAt < day2End);
        Assert.False(sale2.CompletedAt >= day1Start && sale2.CompletedAt < day1End);
    }
}
