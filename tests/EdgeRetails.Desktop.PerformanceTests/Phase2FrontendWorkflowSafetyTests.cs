using System.Collections.ObjectModel;
using System.Reflection;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase2FrontendWorkflowSafetyTests
{
    [Fact]
    public async Task ExpenseVoid_CanVoidReflectsBackendExpense_AndRetryPreservesStableOperationId()
    {
        var expenseId = Guid.NewGuid();
        var existingExpense = new ExpenseRecord
        {
            Id = "1",
            BackendId = expenseId,
            Category = "Utilities",
            Subcategory = "Electric",
            Amount = 150m,
            Date = DateTime.UtcNow,
            PaymentMethod = "Cash",
            StaffMember = "Admin",
            Note = "Monthly bill"
        };

        var testService = new MockBusinessOperationsService { FailFirstVoid = true };
        var toasts = new MockToastService();
        var closeCalled = false;
        var savedCalled = false;

        var vm = new ExpenseEditViewModel(
            toastService: toasts,
            close: () => closeCalled = true,
            saved: () => savedCalled = true,
            backendService: testService,
            existing: existingExpense,
            categories: ["Utilities"]);

        Assert.True(vm.CanVoid);
        Assert.False(vm.CanEdit); // Posted backend expenses are immutable

        vm.VoidReason = "Duplicate entry entered in error";

        // First attempt will fail due to FailFirstVoid
        var voidTask1 = ExecutePrivateMethodAsync(vm, "VoidAsync");
        await voidTask1;

        Assert.False(closeCalled);
        Assert.False(savedCalled);
        Assert.Single(testService.VoidCalls);
        var firstCall = testService.VoidCalls[0];
        Assert.Equal(expenseId, firstCall.ExpenseId);
        Assert.Equal("Duplicate entry entered in error", firstCall.Reason);
        Assert.NotEqual(Guid.Empty, firstCall.ClientOperationId);

        // Retry attempt
        var voidTask2 = ExecutePrivateMethodAsync(vm, "VoidAsync");
        await voidTask2;

        Assert.True(closeCalled);
        Assert.True(savedCalled);
        Assert.Equal(2, testService.VoidCalls.Count);
        var secondCall = testService.VoidCalls[1];
        Assert.Equal(expenseId, secondCall.ExpenseId);
        Assert.Equal(firstCall.ClientOperationId, secondCall.ClientOperationId); // Exact operation identity preserved
    }

    [Fact]
    public async Task ExpenseVoid_AtomicSubmissionGateBlocksConcurrentReentrantInvocations()
    {
        var expenseId = Guid.NewGuid();
        var existingExpense = new ExpenseRecord
        {
            Id = "2",
            BackendId = expenseId,
            Category = "Rent",
            Subcategory = "Office",
            Amount = 500m,
            Date = DateTime.UtcNow,
            PaymentMethod = "Cash",
            StaffMember = "Admin",
            Note = "Office rent"
        };

        var testService = new MockBusinessOperationsService { DelayVoidCompletion = true };
        var toasts = new MockToastService();

        var vm = new ExpenseEditViewModel(
            toastService: toasts,
            close: () => { },
            saved: () => { },
            backendService: testService,
            existing: existingExpense,
            categories: ["Rent"]);

        vm.VoidReason = "Void concurrent test";

        // Start first void with delayed completion
        var first = ExecutePrivateMethodAsync(vm, "VoidAsync");
        // Second concurrent invocation should be dropped by atomic submission gate
        var second = ExecutePrivateMethodAsync(vm, "VoidAsync");

        testService.VoidRelease.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(testService.VoidCalls);
    }

    [Fact]
    public async Task WarrantyTimeline_StaleCompletionFromEarlierSelectionIsDiscarded()
    {
        var workId1 = Guid.NewGuid();
        var workId2 = Guid.NewGuid();

        var tcs1 = new TaskCompletionSource<IReadOnlyList<WarrantyEventDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs2 = new TaskCompletionSource<IReadOnlyList<WarrantyEventDto>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = Proxy<IBackendOperationsService>((method, args) =>
        {
            if (method.Name == nameof(IBackendOperationsService.GetWarrantyClaimTimelineAsync))
            {
                var claimId = (Guid)args![0]!;
                if (claimId == workId1)
                {
                    return tcs1.Task;
                }
                if (claimId == workId2)
                {
                    return tcs2.Task;
                }
                return Task.FromResult<IReadOnlyList<WarrantyEventDto>>([]);
            }
            if (method.Name == nameof(IBackendOperationsService.GetWarrantyDashboardAsync))
            {
                return Task.FromResult(new WarrantyDashboardDto(
                    new WarrantyDashboardSummaryDto(0, 0, 0, 0, 0, 0, 0),
                    Array.Empty<WarrantyQueueRowDto>()));
            }
            return null;
        });

        var toasts = new MockToastService();
        var vm = new WarrantyViewModel(service, toasts);

        var row1 = new WarrantyQueueRowDto(
            WarrantyWorkKind.CustomerClaim,
            workId1,
            "CLM-001",
            "Product A",
            "Customer A",
            "Supplier A",
            DateTimeOffset.UtcNow,
            "Received",
            "WithShop",
            "Pending",
            null,
            null,
            null,
            null);

        var row2 = new WarrantyQueueRowDto(
            WarrantyWorkKind.CustomerClaim,
            workId2,
            "CLM-002",
            "Product B",
            "Customer B",
            "Supplier B",
            DateTimeOffset.UtcNow,
            "Received",
            "WithShop",
            "Pending",
            null,
            null,
            null,
            null);

        // Select row 1: initiates request 1
        vm.SelectedRow = row1;
        Assert.Empty(vm.Timeline);

        // Rapidly select row 2: initiates request 2
        vm.SelectedRow = row2;
        Assert.Empty(vm.Timeline);

        // Request 1 completes later with events for row 1
        tcs1.SetResult([
            new WarrantyEventDto(Guid.NewGuid(), workId1, WarrantyClaimStatus.Received, WarrantyCustody.WithShop, "REVIEW", "Action 1 for Claim 1", Guid.NewGuid(), DateTimeOffset.UtcNow)
        ]);

        // Yield to allow any continuation of request 1 to run
        await Task.Delay(20);

        // Because row 2 was selected (and generation bumped), timeline must NOT receive row 1's events
        Assert.Empty(vm.Timeline);

        // Request 2 completes with events for row 2
        var expectedEvent = new WarrantyEventDto(Guid.NewGuid(), workId2, WarrantyClaimStatus.Received, WarrantyCustody.WithShop, "REVIEW", "Action 2 for Claim 2", Guid.NewGuid(), DateTimeOffset.UtcNow);
        tcs2.SetResult([expectedEvent]);

        await Task.Delay(20);

        // Timeline now contains row 2's events
        Assert.Single(vm.Timeline);
        Assert.Equal(expectedEvent.Note, vm.Timeline[0].Note);
    }

    [Fact]
    public async Task WarrantyAction_ReentrantClicksAreBlockedByAtomicActionGate()
    {
        var actionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionCallCount = 0;

        var service = Proxy<IBackendOperationsService>((method, args) =>
        {
            if (method.Name == nameof(IBackendOperationsService.BeginWarrantyClaimReviewAsync))
            {
                actionCallCount++;
                return actionTcs.Task;
            }
            if (method.Name == nameof(IBackendOperationsService.GetWarrantyClaimTimelineAsync))
            {
                return Task.FromResult<IReadOnlyList<WarrantyEventDto>>([]);
            }
            if (method.Name == nameof(IBackendOperationsService.GetWarrantyDashboardAsync))
            {
                return Task.FromResult(new WarrantyDashboardDto(
                    new WarrantyDashboardSummaryDto(0, 0, 0, 0, 0, 0, 0),
                    Array.Empty<WarrantyQueueRowDto>()));
            }
            return null;
        });

        var toasts = new MockToastService();
        var vm = new WarrantyViewModel(service, toasts);

        var workId = Guid.NewGuid();
        var row = new WarrantyQueueRowDto(
            WarrantyWorkKind.CustomerClaim,
            workId,
            "CLM-003",
            "Product C",
            "Customer C",
            "Supplier C",
            DateTimeOffset.UtcNow,
            "Received",
            "WithShop",
            "Pending",
            null,
            null,
            null,
            null);

        vm.SelectedRow = row;
        vm.ActionNote = "Under review";

        // Execute BeginReviewCommand
        vm.BeginReviewCommand.Execute(null);

        // Immediately try executing again while first is awaiting
        vm.BeginReviewCommand.Execute(null);

        // Release the first action
        actionTcs.SetResult();
        await Task.Delay(20);

        // Exactly one call should have been recorded by the service
        Assert.Equal(1, actionCallCount);
    }

    private static Task ExecutePrivateMethodAsync(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException($"Method {methodName} not found.");
        return (Task)method.Invoke(target, null)!;
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)proxy).Handler = invoke;
        return proxy;
    }

    public class ServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    private sealed class MockBusinessOperationsService : IBackendBusinessOperationsService
    {
        public List<(Guid ExpenseId, string Reason, Guid ClientOperationId)> VoidCalls { get; } = [];
        public TaskCompletionSource VoidRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailFirstVoid { get; init; }
        public bool DelayVoidCompletion { get; init; }

        public Task<IReadOnlyList<string>> GetExpenseCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["Utilities", "Rent"]);

        public Task<IReadOnlyList<ExpenseRecord>> GetExpensesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExpenseRecord>>([]);

        public Task<ExpenseRecord> PostExpenseAsync(string category, string subcategory, decimal amount,
            DateTime date, string paymentMethod, string note, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task VoidExpenseAsync(Guid expenseId, string reason, Guid? clientOperationId = null,
            CancellationToken cancellationToken = default)
        {
            var opId = clientOperationId ?? Guid.NewGuid();
            VoidCalls.Add((expenseId, reason, opId));

            if (FailFirstVoid && VoidCalls.Count == 1)
            {
                throw new DesktopApiException("network.timeout", "Simulated timeout during void.");
            }

            if (DelayVoidCompletion && !VoidRelease.Task.IsCompleted)
            {
                await VoidRelease.Task;
            }
        }

        public Task SaveCustomerAsync(CustomerDirectoryRecord? existing, string name, string phone,
            string address, string notes, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CustomerDirectoryRecord>> GetCustomersAsync(string? search, int pageSize = 100,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SupplierDirectoryRecord>> GetSuppliersAsync(string? search, int pageSize = 100,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReportSnapshot> GetReportAsync(ReportPeriodMode mode, DateTime selectedDate, int selectedMonth,
            int selectedYear, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MockToastService : IToastService
    {
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; } = new(new ObservableCollection<ToastMessage>());
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }
}
