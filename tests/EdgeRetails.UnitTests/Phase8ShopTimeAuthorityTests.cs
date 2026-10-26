using EdgeRetails.Application.Abstractions;
using EdgeRetails.Infrastructure.Services;

namespace EdgeRetails.UnitTests;

public sealed class Phase8ShopTimeAuthorityTests
{
    [Fact]
    public void BusinessDayCutoff_KeepsEarlyMorningOnPreviousShopDate()
    {
        var cache = new ShopTimeZoneCache();
        var karachi = ShopClock.ResolveTimeZoneById("Asia/Karachi");
        cache.Refresh(karachi, new TimeOnly(3, 0));
        var authority = new ShopTimeAuthority(cache);
        // 2026-10-13 01:30 PKT = 2026-10-12 20:30 UTC
        var utc = new DateTimeOffset(2026, 10, 12, 20, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 12), authority.ToShopDate(utc));
        // 2026-10-13 03:30 PKT = 2026-10-12 22:30 UTC
        var afterCutoff = new DateTimeOffset(2026, 10, 12, 22, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 13), authority.ToShopDate(afterCutoff));
    }

    [Fact]
    public void ClockMovedBackward_FlagsIntegrityCompromised()
    {
        var cache = new ShopTimeZoneCache();
        var authority = new ShopTimeAuthority(cache);
        var field = typeof(ShopTimeAuthority).GetField(
            "_lastUtcTicks",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(authority, DateTimeOffset.UtcNow.AddHours(2).UtcTicks);
        _ = authority.UtcNow;
        Assert.False(authority.Integrity.IsTrusted);
        Assert.Equal("time.clock_moved_backward", authority.Integrity.AnomalyCode);
    }
}
