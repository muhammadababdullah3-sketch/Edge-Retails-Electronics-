using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Common;

namespace EdgeRetails.Application.Features.Posting;

public sealed class ShopPeriodBoundaryService : IShopPeriodBoundaryService
{
    private readonly IShopClock _clock;

    public ShopPeriodBoundaryService(IShopClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetDailyUtcBoundary(DateOnly day)
    {
        return _clock.GetShopDayUtcRange(day);
    }

    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetDateRangeUtcBoundary(DateOnly startDate, DateOnly endDate)
    {
        if (endDate < startDate)
        {
            throw new BusinessRuleException(
                "boundary.invalid_range",
                $"End date {endDate:yyyy-MM-dd} cannot be earlier than start date {startDate:yyyy-MM-dd}.");
        }

        var startUtc = _clock.ToUtc(startDate, TimeOnly.MinValue);
        var endUtc = _clock.ToUtc(endDate.AddDays(1), TimeOnly.MinValue);
        return (startUtc, endUtc);
    }

    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetHourlyUtcBoundary(DateOnly day, int hour)
    {
        if (hour < 0 || hour > 23)
        {
            throw new BusinessRuleException(
                "boundary.invalid_hour",
                $"Hour must be between 0 and 23. Received: {hour}.");
        }

        var startUtc = _clock.ToUtc(day, new TimeOnly(hour, 0, 0));
        var nextHour = hour == 23 ? day.AddDays(1) : day;
        var nextHourTime = hour == 23 ? TimeOnly.MinValue : new TimeOnly(hour + 1, 0, 0);
        var endUtc = _clock.ToUtc(nextHour, nextHourTime);
        return (startUtc, endUtc);
    }

    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetMonthlyUtcBoundary(int year, int month)
    {
        if (month < 1 || month > 12)
        {
            throw new BusinessRuleException(
                "boundary.invalid_month",
                $"Month must be between 1 and 12. Received: {month}.");
        }

        var startDay = new DateOnly(year, month, 1);
        var endDay = startDay.AddMonths(1);

        var startUtc = _clock.ToUtc(startDay, TimeOnly.MinValue);
        var endUtc = _clock.ToUtc(endDay, TimeOnly.MinValue);
        return (startUtc, endUtc);
    }

    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetYearlyUtcBoundary(int year)
    {
        var startDay = new DateOnly(year, 1, 1);
        var endDay = new DateOnly(year + 1, 1, 1);

        var startUtc = _clock.ToUtc(startDay, TimeOnly.MinValue);
        var endUtc = _clock.ToUtc(endDay, TimeOnly.MinValue);
        return (startUtc, endUtc);
    }
}
