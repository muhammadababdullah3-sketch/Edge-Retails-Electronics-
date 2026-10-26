using EdgeRetails.Application.Abstractions;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Infrastructure.Services;

public static class ShopTimeZoneBootstrap
{
    public static async Task WarmCacheFromDatabaseAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var cache = scope.ServiceProvider.GetService<IShopTimeZoneCache>();
        if (cache is null)
        {
            return;
        }

        var db = scope.ServiceProvider.GetService<EdgeRetailsDbContext>();
        if (db is null)
        {
            return;
        }

        var profile = await db.ShopProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProfileKey == "PRIMARY", cancellationToken);
        if (profile is null || string.IsNullOrWhiteSpace(profile.TimeZoneId))
        {
            return;
        }

        cache.Refresh(
            ShopClock.ResolveTimeZoneById(profile.TimeZoneId),
            profile.BusinessDayCutoffLocalTime);
    }
}
