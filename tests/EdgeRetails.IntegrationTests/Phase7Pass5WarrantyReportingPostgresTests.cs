using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5WarrantyReportingPostgresTests
{
    // NEW_COVERAGE: isolated persisted financial read fixtures; not business mutation acceptance.
    [Fact]
    public async Task CustomerMetricsDeriveActiveProjectCountAndBalanceFromCanonicalFacts()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var arrange = provider.CreateAsyncScope();
        var db = arrange.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);
        var other = await Phase2PostgresTestHarness.SeedCustomerAsync(db);
        var actor = await IntegrationIdentitySeeder.CreateActorAsync(db);
        var now = DateTimeOffset.UtcNow;
        ThakaProject Project(Guid customerId, ThakaProjectStatus status) => new()
        {
            CustomerId = customerId, ProjectNumber = "P5-" + Guid.NewGuid(), ProjectName = "Read proof",
            Status = status, StartedOn = DateOnly.FromDateTime(now.UtcDateTime), CreatedAt = now, CreatedBy = actor, Version = 1
        };
        var active = Project(customer.Id, ThakaProjectStatus.Active);
        var overpaid = Project(customer.Id, ThakaProjectStatus.Active);
        var closed = Project(customer.Id, ThakaProjectStatus.Closed);
        db.ThakaProjects.AddRange(active, overpaid, closed, Project(other.Id, ThakaProjectStatus.Active));
        ThakaMaterialIssue Issue(ThakaProject project, decimal charge) => new()
        {
            ProjectId = project.Id, ChallanNumber = "P5-" + Guid.NewGuid(), ClientOperationId = Guid.NewGuid(),
            TotalCharge = charge, TotalCost = 0m, GrossProfit = charge, IssuedAt = now, IssuedBy = actor
        };
        ThakaPayment Payment(ThakaProject project, decimal amount) => new()
        {
            ProjectId = project.Id, ReceiptNumber = "P5-" + Guid.NewGuid(), ClientOperationId = Guid.NewGuid(),
            Amount = amount, PaymentMethod = ThakaPaymentMethod.Bank, RecordedAt = now, RecordedBy = actor
        };
        var issue = Issue(active, 100m);
        var payment = Payment(active, 40m);
        db.ThakaMaterialIssues.AddRange(issue, Issue(overpaid, 10m), Issue(closed, 999m));
        db.ThakaPayments.AddRange(payment, Payment(overpaid, 20m));
        db.ThakaMaterialReversals.Add(new()
        {
            ProjectId = active.Id, MaterialIssueId = issue.Id, ReversalNumber = "P5-" + Guid.NewGuid(),
            ClientOperationId = Guid.NewGuid(), ReversedCharge = 10m, Reason = "Read fixture",
            ReversedBy = actor, ReversedAt = now
        });
        db.ThakaPaymentReversals.Add(new()
        {
            ProjectId = active.Id, PaymentId = payment.Id, ReversalNumber = "P5-" + Guid.NewGuid(),
            ClientOperationId = Guid.NewGuid(), Amount = 20m, Reason = "Read fixture", ReversedBy = actor, ReversedAt = now
        });
        db.ThakaSettlements.Add(new()
        {
            ProjectId = active.Id, SettlementNumber = "P5-" + Guid.NewGuid(), ClientOperationId = Guid.NewGuid(),
            GrossMaterialChargesSnapshot = 100m, PaymentsCollectedSnapshot = 20m,
            SettlementDiscount = 5m, BalanceBeforeSettlement = 70m, SettledAt = now, SettledBy = actor
        });
        await db.SaveChangesAsync();

        await using var read = provider.CreateAsyncScope();
        var rows = await read.ServiceProvider.GetRequiredService<IPartyDirectoryReadService>().GetCustomersAsync(customer.Name, 100);
        var row = Assert.Single(rows, x => x.CustomerId == customer.Id);
        Assert.Equal(2, row.ActiveThakaCount);
        Assert.Equal(65m, row.CurrentThakaBalance);
        Assert.Equal(0m, row.NetSales);
        var canonical = await read.ServiceProvider.GetRequiredService<IThakaReadService>().GetProjectsAsync(default);
        Assert.Equal(canonical.Where(x => x.CustomerId == customer.Id && x.Status == ThakaProjectStatus.Active).Sum(x => x.Balance), row.CurrentThakaBalance);
    }

    // NEW_COVERAGE D13: canonical PostgreSQL writers establish every sold/claimed/returned source.
    [Theory]
    [InlineData(TrackingMode.Quantity, 6, false)]
    [InlineData(TrackingMode.Length, 4, true)]
    public async Task PriorClaimReducesSelectedSupplierCapacityDespiteOtherSupplierRemaining(
        TrackingMode mode, int sourceAQuantity, bool exhaustSource)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedMultiSupplierSaleAsync(provider, mode, sourceAQuantity);
        var priorClaimQuantity = exhaustSource ? fixture.SourceAQuantity : fixture.SourceAQuantity - 2m;
        var requestedQuantity = exhaustSource ? 1m : 3m;
        var original = await OriginalSourceSnapshotAsync(provider, fixture);
        await ClaimAsync(provider, fixture, fixture.SupplierAId, priorClaimQuantity);
        Assert.True(requestedQuantity <= 10m - priorClaimQuantity);
        Assert.True(requestedQuantity > fixture.SourceAQuantity - priorClaimQuantity);
        var before = await CanonicalStateSnapshotAsync(provider, fixture);

        await using var attempt = provider.CreateAsyncScope();
        var result = await attempt.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
            ClaimCommand(fixture, fixture.SupplierAId, requestedQuantity), default);
        Assert.False(result.IsSuccess, "D13: original sold quantity must not be reused as remaining supplier capacity.");
        Assert.Equal("warranty.supplier_quantity_mismatch", result.Error?.Code);
        Assert.Equal(before, await CanonicalStateSnapshotAsync(provider, fixture));
        Assert.Equal(original, await OriginalSourceSnapshotAsync(provider, fixture));
        await AssertClaimedQuantityAsync(provider, fixture, fixture.SupplierAId, priorClaimQuantity);
    }

    [Theory]
    [InlineData(TrackingMode.Quantity, 6)]
    [InlineData(TrackingMode.Length, 4)]
    public async Task PriorReturnAndOtherSupplierClaimReduceSelectedSourceCapacity(
        TrackingMode mode, int sourceAQuantity)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedMultiSupplierSaleAsync(provider, mode, sourceAQuantity);
        var original = await OriginalSourceSnapshotAsync(provider, fixture);
        var returnedId = await ReturnAsync(provider, fixture, 2m);
        await AssertReturnedSupplierQuantityAsync(provider, fixture, returnedId, fixture.SupplierAId, 2m);
        await ClaimAsync(provider, fixture, fixture.SupplierBId, 1m);
        var request = fixture.SourceAQuantity - 1m;
        Assert.True(request <= 10m - 2m - 1m);
        Assert.True(request > fixture.SourceAQuantity - 2m);
        var before = await CanonicalStateSnapshotAsync(provider, fixture);

        await using var attempt = provider.CreateAsyncScope();
        var result = await attempt.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
            ClaimCommand(fixture, fixture.SupplierAId, request), default);
        Assert.False(result.IsSuccess, "D13: returned A stock must reduce A capacity even when global sold capacity remains.");
        Assert.Equal("warranty.supplier_quantity_mismatch", result.Error?.Code);
        Assert.Equal(before, await CanonicalStateSnapshotAsync(provider, fixture));
        Assert.Equal(original, await OriginalSourceSnapshotAsync(provider, fixture));
        await AssertClaimedQuantityAsync(provider, fixture, fixture.SupplierBId, 1m);
    }

    [Theory]
    [InlineData(TrackingMode.Quantity, 6)]
    [InlineData(TrackingMode.Length, 4)]
    public async Task FutureReturnUsesUnclaimedSourceAndConservesOriginalEvidenceOnReplay(
        TrackingMode mode, int sourceAQuantity)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedMultiSupplierSaleAsync(provider, mode, sourceAQuantity);
        var original = await OriginalSourceSnapshotAsync(provider, fixture);
        await ClaimAsync(provider, fixture, fixture.SupplierAId, fixture.SourceAQuantity);
        var intent = ReturnCommand(fixture, 2m);
        await using var attempt = provider.CreateAsyncScope();
        var result = await attempt.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(intent, default);
        Assert.True(result.IsSuccess, result.Error?.Message);

        // A is fully reserved by its canonical customer claim; the available return source is B.
        await AssertReturnedSupplierQuantityAsync(provider, fixture, result.Value!.SaleReturnId, fixture.SupplierBId, 2m);
        await AssertClaimedQuantityAsync(provider, fixture, fixture.SupplierAId, fixture.SourceAQuantity);
        Assert.Equal(original, await OriginalSourceSnapshotAsync(provider, fixture));
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(2m, (await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId)).SellableQty);
        Assert.Equal(2m, (await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId)).CostedQty);
        Assert.Equal(200m, (await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId)).TotalInventoryCost);
        var beforeReplay = await CanonicalStateSnapshotAsync(provider, fixture);
        await using var replayScope = provider.CreateAsyncScope();
        var replay = await replayScope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(intent, default);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.True(replay.Value!.WasExisting);
        Assert.Equal(result.Value.SaleReturnId, replay.Value.SaleReturnId);
        Assert.Equal(beforeReplay, await CanonicalStateSnapshotAsync(provider, fixture));
        Assert.Equal(original, await OriginalSourceSnapshotAsync(provider, fixture));
    }

    // NEW_COVERAGE: canonical concurrent writers contend for exactly one remaining source unit.
    [Theory]
    [InlineData(TrackingMode.Quantity, true)]
    [InlineData(TrackingMode.Length, true)]
    [InlineData(TrackingMode.Quantity, false)]
    [InlineData(TrackingMode.Length, false)]
    public async Task D13RemainingOneSourceSerializesClaimReturnAndReturnReturn(TrackingMode mode, bool claimAgainstReturn)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, mode, 4m);
        await ClaimAsync(provider, f, f.SupplierAId, f.SourceAQuantity);
        await ClaimAsync(provider, f, f.SupplierBId, 10m - f.SourceAQuantity - 1m);
        var original = await OriginalSourceSnapshotAsync(provider, f);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Attempt(bool claim)
        {
            await using var scope = provider.CreateAsyncScope();
            await start.Task;
            if (claim)
            {
                return (await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
                    ClaimCommand(f, f.SupplierBId, 1m), default)).IsSuccess;
            }
            return (await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(
                ReturnCommand(f, 1m), default)).IsSuccess;
        }
        var a = Attempt(claimAgainstReturn);
        var b = Attempt(false);
        start.SetResult();
        var results = await Task.WhenAll(a, b);
        Assert.Single(results, x => x);
        Assert.Single(results, x => !x);
        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var claimItemIds = db.WarrantyClaimItems.Where(x => x.OriginalSaleItemId == f.SaleItemId).Select(x => x.Id);
        var returnItemIds = db.SaleReturnItems.Where(x => x.SaleItemId == f.SaleItemId).Select(x => x.Id);
        var claimed = await db.WarrantyClaimSourceAllocations.Where(x => claimItemIds.Contains(x.ClaimItemId)).SumAsync(x => x.BaseQuantity);
        var returned = await db.SaleReturnSourceAllocations.Where(x => returnItemIds.Contains(x.SaleReturnItemId)).SumAsync(x => x.BaseQuantity);
        Assert.Equal(10m, claimed + returned);
        Assert.Equal(original, await OriginalSourceSnapshotAsync(provider, f));
    }

    // NEW_COVERAGE: fractional Length uses immutable source facts and replay cannot duplicate them.
    [Fact]
    public async Task D13FractionalLengthClaimAndReturnReplayPreserveSourceFacts()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Length, 4.25m);
        var original = await OriginalSourceSnapshotAsync(provider, f);
        var claimCommand = ClaimCommand(f, f.SupplierAId, 0.25m);
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(claimCommand, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        var afterClaim = await CanonicalStateSnapshotAsync(provider, f);
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(claimCommand, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        Assert.Equal(afterClaim, await CanonicalStateSnapshotAsync(provider, f));
        var returnCommand = ReturnCommand(f, 0.125m);
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(returnCommand, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        var afterReturn = await CanonicalStateSnapshotAsync(provider, f);
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(returnCommand, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(result.Value!.WasExisting);
        }
        Assert.Equal(afterReturn, await CanonicalStateSnapshotAsync(provider, f));
        Assert.Equal(original, await OriginalSourceSnapshotAsync(provider, f));
    }

    // WAVE A: Multiple same-product SaleItems + source attribution
    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task D13_MultipleSameProductSaleItems_AttributionAndCapacity(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiLineSameProductSaleAsync(provider, mode, 6m, 4m);

        // 1. Return 2m against SaleItem 1 (Supplier A)
        var returnCmd = new CreateSaleReturnCommand(
            f.SaleId, "DEFECTIVE", "Return Line 1", RefundMethod.Bank, f.ActorId, Guid.NewGuid(),
            [new SaleReturnLineInput(f.SaleItem1Id, 2m, SaleReturnDisposition.RestockSellable, [])]);
        await using (var scope = provider.CreateAsyncScope())
        {
            var retRes = await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(returnCmd, default);
            Assert.True(retRes.IsSuccess, retRes.Error?.Message);
        }

        // Verify SaleReturnSourceAllocation links to SaleItem 1's consumption only
        await using (var read = provider.CreateAsyncScope())
        {
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var consumption1 = await db.InventoryLotConsumptions.SingleAsync(x => x.MovementId == f.SaleMovement1Id);
            var alloc = await db.SaleReturnSourceAllocations.SingleAsync(x => x.SaleConsumptionId == consumption1.Id);
            Assert.Equal(2m, alloc.BaseQuantity);

            // Restored lot purchase item matches Purchase A (Supplier A)
            var restoredLot = await db.InventoryLots.SingleAsync(x => x.Id == alloc.RestoredInventoryLotId);
            var purchaseItemA = await db.PurchaseItems.SingleAsync(x => x.PurchaseId == f.PurchaseAId);
            Assert.Equal(purchaseItemA.Id, restoredLot.PurchaseItemId);
        }

        // 2. Warranty claim 2m against SaleItem 2 with Supplier B
        var claimCmd = new CreateWarrantyClaimCommand(
            f.CustomerId, f.SaleId, f.SupplierBId, f.ActorId,
            [new WarrantyClaimItemInput(f.ProductId, 2m, "Fault Line 2", f.SaleItem2Id)], Guid.NewGuid());
        await using (var scope = provider.CreateAsyncScope())
        {
            var claimRes = await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(claimCmd, default);
            Assert.True(claimRes.IsSuccess, claimRes.Error?.Message);
        }

        // Verify WarrantyClaimSourceAllocation links to SaleItem 2's consumption only
        await using (var read = provider.CreateAsyncScope())
        {
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var consumption2 = await db.InventoryLotConsumptions.SingleAsync(x => x.MovementId == f.SaleMovement2Id);
            var claimAlloc = await db.WarrantyClaimSourceAllocations.SingleAsync(x => x.SaleConsumptionId == consumption2.Id);
            Assert.Equal(2m, claimAlloc.BaseQuantity);
        }

        // 3. Attempt overcapacity return on Line 1 of 5m (already returned 2m, total 7m > 6m original)
        // Even though Sale has 10m total and Line 2 still has 2m unclaimed, Line 1 CANNOT borrow from Line 2!
        var overReturnCmd = new CreateSaleReturnCommand(
            f.SaleId, "DEFECTIVE", "Over return Line 1", RefundMethod.Bank, f.ActorId, Guid.NewGuid(),
            [new SaleReturnLineInput(f.SaleItem1Id, 5m, SaleReturnDisposition.RestockSellable, [])]);
        await using (var scope = provider.CreateAsyncScope())
        {
            var overRes = await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(overReturnCmd, default);
            Assert.False(overRes.IsSuccess);
            Assert.Equal("sales.return_exceeds_original", overRes.Error?.Code);
        }

        // 4. CommercialExchange return of 1m against Line 1
        var repl = await SeedReplacementProductWithStockAsync(provider, 10m);
        var exchCmd = new CommercialExchangeCommand(
            Guid.NewGuid(), f.SaleId, f.ActorId, null, f.CustomerId,
            "EXCHANGE", "Exchange Line 1",
            [new SaleReturnLineInput(f.SaleItem1Id, 1m, SaleReturnDisposition.RestockSellable, [])],
            [new CompleteSaleLineInput(repl.ProductId, repl.ProductUnitId, 1m, 150m, [])],
            0m, SalePaymentMethod.Other, 0m, "EXCHANGE-REF");
        await using (var scope = provider.CreateAsyncScope())
        {
            var exchRes = await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(exchCmd, default);
            Assert.True(exchRes.IsSuccess, exchRes.Error?.Message);
        }

        // Check remaining capacities:
        // Line 1: 6m - 2m (return) - 1m (exchange) = 3m remaining
        // Line 2: 4m - 2m (claim) = 2m remaining
        await using (var read = provider.CreateAsyncScope())
        {
            var sales = read.ServiceProvider.GetRequiredService<ISalesRepository>();
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            var cap1 = await sales.GetSoldSourceCapacityForUpdateAsync(f.SaleItem1Id, default);
            var cap2 = await sales.GetSoldSourceCapacityForUpdateAsync(f.SaleItem2Id, default);
            Assert.Equal(3m, cap1.Sum(x => x.RemainingQuantity));
            Assert.Equal(2m, cap2.Sum(x => x.RemainingQuantity));
            await tx.RollbackAsync();
        }
    }

    // WAVE B: Wrong Supplier, wrong source, wrong lot, wrong PurchaseItem, overcapacity
    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task D13_WrongSupplier_ClaimFailsClosed(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, mode, 6m);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var supplierC = await Phase2PostgresTestHarness.SeedSupplierAsync(db, "D13-unrelated-C", "UC");
        var beforeSnapshot = await CanonicalStateSnapshotAsync(provider, f);
        var originalSnapshot = await OriginalSourceSnapshotAsync(provider, f);

        var result = await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
            ClaimCommand(f, supplierC.Id, 1m), default);
        Assert.False(result.IsSuccess);
        Assert.Equal("warranty.supplier_quantity_mismatch", result.Error?.Code);

        Assert.Equal(beforeSnapshot, await CanonicalStateSnapshotAsync(provider, f));
        Assert.Equal(originalSnapshot, await OriginalSourceSnapshotAsync(provider, f));
        Assert.Empty(await db.WarrantyClaims.Where(x => x.OriginalSaleId == f.SaleId).ToListAsync());

        var saleConsumptionIds = await db.InventoryLotConsumptions.Where(c => c.MovementId == f.SaleMovementId).Select(c => c.Id).ToListAsync();
        Assert.Empty(await db.WarrantyClaimSourceAllocations.Where(x => saleConsumptionIds.Contains(x.SaleConsumptionId)).ToListAsync());
    }

    [Fact]
    public async Task D13_WrongSourceOrLotOrPurchaseItem_FailsClosed()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 6m);

        await using (var corruptScope = provider.CreateAsyncScope())
        {
            var db = corruptScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var other = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            var lot = await db.InventoryLots.FirstAsync(x => x.ProductId == f.ProductId);
            lot.ProductId = other.ProductId;
            await db.SaveChangesAsync();
        }

        await using (var testScope = provider.CreateAsyncScope())
        {
            var sales = testScope.ServiceProvider.GetRequiredService<ISalesRepository>();
            var db = testScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                sales.GetSoldSourceCapacityForUpdateAsync(f.SaleItemId, default));
            Assert.Equal("sales.source_reconciliation_required", ex.Code);
            await tx.RollbackAsync();
        }
    }

    [Fact]
    public async Task D13_Overcapacity_SaleReturn_WarrantyClaim_ExchangeReturn_FailsClosed()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 6m);
        var repl = await SeedReplacementProductWithStockAsync(provider);

        var retId = await ReturnAsync(provider, f, 8m);
        Assert.NotEqual(Guid.Empty, retId);

        // Remaining capacity is 2m.
        // 1. Attempt SaleReturn of 3m (exceeds remaining 2m)
        await using (var scope = provider.CreateAsyncScope())
        {
            var retResult = await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(
                ReturnCommand(f, 3m), default);
            Assert.False(retResult.IsSuccess);
            Assert.Equal("sales.return_exceeds_original", retResult.Error?.Code);
        }

        // 2a. Attempt WarrantyClaim of 3m (exceeds eligible remaining 2m)
        await using (var scope = provider.CreateAsyncScope())
        {
            var claimResult = await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
                ClaimCommand(f, f.SupplierBId, 3m), default);
            Assert.False(claimResult.IsSuccess);
            Assert.Equal("warranty.quantity_exceeds_eligible", claimResult.Error?.Code);
        }

        // 2b. Attempt WarrantyClaim of 1m against Supplier A (Supplier A had 6m, 8m returned exhausted it -> remaining 0m)
        await using (var scope = provider.CreateAsyncScope())
        {
            var claimResultA = await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
                ClaimCommand(f, f.SupplierAId, 1m), default);
            Assert.False(claimResultA.IsSuccess);
            Assert.Equal("warranty.supplier_quantity_mismatch", claimResultA.Error?.Code);
        }

        // 3. Attempt CommercialExchange return of 3m (exceeds remaining 2m)
        await using (var scope = provider.CreateAsyncScope())
        {
            var exchResult = await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(
                ExchangeCommand(f, repl, 3m), default);
            Assert.False(exchResult.IsSuccess);
            Assert.Equal("sales.return_exceeds_original", exchResult.Error?.Code);
        }
    }

    // WAVE C: Fractional Length exact precision & arithmetic
    [Fact]
    public async Task D13_FractionalLength_ExactPrecisionArithmeticAndCapacity()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Length, 5.2500m);
        var original = await OriginalSourceSnapshotAsync(provider, f);

        // Step 1: Return 3.2500m
        var ret1 = await ReturnAsync(provider, f, 3.2500m);
        Assert.NotEqual(Guid.Empty, ret1);

        // Step 2: Claim 0.1250m against Supplier A
        await ClaimAsync(provider, f, f.SupplierAId, 0.1250m);

        // Remaining capacity outside warranty is 10.0000 - 3.2500 - 0.1250 = 6.6250m
        // Step 3: Attempt return of 6.6251m (0.0001 over capacity outside warranty) -> fails closed
        await using (var scope = provider.CreateAsyncScope())
        {
            var overResult = await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(
                ReturnCommand(f, 6.6251m), default);
            Assert.False(overResult.IsSuccess);
            Assert.Equal("sales.return_unit_active_warranty", overResult.Error?.Code);
        }

        // Step 4: Return exact 6.6250m -> succeeds
        var ret2 = await ReturnAsync(provider, f, 6.6250m);
        Assert.NotEqual(Guid.Empty, ret2);

        // Step 5: Remaining capacity is now exactly 0.0000m. Any further attempt must fail closed!
        await using (var scope = provider.CreateAsyncScope())
        {
            var zeroReturn = await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(
                ReturnCommand(f, 0.0001m), default);
            Assert.False(zeroReturn.IsSuccess);
            Assert.Equal("sales.return_unit_active_warranty", zeroReturn.Error?.Code);

            var zeroClaim = await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
                ClaimCommand(f, f.SupplierBId, 0.0001m), default);
            Assert.False(zeroClaim.IsSuccess);
            Assert.Equal("warranty.quantity_exceeds_eligible", zeroClaim.Error?.Code);
        }

        Assert.Equal(original, await OriginalSourceSnapshotAsync(provider, f));
    }

    // WAVE E: CommercialExchange race / replay / rollback
    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task D13_CommercialExchange_RaceWithSaleReturn_Serializes(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, mode, 5m);
        var repl = await SeedReplacementProductWithStockAsync(provider, 10m);

        var priorRet = await ReturnAsync(provider, f, 9m);
        Assert.NotEqual(Guid.Empty, priorRet);

        var barrier = new Barrier(2);
        var t1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return (await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(
                ExchangeCommand(f, repl, 1m), default)).IsSuccess;
        });
        var t2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return (await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(
                ReturnCommand(f, 1m), default)).IsSuccess;
        });

        var results = await Task.WhenAll(t1, t2);

        Assert.Single(results, x => x);
        Assert.Single(results, x => !x);

        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var consumptionIds = await db.InventoryLotConsumptions
            .Where(c => c.MovementId == f.SaleMovementId)
            .Select(c => c.Id)
            .ToListAsync();
        var totalAllocated = await db.SaleReturnSourceAllocations
            .Where(x => consumptionIds.Contains(x.SaleConsumptionId))
            .SumAsync(x => x.BaseQuantity);
        Assert.Equal(10m, totalAllocated);
    }

    [Fact]
    public async Task D13_CommercialExchange_RaceWithWarrantyClaim_Serializes()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 5m);
        var repl = await SeedReplacementProductWithStockAsync(provider, 10m);

        await ReturnAsync(provider, f, 9m);

        var barrier = new Barrier(2);
        var t1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return (await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(
                ExchangeCommand(f, repl, 1m), default)).IsSuccess;
        });
        var t2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return (await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
                ClaimCommand(f, f.SupplierBId, 1m), default)).IsSuccess;
        });

        var results = await Task.WhenAll(t1, t2);

        Assert.Single(results, x => x);
        Assert.Single(results, x => !x);

        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var consumptionIds = await db.InventoryLotConsumptions
            .Where(c => c.MovementId == f.SaleMovementId)
            .Select(c => c.Id)
            .ToListAsync();
        var retSum = await db.SaleReturnSourceAllocations
            .Where(x => consumptionIds.Contains(x.SaleConsumptionId))
            .SumAsync(x => x.BaseQuantity);
        var claimSum = await db.WarrantyClaimSourceAllocations
            .Where(x => consumptionIds.Contains(x.SaleConsumptionId))
            .SumAsync(x => x.BaseQuantity);
        Assert.Equal(10m, retSum + claimSum);
    }

    [Fact]
    public async Task D13_CommercialExchange_RaceWithExchange_Serializes()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 5m);
        var repl = await SeedReplacementProductWithStockAsync(provider, 10m);

        await ReturnAsync(provider, f, 9m);

        var barrier = new Barrier(2);
        var t1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return (await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(
                ExchangeCommand(f, repl, 1m), default)).IsSuccess;
        });
        var t2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return (await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(
                ExchangeCommand(f, repl, 1m), default)).IsSuccess;
        });

        var results = await Task.WhenAll(t1, t2);

        Assert.Single(results, x => x);
        Assert.Single(results, x => !x);

        await using var read = provider.CreateAsyncScope();
        var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var consumptionIds = await db.InventoryLotConsumptions
            .Where(c => c.MovementId == f.SaleMovementId)
            .Select(c => c.Id)
            .ToListAsync();
        var totalAllocated = await db.SaleReturnSourceAllocations
            .Where(x => consumptionIds.Contains(x.SaleConsumptionId))
            .SumAsync(x => x.BaseQuantity);
        Assert.Equal(10m, totalAllocated);
    }

    [Fact]
    public async Task D13_CommercialExchange_ReplayConservesSourceFacts()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 6m);
        var repl = await SeedReplacementProductWithStockAsync(provider, 10m);
        var opId = Guid.NewGuid();
        var cmd = ExchangeCommand(f, repl, 2m, clientOpId: opId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var res = await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(cmd, default);
            Assert.True(res.IsSuccess, res.Error?.Message);
        }

        int allocCount1, moveCount1;
        await using (var read = provider.CreateAsyncScope())
        {
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            allocCount1 = await db.SaleReturnSourceAllocations.CountAsync();
            moveCount1 = await db.InventoryMovements.CountAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var replay = await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(cmd, default);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.True(replay.Value!.WasExisting);
        }

        await using (var read = provider.CreateAsyncScope())
        {
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(allocCount1, await db.SaleReturnSourceAllocations.CountAsync());
            Assert.Equal(moveCount1, await db.InventoryMovements.CountAsync());
        }
    }

    [Fact]
    public async Task D13_CommercialExchange_Rollback_LeavesZeroState()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 6m);
        var beforeSnapshot = await CanonicalStateSnapshotAsync(provider, f);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var repl = await SeedReplacementProductWithStockAsync(provider, 10m);
            await using var tx = await db.Database.BeginTransactionAsync();

            var exch = scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>();
            var res = await exch.HandleAsync(ExchangeCommand(f, repl, 2m), default);
            Assert.True(res.IsSuccess, res.Error?.Message);

            await tx.RollbackAsync();
        }

        var afterSnapshot = await CanonicalStateSnapshotAsync(provider, f);
        Assert.Equal(beforeSnapshot, afterSnapshot);

        await using (var read = provider.CreateAsyncScope())
        {
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var consumptionIds = await db.InventoryLotConsumptions
                .Where(c => c.MovementId == f.SaleMovementId)
                .Select(c => c.Id)
                .ToListAsync();
            Assert.Empty(await db.SaleReturnSourceAllocations
                .Where(x => consumptionIds.Contains(x.SaleConsumptionId))
                .ToListAsync());
        }
    }

    // WAVE F: Payload mismatch characterization
    [Fact]
    public async Task D13_CommercialExchange_PayloadMismatch_FailsClosed()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 6m);
        var repl = await SeedReplacementProductWithStockAsync(provider, 10m);
        var opId = Guid.NewGuid();
        var cmd1 = ExchangeCommand(f, repl, 2m, clientOpId: opId);

        await using (var scope = provider.CreateAsyncScope())
        {
            var res1 = await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(cmd1, default);
            Assert.True(res1.IsSuccess, res1.Error?.Message);
        }

        var cmd2 = new CommercialExchangeCommand(
            opId, f.SaleId, f.ActorId, null, f.CustomerId,
            "DEFECTIVE_MISMATCH", "Different payload",
            cmd1.ReturnLines, cmd1.ReplacementLines,
            0m, SalePaymentMethod.Other, 0m, "EXCHANGE-REF");

        await using (var scope = provider.CreateAsyncScope())
        {
            var res2 = await scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>().HandleAsync(cmd2, default);
            Assert.False(res2.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", res2.Error?.Code);
        }
    }

    // WAVE G: Permission, Audit, Append-Only, Legacy Ambiguity
    [Fact]
    public async Task D13_Permission_DenialFailsClosed()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 6m);
        var invalidActorId = Guid.NewGuid();

        await using (var scope = provider.CreateAsyncScope())
        {
            var cmd = new CreateSaleReturnCommand(
                f.SaleId, "DEFECTIVE", "Note", RefundMethod.Bank, invalidActorId, Guid.NewGuid(),
                [new SaleReturnLineInput(f.SaleItemId, 1m, SaleReturnDisposition.RestockSellable, [])]);
            var res = await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(cmd, default);
            Assert.False(res.IsSuccess);
        }

        await using (var read = provider.CreateAsyncScope())
        {
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var consumptionIds = await db.InventoryLotConsumptions
                .Where(c => c.MovementId == f.SaleMovementId)
                .Select(c => c.Id)
                .ToListAsync();
            Assert.Empty(await db.SaleReturnSourceAllocations
                .Where(x => consumptionIds.Contains(x.SaleConsumptionId))
                .ToListAsync());
            Assert.Empty(await db.SaleReturns.Where(x => x.SaleId == f.SaleId).ToListAsync());
        }
    }

    [Fact]
    public async Task D13_AuditEvidence_Verified()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 6m);

        var retId = await ReturnAsync(provider, f, 2m);
        Assert.NotEqual(Guid.Empty, retId);

        await using (var read = provider.CreateAsyncScope())
        {
            var db = read.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var audit = await db.BusinessAuditEvents.Where(x => x.ActorId == f.ActorId && x.Action == "SALE_RETURN_COMPLETED").FirstOrDefaultAsync();
            Assert.NotNull(audit);
            Assert.Equal("SALE_RETURN", audit.EntityType);
            Assert.Equal(retId, audit.EntityId);
        }
    }

    [Fact]
    public async Task D13_AppendOnly_EFAndSqlRefusal()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 6m);

        var retId = await ReturnAsync(provider, f, 2m);
        Assert.NotEqual(Guid.Empty, retId);
        await ClaimAsync(provider, f, f.SupplierAId, 2m);

        // 1. EF Modification refusal
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var alloc = await db.SaleReturnSourceAllocations.FirstAsync();
            alloc.BaseQuantity = 99m;
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // 2. EF Deletion refusal
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var alloc = await db.WarrantyClaimSourceAllocations.FirstAsync();
            db.WarrantyClaimSourceAllocations.Remove(alloc);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // 3. Direct SQL Update refusal
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var alloc = await db.SaleReturnSourceAllocations.FirstAsync();
            var sqlEx = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE warranty.sale_return_source_allocations SET base_quantity = 50 WHERE id = {alloc.Id}"));
            Assert.Equal("23514", sqlEx.SqlState);
            Assert.Contains("PASS5_PROVENANCE_APPEND_ONLY", sqlEx.MessageText);
        }

        // 4. Direct SQL Delete refusal
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var alloc = await db.WarrantyClaimSourceAllocations.FirstAsync();
            var sqlEx = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM warranty.claim_source_allocations WHERE id = {alloc.Id}"));
            Assert.Equal("23514", sqlEx.SqlState);
            Assert.Contains("PASS5_PROVENANCE_APPEND_ONLY", sqlEx.MessageText);
        }
    }

    [Fact]
    public async Task D13_LegacyAmbiguity_FailsClosed()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedMultiSupplierSaleAsync(provider, TrackingMode.Quantity, 6m);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var consumption = await db.InventoryLotConsumptions.FirstAsync(x => x.MovementId == f.SaleMovementId);
            consumption.Quantity = 0.5m;
            await db.SaveChangesAsync();
        }

        await using (var testScope = provider.CreateAsyncScope())
        {
            var sales = testScope.ServiceProvider.GetRequiredService<ISalesRepository>();
            var db = testScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                sales.GetSoldSourceCapacityForUpdateAsync(f.SaleItemId, default));
            Assert.Equal("sales.source_reconciliation_required", ex.Code);
            await tx.RollbackAsync();
        }
    }

    private sealed record MultiLineSameProductFixture(
        Guid ProductId, Guid ProductUnitId, Guid ActorId, Guid CustomerId,
        Guid SupplierAId, Guid SupplierBId,
        Guid SaleId, Guid SaleItem1Id, Guid SaleItem2Id,
        Guid SaleMovement1Id, Guid SaleMovement2Id,
        Guid PurchaseAId, Guid PurchaseBId,
        decimal Line1Quantity, decimal Line2Quantity);

    private static async Task<MultiLineSameProductFixture> SeedMultiLineSameProductSaleAsync(
        ServiceProvider provider, TrackingMode mode, decimal line1Qty, decimal line2Qty)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode;
        product.DefaultWarrantyMonths = 12;
        if (mode == TrackingMode.Length)
        {
            (await db.Units.SingleAsync(x => x.Id == seed.UnitId)).DisplayDecimalPlaces = 4;
        }

        var suffix2 = Guid.NewGuid().ToString("N")[..8];
        var unit2 = new Unit
        {
            Name = "Unit2-" + suffix2,
            Symbol = "u2-" + suffix2,
            DisplayDecimalPlaces = mode == TrackingMode.Length ? 4 : 0
        };
        db.Units.Add(unit2);
        var productUnit2 = new ProductUnit
        {
            ProductId = seed.ProductId,
            UnitId = unit2.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = false,
            IsDefaultSaleUnit = false,
            IsActive = true
        };
        db.ProductUnits.Add(productUnit2);

        await db.SaveChangesAsync();
        var supplierB = await Phase2PostgresTestHarness.SeedSupplierAsync(db, "D13-multi-B", "MB");
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);

        async Task<Guid> PurchaseAsync(Guid supplierId, Guid puId, decimal quantity)
        {
            var result = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                supplierId, "D13-ML-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(seed.ProductId, puId, quantity, 100m, 150m, [])],
                InitialPaymentAmount: 0m), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            db.ChangeTracker.Clear();
            return result.Value!.PurchaseId;
        }

        var purchaseA = await PurchaseAsync(seed.SupplierId, seed.ProductUnitId, line1Qty);
        var purchaseB = await PurchaseAsync(supplierB.Id, productUnit2.Id, line2Qty);

        var sold = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, seed.ActorId, null, 0m, SalePaymentMethod.Bank, (line1Qty + line2Qty) * 150m, "D13-ML", null,
            [
                new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, line1Qty, 150m, []),
                new CompleteSaleLineInput(seed.ProductId, productUnit2.Id, line2Qty, 150m, [])
            ]), default);
        Assert.True(sold.IsSuccess, sold.Error?.Message);
        db.ChangeTracker.Clear();

        var items = await db.SaleItems.AsNoTracking().Where(x => x.SaleId == sold.Value!.SaleId).ToListAsync();
        Assert.Equal(2, items.Count);
        var item1 = items.Single(x => x.ProductUnitId == seed.ProductUnitId);
        var item2 = items.Single(x => x.ProductUnitId == productUnit2.Id);

        return new(seed.ProductId, seed.ProductUnitId, seed.ActorId, customer.Id,
            seed.SupplierId, supplierB.Id,
            sold.Value!.SaleId, item1.Id, item2.Id,
            item1.InventoryMovementId, item2.InventoryMovementId,
            purchaseA, purchaseB, line1Qty, line2Qty);
    }

    private static async Task<QuantityProductFixture> SeedReplacementProductWithStockAsync(
        ServiceProvider provider, decimal stockQty = 10m)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 150m);
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "D13-REPL-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, stockQty, 100m, 150m, [])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        return seed;
    }

    private static CommercialExchangeCommand ExchangeCommand(
        SoldSourceFixture f, QuantityProductFixture repl, decimal returnQty, decimal replQty = 1m, Guid? clientOpId = null)
    {
        var netDiff = (replQty * 150m) - (returnQty * 150m);
        return new(clientOpId ?? Guid.NewGuid(), f.SaleId, f.ActorId, null, f.CustomerId,
            "EXCHANGE", "D13 source exchange",
            [new SaleReturnLineInput(f.SaleItemId, returnQty, SaleReturnDisposition.RestockSellable, [])],
            [new CompleteSaleLineInput(repl.ProductId, repl.ProductUnitId, replQty, 150m, [])],
            0m, SalePaymentMethod.Other, netDiff > 0 ? netDiff : 0m, "EXCHANGE-REF");
    }

    private sealed record SoldSourceFixture(
        Guid ProductId, Guid ActorId, Guid CustomerId, Guid SupplierAId, Guid SupplierBId,
        Guid SaleId, Guid SaleItemId, Guid SaleMovementId, Guid PurchaseAId, Guid PurchaseBId,
        decimal SourceAQuantity, Guid ProductUnitId);

    private static async Task<SoldSourceFixture> SeedMultiSupplierSaleAsync(
        ServiceProvider provider, TrackingMode mode, decimal sourceAQuantity)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        product.TrackingMode = mode;
        product.DefaultWarrantyMonths = 12;
        if (mode == TrackingMode.Length)
        {
            (await db.Units.SingleAsync(x => x.Id == seed.UnitId)).DisplayDecimalPlaces = 4;
        }
        await db.SaveChangesAsync();
        var supplierB = await Phase2PostgresTestHarness.SeedSupplierAsync(db, "D13-source-B", "DB");
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db);

        async Task<Guid> PurchaseAsync(Guid supplierId, decimal quantity)
        {
            var result = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                supplierId, "D13-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, quantity, 100m, 150m, [])],
                InitialPaymentAmount: 0m), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            db.ChangeTracker.Clear();
            return result.Value!.PurchaseId;
        }

        var purchaseA = await PurchaseAsync(seed.SupplierId, sourceAQuantity);
        var purchaseB = await PurchaseAsync(supplierB.Id, 10m - sourceAQuantity);
        var sold = await services.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), customer.Id, seed.ActorId, null, 0m, SalePaymentMethod.Bank, 1500m, "D13", null,
            [new CompleteSaleLineInput(seed.ProductId, seed.ProductUnitId, 10m, 150m, [])]), default);
        Assert.True(sold.IsSuccess, sold.Error?.Message);
        db.ChangeTracker.Clear();
        var item = await db.SaleItems.AsNoTracking().SingleAsync(x => x.SaleId == sold.Value!.SaleId);
        Assert.Equal(10m, item.BaseQuantity);
        Assert.NotNull(item.WarrantyValidUntil);
        var sources = await (
            from consumption in db.InventoryLotConsumptions.AsNoTracking()
            join lot in db.InventoryLots.AsNoTracking() on consumption.LotId equals lot.Id
            join purchaseItem in db.PurchaseItems.AsNoTracking() on lot.PurchaseItemId equals purchaseItem.Id
            join purchase in db.Purchases.AsNoTracking() on purchaseItem.PurchaseId equals purchase.Id
            where consumption.MovementId == item.InventoryMovementId
            orderby consumption.Id
            select new { purchase.SupplierId, consumption.Quantity }).ToArrayAsync();
        Assert.Equal(sourceAQuantity, sources.Where(x => x.SupplierId == seed.SupplierId).Sum(x => x.Quantity));
        Assert.Equal(10m - sourceAQuantity, sources.Where(x => x.SupplierId == supplierB.Id).Sum(x => x.Quantity));
        Assert.Equal(10m, sources.Sum(x => x.Quantity));
        Assert.Equal(2, sources.Length);
        Assert.Equal(0, await db.InventoryUnits.CountAsync(x => x.ProductId == seed.ProductId));
        // A/B in these assertions follow persisted consumption.Id order, the repository's return order.
        // Receipt order is deliberately not used as evidence of which supplier a prior return consumes.
        var firstSource = sources[0];
        var secondSource = sources[1];
        Assert.NotEqual(firstSource.SupplierId, secondSource.SupplierId);
        return new(seed.ProductId, seed.ActorId, customer.Id, firstSource.SupplierId, secondSource.SupplierId,
            sold.Value!.SaleId, item.Id, item.InventoryMovementId, purchaseA, purchaseB, firstSource.Quantity, seed.ProductUnitId);
    }

    private static CreateWarrantyClaimCommand ClaimCommand(SoldSourceFixture f, Guid supplierId, decimal quantity) =>
        new(f.CustomerId, f.SaleId, supplierId, f.ActorId,
            [new WarrantyClaimItemInput(f.ProductId, quantity, "D13 canonical source fault", f.SaleItemId)], Guid.NewGuid());

    private static async Task ClaimAsync(ServiceProvider provider, SoldSourceFixture f, Guid supplierId, decimal quantity)
    {
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>().HandleAsync(
            ClaimCommand(f, supplierId, quantity), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var claim = await db.WarrantyClaims.AsNoTracking().SingleAsync(x => x.Id == result.Value);
        Assert.Equal(supplierId, claim.SupplierId);
        Assert.Equal(f.CustomerId, claim.CustomerId);
        Assert.Equal(f.SaleId, claim.OriginalSaleId);
        Assert.Equal(f.ActorId, claim.CreatedBy);
        Assert.Equal(WarrantyClaimStatus.Received, claim.Status);
        Assert.Equal(quantity, await db.WarrantyClaimItems.AsNoTracking().Where(x => x.ClaimId == result.Value).SumAsync(x => x.Quantity));
        Assert.All(await db.WarrantyClaimItems.AsNoTracking().Where(x => x.ClaimId == result.Value).ToArrayAsync(),
            x => Assert.Equal(f.SaleItemId, x.OriginalSaleItemId));
    }

    private static CreateSaleReturnCommand ReturnCommand(SoldSourceFixture f, decimal quantity) =>
        new(f.SaleId, "DEFECTIVE", "D13 source capacity", RefundMethod.Bank, f.ActorId, Guid.NewGuid(),
            [new SaleReturnLineInput(f.SaleItemId, quantity, SaleReturnDisposition.RestockSellable, [])]);

    private static async Task<Guid> ReturnAsync(ServiceProvider provider, SoldSourceFixture f, decimal quantity)
    {
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>().HandleAsync(ReturnCommand(f, quantity), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!.SaleReturnId;
    }

    private static async Task AssertClaimedQuantityAsync(ServiceProvider provider, SoldSourceFixture f, Guid supplierId, decimal expected)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var actual = await (from item in db.WarrantyClaimItems.AsNoTracking()
                            join claim in db.WarrantyClaims.AsNoTracking() on item.ClaimId equals claim.Id
                            where item.OriginalSaleItemId == f.SaleItemId && claim.SupplierId == supplierId
                            select item.Quantity).SumAsync();
        Assert.Equal(expected, actual);
    }

    private static async Task AssertReturnedSupplierQuantityAsync(
        ServiceProvider provider, SoldSourceFixture f, Guid returnId, Guid supplierId, decimal expected)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var sources = await (from lot in db.InventoryLots.AsNoTracking()
                             join movement in db.InventoryMovements.AsNoTracking() on lot.SourceMovementId equals movement.Id
                             join item in db.PurchaseItems.AsNoTracking() on lot.PurchaseItemId equals item.Id
                             join purchase in db.Purchases.AsNoTracking() on item.PurchaseId equals purchase.Id
                             where lot.ProductId == f.ProductId && movement.MovementType == InventoryMovementType.SaleReturn
                                 && movement.ReferenceType == "SALE_RETURN" && movement.ReferenceId == returnId
                             select new { purchase.SupplierId, lot.ReceivedQuantity }).ToArrayAsync();
        Assert.NotEmpty(sources);
        Assert.All(sources, x => Assert.Equal(supplierId, x.SupplierId));
        Assert.Equal(expected, sources.Sum(x => x.ReceivedQuantity));
        Assert.Equal(expected, await db.SaleReturnItems.AsNoTracking().Where(x => x.SaleReturnId == returnId).SumAsync(x => x.BaseQuantity));
    }

    private static async Task<string> OriginalSourceSnapshotAsync(ServiceProvider provider, SoldSourceFixture f)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var originalLotIds = await db.InventoryLotConsumptions.AsNoTracking().Where(x => x.MovementId == f.SaleMovementId)
            .Select(x => x.LotId).ToArrayAsync();
        return JsonSerializer.Serialize(new
        {
            SaleItem = await db.SaleItems.AsNoTracking().SingleAsync(x => x.Id == f.SaleItemId),
            Consumptions = await db.InventoryLotConsumptions.AsNoTracking().Where(x => x.MovementId == f.SaleMovementId).OrderBy(x => x.Id).ToArrayAsync(),
            Lots = await db.InventoryLots.AsNoTracking().Where(x => originalLotIds.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(),
            Purchases = await db.Purchases.AsNoTracking().Where(x => x.Id == f.PurchaseAId || x.Id == f.PurchaseBId).OrderBy(x => x.Id).ToArrayAsync(),
            PurchaseItems = await db.PurchaseItems.AsNoTracking().Where(x => x.PurchaseId == f.PurchaseAId || x.PurchaseId == f.PurchaseBId).OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    private static async Task<string> CanonicalStateSnapshotAsync(ServiceProvider provider, SoldSourceFixture f)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var claimIds = db.WarrantyClaims.Where(x => x.OriginalSaleId == f.SaleId).Select(x => x.Id);
        var movementIds = db.InventoryMovements.Where(x => x.ProductId == f.ProductId).Select(x => x.Id);
        var lotIds = db.InventoryLots.Where(x => x.ProductId == f.ProductId).Select(x => x.Id);
        // Failure outcome/document reservation evidence may grow on refusal; canonical economic facts may not.
        return JsonSerializer.Serialize(new
        {
            Claims = await db.WarrantyClaims.AsNoTracking().Where(x => x.OriginalSaleId == f.SaleId).OrderBy(x => x.Id).ToArrayAsync(),
            ClaimItems = await db.WarrantyClaimItems.AsNoTracking().Where(x => claimIds.Contains(x.ClaimId)).OrderBy(x => x.Id).ToArrayAsync(),
            ClaimSources = await db.WarrantyClaimSourceAllocations.AsNoTracking().Where(x => db.WarrantyClaimItems.Any(i => i.Id == x.ClaimItemId && i.OriginalSaleItemId == f.SaleItemId)).OrderBy(x => x.Id).ToArrayAsync(),
            ReturnSources = await db.SaleReturnSourceAllocations.AsNoTracking().Where(x => db.SaleReturnItems.Any(i => i.Id == x.SaleReturnItemId && i.SaleItemId == f.SaleItemId)).OrderBy(x => x.Id).ToArrayAsync(),
            Events = await db.WarrantyClaimEvents.AsNoTracking().Where(x => claimIds.Contains(x.ClaimId)).OrderBy(x => x.Id).ToArrayAsync(),
            Returns = await db.SaleReturns.AsNoTracking().Where(x => x.SaleId == f.SaleId).OrderBy(x => x.Id).ToArrayAsync(),
            ReturnItems = await db.SaleReturnItems.AsNoTracking().Where(x => x.SaleItemId == f.SaleItemId).OrderBy(x => x.Id).ToArrayAsync(),
            Movements = await db.InventoryMovements.AsNoTracking().Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync(),
            Effects = await db.InventoryMovementEffects.AsNoTracking().Where(x => movementIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            Consumptions = await db.InventoryLotConsumptions.AsNoTracking().Where(x => movementIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            Lots = await db.InventoryLots.AsNoTracking().Where(x => x.ProductId == f.ProductId).OrderBy(x => x.Id).ToArrayAsync(),
            LotBalances = await db.InventoryLotBucketBalances.AsNoTracking().Where(x => lotIds.Contains(x.LotId)).OrderBy(x => x.Id).ToArrayAsync(),
            Balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == f.ProductId),
            Cost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == f.ProductId)
        });
    }

    // =========================================================================
    // D17: NET PROFIT & OPERATING RECOVERY REPORTING INTEGRATION TESTS
    // =========================================================================

    [Fact]
    public async Task D17_WarrantyRecoveryLoss_IncreasesInventoryLossAndReducesNetProfit()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        Guid productId, actorId, supplierId, purchaseItemId;
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            productId = seed.ProductId;
            actorId = seed.ActorId;
            supplierId = seed.SupplierId;

            var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new CreatePurchaseCommand(
                    supplierId, "D17-ARRANGE-" + Guid.NewGuid(), today, null, 0m,
                    PurchaseSettlementMode.External, actorId, Guid.NewGuid(),
                    [new CreatePurchaseLineInput(productId, seed.ProductUnitId, 10m, 100m, 150m, [])],
                    InitialPaymentAmount: 0m), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);

            var item = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == purchase.Value!.PurchaseId);
            purchaseItemId = item.Id;

            var damaged = await scope.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
                new TransferInventoryConditionCommand(productId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, actorId, "Damaged for test"), default);
            Assert.True(damaged.IsSuccess, damaged.Error?.Message);
        }

        Guid caseId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var send = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new SendShopStockToSupplierWarrantyCommand(productId, InventoryBucket.Damaged, 1m, supplierId, purchaseItemId, "Send shop warranty", actorId, Guid.NewGuid(), []), default);
            Assert.True(send.IsSuccess, send.Error?.Message);
            caseId = send.Value;
        }

        ReportingSnapshotDto beforeSnapshot;
        await using (var scope = provider.CreateAsyncScope())
        {
            beforeSnapshot = await scope.ServiceProvider.GetRequiredService<IReportingReadService>()
                .GetSnapshotAsync(ReportingPeriodKind.Daily, today, today.Month, today.Year, default);
        }

        // Credited at 60m (carrying value was 100m -> deficit recognized loss = 40m)
        await using (var scope = provider.CreateAsyncScope())
        {
            var resolve = await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
                new ReceiveShopStockWarrantyCommand(caseId, WarrantyResolutionType.Credited, actorId, null, null, "Credit shortfall", Guid.NewGuid(),
                    SupplierCreditAmount: 60m, ResolvedQuantity: 1m), default);
            Assert.True(resolve.IsSuccess, resolve.Error?.Message);
        }

        ReportingSnapshotDto afterSnapshot;
        await using (var scope = provider.CreateAsyncScope())
        {
            afterSnapshot = await scope.ServiceProvider.GetRequiredService<IReportingReadService>()
                .GetSnapshotAsync(ReportingPeriodKind.Daily, today, today.Month, today.Year, default);
        }

        Assert.Equal(beforeSnapshot.WarrantyRecoveryGain, afterSnapshot.WarrantyRecoveryGain);
        Assert.Equal(beforeSnapshot.InventoryLoss + 40m, afterSnapshot.InventoryLoss);
        Assert.Equal(beforeSnapshot.NetSales, afterSnapshot.NetSales);
        Assert.Equal(beforeSnapshot.GrossProfit, afterSnapshot.GrossProfit);
        Assert.Equal(beforeSnapshot.NetProfit - 40m, afterSnapshot.NetProfit);
    }

    [Fact]
    public async Task D17_WarrantyRecoveryGain_IncreasesNetProfitWithoutCreatingFakeSales()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        Guid productId, actorId, supplierId, purchaseItemId;
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            productId = seed.ProductId;
            actorId = seed.ActorId;
            supplierId = seed.SupplierId;

            var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new CreatePurchaseCommand(
                    supplierId, "D17-ARRANGE-" + Guid.NewGuid(), today, null, 0m,
                    PurchaseSettlementMode.External, actorId, Guid.NewGuid(),
                    [new CreatePurchaseLineInput(productId, seed.ProductUnitId, 10m, 100m, 150m, [])],
                    InitialPaymentAmount: 0m), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);

            var item = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == purchase.Value!.PurchaseId);
            purchaseItemId = item.Id;

            var damaged = await scope.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
                new TransferInventoryConditionCommand(productId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, actorId, "Damaged for gain test"), default);
            Assert.True(damaged.IsSuccess, damaged.Error?.Message);
        }

        Guid caseId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var send = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new SendShopStockToSupplierWarrantyCommand(productId, InventoryBucket.Damaged, 1m, supplierId, purchaseItemId, "Send shop warranty", actorId, Guid.NewGuid(), []), default);
            Assert.True(send.IsSuccess, send.Error?.Message);
            caseId = send.Value;
        }

        ReportingSnapshotDto beforeSnapshot;
        await using (var scope = provider.CreateAsyncScope())
        {
            beforeSnapshot = await scope.ServiceProvider.GetRequiredService<IReportingReadService>()
                .GetSnapshotAsync(ReportingPeriodKind.Daily, today, today.Month, today.Year, default);
        }

        // Credited at 140m (carrying value was 100m -> recovery gain = 40m)
        await using (var scope = provider.CreateAsyncScope())
        {
            var resolve = await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
                new ReceiveShopStockWarrantyCommand(caseId, WarrantyResolutionType.Credited, actorId, null, null, "Credit surplus", Guid.NewGuid(),
                    SupplierCreditAmount: 140m, ResolvedQuantity: 1m), default);
            Assert.True(resolve.IsSuccess, resolve.Error?.Message);
        }

        ReportingSnapshotDto afterSnapshot;
        await using (var scope = provider.CreateAsyncScope())
        {
            afterSnapshot = await scope.ServiceProvider.GetRequiredService<IReportingReadService>()
                .GetSnapshotAsync(ReportingPeriodKind.Daily, today, today.Month, today.Year, default);
        }

        Assert.Equal(beforeSnapshot.WarrantyRecoveryGain + 40m, afterSnapshot.WarrantyRecoveryGain);
        Assert.Equal(beforeSnapshot.InventoryLoss, afterSnapshot.InventoryLoss);
        Assert.Equal(beforeSnapshot.NetSales, afterSnapshot.NetSales);
        Assert.Equal(beforeSnapshot.NetCOGS, afterSnapshot.NetCOGS);
        Assert.Equal(beforeSnapshot.GrossProfit, afterSnapshot.GrossProfit);
        Assert.Equal(beforeSnapshot.NetProfit + 40m, afterSnapshot.NetProfit);
    }

    [Fact]
    public async Task D17_FoundRecoveryGain_DoesNotEraseHistoricalLoss_AndIncreasesNetProfit()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        Guid productId, actorId, supplierId, unitId;
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            var seed = await Phase2PostgresTestHarness.SeedSerializedProductAsync(db, defaultSalePrice: 200m);
            productId = seed.ProductId;
            actorId = seed.ActorId;
            supplierId = seed.SupplierId;

            var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
                new CreatePurchaseCommand(
                    supplierId, "D17-ARRANGE-" + Guid.NewGuid(), today, null, 0m,
                    PurchaseSettlementMode.External, actorId, Guid.NewGuid(),
                    [new CreatePurchaseLineInput(productId, seed.ProductUnitId, 1m, 100m, 200m,
                        [new SerializedIdentityInput("D17-FOUND-" + Guid.NewGuid())])],
                    InitialPaymentAmount: 0m), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);

            var unit = await db.InventoryUnits.AsNoTracking().SingleAsync(x => x.ProductId == productId);
            unitId = unit.Id;
        }

        // Post shortage (Lost) -> recognized loss = 100m
        Guid missingOpId = Guid.NewGuid();
        Guid missingMovementId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var shortage = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
                new CreateStockAdjustmentCommand(
                    StockAdjustmentMode.Delta, StockAdjustmentReason.Lost,
                    [new StockAdjustmentItemCommand(productId, null, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, 1m, null,
                        InventoryUnitIds: [unitId])],
                    actorId, missingOpId), default);
            Assert.True(shortage.IsSuccess, shortage.Error?.Message);

            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var m = await db.InventoryMovements.AsNoTracking().SingleAsync(x => x.CorrelationId == missingOpId);
            missingMovementId = m.Id;
        }

        ReportingSnapshotDto afterLostSnapshot;
        await using (var scope = provider.CreateAsyncScope())
        {
            afterLostSnapshot = await scope.ServiceProvider.GetRequiredService<IReportingReadService>()
                .GetSnapshotAsync(ReportingPeriodKind.Daily, today, today.Month, today.Year, default);
        }
        Assert.True(afterLostSnapshot.InventoryLoss >= 100m);
        Assert.Equal(0m, afterLostSnapshot.InventoryLossRecoveryGain);

        // Recover found unit
        await using (var scope = provider.CreateAsyncScope())
        {
            var found = await scope.ServiceProvider.GetRequiredService<FoundInventoryUnitHandler>().HandleAsync(
                new FoundInventoryUnitCommand(productId, unitId, missingMovementId, InventoryBucket.Sellable, "Physically found behind shelf", actorId, Guid.NewGuid()), default);
            Assert.True(found.IsSuccess, found.Error?.Message);
        }

        ReportingSnapshotDto afterFoundSnapshot;
        await using (var scope = provider.CreateAsyncScope())
        {
            afterFoundSnapshot = await scope.ServiceProvider.GetRequiredService<IReportingReadService>()
                .GetSnapshotAsync(ReportingPeriodKind.Daily, today, today.Month, today.Year, default);
        }

        // Historical loss is preserved (not erased!)
        Assert.Equal(afterLostSnapshot.InventoryLoss, afterFoundSnapshot.InventoryLoss);
        // Explicit recovery gain recognized
        Assert.True(afterFoundSnapshot.InventoryLossRecoveryGain >= 100m);
        // Net sales not corrupted
        Assert.Equal(afterLostSnapshot.NetSales, afterFoundSnapshot.NetSales);
        // Gross profit not corrupted
        Assert.Equal(afterLostSnapshot.GrossProfit, afterFoundSnapshot.GrossProfit);
        // Net profit increases by the recovery gain
        Assert.Equal(afterLostSnapshot.NetProfit + 100m, afterFoundSnapshot.NetProfit);
    }
}
