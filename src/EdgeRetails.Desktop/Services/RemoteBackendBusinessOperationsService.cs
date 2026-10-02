using System.Net;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Desktop.Services;

/// <summary>Supplier/customer directories, expenses, and reports over the local Server API.</summary>
public sealed class RemoteBackendBusinessOperationsService(DesktopApiClient apiClient) : IBackendBusinessOperationsService
{
    private Guid? _pendingExpenseOperationId;
    private string? _pendingExpensePayload;
    private Guid? _pendingCustomerOperationId;
    private string? _pendingCustomerPayload;
    private Guid? _pendingSupplierOperationId;
    private string? _pendingSupplierPayload;

    public async Task<IReadOnlyList<string>> GetExpenseCategoriesAsync(CancellationToken cancellationToken = default) =>
        [.. (await apiClient.GetAsync<IReadOnlyList<ExpenseCategoryDto>>("/api/expenses/categories", cancellationToken)).Select(row => row.Name)];

    public async Task<IReadOnlyList<ExpenseRecord>> GetExpensesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await GetAllExpensesAsync(cancellationToken);
        return [.. rows.Select(MapExpense)];
    }

    public async Task<ExpenseRecord> PostExpenseAsync(string category, string subcategory, decimal amount,
        DateTime date, string paymentMethod, string note, CancellationToken cancellationToken = default)
    {
        var categories = await apiClient.GetAsync<IReadOnlyList<ExpenseCategoryDto>>("/api/expenses/categories", cancellationToken);
        var categoryMatches = categories.Where(x => string.Equals(x.Name, category?.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (categoryMatches.Length != 1)
        {
            throw new InvalidOperationException("Selected expense category is not available uniquely in the backend.");
        }

        var subcategories = await apiClient.GetAsync<IReadOnlyList<ExpenseSubcategoryDto>>("/api/expenses/subcategories", cancellationToken);
        var subcategoryMatches = subcategories.Where(x => x.CategoryId == categoryMatches[0].Id &&
            string.Equals(x.Name, subcategory?.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var method = paymentMethod.Trim().ToUpperInvariant() switch
        {
            "CASH" => ExpensePaymentMethod.Cash,
            "BANK" => ExpensePaymentMethod.Bank,
            "OTHER" => ExpensePaymentMethod.Other,
            _ => throw new InvalidOperationException("Unsupported expense payment method.")
        };
        var payloadKey = string.Join("|", categoryMatches[0].Id, subcategoryMatches.SingleOrDefault()?.Id,
            DateOnly.FromDateTime(date), amount, method, subcategory.Trim(), note?.Trim());
        if (!string.Equals(payloadKey, _pendingExpensePayload, StringComparison.Ordinal) || _pendingExpenseOperationId is null)
        {
            _pendingExpensePayload = payloadKey;
            _pendingExpenseOperationId = Guid.CreateVersion7();
        }
        PostExpenseResult result;
        try
        {
            result = await apiClient.PostAsync<PostExpenseRequest, PostExpenseResult>("/api/expenses",
                new(_pendingExpenseOperationId.Value, categoryMatches[0].Id, subcategoryMatches.SingleOrDefault()?.Id,
                DateOnly.FromDateTime(date), amount, method, subcategory.Trim(), string.IsNullOrWhiteSpace(note) ? null : note.Trim()),
                cancellationToken);
        }
        catch (DesktopApiException ex) when (IsDefinitiveRejection(ex.StatusCode))
        {
            _pendingExpenseOperationId = null;
            _pendingExpensePayload = null;
            throw;
        }
        var rows = await GetAllExpensesAsync(cancellationToken);
        var committed = rows.SingleOrDefault(x => x.ExpenseId == result.ExpenseId)
            ?? throw new InvalidOperationException("Expense was posted but could not be read back from backend authority; retry with the same operation intent.");
        _pendingExpenseOperationId = null;
        _pendingExpensePayload = null;
        return MapExpense(committed);
    }

    public async Task<IReadOnlyList<CustomerDirectoryRecord>> GetCustomersAsync(string? search, int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var rows = await GetAllCustomersAsync(search, cancellationToken);
        return [.. rows.Select(x => new CustomerDirectoryRecord
        {
            Id = x.CustomerId.ToString("D"), BackendId = x.CustomerId, Name = x.Name, Phone = x.Phone ?? string.Empty,
            Address = x.Address ?? string.Empty, Notes = x.Notes ?? string.Empty, LocalSales = x.NetSales,
            LastSale = x.LastSaleAt?.LocalDateTime, ActiveThaka = x.ActiveThakaProject ?? "—"
        })];
    }

    public async Task SaveCustomerAsync(CustomerDirectoryRecord? existing, string name, string phone, string address,
        string notes, CancellationToken cancellationToken = default)
    {
        var payload = string.Join("|", existing?.BackendId, name.Trim(), Normalize(phone), Normalize(address), Normalize(notes));
        var operationId = GetStableIntentId(ref _pendingCustomerOperationId, ref _pendingCustomerPayload, payload, "customer");
        var request = new SaveCustomerRequest(name.Trim(), Normalize(phone), Normalize(address), true, Normalize(notes),
            CorrelationId: Guid.CreateVersion7(), ClientOperationId: operationId);
        try
        {
            if (existing?.BackendId is Guid id)
            {
                await apiClient.PutAsync<SaveCustomerRequest, Guid>($"/api/customers/{id:D}", request, cancellationToken);
            }
            else
            {
                await apiClient.PostAsync<SaveCustomerRequest, Guid>("/api/customers", request, cancellationToken);
            }

            _pendingCustomerOperationId = null;
            _pendingCustomerPayload = null;
        }
        catch (DesktopApiException ex) when (IsDefinitiveRejection(ex.StatusCode))
        {
            _pendingCustomerOperationId = null;
            _pendingCustomerPayload = null;
            throw;
        }
    }

    public async Task<IReadOnlyList<SupplierDirectoryRecord>> GetSuppliersAsync(string? search, int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var rows = await GetAllSuppliersAsync(search, cancellationToken);
        return [.. rows.Select(x => new SupplierDirectoryRecord
        {
            Id = x.SupplierId.ToString("D"), BackendId = x.SupplierId, Name = x.Name, Phone = x.Phone ?? string.Empty,
            City = x.City ?? string.Empty, Address = x.Address ?? string.Empty, Notes = x.Notes ?? string.Empty,
            TotalPurchases = x.NetPurchases, LastPurchase = x.LastPurchaseDate?.ToDateTime(TimeOnly.MinValue)
        })];
    }

    public async Task SaveSupplierAsync(SupplierDirectoryRecord? existing, string name, string phone, string city,
        string address, string notes, CancellationToken cancellationToken = default)
    {
        var payload = string.Join("|", existing?.BackendId, name.Trim(), Normalize(phone), Normalize(city), Normalize(address), Normalize(notes));
        var operationId = GetStableIntentId(ref _pendingSupplierOperationId, ref _pendingSupplierPayload, payload, "supplier");
        var request = new SaveSupplierRequest(name.Trim(), Normalize(phone), Normalize(city), Normalize(address), true,
            Normalize(notes), CorrelationId: Guid.CreateVersion7(), ClientOperationId: operationId);
        try
        {
            if (existing?.BackendId is Guid id)
            {
                await apiClient.PutAsync<SaveSupplierRequest, Guid>($"/api/suppliers/{id:D}", request, cancellationToken);
            }
            else
            {
                await apiClient.PostAsync<SaveSupplierRequest, Guid>("/api/suppliers", request, cancellationToken);
            }

            _pendingSupplierOperationId = null;
            _pendingSupplierPayload = null;
        }
        catch (DesktopApiException ex) when (IsDefinitiveRejection(ex.StatusCode))
        {
            _pendingSupplierOperationId = null;
            _pendingSupplierPayload = null;
            throw;
        }
    }

    public async Task<ReportSnapshot> GetReportAsync(ReportPeriodMode mode, DateTime selectedDate,
        int selectedMonth, int selectedYear, CancellationToken cancellationToken = default)
    {
        var kind = mode switch { ReportPeriodMode.Daily => ReportingPeriodKind.Daily, ReportPeriodMode.Monthly => ReportingPeriodKind.Monthly, ReportPeriodMode.Yearly => ReportingPeriodKind.Yearly, _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
        var date = DateOnly.FromDateTime(selectedDate);
        var dto = await apiClient.GetAsync<ReportingSnapshotDto>(
            $"/api/reports/snapshot?mode={kind}&date={date:yyyy-MM-dd}&month={selectedMonth}&year={selectedYear}", cancellationToken);
        return new ReportSnapshot
        {
            Mode = mode, PeriodLabel = dto.PeriodLabel, NetSales = dto.NetSales, GrossProfit = dto.GrossProfit,
            Expenses = dto.Expenses, NetProfit = dto.NetProfit, Purchases = dto.Purchases, ThakaMaterial = dto.ThakaMaterial,
            SalesCount = dto.SalesCount, AverageInvoiceValue = dto.AverageInvoiceValue,
            AverageGrossProfitPerInvoice = dto.AverageGrossProfitPerInvoice, AverageItemsPerInvoice = dto.AverageItemsPerInvoice,
            AverageDailySales = dto.AverageDailySales, AverageDailyGrossProfit = dto.AverageDailyGrossProfit,
            AverageDailyNetProfit = dto.AverageDailyNetProfit, AverageDailyExpenses = dto.AverageDailyExpenses,
            AverageMonthlySales = dto.AverageMonthlySales, AverageMonthlyNetProfit = dto.AverageMonthlyNetProfit,
            AverageMonthlyExpenses = dto.AverageMonthlyExpenses, ProfitSeriesLabel = dto.ProfitSeriesLabel,
            ShowExpenseSeries = dto.ShowExpenseSeries,
            Trend = [.. dto.Trend.Select(x => new ReportTrendPoint { Label = x.Label, Sales = x.Sales, Profit = x.Profit, Expenses = x.Expenses })],
            ExpenseBreakdown = [.. dto.ExpenseBreakdown.Select(x => new ReportExpenseBreakdownItem { Category = x.Category, Amount = x.Amount, Share = x.Share })],
            ThakaActivity = [.. dto.ThakaActivity.Select(x => new ReportThakaActivityItem { ProjectName = x.ProjectName, IssueCount = x.IssueCount, MaterialValue = x.MaterialValue })]
        };
    }

    private async Task<IReadOnlyList<ExpenseRowDto>> GetAllExpensesAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var result = new List<ExpenseRowDto>();
        DateOnly? beforeDate = null;
        Guid? beforeId = null;
        while (true)
        {
            var path = $"/api/expenses?pageSize={pageSize}";
            if (beforeDate.HasValue && beforeId.HasValue)
            {
                path += $"&beforeExpenseDate={beforeDate:yyyy-MM-dd}&beforeExpenseId={beforeId:D}";
            }

            var page = await apiClient.GetAsync<IReadOnlyList<ExpenseRowDto>>(path, cancellationToken);
            result.AddRange(page);
            if (page.Count < pageSize)
            {
                return result;
            }

            var last = page[^1];
            beforeDate = last.ExpenseDate;
            beforeId = last.ExpenseId;
        }
    }

    private async Task<IReadOnlyList<CustomerDirectoryDto>> GetAllCustomersAsync(string? search, CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var result = new List<CustomerDirectoryDto>();
        string? beforeName = null;
        Guid? beforeId = null;
        while (true)
        {
            var path = $"/api/customers?search={Uri.EscapeDataString(search ?? string.Empty)}&pageSize={pageSize}";
            if (beforeName is not null && beforeId.HasValue)
            {
                path += $"&beforeName={Uri.EscapeDataString(beforeName)}&beforeCustomerId={beforeId:D}";
            }

            var page = await apiClient.GetAsync<IReadOnlyList<CustomerDirectoryDto>>(path, cancellationToken);
            result.AddRange(page);
            if (page.Count < pageSize)
            {
                return result;
            }

            beforeName = page[^1].Name;
            beforeId = page[^1].CustomerId;
        }
    }

    private async Task<IReadOnlyList<SupplierDirectoryDto>> GetAllSuppliersAsync(string? search, CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var result = new List<SupplierDirectoryDto>();
        string? beforeName = null;
        Guid? beforeId = null;
        while (true)
        {
            var path = $"/api/suppliers?search={Uri.EscapeDataString(search ?? string.Empty)}&pageSize={pageSize}";
            if (beforeName is not null && beforeId.HasValue)
            {
                path += $"&beforeName={Uri.EscapeDataString(beforeName)}&beforeSupplierId={beforeId:D}";
            }

            var page = await apiClient.GetAsync<IReadOnlyList<SupplierDirectoryDto>>(path, cancellationToken);
            result.AddRange(page);
            if (page.Count < pageSize)
            {
                return result;
            }

            beforeName = page[^1].Name;
            beforeId = page[^1].SupplierId;
        }
    }

    private static Guid GetStableIntentId(ref Guid? id, ref string? payload, string requestedPayload, string operation)
    {
        if (id.HasValue && payload != requestedPayload)
        {
            throw new InvalidOperationException($"The previous {operation} outcome is unknown. Refresh and reconcile it before starting another save.");
        }

        if (!id.HasValue)
        {
            id = Guid.CreateVersion7();
            payload = requestedPayload;
        }
        return id.Value;
    }

    private static bool IsDefinitiveRejection(HttpStatusCode? status) => status is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError;

    private static ExpenseRecord MapExpense(ExpenseRowDto row) => new()
    {
        Id = row.ExpenseNumber, BackendId = row.ExpenseId, Category = row.CategoryName,
        Subcategory = row.SubcategoryName ?? row.Description, Amount = row.Amount,
        Date = row.ExpenseDate.ToDateTime(TimeOnly.MinValue), PaymentMethod = row.PaymentMethod.ToString(),
        StaffMember = row.CreatedByName, Note = row.Reference ?? string.Empty
    };

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record SaveCustomerRequest(string Name, string? Phone, string? Address, bool IsActive, string? Notes, Guid? ActorId = null, Guid? CorrelationId = null, Guid? ClientOperationId = null);
    private sealed record SaveSupplierRequest(string Name, string? Phone, string? City, string? Address, bool IsActive, string? Notes, string? ExplicitDealerPrefix = null, Guid? ActorId = null, Guid? CorrelationId = null, Guid? ClientOperationId = null);
    private sealed record PostExpenseRequest(Guid ClientOperationId, Guid CategoryId, Guid? SubcategoryId, DateOnly ExpenseDate, decimal Amount, ExpensePaymentMethod PaymentMethod, string Description, string? Reference = null, Guid? ActorId = null);
    private sealed record PostExpenseResult(Guid ExpenseId, string ExpenseNumber, bool WasExisting);
}
