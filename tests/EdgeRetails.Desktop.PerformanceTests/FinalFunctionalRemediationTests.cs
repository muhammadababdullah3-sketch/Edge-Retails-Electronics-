using System.Collections.ObjectModel;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Application.Production.Backup;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Finance;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class FinalFunctionalRemediationTests
{
    [Fact]
    public async Task F15_RemoteBackendBusinessOperationsService_Expenses_DoesNotLoopAndForwardsKeysetCursor()
    {
        var calls = 0;
        Uri? requestedUri = null;
        using var client = CreateApiClient(request =>
        {
            calls++;
            requestedUri = request.RequestUri;
            return JsonResponse("[]");
        });

        var service = new RemoteBackendBusinessOperationsService(client);
        var beforeDate = new DateOnly(2026, 10, 1);
        var beforeId = Guid.NewGuid();

        var result = await service.GetExpensesAsync(50, beforeDate, beforeId);

        Assert.Equal(1, calls); // Strictly single-flight page, no unbounded while-loop
        Assert.Empty(result);
        var query = ParseQuery(requestedUri!);
        Assert.Equal("50", query["pageSize"]);
        Assert.Equal("2026-10-01", query["beforeExpenseDate"]);
        Assert.Equal(beforeId.ToString("D"), query["beforeExpenseId"]);
    }

    [Fact]
    public async Task F15_RemoteBackendBusinessOperationsService_Customers_DoesNotLoopAndForwardsKeysetCursor()
    {
        var calls = 0;
        Uri? requestedUri = null;
        using var client = CreateApiClient(request =>
        {
            calls++;
            requestedUri = request.RequestUri;
            return JsonResponse("[]");
        });

        var service = new RemoteBackendBusinessOperationsService(client);
        var beforeCustomerId = Guid.NewGuid();

        var result = await service.GetCustomersAsync("Acme", 75, "Acme Store", beforeCustomerId);

        Assert.Equal(1, calls); // Strictly single-flight page, no unbounded while-loop
        Assert.Empty(result);
        var query = ParseQuery(requestedUri!);
        Assert.Equal("75", query["pageSize"]);
        Assert.Equal("Acme", query["search"]);
        Assert.Equal("Acme Store", query["beforeName"]);
        Assert.Equal(beforeCustomerId.ToString("D"), query["beforeCustomerId"]);
        Assert.Equal("true", query["includeInactive"]);
    }

    [Fact]
    public async Task F15_RemoteBackendBusinessOperationsService_Suppliers_DoesNotLoopAndForwardsKeysetCursor()
    {
        var calls = 0;
        Uri? requestedUri = null;
        using var client = CreateApiClient(request =>
        {
            calls++;
            requestedUri = request.RequestUri;
            return JsonResponse("[]");
        });

        var service = new RemoteBackendBusinessOperationsService(client);
        var beforeSupplierId = Guid.NewGuid();

        var result = await service.GetSuppliersAsync("Global", 60, "Global Traders", beforeSupplierId);

        Assert.Equal(1, calls); // Strictly single-flight page, no unbounded while-loop
        Assert.Empty(result);
        var query = ParseQuery(requestedUri!);
        Assert.Equal("60", query["pageSize"]);
        Assert.Equal("Global", query["search"]);
        Assert.Equal("Global Traders", query["beforeName"]);
        Assert.Equal(beforeSupplierId.ToString("D"), query["beforeSupplierId"]);
    }

    [Fact]
    public async Task F15_ExpensesViewModel_InitialPageIsBounded_AndLoadMoreUsesKeysetTieBreaker()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var date1 = new DateTime(2026, 10, 5);
        var date2 = new DateTime(2026, 10, 4);

        var firstPage = Enumerable.Range(0, 100).Select(i => new ExpenseRecord
        {
            Id = $"EXP-{i:D3}",
            BackendId = i == 99 ? id1 : Guid.NewGuid(),
            Category = "General",
            Subcategory = "Office",
            Amount = 100m,
            Date = i == 99 ? date1 : DateTime.UtcNow,
            PaymentMethod = "Cash",
            StaffMember = "Admin",
            Note = "Test"
        }).ToList();

        var secondPage = new List<ExpenseRecord>
        {
            new()
            {
                Id = "EXP-100",
                BackendId = id2,
                Category = "General",
                Subcategory = "Office",
                Amount = 100m,
                Date = date2,
                PaymentMethod = "Cash",
                StaffMember = "Admin",
                Note = "Next"
            }
        };

        int? requestedPageSize = null;
        DateOnly? observedBeforeDate = null;
        Guid? observedBeforeId = null;

        var mock = new StubBusinessOperationsService
        {
            OnGetExpenses = (size, beforeDate, beforeId) =>
            {
                requestedPageSize = size;
                observedBeforeDate = beforeDate;
                observedBeforeId = beforeId;
                return Task.FromResult<IReadOnlyList<ExpenseRecord>>(beforeDate.HasValue ? secondPage : firstPage);
            }
        };

        var vm = new ExpensesViewModel(new StubToastService(), new DialogService(), mock);
        vm.SelectPeriodCommand.Execute("All");
        // Allow initial backend refresh task to settle
        await Task.Delay(50);

        Assert.Equal(100, requestedPageSize);
        Assert.Equal(100, vm.FilteredExpenses.Count);
        Assert.True(vm.HasMoreExpenses);
        Assert.True(vm.CanLoadMoreExpenses);

        // Execute LoadMoreExpensesCommand
        vm.LoadMoreExpensesCommand.Execute(null);
        await Task.Delay(50);

        // Keyset tie-breaker: (ExpenseDate DESC, Id DESC)
        Assert.Equal(DateOnly.FromDateTime(date1), observedBeforeDate);
        Assert.Equal(id1, observedBeforeId);
        Assert.Equal(101, vm.FilteredExpenses.Count);
        Assert.False(vm.HasMoreExpenses);
    }

    [Fact]
    public async Task F15_CustomersViewModel_InitialPageIsBounded_AndLoadMoreUsesKeysetTieBreaker()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var firstPage = Enumerable.Range(0, 100).Select(i => new CustomerDirectoryRecord
        {
            Id = $"CUST-{i:D3}",
            BackendId = i == 99 ? id1 : Guid.NewGuid(),
            Name = i == 99 ? "Zeta Corp" : $"Customer {i:D3}",
            Phone = "123456",
            Address = "Address",
            Notes = "Notes",
            IsActive = true
        }).ToList();

        var secondPage = new List<CustomerDirectoryRecord>
        {
            new()
            {
                Id = "CUST-100",
                BackendId = id2,
                Name = "Zeta Plus",
                Phone = "123456",
                Address = "Address",
                Notes = "Notes",
                IsActive = true
            }
        };

        int? requestedPageSize = null;
        string? observedBeforeName = null;
        Guid? observedBeforeCustomerId = null;

        var mock = new StubBusinessOperationsService
        {
            OnGetCustomers = (search, size, beforeName, beforeCustomerId) =>
            {
                requestedPageSize = size;
                observedBeforeName = beforeName;
                observedBeforeCustomerId = beforeCustomerId;
                return Task.FromResult<IReadOnlyList<CustomerDirectoryRecord>>(beforeCustomerId.HasValue ? secondPage : firstPage);
            }
        };

        var vm = new CustomersViewModel(new StubToastService(), new DialogService(), new DrawerService(), mock);
        await Task.Delay(50);

        Assert.Equal(100, requestedPageSize);
        Assert.Equal(100, vm.FilteredCustomers.Count);
        Assert.True(vm.HasMoreCustomers);
        Assert.True(vm.CanLoadMoreCustomers);

        // Execute LoadMoreCustomersCommand
        vm.LoadMoreCustomersCommand.Execute(null);
        await Task.Delay(50);

        // Keyset tie-breaker: (Name ASC, Id ASC)
        Assert.Equal("Zeta Corp", observedBeforeName);
        Assert.Equal(id1, observedBeforeCustomerId);
        Assert.Equal(101, vm.FilteredCustomers.Count);
        Assert.False(vm.HasMoreCustomers);
    }

    [Fact]
    public async Task F15_SuppliersViewModel_InitialPageIsBounded_AndLoadMoreUsesKeysetTieBreaker()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var firstPage = Enumerable.Range(0, 100).Select(i => new SupplierDirectoryRecord
        {
            Id = $"SUP-{i:D3}",
            BackendId = i == 99 ? id1 : Guid.NewGuid(),
            Name = i == 99 ? "Zenith Suppliers" : $"Supplier {i:D3}",
            Phone = "123456",
            City = "Metropolis",
            Address = "Address",
            Notes = "Notes"
        }).ToList();

        var secondPage = new List<SupplierDirectoryRecord>
        {
            new()
            {
                Id = "SUP-100",
                BackendId = id2,
                Name = "Zenith World",
                Phone = "123456",
                City = "Metropolis",
                Address = "Address",
                Notes = "Notes"
            }
        };

        int? requestedPageSize = null;
        string? observedBeforeName = null;
        Guid? observedBeforeSupplierId = null;

        var mock = new StubBusinessOperationsService
        {
            OnGetSuppliers = (search, size, beforeName, beforeSupplierId) =>
            {
                requestedPageSize = size;
                observedBeforeName = beforeName;
                observedBeforeSupplierId = beforeSupplierId;
                return Task.FromResult<IReadOnlyList<SupplierDirectoryRecord>>(beforeSupplierId.HasValue ? secondPage : firstPage);
            }
        };

        var vm = new SuppliersViewModel(new StubToastService(), new DialogService(), new DrawerService(), mock);
        await Task.Delay(50);

        Assert.Equal(100, requestedPageSize);
        Assert.Equal(100, vm.FilteredSuppliers.Count);
        Assert.True(vm.HasMoreSuppliers);
        Assert.True(vm.CanLoadMoreSuppliers);

        // Execute LoadMoreSuppliersCommand
        vm.LoadMoreSuppliersCommand.Execute(null);
        await Task.Delay(50);

        // Keyset tie-breaker: (Name ASC, Id ASC)
        Assert.Equal("Zenith Suppliers", observedBeforeName);
        Assert.Equal(id1, observedBeforeSupplierId);
        Assert.Equal(101, vm.FilteredSuppliers.Count);
        Assert.False(vm.HasMoreSuppliers);
    }

    [Fact]
    public async Task F15_SupplierDetailViewModel_StatementPaging_FollowsDeterministicKeyset()
    {
        var supplierId = Guid.NewGuid();
        var supplier = new SupplierDirectoryRecord
        {
            Id = supplierId.ToString("D"),
            BackendId = supplierId,
            Name = "Apex Supplies"
        };

        var now = DateTimeOffset.UtcNow;
        var entryId1 = Guid.NewGuid();
        var entryId2 = Guid.NewGuid();

        var firstPageStatement = Enumerable.Range(0, 100).Select(i => new SupplierKhataEntryDto(
            i == 99 ? entryId1 : Guid.NewGuid(),
            $"ENT-{i:D3}",
            SupplierAccountEntryType.Purchase,
            SupplierAccountDirection.IncreasePayable,
            100m,
            100m,
            100m * (i + 1),
            "PURCHASE",
            Guid.NewGuid(),
            i == 99 ? now.AddHours(-1) : now,
            i == 99 ? now.AddHours(-2) : now,
            Guid.NewGuid(),
            null,
            null)).ToList();

        var secondPageStatement = new List<SupplierKhataEntryDto>
        {
            new(entryId2, "ENT-100", SupplierAccountEntryType.Purchase, SupplierAccountDirection.IncreasePayable,
                50m, 50m, 10050m, "PURCHASE", Guid.NewGuid(), now.AddHours(-3), now.AddHours(-4), Guid.NewGuid(), null, null)
        };

        int? observedPageSize = null;
        DateTimeOffset? observedOccurredAt = null;
        DateTimeOffset? observedCreatedAt = null;
        Guid? observedEntryId = null;

        var mockOps = new StubOperationsService
        {
            OnGetSupplierWorkspace = (id, size, beforeOccurredAt, beforeCreatedAt, beforeEntryId) =>
            {
                if (size != 1)
                {
                    observedPageSize = size;
                }
                observedOccurredAt = beforeOccurredAt;
                observedCreatedAt = beforeCreatedAt;
                observedEntryId = beforeEntryId;

                var statement = beforeOccurredAt.HasValue ? secondPageStatement : firstPageStatement;
                return Task.FromResult(new SupplierAccountWorkspaceDto(
                    new(id, 1000m, 1000m, 0m, 0m, 0m, 1000m, 0m, 1000m, 0m, 0m),
                    statement,
                    [],
                    [],
                    [],
                    new(0, 0, 0, 0, 0, 0)));
            }
        };

        var vm = new SupplierDetailViewModel(
            supplier,
            new DrawerService(),
            new DialogService(),
            new StubToastService(),
            operationsService: mockOps);

        await Task.Delay(50);

        Assert.Equal(100, observedPageSize);
        Assert.Equal(100, vm.Workspace!.Statement.Count);
        Assert.True(vm.HasMoreStatement);
        Assert.True(vm.CanLoadMoreStatement);

        // Execute LoadMoreStatementCommand
        vm.LoadMoreStatementCommand.Execute(null);
        await Task.Delay(50);

        // Keyset tie-breaker: (OccurredAt DESC, CreatedAt DESC, EntryId DESC)
        Assert.Equal(now.AddHours(-1), observedOccurredAt);
        Assert.Equal(now.AddHours(-2), observedCreatedAt);
        Assert.Equal(entryId1, observedEntryId);
        Assert.Equal(101, vm.Workspace.Statement.Count);
        Assert.False(vm.HasMoreStatement);
    }

    [Fact]
    public void F16_BackupDiagnosticsDisplay_Format_EvaluatesFreshnessTruthfully()
    {
        // 1. Never Run: 0 verified backups
        var emptyDiagnostics = new BackupHistoryDiagnosticsResponse(0, [], 0, []);
        var neverRunText = BackupDiagnosticsDisplay.Format(emptyDiagnostics);
        Assert.StartsWith("Never Run", neverRunText);
        Assert.Contains("No verified backups", neverRunText);

        // 2. Fresh: verified backup within 24 hours
        var freshTime = DateTimeOffset.UtcNow.AddHours(-2);
        var freshDiagnostics = new BackupHistoryDiagnosticsResponse(
            1,
            [new SafeBackupRecord(Guid.NewGuid(), freshTime, 1024, "18", "18", "1", null, "protected", 1)],
            0,
            []);
        var freshText = BackupDiagnosticsDisplay.Format(freshDiagnostics);
        Assert.StartsWith("Fresh", freshText);
        Assert.Contains(freshTime.ToString("yyyy-MM-dd HH:mm"), freshText);

        // 3. Stale: verified backup older than 24 hours
        var staleTime = DateTimeOffset.UtcNow.AddHours(-25);
        var staleDiagnostics = new BackupHistoryDiagnosticsResponse(
            1,
            [new SafeBackupRecord(Guid.NewGuid(), staleTime, 1024, "18", "18", "1", null, "protected", 1)],
            1,
            [new BackupHistoryIssueCount("bad_crc", 1)]);
        var staleText = BackupDiagnosticsDisplay.Format(staleDiagnostics);
        Assert.StartsWith("Stale", staleText);
        Assert.Contains("1 invalid artifact(s)", staleText);
    }

    [Fact]
    public async Task F16_RemoteBackendSettingsService_TruthfulDiagnosticsReporting()
    {
        using var api = CreateApiClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/settings" => JsonResponse("{\"shopName\":\"Edge Shop\"}"),
            "/api/auth/accounts" => JsonResponse("[]"),
            "/api/system/ready" => JsonResponse("{\"status\":\"Ready\",\"canConnect\":true,\"hasPendingMigrations\":false}"),
            "/api/backups/diagnostics" => JsonResponse("{\"verifiedCount\":0,\"verifiedBackups\":[],\"invalidArtifactCount\":0,\"invalidArtifacts\":[]}"),
            _ => throw new InvalidOperationException($"Unexpected endpoint: {request.RequestUri}")
        });

        var service = new RemoteBackendSettingsService(api, () => Guid.NewGuid());
        var settings = await service.LoadAsync();

        // Must report truthful values rather than fabricated metrics
        Assert.Equal("Ready", settings.DatabaseStatus);
        Assert.Equal("Unavailable · Storage metrics not exposed by server", settings.DatabaseSize);
        Assert.Equal("Unavailable · Background worker heartbeat endpoint not attached", settings.WorkerStatus);
        Assert.Equal("Unavailable · License server endpoint not attached", settings.LicenseStatus);
        Assert.StartsWith("Never Run", settings.LastBackupDisplay);
    }

    private static DesktopApiClient CreateApiClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        return new DesktopApiClient(http, ownsClient: true);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    };

    private static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
            parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty,
            StringComparer.OrdinalIgnoreCase);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubToastService : IToastService
    {
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; } = new(new ObservableCollection<ToastMessage>()); public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }

    private sealed class StubBusinessOperationsService : IBackendBusinessOperationsService
    {
        public Func<int, DateOnly?, Guid?, Task<IReadOnlyList<ExpenseRecord>>>? OnGetExpenses { get; init; }
        public Func<string?, int, string?, Guid?, Task<IReadOnlyList<CustomerDirectoryRecord>>>? OnGetCustomers { get; init; }
        public Func<string?, int, string?, Guid?, Task<IReadOnlyList<SupplierDirectoryRecord>>>? OnGetSuppliers { get; init; }

        public Task<IReadOnlyList<string>> GetExpenseCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["General"]);

        public Task<IReadOnlyList<ExpenseRecord>> GetExpensesAsync(CancellationToken cancellationToken = default) =>
            GetExpensesAsync(100, null, null, cancellationToken);

        public Task<IReadOnlyList<ExpenseRecord>> GetExpensesAsync(int pageSize, DateOnly? beforeExpenseDate = null, Guid? beforeExpenseId = null, CancellationToken cancellationToken = default) =>
            OnGetExpenses?.Invoke(pageSize, beforeExpenseDate, beforeExpenseId) ?? Task.FromResult<IReadOnlyList<ExpenseRecord>>([]);

        public Task<ExpenseRecord> PostExpenseAsync(string category, string subcategory, decimal amount, DateTime date, string paymentMethod, string note, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<CustomerDirectoryRecord>> GetCustomersAsync(string? search, int pageSize = 100, CancellationToken cancellationToken = default) =>
            GetCustomersAsync(search, pageSize, null, null, cancellationToken);

        public Task<IReadOnlyList<CustomerDirectoryRecord>> GetCustomersAsync(string? search, int pageSize, string? beforeName, Guid? beforeCustomerId, CancellationToken cancellationToken = default) =>
            OnGetCustomers?.Invoke(search, pageSize, beforeName, beforeCustomerId) ?? Task.FromResult<IReadOnlyList<CustomerDirectoryRecord>>([]);

        public Task SaveCustomerAsync(CustomerDirectoryRecord? existing, string name, string phone, string address, string notes, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<SupplierDirectoryRecord>> GetSuppliersAsync(string? search, int pageSize = 100, CancellationToken cancellationToken = default) =>
            GetSuppliersAsync(search, pageSize, null, null, cancellationToken);

        public Task<IReadOnlyList<SupplierDirectoryRecord>> GetSuppliersAsync(string? search, int pageSize, string? beforeName, Guid? beforeSupplierId, CancellationToken cancellationToken = default) =>
            OnGetSuppliers?.Invoke(search, pageSize, beforeName, beforeSupplierId) ?? Task.FromResult<IReadOnlyList<SupplierDirectoryRecord>>([]);

        public Task SaveSupplierAsync(SupplierDirectoryRecord? existing, string name, string phone, string city, string address, string notes, string? explicitDealerPrefix = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ReportSnapshot> GetReportAsync(ReportPeriodMode mode, DateTime selectedDate, int selectedMonth, int selectedYear, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class StubOperationsService : IBackendOperationsService
    {
        public Func<Guid, int, DateTimeOffset?, DateTimeOffset?, Guid?, Task<SupplierAccountWorkspaceDto>>? OnGetSupplierWorkspace { get; init; }

        public Task<SupplierAccountWorkspaceDto> GetSupplierWorkspaceAsync(Guid supplierId, int pageSize = 200, DateTimeOffset? beforeOccurredAt = null, DateTimeOffset? beforeCreatedAt = null, Guid? beforeEntryId = null, CancellationToken cancellationToken = default) =>
            OnGetSupplierWorkspace?.Invoke(supplierId, pageSize, beforeOccurredAt, beforeCreatedAt, beforeEntryId)
            ?? Task.FromResult(new SupplierAccountWorkspaceDto(
                new(supplierId, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), [], [], [], [], new(0, 0, 0, 0, 0, 0)));

        public Task CreateSupplierPaymentAsync(Guid supplierId, decimal amount, SupplierPaymentPurpose purpose, SupplierSettlementMethod method, Guid clientOperationId, string? externalReference, string? note, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task ReverseSupplierPaymentAsync(Guid paymentId, string reason, Guid clientOperationId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task CreateSupplierRefundAsync(Guid supplierId, decimal amount, SupplierSettlementMethod method, Guid clientOperationId, string? externalReference, string? note, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task ReverseSupplierRefundAsync(Guid refundId, string reason, Guid clientOperationId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<WarrantyDashboardDto> GetWarrantyDashboardAsync(
        string? search,
        int pageSize = 100,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeWorkId = null,
        CancellationToken cancellationToken = default,
        WarrantyWorkKind? beforeWorkKind = null) => throw new NotImplementedException();

        public Task<IReadOnlyList<WarrantyEventDto>> GetWarrantyClaimTimelineAsync(
        Guid claimId,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<WarrantyClaimIntakeRowDto>> SearchWarrantyClaimIntakeAsync(
        string search,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<Guid> CreateWarrantyClaimAsync(
        Guid customerId,
        Guid saleId,
        Guid? supplierId,
        Guid productId,
        decimal quantity,
        Guid saleItemId,
        string faultDescription,
        IReadOnlyList<WarrantyClaimUnitInput>? units,
        Guid clientOperationId,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task BeginWarrantyClaimReviewAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task SendWarrantyClaimToSupplierAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task MarkWarrantySupplierProcessingAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task ResolveWarrantyClaimAsync(
        Guid claimId,
        WarrantyResolutionType resolution,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task ReceiveCustomerWarrantyReplacementAsync(
        Guid claimId,
        IReadOnlyList<CustomerWarrantyReplacementUnitInput> units,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task HandoverWarrantyClaimAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task CancelWarrantyClaimAsync(
        Guid claimId,
        Guid clientOperationId,
        string? note,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<Guid> SendShopStockWarrantyAsync(
        Guid productId,
        InventoryBucket sourceBucket,
        decimal quantity,
        Guid supplierId,
        Guid? sourcePurchaseItemId,
        string faultDescription,
        Guid clientOperationId,
        IReadOnlyCollection<Guid>? inventoryUnitIds,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task ReceiveShopStockWarrantyAsync(
        Guid caseId,
        WarrantyResolutionType resolution,
        IReadOnlyCollection<Guid>? originalInventoryUnitIds,
        IReadOnlyList<ReplacementSerializedUnitInput>? replacementUnits,
        string? note,
        Guid clientOperationId,
        decimal? supplierCreditAmount,
        string? supplierReference,
        CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
