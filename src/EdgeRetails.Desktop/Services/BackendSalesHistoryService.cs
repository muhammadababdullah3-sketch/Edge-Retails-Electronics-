using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendSalesHistoryPage(
    IReadOnlyList<SalesHistoryRowDto> Rows,
    DateTimeOffset? NextCompletedAt,
    Guid? NextSaleId,
    bool HasMore);

public interface IBackendSalesHistoryService
{
    Task<BackendSalesHistoryPage> GetPageAsync(
        SalesHistoryPeriod period,
        string? search,
        int pageSize = 200,
        DateTimeOffset? beforeCompletedAt = null,
        Guid? beforeSaleId = null,
        CancellationToken cancellationToken = default);
}

public sealed class BackendSalesHistoryService : IBackendSalesHistoryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BackendSalesHistoryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<BackendSalesHistoryPage> GetPageAsync(
        SalesHistoryPeriod period,
        string? search,
        int pageSize = 200,
        DateTimeOffset? beforeCompletedAt = null,
        Guid? beforeSaleId = null,
        CancellationToken cancellationToken = default)
    {
        var (fromUtc, toUtc) = GetBounds(period);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<ISalesReadService>();
        var take = Math.Clamp(pageSize, 1, 200);
        var rows = (await reads.GetHistoryAsync(
            new GetSalesHistoryQuery(
                fromUtc,
                toUtc,
                string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                take,
                beforeCompletedAt,
                beforeSaleId),
            cancellationToken)).ToArray();

        var hasMore = rows.Length == take;
        var last = rows.LastOrDefault();
        return new BackendSalesHistoryPage(
            rows,
            hasMore ? last?.CompletedAt : null,
            hasMore ? last?.SaleId : null,
            hasMore);
    }

    private static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) GetBounds(
        SalesHistoryPeriod period)
    {
        var localToday = DateTime.Today;
        var localStart = period switch
        {
            SalesHistoryPeriod.Today => localToday,
            SalesHistoryPeriod.Yesterday => localToday.AddDays(-1),
            SalesHistoryPeriod.ThisWeek => StartOfWeek(localToday),
            SalesHistoryPeriod.ThisMonth => new DateTime(localToday.Year, localToday.Month, 1),
            _ => localToday
        };
        var localEnd = period switch
        {
            SalesHistoryPeriod.Today => localToday.AddDays(1),
            SalesHistoryPeriod.Yesterday => localToday,
            SalesHistoryPeriod.ThisWeek => localToday.AddDays(1),
            SalesHistoryPeriod.ThisMonth => new DateTime(localToday.Year, localToday.Month, 1).AddMonths(1),
            _ => localToday.AddDays(1)
        };

        var zone = TimeZoneInfo.Local;
        return (
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone)),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, zone)));
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var delta = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-delta).Date;
    }
}