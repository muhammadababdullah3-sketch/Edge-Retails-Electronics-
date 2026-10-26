using EdgeRetails.Application.Abstractions;

namespace EdgeRetails.Infrastructure.Services;

public sealed class ShopTimeZoneCache : IShopTimeZoneCache
{
    private volatile TimeZoneInfo _current = ShopClock.ResolveShopTimeZone();
    private TimeOnly? _businessDayCutoffLocalTime;

    public TimeZoneInfo Current => _current;

    public TimeOnly? BusinessDayCutoffLocalTime => _businessDayCutoffLocalTime;

    public void Refresh(TimeZoneInfo shopTimeZone, TimeOnly? businessDayCutoffLocalTime = null)
    {
        _current = shopTimeZone ?? throw new ArgumentNullException(nameof(shopTimeZone));
        _businessDayCutoffLocalTime = businessDayCutoffLocalTime;
    }
}
