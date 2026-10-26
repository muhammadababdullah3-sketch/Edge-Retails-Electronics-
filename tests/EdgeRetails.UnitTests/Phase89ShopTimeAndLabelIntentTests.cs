using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Production;
using EdgeRetails.Infrastructure.Production.Printing;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.UnitTests;

public sealed class Phase89ShopTimeAndLabelIntentTests
{
    [Fact]
    public void ShopClock_UsesRefreshedTimeZoneFromCache()
    {
        var cache = new ShopTimeZoneCache();
        var karachi = ShopClock.ResolveTimeZoneById("Asia/Karachi");
        var utc = new DateTimeOffset(2026, 10, 12, 19, 0, 30, TimeSpan.Zero);
        cache.Refresh(TimeZoneInfo.Utc);
        var utcClock = new SystemClock(cache);
        Assert.Equal(DateOnly.FromDateTime(utc.UtcDateTime), utcClock.ToShopDate(utc));

        cache.Refresh(karachi);
        var karachiClock = new SystemClock(cache);
        Assert.Equal(new DateOnly(2026, 10, 13), karachiClock.ToShopDate(utc));
    }

    [Fact]
    public void LabelIntentWriter_PersistsOriginalRowsInSameContext()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new EdgeRetailsDbContext(options);
        var writer = new EfPhysicalLabelPrintIntentWriter(db);
        var unitId = Guid.NewGuid();
        var opId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        writer.AddOriginalIntents(opId, Guid.NewGuid(), [unitId], at);
        db.SaveChanges();

        var row = db.PhysicalLabelPrintIntents.Single();
        Assert.Equal(unitId, row.InventoryUnitId);
        Assert.Equal(PhysicalLabelPrintIntentKind.Original, row.Kind);
        Assert.Equal(PhysicalLabelPrintIntentStatus.PendingDispatch, row.Status);
        Assert.Equal(opId, row.SourceClientOperationId);
    }
}
