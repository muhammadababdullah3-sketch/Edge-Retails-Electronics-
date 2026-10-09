using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase7F15SupplierAdapterTests
{
    [Fact]
    public async Task SupplierDetail_UsesRealAdapterForInitialPageAndContinuation()
    {
        var fixture = new WorkspaceServer(101, true);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(),
            operationsService: new RemoteBackendOperationsService(api));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.Workspace is null || vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(100, vm.Workspace.Statement.Count);
        Assert.True(vm.HasMoreStatement);
        vm.LoadMoreStatementCommand.Execute(null);
        while (vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(101, vm.Workspace.Statement.Count);
        Assert.Equal(101, vm.Workspace.Statement.Select(x => x.EntryId).Distinct().Count());
        Assert.False(vm.HasMoreStatement);
    }

    [Theory]
    [InlineData(101, false)]
    [InlineData(251, false)]
    [InlineData(251, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    public async Task RealAdapter_ServerChronologicalPages_AreBoundedCompleteAndTruthful(int count, bool equalTimes)
    {
        var fixture = new WorkspaceServer(count, equalTimes);
        using var api = fixture.Api();
        IBackendOperationsService service = new RemoteBackendOperationsService(api);
        var collected = new List<SupplierKhataEntryDto>();
        var page = await service.GetSupplierStatementPageAsync(fixture.SupplierId, 100);
        Assert.Equal(Math.Min(100, count), page.Workspace.Statement.Count);
        Assert.Equal(count > 100, page.HasMore);
        while (true)
        {
            Assert.InRange(page.Workspace.Statement.Count, 0, 100);
            collected.AddRange(page.Workspace.Statement);
            if (!page.HasMore)
            {
                break;
            }
            var oldest = page.Workspace.Statement[^1];
            page = await service.GetSupplierStatementPageAsync(fixture.SupplierId, 100,
                oldest.OccurredAt, oldest.CreatedAt, oldest.EntryId);
        }
        Assert.Equal(count, collected.Count);
        Assert.Equal(count, collected.Select(x => x.EntryId).Distinct().Count());
        Assert.Equal(fixture.Rows.Select(x => x.EntryId), collected.Select(x => x.EntryId));
        if (count > 0)
        {
            var last = collected[^1];
            var terminal = await service.GetSupplierStatementPageAsync(fixture.SupplierId, 100,
                last.OccurredAt, last.CreatedAt, last.EntryId);
            Assert.Empty(terminal.Workspace.Statement);
            Assert.False(terminal.HasMore);
        }
        Assert.True(fixture.Calls < 12);
    }

    [Fact]
    public async Task InitialStatementBound_DoesNotLoseIndependentHistories()
    {
        var fixture = new WorkspaceServer(251, true, mixed: true);
        using var api = fixture.Api();
        IBackendOperationsService service = new RemoteBackendOperationsService(api);
        var page = await service.GetSupplierStatementPageAsync(fixture.SupplierId, 100);
        Assert.Equal(100, page.Workspace.Statement.Count);
        Assert.True(page.HasMore);
        Assert.Equal(100, page.Workspace.Payments.Count);
        Assert.Equal(100, page.Workspace.Refunds.Count);
        Assert.Equal(100, page.Workspace.SuppliedProducts.Count);
        Assert.Equal(100, page.Workspace.Payments.Select(x => x.PaymentId).Distinct().Count());
        Assert.Equal(100, page.Workspace.Refunds.Select(x => x.RefundId).Distinct().Count());
        Assert.Equal(100, page.Workspace.SuppliedProducts.Select(x => x.ProductId).Distinct().Count());
        Assert.Equal(2, fixture.Calls); // one bounded workspace page and one statement probe
    }

    [Fact]
    public async Task IgnoredStatementCursorFailsClosed()
    {
        var fixture = new WorkspaceServer(101, true);
        fixture.IgnoreStatementCursor = true;
        using var api = fixture.Api();
        IBackendOperationsService service = new RemoteBackendOperationsService(api);
        var error = await Assert.ThrowsAsync<DesktopApiException>(() => service.GetSupplierStatementPageAsync(fixture.SupplierId, 100));
        Assert.Equal("gateway.cursor_invalid", error.Code);
        Assert.Equal(2, fixture.Calls);
    }

    [Fact]
    public async Task Gap2_01_InitialSupplierWorkspace_BoundedForStatementEntries()
    {
        var fixture = new WorkspaceServer(350, false, mixed: true);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(),
            operationsService: new RemoteBackendOperationsService(api));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.Workspace is null || vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.NotNull(vm.Workspace);
        Assert.Equal(100, vm.Workspace.Statement.Count);
        Assert.True(vm.HasMoreStatement);
    }

    [Fact]
    public async Task Gap2_02_InitialSupplierWorkspace_BoundedForPayments()
    {
        var fixture = new WorkspaceServer(50, false, mixed: true);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(),
            operationsService: new RemoteBackendOperationsService(api));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.Workspace is null || vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.NotNull(vm.Workspace);
        Assert.InRange(vm.Workspace.Payments.Count, 1, 100);
    }

    [Fact]
    public async Task Gap2_03_InitialSupplierWorkspace_BoundedForRefunds()
    {
        var fixture = new WorkspaceServer(50, false, mixed: true);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(),
            operationsService: new RemoteBackendOperationsService(api));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.Workspace is null || vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.NotNull(vm.Workspace);
        Assert.InRange(vm.Workspace.Refunds.Count, 1, 100);
    }

    [Fact]
    public async Task Gap2_04_InitialSupplierWorkspace_BoundedForSuppliedProducts()
    {
        var fixture = new WorkspaceServer(50, false, mixed: true);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(),
            operationsService: new RemoteBackendOperationsService(api));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.Workspace is null || vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.NotNull(vm.Workspace);
        Assert.InRange(vm.Workspace.SuppliedProducts.Count, 1, 100);
    }

    [Fact]
    public async Task Gap2_05_IndependentPagination_LoadingMoreStatement_DoesNotAffectOtherSections()
    {
        var fixture = new WorkspaceServer(250, false, mixed: true);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(),
            operationsService: new RemoteBackendOperationsService(api));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.Workspace is null || vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        var initialPaymentsCount = vm.Workspace.Payments.Count;
        var initialRefundsCount = vm.Workspace.Refunds.Count;
        var initialProductsCount = vm.Workspace.SuppliedProducts.Count;

        vm.LoadMoreStatementCommand.Execute(null);
        while (vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.Equal(200, vm.Workspace.Statement.Count);
        Assert.Equal(initialPaymentsCount, vm.Workspace.Payments.Count);
        Assert.Equal(initialRefundsCount, vm.Workspace.Refunds.Count);
        Assert.Equal(initialProductsCount, vm.Workspace.SuppliedProducts.Count);
    }

    [Fact]
    public async Task Gap2_06_IndependentPagination_OtherHistories_DoNotCorruptStatementState()
    {
        var fixture = new WorkspaceServer(150, false, mixed: true);
        using var api = fixture.Api();
        IBackendOperationsService service = new RemoteBackendOperationsService(api);

        var page = await service.GetSupplierStatementPageAsync(fixture.SupplierId, 100);
        Assert.Equal(100, page.Workspace.Statement.Count);
        Assert.True(page.HasMore);

        var entries = page.Workspace.Statement.Select(x => x.EntryId).ToList();
        Assert.Equal(100, entries.Distinct().Count());
        Assert.All(entries, id => Assert.NotEqual(Guid.Empty, id));
    }

    [Fact]
    public async Task Gap2_07_RetainedWindowManagement_RepeatedPagingDoesNotExceedMaxRetainedStatementEntries()
    {
        var fixture = new WorkspaceServer(450, false);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(),
            operationsService: new RemoteBackendOperationsService(api));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.Workspace is null || vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.Equal(100, vm.Workspace.Statement.Count);

        // Page 2 -> 200
        vm.LoadMoreStatementCommand.Execute(null);
        while (vm.IsLoading) { await Task.Delay(10, timeout.Token); }
        Assert.Equal(200, vm.Workspace.Statement.Count);

        // Page 3 -> 300
        vm.LoadMoreStatementCommand.Execute(null);
        while (vm.IsLoading) { await Task.Delay(10, timeout.Token); }
        Assert.Equal(300, vm.Workspace.Statement.Count);

        // Page 4 -> Would be 400, but capped at MaxRetainedStatementEntries (300) per §76.3
        vm.LoadMoreStatementCommand.Execute(null);
        while (vm.IsLoading) { await Task.Delay(10, timeout.Token); }
        Assert.Equal(300, vm.Workspace.Statement.Count);
        Assert.True(vm.HasMoreStatement);
    }

    [Fact]
    public async Task Gap2_08_FinancialTotalsRemainAuthoritative_RegardlessOfRowsInVisibleCollection()
    {
        var fixture = new WorkspaceServer(450, false);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(),
            operationsService: new RemoteBackendOperationsService(api));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.Workspace is null || vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        var initialBalance = vm.Workspace.Summary.CurrentBalance;

        // Advance pages into retained window truncation
        for (int i = 0; i < 3; i++)
        {
            vm.LoadMoreStatementCommand.Execute(null);
            while (vm.IsLoading) { await Task.Delay(10, timeout.Token); }
        }

        // Account balances remain authoritative from the server DTO
        Assert.Equal(initialBalance, vm.Workspace.Summary.CurrentBalance);
        Assert.Equal(300, vm.Workspace.Statement.Count);
    }

    [Fact]
    public async Task Gap2_09_RapidPaginationClicks_DoNotIssueDuplicateConcurrentRequests()
    {
        var fixture = new WorkspaceServer(350, false);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(),
            operationsService: new RemoteBackendOperationsService(api));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (vm.Workspace is null || vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        fixture.HoldNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = fixture.HoldNext;
        var callsBefore = fixture.Calls;

        // Hold the first response so clicks actually overlap.
        vm.LoadMoreStatementCommand.Execute(null);
        vm.LoadMoreStatementCommand.Execute(null);
        vm.LoadMoreStatementCommand.Execute(null);
        hold.SetResult();

        while (vm.IsLoading)
        {
            await Task.Delay(10, timeout.Token);
        }

        // Exactly one continuation and its probe made
        Assert.Equal(callsBefore + 2, fixture.Calls); // one continuation and one exact end probe
    }

    [Theory]
    [InlineData(101, false)]
    [InlineData(350, false)]
    [InlineData(550, true)]
    [InlineData(500, true)]
    public async Task R6_AllSections_AreBoundedIndependentCompleteAndRecoverable(int count, bool equalTimes)
    {
        var fixture = new WorkspaceServer(count, equalTimes, mixed: true, historyCount: count);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(), operationsService: new RemoteBackendOperationsService(api));
        await WaitAsync(vm);
        Assert.Equal(2, fixture.Calls);
        Assert.Equal(100, vm.Workspace!.Payments.Count);
        Assert.Equal(100, vm.Workspace.Refunds.Count);
        Assert.Equal(100, vm.Workspace.SuppliedProducts.Count);
        var summary = vm.Workspace.Summary;
        var statements = vm.Workspace.Statement.Select(x => x.EntryId).ToHashSet();
        var payments = vm.Workspace.Payments.Select(x => x.PaymentId).ToHashSet();
        var refunds = vm.Workspace.Refunds.Select(x => x.RefundId).ToHashSet();
        var products = vm.Workspace.SuppliedProducts.Select(x => x.ProductId).ToHashSet();
        while (vm.HasMoreStatement || vm.HasMorePayments || vm.HasMoreRefunds || vm.HasMoreProducts)
        {
            var oldStatement = vm.Workspace.Statement;
            if (vm.HasMorePayments)
            {
                vm.LoadMorePaymentsCommand.Execute(null);
                await WaitAsync(vm);
                Assert.Same(oldStatement, vm.Workspace.Statement);
                payments.UnionWith(vm.Workspace.Payments.Select(x => x.PaymentId));
            }
            if (vm.HasMoreRefunds)
            {
                vm.LoadMoreRefundsCommand.Execute(null);
                await WaitAsync(vm);
                Assert.Same(oldStatement, vm.Workspace.Statement);
                refunds.UnionWith(vm.Workspace.Refunds.Select(x => x.RefundId));
            }
            if (vm.HasMoreProducts)
            {
                vm.LoadMoreProductsCommand.Execute(null);
                await WaitAsync(vm);
                Assert.Same(oldStatement, vm.Workspace.Statement);
                products.UnionWith(vm.Workspace.SuppliedProducts.Select(x => x.ProductId));
            }
            if (vm.HasMoreStatement)
            {
                vm.LoadMoreStatementCommand.Execute(null);
                await WaitAsync(vm);
                statements.UnionWith(vm.Workspace.Statement.Select(x => x.EntryId));
            }
            Assert.InRange(vm.Workspace.Statement.Count, 1, 300);
            Assert.InRange(vm.Workspace.Payments.Count, 1, 300);
            Assert.InRange(vm.Workspace.Refunds.Count, 1, 300);
            Assert.InRange(vm.Workspace.SuppliedProducts.Count, 1, 300);
            Assert.Equal(vm.Workspace.Statement.Count, vm.Workspace.Statement.Select(x => x.EntryId).Distinct().Count());
            Assert.Same(summary, vm.Workspace.Summary);
        }
        Assert.Equal(count, statements.Count);
        Assert.Equal(count, payments.Count);
        Assert.Equal(count, refunds.Count);
        Assert.Equal(count, products.Count);
        var savedPayments = vm.Workspace.Payments;
        vm.ReloadStatementCommand.Execute(null);
        await WaitAsync(vm);
        Assert.Equal(fixture.Rows.Take(100).Select(x => x.EntryId), vm.Workspace.Statement.Select(x => x.EntryId));
        Assert.Same(savedPayments, vm.Workspace.Payments);
        Assert.True(vm.HasMoreStatement);
        statements = vm.Workspace.Statement.Select(x => x.EntryId).ToHashSet();
        while (vm.HasMoreStatement)
        {
            vm.LoadMoreStatementCommand.Execute(null);
            await WaitAsync(vm);
            statements.UnionWith(vm.Workspace.Statement.Select(x => x.EntryId));
        }
        Assert.Equal(count, statements.Count);
        vm.ReloadPaymentsCommand.Execute(null);
        await WaitAsync(vm);
        Assert.Equal(100, vm.Workspace.Payments.Count);
        Assert.True(vm.HasMorePayments);
        vm.ReloadRefundsCommand.Execute(null);
        await WaitAsync(vm);
        Assert.Equal(100, vm.Workspace.Refunds.Count);
        vm.ReloadProductsCommand.Execute(null);
        await WaitAsync(vm);
        Assert.Equal(100, vm.Workspace.SuppliedProducts.Count);
    }

    [Fact]
    public async Task R6_StaleContinuationCannotOverwriteRefresh_AndRapidRequestsAreSingleFlight()
    {
        var fixture = new WorkspaceServer(550, true);
        using var api = fixture.Api();
        var vm = new SupplierDetailViewModel(
            new SupplierDirectoryRecord { Id = fixture.SupplierId.ToString(), BackendId = fixture.SupplierId, Name = "Supplier" },
            new DrawerService(), new DialogService(), new ToastService(), operationsService: new RemoteBackendOperationsService(api));
        await WaitAsync(vm);
        fixture.HoldNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = fixture.HoldNext;
        var calls = fixture.Calls;
        vm.LoadMoreStatementCommand.Execute(null);
        vm.LoadMoreStatementCommand.Execute(null);
        Assert.Equal(calls + 1, fixture.Calls);
        vm.RefreshAccountCommand.Execute(null);
        await WaitAsync(vm);
        hold.SetResult();
        await Task.Delay(100);
        Assert.Equal(fixture.Rows.Take(100).Select(x => x.EntryId), vm.Workspace!.Statement.Select(x => x.EntryId));
    }

    private static async Task WaitAsync(SupplierDetailViewModel vm)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (vm.IsLoading || vm.Workspace is null)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class WorkspaceServer : HttpMessageHandler
    {
        public Guid SupplierId { get; } = Guid.NewGuid();
        public SupplierKhataEntryDto[] Rows { get; }
        public int Calls { get; private set; }
        public bool IgnoreStatementCursor { get; set; }
        private readonly bool _mixed;
        private readonly int? _historyCount;
        private readonly bool _equalTimes;
        public TaskCompletionSource? HoldNext { get; set; }
        private readonly DateTimeOffset _now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        public WorkspaceServer(int count, bool equalTimes, bool mixed = false, int? historyCount = null)
        {
            _mixed = mixed;
            _historyCount = historyCount;
            _equalTimes = equalTimes;
            Rows = Enumerable.Range(1, count).Select(i => new SupplierKhataEntryDto(Id(i), $"ENT-{i}",
                SupplierAccountEntryType.Purchase, SupplierAccountDirection.IncreasePayable,
                1, 1, i, "PURCHASE", null, equalTimes ? _now : _now.AddMinutes(i), _now,
                SupplierId, null, null)).OrderByDescending(x => x.OccurredAt)
                .ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.EntryId).ToArray();
        }
        public DesktopApiClient Api() => new(new HttpClient(this) { BaseAddress = new Uri("http://127.0.0.1:7150") }, ownsClient: true);
        private static Guid Id(int i) => Guid.Parse($"00000000-0000-0000-0000-{i:D12}");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var hold = HoldNext;
            HoldNext = null;
            if (hold is not null)
            {
                await hold.Task; // Deliberately ignore cancellation to exercise the generation fence.
            }
            var q = request.RequestUri!.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
            var size = int.Parse(q["pageSize"]);
            IEnumerable<SupplierKhataEntryDto> entries = Rows;
            if (!IgnoreStatementCursor && q.TryGetValue("beforeEntryId", out var entryId))
            {
                var cursor = (DateTimeOffset.Parse(q["beforeOccurredAt"]), DateTimeOffset.Parse(q["beforeCreatedAt"]), Guid.Parse(entryId));
                entries = entries.Where(x => (x.OccurredAt, x.CreatedAt, x.EntryId).CompareTo(cursor) < 0);
            }
            // Match the backend's ascending serialization, preserving computed balances.
            var statement = entries.Take(size).Reverse().ToArray();
            var payments = Enumerable.Range(1, _mixed ? _historyCount ?? 203 : 0).Select(i => new SupplierPaymentReadDto(Id(i), $"PAY-{i}", 1,
                SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.External, _equalTimes ? _now : _now.AddMinutes(-i), SupplierSettlementStatus.Posted, null, null)).OrderByDescending(x => x.PaidAt).ThenByDescending(x => x.PaymentId).AsEnumerable();
            if (q.TryGetValue("beforePaymentPaidAt", out var paymentAt))
            {
                payments = payments.Where(x => (x.PaidAt, x.PaymentId).CompareTo((DateTimeOffset.Parse(paymentAt), Guid.Parse(q["beforePaymentId"]))) < 0);
            }
            var refunds = Enumerable.Range(1, _mixed ? _historyCount ?? 102 : 0).Select(i => new SupplierRefundReadDto(Id(i), $"REF-{i}", 1,
                SupplierSettlementMethod.External, _equalTimes ? _now : _now.AddMinutes(-i), SupplierSettlementStatus.Posted, null, null, null, null)).OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RefundId).AsEnumerable();
            if (q.TryGetValue("beforeRefundReceivedAt", out var refundAt))
            {
                refunds = refunds.Where(x => (x.ReceivedAt, x.RefundId).CompareTo((DateTimeOffset.Parse(refundAt), Guid.Parse(q["beforeRefundId"]))) < 0);
            }
            var products = Enumerable.Range(1, _mixed ? _historyCount ?? 204 : 0).Select(i => new SupplierProductContextDto(Id(i), Id(i), $"Product {i:D4}", null, true));
            if (q.TryGetValue("beforeProductName", out var productName))
            {
                products = products.Where(x => string.CompareOrdinal(x.ProductName, productName) > 0);
            }
            var workspace = new SupplierAccountWorkspaceDto(new(SupplierId, 98765m, 98765m, 0, 0, 0, 98765m, 0, 98765m, 0, 0),
                statement, payments.Take(size).ToArray(), refunds.Take(size).ToArray(), products.Take(size).ToArray(), new(0, 0, 0, 0, 0, 0));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(workspace) };
        }
    }
}
