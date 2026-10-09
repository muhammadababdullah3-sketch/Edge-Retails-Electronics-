using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace EdgeRetails.IntegrationTests;

// NEW_COVERAGE: canonical production DI/handlers, isolated PG18 attested by the harness.
// Each rejection compares fresh persisted business state, allowing only an explicit failed diagnostic outcome.
[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass2HostileNumericPostgresTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(6)]
    [InlineData(0)]
    [InlineData(10)]
    public async Task F01_Quantity_TenToTarget_PersistsTargetAndExactLoss(int target)
    {
        await using var p = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(p, TrackingMode.Quantity, 1, [100m], 10m);
        await using var scope = p.CreateAsyncScope();
        var operation = Guid.NewGuid();
        var r = await scope.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            Adjustment(f, StockAdjustmentMode.SetPhysicalCount, target, [], operation), default);
        Assert.True(r.IsSuccess, r.Error?.Code + ":" + r.Error?.Message);
        await using var v = p.CreateAsyncScope();
        var db = v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await AssertPoolAsync(db, f.ProductId, target, target * 100m);
        Assert.Equal(target, await LotQuantityAsync(db, f.ProductId, InventoryBucket.Sellable));
        var moves = await db.InventoryMovements.Where(x => x.CorrelationId == operation).ToListAsync();
        Assert.Equal((10 - target) * 100m, moves.Sum(x => x.RecognizedLossAmount));
        if (target == 10)
        {
            Assert.Empty(moves);
            Assert.Equal(1000m, (await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId)).TotalInventoryCost);
        }
        output.WriteLine($"F01 persisted stock10 -> target{target} -> stock{target}; cost{target * 100}; recognized loss{(10-target)*100}.");
    }

    [Fact]
    public async Task F01_Delta_RemainsRequestedBaseDelta_AndQuantityPositiveCountUsesDifference()
    {
        await using var p = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(p, TrackingMode.Quantity, 1, [100m], 10m);
        await using var s = p.CreateAsyncScope();
        var h = s.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>();
        var decrease = await h.HandleAsync(Adjustment(f, StockAdjustmentMode.Delta, 2m, [], Guid.NewGuid()), default);
        Assert.True(decrease.IsSuccess, decrease.Error?.Message);
        await using(var v = p.CreateAsyncScope())
        {
            await AssertPoolAsync(v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(), f.ProductId, 8m, 800m);
        }
        // A current8 -> target10 increase adds2, despite direction hint Decrease.
        var count = await h.HandleAsync(Adjustment(f, StockAdjustmentMode.SetPhysicalCount, 10m, [], Guid.NewGuid()) with
        { Items = [new(f.ProductId, f.UnitId, StockAdjustmentDirection.Decrease, InventoryBucket.Sellable, 10m, 100m)] }, default);
        Assert.True(count.IsSuccess, count.Error?.Message);
        await using var verify = p.CreateAsyncScope();
        await AssertPoolAsync(verify.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(), f.ProductId, 10m, 1000m);
    }

    [Fact]
    public async Task F01_D_ADJ_1_Container_ThreeTimes50_ToTargetTwo_PreservesExactIdentity()
    {
        await using var p = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(p, TrackingMode.Container, 50m, [10000m, 12000m, 9000m]);
        var ids = f.Units.Select(x => x.Id).ToArray();
        var sequence = await SequenceAsync(p, f);
        var op = Guid.NewGuid();
        await using var s = p.CreateAsyncScope();
        var r = await s.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            Adjustment(f, StockAdjustmentMode.SetPhysicalCount, 2m, [ids[0]], op) with { Reason = StockAdjustmentReason.Lost }, default);
        Assert.True(r.IsSuccess, r.Error?.Code + ":" + r.Error?.Message);
        await using var v = p.CreateAsyncScope();
        var db = v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await AssertPoolAsync(db, f.ProductId, 100m, 21000m);
        var units = await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).OrderBy(x => x.ItemSequence).ToListAsync();
        Assert.Equal(3, units.Count);
        Assert.Equal(2, units.Count(x => x.Status == InventoryUnitStatus.InStock));
        Assert.Equal(InventoryUnitStatus.Missing, units.Single(x => x.Id == ids[0]).Status);
        Assert.Equal(f.Units.Select(x => (x.Id, x.TrackingCode, x.ItemSequence, x.AcquisitionCost)),
            units.Select(x => (x.Id, x.TrackingCode, x.ItemSequence, x.AcquisitionCost)));
        var move = await db.InventoryMovements.SingleAsync(x => x.CorrelationId == op);
        var effect = await db.InventoryMovementEffects.SingleAsync(x => x.MovementId == move.Id);
        Assert.Equal(-50m, effect.QuantityDelta);
        Assert.Equal(150m, effect.QuantityBefore);
        Assert.Equal(100m, effect.QuantityAfter);
        Assert.Single(await db.InventoryMovementUnits.Where(x => x.MovementId == move.Id).ToListAsync());
        Assert.Equal(10000m, move.RecognizedLossAmount);
        Assert.Equal(100m, await LotQuantityAsync(db, f.ProductId, InventoryBucket.Sellable));
        Assert.Equal(sequence, await SequenceAsync(p, f));
        output.WriteLine("Container count3/base150 -> target count2/base100; physical delta-1/base delta-50; loss10000; TrackingCode/sequence unchanged.");
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task F01_NonPhysical_TargetUsesSelectedProductUnitFactor(TrackingMode mode)
    {
        await using var p=Phase2PostgresTestHarness.BuildProvider();
        var f=await SeedAsync(p,mode,2m,[100m],5m);
        await using var s=p.CreateAsyncScope();
        var r=await s.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            Adjustment(f,StockAdjustmentMode.SetPhysicalCount,3m,[],Guid.NewGuid()),default);
        Assert.True(r.IsSuccess,r.Error?.Message);
        await using var v=p.CreateAsyncScope();
        await AssertPoolAsync(v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(),f.ProductId,6m,300m);
    }

    [Theory]
    [InlineData(TrackingMode.Serialized)]
    [InlineData(TrackingMode.IndividualPiece)]
    [InlineData(TrackingMode.Container)]
    public async Task F01_Physical_PositiveFailsClosed_ZeroRequiresExactIds_AndZeroMarksMissingAll(TrackingMode mode)
    {
        await using var p = Phase2PostgresTestHarness.BuildProvider();
        var factor = mode == TrackingMode.Container ? 50m : 1m;
        var f = await SeedAsync(p, mode, factor, [10000m, 12000m]);
        var before = await StateAsync(p, f);
        await using var s = p.CreateAsyncScope();
        var h = s.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>();
        var positive = await h.HandleAsync(Adjustment(f, StockAdjustmentMode.SetPhysicalCount, 3m, [], Guid.NewGuid()), default);
        Assert.Equal("inventory.physical_positive_adjustment_unsupported", positive.Error?.Code);
        Assert.Equal(before, await StateAsync(p, f));
        var missing = await h.HandleAsync(Adjustment(f, StockAdjustmentMode.SetPhysicalCount, 0m, [], Guid.NewGuid()), default);
        Assert.Equal("inventory.serialized_exact_units_required", missing.Error?.Code);
        Assert.Equal(before, await StateAsync(p, f));
        var result = await h.HandleAsync(Adjustment(f, StockAdjustmentMode.SetPhysicalCount, 0m,
            f.Units.Select(x => x.Id).ToArray(), Guid.NewGuid()) with { Reason = StockAdjustmentReason.Lost }, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        await using var v = p.CreateAsyncScope();
        var db = v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await AssertPoolAsync(db, f.ProductId, 0m, 0m);
        Assert.All(await db.InventoryUnits.Where(x => x.ProductId == f.ProductId).ToListAsync(), x => Assert.Equal(InventoryUnitStatus.Missing, x.Status));
        Assert.Equal(0m, await LotQuantityAsync(db, f.ProductId, InventoryBucket.Sellable));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other_product")]
    [InlineData("inactive")]
    [InlineData("zero_factor")]
    [InlineData("changed_factor")]
    public async Task D_ADJ_1_Container_InvalidProductUnitOrChangedFactor_LeavesBusinessUnchanged(string kind)
    {
        await using var p = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(p, TrackingMode.Container, 50m, [10000m]);
        Guid? unitId = f.UnitId;
        await using var s = p.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        if(kind == "missing") { unitId = null; }
        if(kind == "other_product") { unitId = (await Phase2PostgresTestHarness.SeedQuantityProductAsync(db)).ProductUnitId; }
        if(kind is "inactive" or "zero_factor" or "changed_factor")
        {
            var u = await db.ProductUnits.SingleAsync(x => x.Id == f.UnitId);
            if (kind == "zero_factor")
            {
                var stable = await StateAsync(p, f);
                u.FactorToBaseUnit = 0m;
                var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Contains("ck_product_units_factor_positive", exception.InnerException?.Message);
                db.ChangeTracker.Clear();
                Assert.Equal(50m, (await db.ProductUnits.SingleAsync(x => x.Id == f.UnitId)).FactorToBaseUnit);
                Assert.Equal(stable, await StateAsync(p, f));
                output.WriteLine("Zero factor refused by canonical PostgreSQL CHECK before any operation; original factor50 retained.");
                return;
            }
            if(kind == "inactive") { u.IsActive = false; }
            else { u.FactorToBaseUnit = kind == "zero_factor" ? 0m : 25m; }
            await db.SaveChangesAsync();
        }
        var before = await StateAsync(p, f);
        var item = new StockAdjustmentItemCommand(f.ProductId, unitId, StockAdjustmentDirection.Decrease,
            InventoryBucket.Sellable, 50m, null, InventoryUnitIds: [f.Units[0].Id]);
        var r = await s.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            new(StockAdjustmentMode.Delta, StockAdjustmentReason.PhysicalCountCorrection, [item], f.ActorId, Guid.NewGuid()), default);
        Assert.False(r.IsSuccess);
        Assert.Equal(before, await StateAsync(p, f));
        output.WriteLine($"Container invalid authority {kind}: {r.Error?.Code}; zero persisted business effect.");
    }

    [Fact]
    public async Task F02_OneContainer50_Cost10000_PerBase200_ExactPersistedLoss()
    {
        await using var p=Phase2PostgresTestHarness.BuildProvider();
        var f=await SeedAsync(p,TrackingMode.Container,50m,[10000m]);
        Assert.Equal(200m,f.Units[0].AcquisitionCost/50m);
        await TransferAsync(p,f,InventoryBucket.Sellable,InventoryBucket.Damaged,50m,[f.Units[0].Id]);
        await TransferAsync(p,f,InventoryBucket.Damaged,InventoryBucket.Scrap,50m,[f.Units[0].Id]);
        await using var v=p.CreateAsyncScope();var db=v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(10000m,await db.InventoryMovements.Where(x=>x.ProductId==f.ProductId).SumAsync(x=>x.RecognizedLossAmount));
        Assert.Equal(0m,(await db.ProductCostStates.SingleAsync(x=>x.ProductId==f.ProductId)).TotalInventoryCost);
        Assert.Equal(50m,(await db.StockBalances.SingleAsync(x=>x.ProductId==f.ProductId)).ScrapQty);
        output.WriteLine("Single Container base50/acquisition10000 = per-base200; persisted loss10000/value0.");
    }

    [Theory]
    [InlineData(TrackingMode.Serialized)]
    [InlineData(TrackingMode.IndividualPiece)]
    [InlineData(TrackingMode.Container)]
    public async Task F02_P7N03_ExactScrap_TenThousandPlusTwelveThousand_NoDoubleRecognition(TrackingMode mode)
    {
        await using var p = Phase2PostgresTestHarness.BuildProvider();
        var factor = mode == TrackingMode.Container ? 50m : 1m;
        var f = await SeedAsync(p, mode, factor, [10000m, 12000m]);
        await TransferAsync(p, f, InventoryBucket.Sellable, InventoryBucket.Damaged, 2m * factor, f.Units.Select(x=>x.Id).ToArray());
        await TransferAsync(p, f, InventoryBucket.Damaged, InventoryBucket.Scrap, 2m * factor, f.Units.Select(x=>x.Id).ToArray());
        await using var v = p.CreateAsyncScope();
        var db = v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var cost = await db.ProductCostStates.SingleAsync(x => x.ProductId == f.ProductId);
        Assert.Equal(0m, cost.CostedQty); Assert.Equal(0m, cost.TotalInventoryCost);
        var balance = await db.StockBalances.SingleAsync(x=>x.ProductId==f.ProductId);
        Assert.Equal(0m,balance.SellableQty); Assert.Equal(0m,balance.DamagedQty); Assert.Equal(2m*factor,balance.ScrapQty);
        Assert.Equal(2m*factor,await LotQuantityAsync(db,f.ProductId,InventoryBucket.Scrap));
        var loss = await db.InventoryMovements.Where(x=>x.ProductId==f.ProductId && x.MovementType==InventoryMovementType.WriteOffToScrap).SumAsync(x=>x.RecognizedLossAmount);
        Assert.Equal(22000m,loss);
        Assert.All(await db.InventoryUnits.Where(x=>x.ProductId==f.ProductId).ToListAsync(),x=>Assert.Equal(InventoryUnitStatus.Scrapped,x.Status));
        var rule=InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.Scrapped);
        Assert.False(rule.ContributesToProductCostState); Assert.True(rule.IsTerminal);
        var before=await StateAsync(p,f);
        await using var s=p.CreateAsyncScope();
        var again=await s.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new(f.ProductId,InventoryBucket.Damaged,InventoryBucket.Scrap,2m*factor,f.ActorId,"Repeat",InventoryUnitIds:f.Units.Select(x=>x.Id).ToArray()),default);
        Assert.False(again.IsSuccess); Assert.Equal(before,await StateAsync(p,f));
        output.WriteLine($"{mode}: acquisition10000+12000 removed22000, loss22000, remaining inventory0; repeat zero business effect. First pack per-base={10000m/factor}.");
    }

    [Theory]
    [MemberData(nameof(ConditionVectors))]
    public async Task F03_RawFractionsAndWrongExactSelections_LeaveEveryBusinessRowUnchanged(TrackingMode mode, string vector)
    {
        await using var p=Phase2PostgresTestHarness.BuildProvider();
        var factor = mode == TrackingMode.Container ? 50m : 1m;
        var f=await SeedAsync(p,mode,factor,[10000m,12000m]);
        var quantity=vector is "too_few" or "too_many" or "duplicate" ? 2m * factor : decimal.Parse(vector,System.Globalization.CultureInfo.InvariantCulture);
        var ids=vector switch {"too_few"=>new[]{f.Units[0].Id},"too_many"=>new[]{f.Units[0].Id,f.Units[1].Id,Guid.NewGuid()},"duplicate"=>new[]{f.Units[0].Id,f.Units[0].Id},_=>new[]{f.Units[0].Id}};
        var before=await StateAsync(p,f);
        await using var s=p.CreateAsyncScope();
        var r=await s.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new(f.ProductId,InventoryBucket.Sellable,InventoryBucket.Damaged,quantity,f.ActorId,"Hostile raw quantity",InventoryUnitIds:ids),default);
        Assert.False(r.IsSuccess); Assert.Equal(before,await StateAsync(p,f));
        if(decimal.TryParse(vector,out _)) { Assert.Equal("inventory.serialized_quantity_whole",r.Error?.Code); }
        Assert.False(QuantityMath.IsWhole(1.0000001m));
        output.WriteLine($"F03 {vector}: {r.Error?.Code}; exact canonical whole check before rounding; no persistence.");
    }

    public static IEnumerable<object[]> ConditionVectors =>
        from mode in new[]{TrackingMode.Serialized,TrackingMode.IndividualPiece,TrackingMode.Container}
        from vector in new[]{"1.5","2.25","1.0000001","too_few","too_many","duplicate"}
        select new object[]{mode,vector};

    [Theory]
    [MemberData(nameof(PhysicalTargetVectors))]
    public async Task F01_RawFractionalPhysicalTarget_DecreaseAndNoOpFailBeforeRounding(TrackingMode mode, string raw)
    {
        await using var p=Phase2PostgresTestHarness.BuildProvider();
        var f=await SeedAsync(p,mode,mode==TrackingMode.Container?50m:1m,[10000m,12000m]);
        var before=await StateAsync(p,f);
        var target=decimal.Parse(raw,System.Globalization.CultureInfo.InvariantCulture);
        var ids=target<2m?new[]{f.Units[0].Id}:Array.Empty<Guid>();
        await using var s=p.CreateAsyncScope();
        var r=await s.ServiceProvider.GetRequiredService<CreateStockAdjustmentHandler>().HandleAsync(
            Adjustment(f,StockAdjustmentMode.SetPhysicalCount,target,ids,Guid.NewGuid()),default);
        Assert.False(r.IsSuccess,"Raw fractional physical target must fail before it is rounded to a decrement or no-op.");
        Assert.Equal("inventory.physical_count_invalid",r.Error?.Code);
        Assert.Equal(before,await StateAsync(p,f));
        output.WriteLine($"{mode} target{raw}: rejected raw fraction before decrease/no-op normalization, all persisted business rows unchanged.");
    }

    public static IEnumerable<object[]> PhysicalTargetVectors =>
        from mode in new[]{TrackingMode.Serialized,TrackingMode.IndividualPiece,TrackingMode.Container}
        from raw in new[]{"1.000000001","2.000000001"}
        select new object[]{mode,raw};

    [Fact]
    public async Task F04_P7N03_Thaka_ContainerTwoTimes50_ExactProjectValueAndReversal_DespiteMutableUom()
    {
        await using var p=Phase2PostgresTestHarness.BuildProvider();
        var f=await SeedAsync(p,TrackingMode.Container,50m,[10000m,12000m]);
        var sequence=await SequenceAsync(p,f);
        await using var s=p.CreateAsyncScope();
        var r=await s.ServiceProvider.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(new(Guid.NewGuid(),f.ProjectId,f.ActorId,"2 packs",
            [new(f.ProductId,f.UnitId,2m,500m,f.Units.Select(x=>x.Id).ToArray())]),default);
        Assert.True(r.IsSuccess,r.Error?.Message);
        await using var v=p.CreateAsyncScope();
        var db=v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await AssertPoolAsync(db,f.ProductId,0m,0m);
        var item=await db.ThakaMaterialIssueItems.SingleAsync(x=>x.MaterialIssueId==r.Value!.MaterialIssueId);
        Assert.Equal(2m,item.EnteredQuantity);Assert.Equal(50m,item.FactorToBaseSnapshot);Assert.Equal(100m,item.BaseQuantity);
        Assert.Equal(50000m,item.LineCharge);Assert.Equal(22000m,item.TotalCostSnapshot);
        Assert.Equal(50000m,r.Value!.TotalCharge);Assert.Equal(22000m,r.Value.TotalCost);
        var issueHeader=await db.ThakaMaterialIssues.SingleAsync(x=>x.Id==r.Value.MaterialIssueId);
        Assert.Equal(50000m,issueHeader.TotalCharge);Assert.Equal(22000m,issueHeader.TotalCost);
        var projectNumber=(await db.ThakaProjects.SingleAsync(x=>x.Id==f.ProjectId)).ProjectNumber;
        var issuedPage=await v.ServiceProvider.GetRequiredService<IThakaReadService>().GetProjectsPageAsync(new(Search:projectNumber),default);
        Assert.Equal(50000m,Assert.Single(issuedPage.Items).MaterialValue);
        var units=await db.InventoryUnits.Where(x=>x.ProductId==f.ProductId).ToListAsync();
        Assert.All(units,x=>Assert.Equal(InventoryUnitStatus.IssuedThaka,x.Status));
        Assert.False(InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.IssuedThaka).ContributesToProductCostState);
        // Reversal must consume original issue and received snapshots after a legal mutable UOM edit.
        (await db.ProductUnits.SingleAsync(x=>x.Id==f.UnitId)).FactorToBaseUnit=25m;
        await db.SaveChangesAsync();
        var rev=await v.ServiceProvider.GetRequiredService<ReverseThakaMaterialHandler>().HandleAsync(
            new(Guid.NewGuid(),f.ProjectId,r.Value.MaterialIssueId,"Return exact packs",f.ActorId),default);
        Assert.True(rev.IsSuccess,rev.Error?.Message);
        await using var end=p.CreateAsyncScope();
        var endDb=end.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await AssertPoolAsync(endDb,f.ProductId,100m,22000m);
        var reversal=await endDb.ThakaMaterialReversals.SingleAsync(x=>x.MaterialIssueId==r.Value.MaterialIssueId);
        Assert.Equal(50000m,reversal.ReversedCharge);Assert.Equal(22000m,reversal.RestoredCost);
        var persistedIssue=await endDb.ThakaMaterialIssues.SingleAsync(x=>x.Id==r.Value.MaterialIssueId);
        Assert.Equal(0m,persistedIssue.TotalCharge-reversal.ReversedCharge);Assert.Equal(0m,persistedIssue.TotalCost-reversal.RestoredCost);
        Assert.Equal(100m,await LotQuantityAsync(endDb,f.ProductId,InventoryBucket.Sellable));
        var restored=await endDb.InventoryUnits.Where(x=>x.ProductId==f.ProductId).OrderBy(x=>x.ItemSequence).ToListAsync();
        Assert.All(restored,x=>Assert.Equal(InventoryUnitStatus.InStock,x.Status));
        Assert.Equal(f.Units.Select(x=>(x.Id,x.TrackingCode,x.ItemSequence,x.AcquisitionCost)),restored.Select(x=>(x.Id,x.TrackingCode,x.ItemSequence,x.AcquisitionCost)));
        Assert.Equal(sequence,await SequenceAsync(p,f));
        output.WriteLine("Thaka entered2 * factor50 = base100; charge50000; carrying cost22000; reversal restores100/22000, project charge0/cost0; original identity and sequence unchanged despite factor25 edit.");
        var finalPage=await end.ServiceProvider.GetRequiredService<IThakaReadService>().GetProjectsPageAsync(new(Search:projectNumber),default);
        Assert.Equal(0m,Assert.Single(finalPage.Items).MaterialValue);
        // Retain the real project-detail challenge, after every required persisted numeric proof.
        // Its inherited DTO materialization defect is out of scope; never mask it to claim green.
        var finalProject=await end.ServiceProvider.GetRequiredService<IThakaReadService>().GetProjectAsync(f.ProjectId,default);
        Assert.NotNull(finalProject);Assert.Equal(0m,finalProject.Project.MaterialValue);
    }

    [Theory]
    [InlineData("same_line")]
    [InlineData("same_product_cross_line")]
    [InlineData("inconsistent_product")]
    [InlineData("valid_unique")]
    public async Task P7N02_CommandWideSelection_RejectsDuplicatesBeforePartialEffects(string vector)
    {
        await using var p=Phase2PostgresTestHarness.BuildProvider();
        var f=await SeedAsync(p,TrackingMode.Serialized,1m,[10000m,12000m]);
        var before=await StateAsync(p,f);
        await using var s=p.CreateAsyncScope();
        var db=s.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var second=f.ProductId;var unitId=f.UnitId;
        if(vector=="inconsistent_product") {var other=await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);second=other.ProductId;unitId=other.ProductUnitId;}
        if(vector=="same_product_cross_line")
        {
            // A distinct valid UOM avoids the earlier duplicate ProductId/ProductUnitId
            // line guard, so the command-wide shared exact-unit guard is actually reached.
            var unit=new Unit{Name="Alternate-"+Guid.NewGuid(),Symbol="ALT",DisplayDecimalPlaces=0};
            var alternate=new ProductUnit{ProductId=f.ProductId,UnitId=unit.Id,FactorToBaseUnit=1m,IsActive=true,CanUseInThaka=true};
            db.Units.Add(unit);db.ProductUnits.Add(alternate);await db.SaveChangesAsync();unitId=alternate.Id;
        }
        IReadOnlyList<IssueThakaMaterialLineInput> lines=vector switch
        {
            "same_line"=>[new(f.ProductId,f.UnitId,2m,500m,[f.Units[0].Id,f.Units[0].Id])],
            "valid_unique"=>[new(f.ProductId,f.UnitId,2m,500m,f.Units.Select(x=>x.Id).ToArray())],
            _=>[new(f.ProductId,f.UnitId,1m,500m,[f.Units[0].Id]),new(second,unitId,1m,500m,[f.Units[0].Id])]
        };
        var op=Guid.NewGuid();
        var r=await s.ServiceProvider.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(new(op,f.ProjectId,f.ActorId,"command-wide IDs",lines),default);
        if(vector=="valid_unique")
        {
            Assert.True(r.IsSuccess,r.Error?.Message);
            await using var v=p.CreateAsyncScope();var verify=v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await AssertPoolAsync(verify,f.ProductId,0m,0m);
            Assert.Equal(2,await verify.ThakaMaterialIssueUnits.CountAsync(x=>f.Units.Select(u=>u.Id).Contains(x.InventoryUnitId)));
        }
        else
        {
            Assert.Equal("thaka.serial_selection_invalid",r.Error?.Code);
            Assert.Equal(before,await StateAsync(p,f));
            await using var v=p.CreateAsyncScope();var verify=v.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcome=await verify.OperationOutcomes.SingleAsync(x=>x.ClientOperationId==op);
            Assert.False(outcome.WasCommitted);Assert.Equal("thaka.serial_selection_invalid",outcome.ErrorCode);
        }
        output.WriteLine($"Duplicate vector {vector}: success={r.IsSuccess}; rejection precedes persisted business effects; only failed diagnostic outcome allowed.");
    }

    private static CreateStockAdjustmentCommand Adjustment(Fixture f,StockAdjustmentMode mode,decimal quantity,IReadOnlyList<Guid> ids,Guid op)=>
        new(mode,StockAdjustmentReason.PhysicalCountCorrection,[new(f.ProductId,f.UnitId,StockAdjustmentDirection.Decrease,InventoryBucket.Sellable,quantity,null,InventoryUnitIds:ids)],f.ActorId,op);

    private static async Task TransferAsync(ServiceProvider p,Fixture f,InventoryBucket from,InventoryBucket to,decimal quantity,Guid[] ids)
    {
        await using var s=p.CreateAsyncScope();
        var r=await s.ServiceProvider.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(new(f.ProductId,from,to,quantity,f.ActorId,"Exact numeric proof",InventoryUnitIds:ids),default);
        Assert.True(r.IsSuccess,r.Error?.Code+":"+r.Error?.Message);
    }

    private static async Task<Fixture> SeedAsync(ServiceProvider p,TrackingMode mode,decimal factor,decimal[] acquisitionCosts,decimal quantity=1m)
    {
        await using var s=p.CreateAsyncScope();var db=s.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed=await Phase2PostgresTestHarness.SeedSerializedProductAsync(db,500m);
        var product=await db.Products.SingleAsync(x=>x.Id==seed.ProductId);
        product.TrackingMode=mode;product.SerialTrackingEnabled=mode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container;
        var productUnit=await db.ProductUnits.SingleAsync(x=>x.Id==seed.ProductUnitId);productUnit.FactorToBaseUnit=factor;
        await db.SaveChangesAsync();
        foreach(var cost in acquisitionCosts)
        {
            var identities=mode is TrackingMode.Quantity or TrackingMode.Length?Array.Empty<SerializedIdentityInput>():new[]{new SerializedIdentityInput("P7H-"+Guid.NewGuid().ToString("N"))};
            var r=await s.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new(seed.SupplierId,"P7H-"+Guid.NewGuid(),DateOnly.FromDateTime(DateTime.UtcNow),null,0m,PurchaseSettlementMode.External,seed.ActorId,Guid.NewGuid(),
                [new(seed.ProductId,seed.ProductUnitId,quantity,cost,500m,identities)]),default);
            Assert.True(r.IsSuccess,r.Error?.Code+":"+r.Error?.Message);
        }
        db.ChangeTracker.Clear();
        var units=await db.InventoryUnits.Where(x=>x.ProductId==seed.ProductId).OrderBy(x=>x.ItemSequence).ToArrayAsync();
        var customer=await Phase2PostgresTestHarness.SeedCustomerAsync(db);
        var project=new ThakaProject{ProjectNumber="P7H-"+Guid.NewGuid(),CustomerId=customer.Id,ProjectName="Hostile numeric",Status=ThakaProjectStatus.Active,CreatedBy=seed.ActorId,CreatedAt=DateTimeOffset.UtcNow};
        db.ThakaProjects.Add(project);await db.SaveChangesAsync();
        return new(seed.ProductId,seed.ProductUnitId,seed.ActorId,project.Id,units);
    }

    private static async Task AssertPoolAsync(EdgeRetailsDbContext db,Guid id,decimal quantity,decimal cost)
    {
        Assert.Equal(quantity,(await db.StockBalances.SingleAsync(x=>x.ProductId==id)).SellableQty);
        var pool=await db.ProductCostStates.SingleAsync(x=>x.ProductId==id);
        Assert.Equal(quantity,pool.CostedQty);Assert.Equal(cost,pool.TotalInventoryCost);
    }
    private static Task<decimal> LotQuantityAsync(EdgeRetailsDbContext db,Guid id,InventoryBucket bucket)=>
        (from b in db.InventoryLotBucketBalances join l in db.InventoryLots on b.LotId equals l.Id where l.ProductId==id && b.StockBucket==bucket select b.Quantity).SumAsync();

    private static async Task<string> SequenceAsync(ServiceProvider p,Fixture f)
    {
        await using var s=p.CreateAsyncScope();var db=s.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        return JsonSerializer.Serialize(await db.SupplierProducts.AsNoTracking().Where(x=>x.ProductId==f.ProductId).OrderBy(x=>x.Id).ToListAsync());
    }
    private static async Task<string> StateAsync(ServiceProvider p,Fixture f)
    {
        await using var s=p.CreateAsyncScope();var db=s.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var lotIds=await db.InventoryLots.Where(x=>x.ProductId==f.ProductId).Select(x=>x.Id).ToArrayAsync();
        var moveIds=await db.InventoryMovements.Where(x=>x.ProductId==f.ProductId).Select(x=>x.Id).ToArrayAsync();
        var issueIds=await db.ThakaMaterialIssues.Where(x=>x.ProjectId==f.ProjectId).Select(x=>x.Id).ToArrayAsync();
        var itemIds=await db.ThakaMaterialIssueItems.Where(x=>issueIds.Contains(x.MaterialIssueId)).Select(x=>x.Id).ToArrayAsync();
        var unitIds=f.Units.Select(x=>x.Id).ToArray();
        return JsonSerializer.Serialize(new
        {
            stock=await db.StockBalances.AsNoTracking().Where(x=>x.ProductId==f.ProductId).OrderBy(x=>x.Id).ToListAsync(),
            cost=await db.ProductCostStates.AsNoTracking().Where(x=>x.ProductId==f.ProductId).OrderBy(x=>x.Id).ToListAsync(),
            units=await db.InventoryUnits.AsNoTracking().Where(x=>x.ProductId==f.ProductId).OrderBy(x=>x.Id).ToListAsync(),
            claims=await db.InventoryUnitIdentityClaims.AsNoTracking().Where(x=>unitIds.Contains(x.InventoryUnitId)).OrderBy(x=>x.Id).ToListAsync(),
            sequence=await db.SupplierProducts.AsNoTracking().Where(x=>x.ProductId==f.ProductId).OrderBy(x=>x.Id).ToListAsync(),
            lots=await db.InventoryLots.AsNoTracking().Where(x=>x.ProductId==f.ProductId).OrderBy(x=>x.Id).ToListAsync(),
            buckets=await db.InventoryLotBucketBalances.AsNoTracking().Where(x=>lotIds.Contains(x.LotId)).OrderBy(x=>x.Id).ToListAsync(),
            consumed=await db.InventoryLotConsumptions.AsNoTracking().Where(x=>lotIds.Contains(x.LotId)).OrderBy(x=>x.Id).ToListAsync(),
            moves=await db.InventoryMovements.AsNoTracking().Where(x=>x.ProductId==f.ProductId).OrderBy(x=>x.Id).ToListAsync(),
            effects=await db.InventoryMovementEffects.AsNoTracking().Where(x=>moveIds.Contains(x.MovementId)).OrderBy(x=>x.Id).ToListAsync(),
            links=await db.InventoryMovementUnits.AsNoTracking().Where(x=>moveIds.Contains(x.MovementId)).OrderBy(x=>x.Id).ToListAsync(),
            adjustments=await db.StockAdjustmentItems.AsNoTracking().Where(x=>x.ProductId==f.ProductId).OrderBy(x=>x.Id).ToListAsync(),
            adjustmentHeaders=await db.StockAdjustments.AsNoTracking().Where(x=>x.ActorId==f.ActorId).OrderBy(x=>x.Id).ToListAsync(),
            audits=await db.BusinessAuditEvents.AsNoTracking().Where(x=>x.ActorId==f.ActorId).OrderBy(x=>x.Id).ToListAsync(),
            issues=await db.ThakaMaterialIssues.AsNoTracking().Where(x=>x.ProjectId==f.ProjectId).OrderBy(x=>x.Id).ToListAsync(),
            items=await db.ThakaMaterialIssueItems.AsNoTracking().Where(x=>issueIds.Contains(x.MaterialIssueId)).OrderBy(x=>x.Id).ToListAsync(),
            issueUnits=await db.ThakaMaterialIssueUnits.AsNoTracking().Where(x=>itemIds.Contains(x.MaterialIssueItemId)).OrderBy(x=>x.Id).ToListAsync(),
            reversals=await db.ThakaMaterialReversals.AsNoTracking().Where(x=>x.ProjectId==f.ProjectId).OrderBy(x=>x.Id).ToListAsync(),
            successOutcomes=await db.OperationOutcomes.AsNoTracking().Where(x=>x.ActorUserId==f.ActorId && x.WasCommitted).OrderBy(x=>x.ClientOperationId).ToListAsync(),
            project=await db.ThakaProjects.AsNoTracking().SingleAsync(x=>x.Id==f.ProjectId)
        });
    }
    private sealed record Fixture(Guid ProductId,Guid UnitId,Guid ActorId,Guid ProjectId,InventoryUnit[] Units);
}
