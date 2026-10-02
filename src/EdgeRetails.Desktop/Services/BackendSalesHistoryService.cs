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
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly DesktopApiClient? _apiClient;

    public BackendSalesHistoryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public BackendSalesHistoryService(DesktopApiClient apiClient)
    {
        _apiClient = apiClient;
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
        var take = Math.Clamp(pageSize, 1, 200);
        SalesHistoryRowDto[] rows;
        if (_apiClient is not null)
        {
            var query = $"/api/sales?fromUtc={Uri.EscapeDataString(fromUtc.ToString("O"))}" +
                $"&toUtc={Uri.EscapeDataString(toUtc.ToString("O"))}&pageSize={take}";
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += $"&search={Uri.EscapeDataString(search.Trim())}";
            }
            if (beforeCompletedAt is not null && beforeSaleId is not null)
            {
                query += $"&beforeCompletedAt={Uri.EscapeDataString(beforeCompletedAt.Value.ToString("O"))}" +
                    $"&beforeSaleId={beforeSaleId.Value:D}";
            }

            rows = await _apiClient.GetAsync<SalesHistoryRowDto[]>(query, cancellationToken);
        }
        else
        {
            await using var scope = _scopeFactory!.CreateAsyncScope();
            var reads = scope.ServiceProvider.GetRequiredService<ISalesReadService>();
            rows = (await reads.GetHistoryAsync(
                new GetSalesHistoryQuery(
                    fromUtc,
                    toUtc,
                    string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                    take,
                    beforeCompletedAt,
                    beforeSaleId),
                cancellationToken)).ToArray();
        }

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
