using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.Production;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Printing;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.UnitTests;

public sealed class Phase9PhysicalLabelPrintIntentTests
{
    [Fact]
    public async Task Coordinator_ClaimThenReceipt_MarksOriginalSucceeded()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new EdgeRetailsDbContext(options);
        var writer = new EfPhysicalLabelPrintIntentWriter(db);
        var coordinator = new EfPhysicalLabelPrintIntentCoordinator(db);
        var unitId = Guid.NewGuid();
        var opId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        writer.AddOriginalIntents(opId, Guid.NewGuid(), [unitId], at);
        await db.SaveChangesAsync();

        var claimed = await coordinator.ClaimOriginalForUnitAsync(unitId, "WS-1", false, CancellationToken.None);
        Assert.True(claimed.IsSuccess);
        Assert.Equal(PhysicalLabelPrintIntentStatus.Dispatched, claimed.Value!.Status);

        await coordinator.ApplyStickerReceiptAsync(
            new ApplyLabelIntentStickerReceipt(unitId, false, true, null, Guid.NewGuid()),
            CancellationToken.None);

        var row = await db.PhysicalLabelPrintIntents.SingleAsync();
        Assert.Equal(PhysicalLabelPrintIntentStatus.Succeeded, row.Status);
    }

    [Fact]
    public async Task Coordinator_OutcomeUnknown_BlocksSecondClaimUntilRetry()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new EdgeRetailsDbContext(options);
        var writer = new EfPhysicalLabelPrintIntentWriter(db);
        var coordinator = new EfPhysicalLabelPrintIntentCoordinator(db);
        var unitId = Guid.NewGuid();
        writer.AddOriginalIntents(Guid.NewGuid(), null, [unitId], DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        await coordinator.ClaimOriginalForUnitAsync(unitId, "WS-1", false, CancellationToken.None);
        await coordinator.ApplyStickerReceiptAsync(
            new ApplyLabelIntentStickerReceipt(unitId, false, false, "print.outcome_unknown", Guid.NewGuid()),
            CancellationToken.None);

        var blocked = await coordinator.ClaimOriginalForUnitAsync(unitId, "WS-1", false, CancellationToken.None);
        Assert.False(blocked.IsSuccess);
        Assert.Equal("print.outcome_unknown", blocked.Error!.Code);

        var retry = await coordinator.ClaimOriginalForUnitAsync(unitId, "WS-1", true, CancellationToken.None);
        Assert.True(retry.IsSuccess);
        Assert.Equal(PhysicalLabelPrintIntentStatus.Dispatched, retry.Value!.Status);
    }

    [Fact]
    public async Task Coordinator_ExpiredDispatchLease_AllowsRecoveryClaim()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new EdgeRetailsDbContext(options);
        var writer = new EfPhysicalLabelPrintIntentWriter(db);
        var coordinator = new EfPhysicalLabelPrintIntentCoordinator(db);
        var unitId = Guid.NewGuid();
        writer.AddOriginalIntents(Guid.NewGuid(), null, [unitId], DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        await coordinator.ClaimOriginalForUnitAsync(unitId, "WS-1", false, CancellationToken.None);

        var row = await db.PhysicalLabelPrintIntents.SingleAsync();
        row.UpdatedAt = DateTimeOffset.UtcNow - EfPhysicalLabelPrintIntentCoordinator.DispatchLeaseDuration - TimeSpan.FromMinutes(1);
        await db.SaveChangesAsync();

        var recovered = await coordinator.ClaimOriginalForUnitAsync(unitId, "WS-2", false, CancellationToken.None);
        Assert.True(recovered.IsSuccess);
        Assert.Equal("WS-2", recovered.Value!.TargetWorkstationId);
        Assert.Equal(2, recovered.Value.AttemptCount);
    }

    [Fact]
    public async Task Coordinator_ReprintIntent_HasDistinctLineage()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new EdgeRetailsDbContext(options);
        var writer = new EfPhysicalLabelPrintIntentWriter(db);
        var coordinator = new EfPhysicalLabelPrintIntentCoordinator(db);
        var unitId = Guid.NewGuid();
        writer.AddOriginalIntents(Guid.NewGuid(), null, [unitId], DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var reprint = await coordinator.RegisterReprintIntentAsync(unitId, "WS-1", CancellationToken.None);
        Assert.True(reprint.IsSuccess);
        Assert.Equal(PhysicalLabelPrintIntentKind.Reprint, reprint.Value!.Kind);

        var rows = await db.PhysicalLabelPrintIntents.Where(x => x.InventoryUnitId == unitId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, x => x.Kind == PhysicalLabelPrintIntentKind.Original);
        Assert.Contains(rows, x => x.Kind == PhysicalLabelPrintIntentKind.Reprint);
    }
}
