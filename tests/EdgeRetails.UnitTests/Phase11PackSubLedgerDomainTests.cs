using System;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase11PackSubLedgerDomainTests
{
    [Fact]
    public void PackState_EnumValuesMatchExpectedDatabaseOrdinal()
    {
        Assert.Equal(1, (int)PackState.Sealed);
        Assert.Equal(2, (int)PackState.Open);
        Assert.Equal(3, (int)PackState.Consumed);
    }

    [Fact]
    public void ValidatePackInvariants_NonPackUnit_PassesWhenPackFieldsNull()
    {
        var unit = new InventoryUnit
        {
            InitialBaseQuantity = null,
            RemainingBaseQuantity = null,
            PackState = null,
            IsLooseStock = false
        };

        unit.ValidatePackInvariants();
    }

    [Fact]
    public void ValidatePackInvariants_SealedPack_RequiresFullQuantityAndNotLoose()
    {
        var validSealed = new InventoryUnit
        {
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 100m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };

        validSealed.ValidatePackInvariants();

        var invalidRemaining = new InventoryUnit
        {
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 90m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };
        Assert.Throws<BusinessRuleException>(() => invalidRemaining.ValidatePackInvariants());

        var invalidLoose = new InventoryUnit
        {
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 100m,
            PackState = PackState.Sealed,
            IsLooseStock = true
        };
        Assert.Throws<BusinessRuleException>(() => invalidLoose.ValidatePackInvariants());
    }

    [Fact]
    public void ValidatePackInvariants_OpenPack_AllowsPartialBoxOrFullLooseStock()
    {
        var partialBox = new InventoryUnit
        {
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 25m,
            PackState = PackState.Open,
            IsLooseStock = false
        };
        partialBox.ValidatePackInvariants();

        var looseStock = new InventoryUnit
        {
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 50m,
            PackState = PackState.Open,
            IsLooseStock = true
        };
        looseStock.ValidatePackInvariants();

        var invalidFullStandardBox = new InventoryUnit
        {
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 50m,
            PackState = PackState.Open,
            IsLooseStock = false
        };
        Assert.Throws<BusinessRuleException>(() => invalidFullStandardBox.ValidatePackInvariants());

        var invalidZeroRemaining = new InventoryUnit
        {
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 0m,
            PackState = PackState.Open,
            IsLooseStock = false
        };
        Assert.Throws<BusinessRuleException>(() => invalidZeroRemaining.ValidatePackInvariants());
    }

    [Fact]
    public void ValidatePackInvariants_ConsumedPack_RequiresExactlyZeroRemaining()
    {
        var validConsumed = new InventoryUnit
        {
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 0m,
            PackState = PackState.Consumed,
            IsLooseStock = false
        };
        validConsumed.ValidatePackInvariants();

        var invalidConsumed = new InventoryUnit
        {
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 5m,
            PackState = PackState.Consumed,
            IsLooseStock = false
        };
        Assert.Throws<BusinessRuleException>(() => invalidConsumed.ValidatePackInvariants());
    }

    [Fact]
    public void UnsealPack_TransitionsSealedToOpen()
    {
        var unit = new InventoryUnit
        {
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 100m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };

        unit.UnsealPack(80m);

        Assert.Equal(PackState.Open, unit.PackState);
        Assert.Equal(80m, unit.RemainingBaseQuantity);
    }

    [Fact]
    public void UnsealPack_ThrowsWhenNotSealed()
    {
        var openUnit = new InventoryUnit
        {
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 80m,
            PackState = PackState.Open,
            IsLooseStock = false
        };
        Assert.Throws<BusinessRuleException>(() => openUnit.UnsealPack(50m));

        var nonPack = new InventoryUnit();
        Assert.Throws<BusinessRuleException>(() => nonPack.UnsealPack(50m));
    }

    [Fact]
    public void ConsumeFromPack_PartialQuantity_LeavesPackOpen()
    {
        var unit = new InventoryUnit
        {
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 100m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };

        unit.ConsumeFromPack(30m);

        Assert.Equal(70m, unit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, unit.PackState);
    }

    [Fact]
    public void ConsumeFromPack_FullRemainingQuantity_MarksPackConsumed()
    {
        var unit = new InventoryUnit
        {
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 40m,
            PackState = PackState.Open,
            IsLooseStock = false
        };

        unit.ConsumeFromPack(40m);

        Assert.Equal(0m, unit.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, unit.PackState);
    }

    [Fact]
    public void ConsumeFromPack_ExceedingQuantity_ThrowsBusinessRuleException()
    {
        var unit = new InventoryUnit
        {
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 20m,
            PackState = PackState.Open,
            IsLooseStock = false
        };

        var ex = Assert.Throws<BusinessRuleException>(() => unit.ConsumeFromPack(25m));
        Assert.Equal("inventory.pack_insufficient_remaining", ex.Code);
    }

    [Fact]
    public void ConsumeFromPack_NonPositiveQuantity_ThrowsBusinessRuleException()
    {
        var unit = new InventoryUnit
        {
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 50m,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };

        var exZero = Assert.Throws<BusinessRuleException>(() => unit.ConsumeFromPack(0m));
        Assert.Equal("inventory.pack_consume_quantity_positive", exZero.Code);

        var exNegative = Assert.Throws<BusinessRuleException>(() => unit.ConsumeFromPack(-5m));
        Assert.Equal("inventory.pack_consume_quantity_positive", exZero.Code);
    }

    [Fact]
    public void ConsumeFromPack_AlreadyConsumed_ThrowsBusinessRuleException()
    {
        var unit = new InventoryUnit
        {
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 0m,
            PackState = PackState.Consumed,
            IsLooseStock = false
        };

        var ex = Assert.Throws<BusinessRuleException>(() => unit.ConsumeFromPack(5m));
        Assert.Equal("inventory.pack_insufficient_remaining", ex.Code);
    }

    [Fact]
    public void PackAllocation_ValidAllocation_ValidatesInvariantsSuccessfully()
    {
        var alloc = new PackAllocation
        {
            Id = Guid.NewGuid(),
            PackUnitId = Guid.NewGuid(),
            MovementId = Guid.NewGuid(),
            LotId = Guid.NewGuid(),
            OperationType = PackAllocationOperationTypes.Sale,
            SaleItemId = Guid.NewGuid(),
            AllocatedBaseQuantity = 10m,
            UnitCostSnapshot = 15.5m,
            TotalCostSnapshot = 155.0m,
            RemainingQuantityAfter = 40m,
            PackStateAfter = PackState.Open,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        alloc.ValidateInvariants();
    }

    [Fact]
    public void PackAllocation_MultipleSourceReferences_ThrowsMutualExclusionException()
    {
        var alloc = new PackAllocation
        {
            Id = Guid.NewGuid(),
            PackUnitId = Guid.NewGuid(),
            MovementId = Guid.NewGuid(),
            LotId = Guid.NewGuid(),
            OperationType = PackAllocationOperationTypes.Sale,
            SaleItemId = Guid.NewGuid(),
            SaleReturnItemId = Guid.NewGuid(), // Violation: Both sale and return specified
            AllocatedBaseQuantity = 10m,
            UnitCostSnapshot = 15.5m,
            TotalCostSnapshot = 155.0m,
            RemainingQuantityAfter = 40m,
            PackStateAfter = PackState.Open,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var ex = Assert.Throws<BusinessRuleException>(() => alloc.ValidateInvariants());
        Assert.Equal("inventory.pack_alloc_multiple_sources", ex.Code);
    }

    [Fact]
    public void PackAllocation_NonPositiveAllocatedQuantity_ThrowsException()
    {
        var alloc = new PackAllocation
        {
            Id = Guid.NewGuid(),
            PackUnitId = Guid.NewGuid(),
            MovementId = Guid.NewGuid(),
            LotId = Guid.NewGuid(),
            OperationType = PackAllocationOperationTypes.Sale,
            SaleItemId = Guid.NewGuid(),
            AllocatedBaseQuantity = 0m,
            UnitCostSnapshot = 15.5m,
            TotalCostSnapshot = 0m,
            RemainingQuantityAfter = 50m,
            PackStateAfter = PackState.Open,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var ex = Assert.Throws<BusinessRuleException>(() => alloc.ValidateInvariants());
        Assert.Equal("inventory.pack_alloc_quantity_positive", ex.Code);
    }

    [Fact]
    public void DbContext_ChangeTracker_RejectsMutationOfPackAllocation()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new EdgeRetailsDbContext(options);

        var alloc = new PackAllocation
        {
            Id = Guid.NewGuid(),
            PackUnitId = Guid.NewGuid(),
            MovementId = Guid.NewGuid(),
            LotId = Guid.NewGuid(),
            OperationType = PackAllocationOperationTypes.Intake,
            AllocatedBaseQuantity = 100m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 1000m,
            RemainingQuantityAfter = 100m,
            PackStateAfter = PackState.Sealed,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        context.PackAllocations.Add(alloc);
        context.SaveChanges();

        // Mutating must throw InvalidOperationException in EnforceAppendOnlyAudit
        alloc.AllocatedBaseQuantity = 999m;
        var ex = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        Assert.Contains("Pack allocations are append-only ledger facts", ex.Message);
    }

    [Fact]
    public void DbContext_ChangeTracker_RejectsDeletionOfPackAllocation()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new EdgeRetailsDbContext(options);

        var alloc = new PackAllocation
        {
            Id = Guid.NewGuid(),
            PackUnitId = Guid.NewGuid(),
            MovementId = Guid.NewGuid(),
            LotId = Guid.NewGuid(),
            OperationType = PackAllocationOperationTypes.Intake,
            AllocatedBaseQuantity = 100m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 1000m,
            RemainingQuantityAfter = 100m,
            PackStateAfter = PackState.Sealed,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        context.PackAllocations.Add(alloc);
        context.SaveChanges();

        // Deleting must throw InvalidOperationException in EnforceAppendOnlyAudit
        context.PackAllocations.Remove(alloc);
        var ex = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        Assert.Contains("Pack allocations are append-only ledger facts", ex.Message);
    }
}
