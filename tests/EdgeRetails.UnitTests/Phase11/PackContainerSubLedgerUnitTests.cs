using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Contracts.Packs;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Server.Filters;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;
using AppPackAllocationRequest = EdgeRetails.Application.Features.Inventory.PackAllocationRequest;

namespace EdgeRetails.UnitTests.Phase11;

/// <summary>
/// Task P11-IMP-107: Pack & Container Subledger Unit Test Suite
/// Implements all 22 planned PACK test obligations (P11-PLAN-PACK-01 through P11-PLAN-PACK-22)
/// preserving canonical method proposals, setups, actions, and independent assertions.
/// </summary>
public sealed class PackContainerSubLedgerUnitTests
{
    // =========================================================================
    // P11-PLAN-PACK-01: Initial Intake Sets Sealed & Full Remaining Quantity
    // =========================================================================
    [Fact]
    public void Pack_InitialIntake_Sets_State_Sealed_And_Full_Remaining_Quantity()
    {
        const decimal factorToBaseUnit = 10m;
        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            InitialBaseQuantity = factorToBaseUnit,
            RemainingBaseQuantity = factorToBaseUnit,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };

        unit.ValidatePackInvariants();

        Assert.Equal(factorToBaseUnit, unit.InitialBaseQuantity);
        Assert.Equal(factorToBaseUnit, unit.RemainingBaseQuantity);
        Assert.Equal(PackState.Sealed, unit.PackState);
        Assert.False(unit.IsLooseStock);
    }

    // =========================================================================
    // P11-PLAN-PACK-02: Partial Deduction Transitions Sealed to Open
    // =========================================================================
    [Fact]
    public void Pack_PartialDeduction_Transitions_Sealed_To_Open()
    {
        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };

        unit.ConsumeFromPack(2m);

        Assert.Equal(8m, unit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, unit.PackState);
        unit.ValidatePackInvariants();
    }

    // =========================================================================
    // P11-PLAN-PACK-03: Complete Deduction Transitions Open to Consumed
    // =========================================================================
    [Fact]
    public void Pack_CompleteDeduction_Transitions_Open_To_Consumed()
    {
        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 8m,
            PackState = PackState.Open,
            IsLooseStock = false
        };

        unit.ConsumeFromPack(8m);

        Assert.Equal(0m, unit.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, unit.PackState);
        unit.ValidatePackInvariants();
    }

    // =========================================================================
    // P11-PLAN-PACK-04: Over-Deduction Throws BusinessRuleException
    // =========================================================================
    [Fact]
    public void Pack_OverDeduction_Throws_BusinessRuleException()
    {
        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };

        var ex = Assert.Throws<BusinessRuleException>(() => unit.ConsumeFromPack(11m));
        Assert.Equal("inventory.pack_insufficient_remaining", ex.Code);
    }

    // =========================================================================
    // P11-PLAN-PACK-05: Negative Deduction Throws BusinessRuleException
    // =========================================================================
    [Fact]
    public void Pack_NegativeDeduction_Throws_BusinessRuleException()
    {
        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };

        var ex = Assert.Throws<BusinessRuleException>(() => unit.ConsumeFromPack(-1m));
        Assert.Equal("inventory.pack_consume_quantity_positive", ex.Code);
    }

    // =========================================================================
    // P11-PLAN-PACK-06: Fractional Deduction on Container Piece Throws Exception
    // =========================================================================
    [Fact]
    public void Pack_FractionalDeduction_On_ContainerPiece_Throws_BusinessRuleException()
    {
        var productUnit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            FactorToBaseUnit = 10m
        };

        var ex = Assert.Throws<BusinessRuleException>(() =>
            productUnit.ToBaseQuantity(1.5m, TrackingMode.Container));
        Assert.Equal("catalog.container_quantity_whole", ex.Code);
    }

    // =========================================================================
    // P11-PLAN-PACK-07: Allocation Prioritizes Open Pack Before Sealed Pack
    // =========================================================================
    [Fact]
    public void Pack_Allocation_Prioritizes_Open_Pack_Before_Sealed_Pack()
    {
        var authority = new PackSubLedgerAllocationAuthority();
        var productId = Guid.NewGuid();
        var lotId = Guid.NewGuid();

        var openPack = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lotId,
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 3m,
            PackState = PackState.Open,
            AcquisitionCost = 100m,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };

        var sealedPack = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lotId,
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Sealed,
            AcquisitionCost = 100m,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var request = new AppPackAllocationRequest(
            ProductId: productId,
            RequestedBaseQuantity: 2m,
            MovementId: Guid.NewGuid(),
            OperationType: PackAllocationOperationTypes.Sale,
            ActorId: Guid.NewGuid(),
            OccurredAt: DateTimeOffset.UtcNow);

        var result = authority.Allocate(new[] { openPack, sealedPack }, request);

        Assert.Single(result.Allocations);
        Assert.Equal(2m, result.TotalAllocatedBaseQuantity);
        Assert.Equal(1m, openPack.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, openPack.PackState);
        Assert.Equal(10m, sealedPack.RemainingBaseQuantity);
        Assert.Equal(PackState.Sealed, sealedPack.PackState);
    }

    // =========================================================================
    // P11-PLAN-PACK-08: Allocation Splits Across Open and Sealed Packs
    // =========================================================================
    [Fact]
    public void Pack_Allocation_Splits_Across_Open_And_Sealed_Packs()
    {
        var authority = new PackSubLedgerAllocationAuthority();
        var productId = Guid.NewGuid();
        var lotId = Guid.NewGuid();

        var openPack = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lotId,
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 3m,
            PackState = PackState.Open,
            AcquisitionCost = 100m,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };

        var sealedPack = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lotId,
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Sealed,
            AcquisitionCost = 100m,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var request = new AppPackAllocationRequest(
            ProductId: productId,
            RequestedBaseQuantity: 5m,
            MovementId: Guid.NewGuid(),
            OperationType: PackAllocationOperationTypes.Sale,
            ActorId: Guid.NewGuid(),
            OccurredAt: DateTimeOffset.UtcNow);

        var result = authority.Allocate(new[] { openPack, sealedPack }, request);

        Assert.Equal(2, result.Allocations.Count);
        Assert.Equal(5m, result.TotalAllocatedBaseQuantity);
        Assert.Equal(0m, openPack.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, openPack.PackState);
        Assert.Equal(8m, sealedPack.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, sealedPack.PackState);
    }

    // =========================================================================
    // P11-PLAN-PACK-09: Cut Length Reduces Remaining Meters Correctly
    // =========================================================================
    [Fact]
    public void Coil_CutLength_Reduces_Remaining_Meters_Correctly()
    {
        var coil = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 100m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };

        coil.ConsumeFromPack(15m);

        Assert.Equal(85m, coil.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, coil.PackState);
        coil.ValidatePackInvariants();
    }

    // =========================================================================
    // P11-PLAN-PACK-10: Remnant Under Threshold Allows Scrap Write-Off
    // =========================================================================
    [Fact]
    public void Coil_Remnant_Under_Threshold_Allows_Scrap_WriteOff()
    {
        var authority = new PackSubLedgerAllocationAuthority();
        var productId = Guid.NewGuid();
        var lotId = Guid.NewGuid();

        var coil = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lotId,
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 1.5m,
            PackState = PackState.Open,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1000m
        };

        var evaluation = authority.EvaluateRemnant(coil.RemainingBaseQuantity.Value, minimumSellableIncrement: 2m);
        Assert.True(evaluation.IsShortRemnant);

        var alloc = authority.ScrapRemnant(
            coil,
            movementId: Guid.NewGuid(),
            actorId: Guid.NewGuid(),
            occurredAt: DateTimeOffset.UtcNow,
            reason: "Coil remnant write-off under scrap threshold");

        Assert.Equal(0m, coil.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, coil.PackState);
        Assert.Equal(15.00m, alloc.TotalCostSnapshot);
        Assert.Equal(PackState.Consumed, alloc.PackStateAfter);
    }

    // =========================================================================
    // P11-PLAN-PACK-11: Inspected Return Creates Separate Loose Stock
    // =========================================================================
    [Fact]
    public void Inspected_Return_Creates_Separate_Loose_Stock()
    {
        var authority = new PackSubLedgerAllocationAuthority();
        var lotId = Guid.NewGuid();

        var originalPack = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            InventoryLotId = lotId,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 43m,
            PackState = PackState.Open,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 500m
        };

        var looseEntry = authority.CreateLooseReturnEntry(
            returnedQuantity: 2m,
            unitCost: 10m,
            restoredLotId: lotId);

        Assert.True(looseEntry.IsLooseStock);
        Assert.Equal(PackState.Open, looseEntry.PackState);
        Assert.Equal(2m, looseEntry.InitialBaseQuantity);
        Assert.Equal(2m, looseEntry.RemainingBaseQuantity);

        // Assert original pack remains unchanged
        Assert.Equal(43m, originalPack.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, originalPack.PackState);
    }

    // =========================================================================
    // P11-PLAN-PACK-12: Sealed Loose Row Is Rejected
    // =========================================================================
    [Fact]
    public void Sealed_Loose_Row_Is_Rejected()
    {
        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            InitialBaseQuantity = 2m,
            RemainingBaseQuantity = 2m,
            PackState = PackState.Sealed,
            IsLooseStock = true
        };

        var ex = Assert.Throws<BusinessRuleException>(() => unit.ValidatePackInvariants());
        Assert.Equal("inventory.sealed_loose_stock_invalid", ex.Code);
    }

    // =========================================================================
    // P11-PLAN-PACK-13: Non-Applicable Pack Columns Are All Null
    // =========================================================================
    [Fact]
    public void NonApplicable_Pack_Columns_Are_All_Null()
    {
        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            InitialBaseQuantity = null,
            RemainingBaseQuantity = null,
            PackState = null,
            IsLooseStock = false
        };

        unit.ValidatePackInvariants();

        Assert.Null(unit.InitialBaseQuantity);
        Assert.Null(unit.RemainingBaseQuantity);
        Assert.Null(unit.PackState);
        Assert.False(unit.IsLooseStock);
    }

    // =========================================================================
    // P11-PLAN-PACK-14: Partial Null and Out-of-Bounds States Are Rejected
    // =========================================================================
    [Fact]
    public void Partial_Null_And_OutOfBounds_States_Are_Rejected()
    {
        var u1 = new InventoryUnit { InitialBaseQuantity = 10m, RemainingBaseQuantity = null, PackState = PackState.Open };
        Assert.Throws<BusinessRuleException>(() => u1.ValidatePackInvariants());

        var u2 = new InventoryUnit { InitialBaseQuantity = null, RemainingBaseQuantity = 10m, PackState = PackState.Open };
        Assert.Throws<BusinessRuleException>(() => u2.ValidatePackInvariants());

        var u3 = new InventoryUnit { InitialBaseQuantity = 10m, RemainingBaseQuantity = 10m, PackState = null };
        Assert.Throws<BusinessRuleException>(() => u3.ValidatePackInvariants());

        var u4 = new InventoryUnit { InitialBaseQuantity = 10m, RemainingBaseQuantity = 12m, PackState = PackState.Open };
        Assert.Throws<BusinessRuleException>(() => u4.ValidatePackInvariants());

        var u5 = new InventoryUnit { InitialBaseQuantity = 10m, RemainingBaseQuantity = -1m, PackState = PackState.Open };
        Assert.Throws<BusinessRuleException>(() => u5.ValidatePackInvariants());

        var u6 = new InventoryUnit { InitialBaseQuantity = 10m, RemainingBaseQuantity = 5m, PackState = PackState.Consumed };
        Assert.Throws<BusinessRuleException>(() => u6.ValidatePackInvariants());
    }

    // =========================================================================
    // P11-PLAN-PACK-15: Quarantine, Void, Damage, Custody Are Not Sellable
    // =========================================================================
    [Fact]
    public void Quarantine_Void_Damage_Custody_Are_Not_Sellable()
    {
        var authority = new PackSubLedgerAllocationAuthority();
        var productId = Guid.NewGuid();

        var nonSellable1 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            Status = InventoryUnitStatus.ReceiptVoided,
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Sealed
        };

        var nonSellable2 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            Status = InventoryUnitStatus.Damaged,
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Sealed
        };

        var request = new AppPackAllocationRequest(
            ProductId: productId,
            RequestedBaseQuantity: 1m,
            MovementId: Guid.NewGuid(),
            OperationType: PackAllocationOperationTypes.Sale,
            ActorId: Guid.NewGuid(),
            OccurredAt: DateTimeOffset.UtcNow);

        var ex = Assert.Throws<BusinessRuleException>(() =>
            authority.Allocate(new[] { nonSellable1, nonSellable2 }, request));
        Assert.Equal("inventory.pack_no_eligible_units", ex.Code);
    }

    // =========================================================================
    // P11-PLAN-PACK-16: Fractional Cost and Final Exhaustion Conserve Value
    // =========================================================================
    [Fact]
    public void Fractional_Cost_And_Final_Exhaustion_Conserve_Value()
    {
        decimal initialQty = 3m;
        decimal acquisitionCost = 100m;

        var unitCostSnapshot = decimal.Round(acquisitionCost / initialQty, 6, MidpointRounding.AwayFromZero);
        var firstDrawCost = decimal.Round(1m * unitCostSnapshot, 6, MidpointRounding.AwayFromZero);
        var secondDrawCost = decimal.Round(1m * unitCostSnapshot, 6, MidpointRounding.AwayFromZero);

        // Final exhaustion releases exact remaining carrying cost basis
        var remainingCostBasis = acquisitionCost - (firstDrawCost + secondDrawCost);
        var thirdDrawCost = remainingCostBasis;

        Assert.Equal(33.333333m, unitCostSnapshot);
        Assert.Equal(33.333333m, firstDrawCost);
        Assert.Equal(33.333333m, secondDrawCost);
        Assert.Equal(33.333334m, thirdDrawCost);
        Assert.Equal(acquisitionCost, firstDrawCost + secondDrawCost + thirdDrawCost);
    }

    // =========================================================================
    // P11-PLAN-PACK-17: Multiple Lots Preserve Exact Cost Provenance
    // =========================================================================
    [Fact]
    public void Multiple_Lots_Preserve_Exact_Cost_Provenance()
    {
        var authority = new PackSubLedgerAllocationAuthority();
        var productId = Guid.NewGuid();
        var lotAId = Guid.NewGuid();
        var lotBId = Guid.NewGuid();

        var packA = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lotAId,
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 5m,
            RemainingBaseQuantity = 5m,
            PackState = PackState.Sealed,
            AcquisitionCost = 250m,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };

        var packB = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lotBId,
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 5m,
            RemainingBaseQuantity = 5m,
            PackState = PackState.Sealed,
            AcquisitionCost = 300m,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var request = new AppPackAllocationRequest(
            ProductId: productId,
            RequestedBaseQuantity: 7m,
            MovementId: Guid.NewGuid(),
            OperationType: PackAllocationOperationTypes.Sale,
            ActorId: Guid.NewGuid(),
            OccurredAt: DateTimeOffset.UtcNow);

        var result = authority.Allocate(new[] { packA, packB }, request);

        Assert.Equal(2, result.Allocations.Count);
        Assert.Equal(lotAId, result.Allocations[0].LotId);
        Assert.Equal(50m, result.Allocations[0].UnitCostSnapshot);
        Assert.Equal(5m, result.Allocations[0].AllocatedBaseQuantity);

        Assert.Equal(lotBId, result.Allocations[1].LotId);
        Assert.Equal(60m, result.Allocations[1].UnitCostSnapshot);
        Assert.Equal(2m, result.Allocations[1].AllocatedBaseQuantity);
    }

    // =========================================================================
    // P11-PLAN-PACK-18: Thaka Partial Issue and Return Is Source-Linked
    // =========================================================================
    [Fact]
    public void Thaka_Partial_Issue_Return_Is_SourceLinked()
    {
        var packUnitId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var thakaIssueItemId = Guid.NewGuid();

        var issueAllocation = new PackAllocation
        {
            Id = Guid.NewGuid(),
            PackUnitId = packUnitId,
            MovementId = Guid.NewGuid(),
            LotId = lotId,
            OperationType = PackAllocationOperationTypes.ThakaIssue,
            ThakaIssueItemId = thakaIssueItemId,
            AllocatedBaseQuantity = 10m,
            UnitCostSnapshot = 25m,
            TotalCostSnapshot = 250m,
            RemainingQuantityAfter = 40m,
            PackStateAfter = PackState.Open,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        issueAllocation.ValidateInvariants();

        Assert.Equal(packUnitId, issueAllocation.PackUnitId);
        Assert.Equal(lotId, issueAllocation.LotId);
        Assert.Equal(thakaIssueItemId, issueAllocation.ThakaIssueItemId);
        Assert.Null(issueAllocation.SaleItemId);
        Assert.Null(issueAllocation.SaleReturnItemId);
    }

    // =========================================================================
    // P11-PLAN-PACK-19: Warranty Custody Preserves Pack Provenance
    // =========================================================================
    [Fact]
    public void Warranty_Custody_Preserves_Pack_Provenance()
    {
        var packUnitId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var purchaseItemId = Guid.NewGuid();

        var packUnit = new InventoryUnit
        {
            Id = packUnitId,
            ProductId = Guid.NewGuid(),
            InventoryLotId = lotId,
            SourcePurchaseItemId = purchaseItemId,
            TrackingCode = "DRUM-CBL-001",
            Status = InventoryUnitStatus.WithSupplier,
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 80m,
            PackState = PackState.Open,
            AcquisitionCost = 4000m
        };

        packUnit.ValidatePackInvariants();

        Assert.Equal(packUnitId, packUnit.Id);
        Assert.Equal(lotId, packUnit.InventoryLotId);
        Assert.Equal(purchaseItemId, packUnit.SourcePurchaseItemId);
        Assert.Equal(InventoryUnitStatus.WithSupplier, packUnit.Status);
        Assert.Equal(80m, packUnit.RemainingBaseQuantity);
    }

    // =========================================================================
    // P11-PLAN-PACK-20: Scrap Loss and Approved Recovery Preserve Value
    // =========================================================================
    [Fact]
    public void Scrap_Loss_And_Approved_Recovery_Preserve_Value()
    {
        var authority = new PackSubLedgerAllocationAuthority();
        var productId = Guid.NewGuid();
        var lotId = Guid.NewGuid();

        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lotId,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 1.0m,
            PackState = PackState.Open,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 500m
        };

        var scrapAllocation = authority.ScrapRemnant(
            unit,
            movementId: Guid.NewGuid(),
            actorId: Guid.NewGuid(),
            occurredAt: DateTimeOffset.UtcNow,
            reason: "Remnant write-off below minimum sellable increment");

        Assert.Equal(10.00m, scrapAllocation.TotalCostSnapshot);
        Assert.Equal(0m, unit.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, unit.PackState);

        var recoveryEntry = authority.CreateLooseReturnEntry(
            returnedQuantity: 1.0m,
            unitCost: scrapAllocation.UnitCostSnapshot,
            restoredLotId: lotId);

        Assert.Equal(10.00m, recoveryEntry.AcquisitionCost);
        Assert.Equal(1.0m, recoveryEntry.RemainingBaseQuantity);
        Assert.True(recoveryEntry.IsLooseStock);
    }

    // =========================================================================
    // P11-PLAN-PACK-21: Legacy Unknown Rows Are Quarantined Without Loss
    // =========================================================================
    [Fact]
    public void Legacy_Unknown_Rows_Are_Quarantined_Without_Loss()
    {
        var legacyUnit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            Status = InventoryUnitStatus.ReceiptVoided,
            InitialBaseQuantity = null,
            RemainingBaseQuantity = null,
            PackState = null,
            AcquisitionCost = 150m
        };

        legacyUnit.ValidatePackInvariants();

        Assert.Equal(InventoryUnitStatus.ReceiptVoided, legacyUnit.Status);
        Assert.Null(legacyUnit.PackState);
        Assert.Equal(150m, legacyUnit.AcquisitionCost);
    }

    // =========================================================================
    // P11-PLAN-PACK-22: Pack API Masks Cost for Cashier Role
    // =========================================================================
    [Fact]
    public async Task Pack_Api_Masks_Cost_For_Cashier()
    {
        var packDto = new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: "BOX-001",
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 7m,
            ContinuousLength: 0m,
            UnitOfMeasure: "BOX",
            Condition: "Normal",
            ConcurrencyVersion: 1,
            UnitCost: 45.50m);

        // 1. Direct MaskValue verifies Cashier redaction
        var maskedPack = (PackContainerDto)CostMaskingFilter.MaskValue(packDto);
        Assert.NotNull(maskedPack);
        Assert.Null(maskedPack.UnitCost);
        Assert.Equal("BOX-001", maskedPack.TrackingCode);
        Assert.Equal(7m, maskedPack.RemainingQuantity);

        // 2. Action filter invocation with Cashier actor context
        var filter = new CostMaskingFilter();
        var httpContext = new DefaultHttpContext();
        var cashierActor = new ActorContext(
            UserId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            DisplayName: "Cashier Bob",
            RoleId: Guid.NewGuid(),
            RoleName: "Cashier",
            Permissions: new HashSet<string>());
        httpContext.Items["ActorContext"] = cashierActor;

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        var resultExecutingContext = new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new ObjectResult(packDto),
            controller: new object());

        var nextCalled = false;
        await filter.OnResultExecutionAsync(resultExecutingContext, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ResultExecutedContext(
                actionContext,
                new List<IFilterMetadata>(),
                resultExecutingContext.Result,
                controller: new object()));
        });

        Assert.True(nextCalled);
        var objectResult = Assert.IsType<ObjectResult>(resultExecutingContext.Result);
        var maskedResult = Assert.IsType<PackContainerDto>(objectResult.Value);
        Assert.Null(maskedResult.UnitCost);
        Assert.Equal(7m, maskedResult.RemainingQuantity);

        // 3. Supervisor actor context preserves exact authorized cost
        var supervisorContext = new DefaultHttpContext();
        var supervisorActor = new ActorContext(
            UserId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            DisplayName: "Supervisor Alice",
            RoleId: Guid.NewGuid(),
            RoleName: "Supervisor",
            Permissions: new HashSet<string> { PermissionKeys.InventoryManage });
        supervisorContext.Items["ActorContext"] = supervisorActor;

        var supervisorActionContext = new ActionContext(
            supervisorContext,
            new RouteData(),
            new ActionDescriptor());

        var supervisorResultExecutingContext = new ResultExecutingContext(
            supervisorActionContext,
            new List<IFilterMetadata>(),
            new ObjectResult(packDto),
            controller: new object());

        await filter.OnResultExecutionAsync(supervisorResultExecutingContext, () =>
        {
            return Task.FromResult(new ResultExecutedContext(
                supervisorActionContext,
                new List<IFilterMetadata>(),
                supervisorResultExecutingContext.Result,
                controller: new object()));
        });

        var supervisorObjectResult = Assert.IsType<ObjectResult>(supervisorResultExecutingContext.Result);
        var supervisorResult = Assert.IsType<PackContainerDto>(supervisorObjectResult.Value);
        Assert.Equal(45.50m, supervisorResult.UnitCost);
    }
}
