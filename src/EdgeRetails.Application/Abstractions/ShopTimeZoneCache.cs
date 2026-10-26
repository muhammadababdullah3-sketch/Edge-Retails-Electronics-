namespace EdgeRetails.Application.Abstractions;

public interface IShopTimeZoneCache
{
    TimeZoneInfo Current { get; }

    /// <summary>Local shop time when the business calendar day advances (null = midnight).</summary>
    TimeOnly? BusinessDayCutoffLocalTime { get; }

    void Refresh(TimeZoneInfo shopTimeZone, TimeOnly? businessDayCutoffLocalTime = null);
}
