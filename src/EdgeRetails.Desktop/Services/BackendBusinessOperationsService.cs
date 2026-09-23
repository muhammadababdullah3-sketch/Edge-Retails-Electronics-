using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Finance;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public interface IBackendBusinessOperationsService
{
    Task<IReadOnlyList<string>> GetExpenseCategoriesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExpenseRecord>> GetExpensesAsync(
        CancellationToken cancellationToken = default);

    Task<ExpenseRecord> PostExpenseAsync(
        string category,
        string subcategory,
        decimal amount,
        DateTime date,
        string paymentMethod,
        string note,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomerDirectoryRecord>> GetCustomersAsync(
        string? search,
        int pageSize = 100,
        CancellationToken cancellationToken = default);

    Task SaveCustomerAsync(
        CustomerDirectoryRecord? existing,
        string name,
        string phone,
        string address,
        string notes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierDirectoryRecord>> GetSuppliersAsync(
        string? search,
        int pageSize = 100,
        CancellationToken cancellationToken = default);

    Task SaveSupplierAsync(
        SupplierDirectoryRecord? existing,
        string name,
        string phone,
        string city,
        string address,
        string notes,
        CancellationToken cancellationToken = default);

    Task<ReportSnapshot> GetReportAsync(
        ReportPeriodMode mode,
        DateTime selectedDate,
        int selectedMonth,
        int selectedYear,
        CancellationToken cancellationToken = default);
}

public sealed class BackendBusinessOperationsService
    : IBackendBusinessOperationsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Guid?> _actorUserId;

    public BackendBusinessOperationsService(
        IServiceScopeFactory scopeFactory,
        Func<Guid?> actorUserId)
    {
        _scopeFactory = scopeFactory;
        _actorUserId = actorUserId;
    }
    public async Task<IReadOnlyList<string>> GetExpenseCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IExpenseReadService>();
        var rows = await reads.GetCategoriesAsync(cancellationToken);
        return rows.Select(x => x.Name).ToArray();
    }

    public async Task<IReadOnlyList<ExpenseRecord>> GetExpensesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IExpenseReadService>();
        var rows = await reads.GetExpensesAsync(cancellationToken);

        return rows.Select(x => new ExpenseRecord
        {
            Id = x.ExpenseNumber,
            BackendId = x.ExpenseId,
            Category = x.CategoryName,
            Subcategory = x.SubcategoryName ?? x.Description,
            Amount = x.Amount,
            Date = x.ExpenseDate.ToDateTime(TimeOnly.MinValue),
            PaymentMethod = x.PaymentMethod.ToString(),
            StaffMember = x.CreatedByName,
            Note = x.Reference ?? string.Empty
        }).ToArray();
    }

    public async Task<ExpenseRecord> PostExpenseAsync(
        string category,
        string subcategory,
        decimal amount,
        DateTime date,
        string paymentMethod,
        string note,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IExpenseReadService>();
        var categories = await reads.GetCategoriesAsync(cancellationToken);
        var matches = categories
            .Where(x => string.Equals(
                x.Name,
                category?.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                "Selected expense category is not available uniquely in the backend.");
        }

        var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
        var result = await handler.HandleAsync(
            new PostExpenseCommand(
                Guid.CreateVersion7(),
                matches[0].Id,
                null,
                DateOnly.FromDateTime(date),
                amount,
                ParseExpensePaymentMethod(paymentMethod),
                Required(subcategory, "Expense subcategory"),
                Normalize(note),
                actor),
            cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Expense could not be posted.");
        }

        var all = await reads.GetExpensesAsync(cancellationToken);
        var row = all.SingleOrDefault(x => x.ExpenseId == result.Value.ExpenseId)
            ?? throw new InvalidOperationException(
                "Expense was posted but could not be read back.");

        return new ExpenseRecord
        {
            Id = row.ExpenseNumber,
            BackendId = row.ExpenseId,
            Category = row.CategoryName,
            Subcategory = row.SubcategoryName ?? row.Description,
            Amount = row.Amount,
            Date = row.ExpenseDate.ToDateTime(TimeOnly.MinValue),
            PaymentMethod = row.PaymentMethod.ToString(),
            StaffMember = row.CreatedByName,
            Note = row.Reference ?? string.Empty
        };
    }
    public async Task<IReadOnlyList<CustomerDirectoryRecord>> GetCustomersAsync(
        string? search = null,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPartyDirectoryReadService>();
        var rows = await reads.GetCustomersAsync(search, Math.Clamp(pageSize, 1, 200), cancellationToken);

        return rows.Select(x => new CustomerDirectoryRecord
        {
            Id = x.CustomerId.ToString("D"),
            BackendId = x.CustomerId,
            Name = x.Name,
            Phone = x.Phone ?? string.Empty,
            Address = x.Address ?? string.Empty,
            Notes = x.Notes ?? string.Empty,
            LocalSales = x.NetSales,
            LastSale = x.LastSaleAt?.LocalDateTime,
            ActiveThaka = x.ActiveThakaProject ?? "—"
        }).ToArray();
    }

    public async Task SaveCustomerAsync(
        CustomerDirectoryRecord? existing,
        string name,
        string phone,
        string address,
        string notes,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SaveCustomerHandler>();
        var result = await handler.HandleAsync(
            new SaveCustomerCommand(
                existing?.BackendId,
                Required(name, "Customer name"),
                Normalize(phone),
                Normalize(address),
                true,
                actor,
                Guid.CreateVersion7(),
                Normalize(notes)),
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Customer could not be saved.");
        }
    }

    public async Task<IReadOnlyList<SupplierDirectoryRecord>> GetSuppliersAsync(
        string? search = null,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPartyDirectoryReadService>();
        var rows = await reads.GetSuppliersAsync(search, Math.Clamp(pageSize, 1, 200), cancellationToken);

        return rows.Select(x => new SupplierDirectoryRecord
        {
            Id = x.SupplierId.ToString("D"),
            BackendId = x.SupplierId,
            Name = x.Name,
            Phone = x.Phone ?? string.Empty,
            City = x.City ?? string.Empty,
            Address = x.Address ?? string.Empty,
            Notes = x.Notes ?? string.Empty,
            TotalPurchases = x.NetPurchases,
            LastPurchase = x.LastPurchaseDate?.ToDateTime(TimeOnly.MinValue)
        }).ToArray();
    }

    public async Task SaveSupplierAsync(
        SupplierDirectoryRecord? existing,
        string name,
        string phone,
        string city,
        string address,
        string notes,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SaveSupplierHandler>();
        var result = await handler.HandleAsync(
            new SaveSupplierCommand(
                existing?.BackendId,
                Required(name, "Supplier name"),
                Normalize(phone),
                Normalize(city),
                Normalize(address),
                true,
                actor,
                Guid.CreateVersion7(),
                Normalize(notes)),
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Supplier could not be saved.");
        }
    }
    public async Task<ReportSnapshot> GetReportAsync(
        ReportPeriodMode mode,
        DateTime selectedDate,
        int selectedMonth,
        int selectedYear,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IReportingReadService>();
        var snapshot = await reads.GetSnapshotAsync(
            mode switch
            {
                ReportPeriodMode.Daily => ReportingPeriodKind.Daily,
                ReportPeriodMode.Monthly => ReportingPeriodKind.Monthly,
                ReportPeriodMode.Yearly => ReportingPeriodKind.Yearly,
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            },
            DateOnly.FromDateTime(selectedDate),
            selectedMonth,
            selectedYear,
            cancellationToken);

        return new ReportSnapshot
        {
            Mode = mode,
            PeriodLabel = snapshot.PeriodLabel,
            NetSales = snapshot.NetSales,
            GrossProfit = snapshot.GrossProfit,
            Expenses = snapshot.Expenses,
            NetProfit = snapshot.NetProfit,
            Purchases = snapshot.Purchases,
            ThakaMaterial = snapshot.ThakaMaterial,
            SalesCount = snapshot.SalesCount,
            AverageInvoiceValue = snapshot.AverageInvoiceValue,
            AverageGrossProfitPerInvoice = snapshot.AverageGrossProfitPerInvoice,
            AverageItemsPerInvoice = snapshot.AverageItemsPerInvoice,
            AverageDailySales = snapshot.AverageDailySales,
            AverageDailyGrossProfit = snapshot.AverageDailyGrossProfit,
            AverageDailyNetProfit = snapshot.AverageDailyNetProfit,
            AverageDailyExpenses = snapshot.AverageDailyExpenses,
            AverageMonthlySales = snapshot.AverageMonthlySales,
            AverageMonthlyNetProfit = snapshot.AverageMonthlyNetProfit,
            AverageMonthlyExpenses = snapshot.AverageMonthlyExpenses,
            ProfitSeriesLabel = snapshot.ProfitSeriesLabel,
            ShowExpenseSeries = snapshot.ShowExpenseSeries,
            Trend = snapshot.Trend.Select(x => new ReportTrendPoint
            {
                Label = x.Label,
                Sales = x.Sales,
                Profit = x.Profit,
                Expenses = x.Expenses
            }).ToArray(),
            ExpenseBreakdown = snapshot.ExpenseBreakdown.Select(x =>
                new ReportExpenseBreakdownItem
                {
                    Category = x.Category,
                    Amount = x.Amount,
                    Share = x.Share
                }).ToArray(),
            ThakaActivity = snapshot.ThakaActivity.Select(x =>
                new ReportThakaActivityItem
                {
                    ProjectName = x.ProjectName,
                    IssueCount = x.IssueCount,
                    MaterialValue = x.MaterialValue
                }).ToArray()
        };
    }

    private Guid RequireActor() =>
        _actorUserId()
        ?? throw new InvalidOperationException(
            "A persistent backend user session is required for this operation.");

    private static ExpensePaymentMethod ParseExpensePaymentMethod(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "CASH" => ExpensePaymentMethod.Cash,
            "BANK" => ExpensePaymentMethod.Bank,
            "OTHER" => ExpensePaymentMethod.Other,
            _ => throw new InvalidOperationException(
                "Unsupported expense payment method.")
        };

    private static string Required(string? value, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{field} is required.");
        }

        return normalized;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
