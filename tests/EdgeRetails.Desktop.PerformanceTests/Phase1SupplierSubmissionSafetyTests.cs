using System.Reflection;
using System.IO;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase1SupplierSubmissionSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOutcomeAndDraftEditRetryOriginalIdentityAndPayload(bool refund)
    {
        var requests = new List<object?[]>();
        var service = Proxy<IBackendOperationsService>((method, args) =>
        {
            if (method.Name == "GetSupplierWorkspaceAsync")
            {
                return Task.FromException<SupplierAccountWorkspaceDto>(new IOException("Unavailable read"));
            }
            requests.Add(args!.ToArray());
            return requests.Count == 1 ? Task.FromException(new IOException("Response lost")) : Task.CompletedTask;
        });
        var vm = Detail(service);
        vm.TransactionAmount = 30m;
        vm.ExternalReference = "original reference";
        vm.TransactionNote = "original reason";
        var method = refund ? "ReceiveRefundAsync" : "PostPaymentAsync";
        var arguments = refund ? Array.Empty<object>() : new object[] { SupplierPaymentPurpose.Advance };
        await Invoke(vm, method, arguments);
        Assert.True(vm.HasUnresolvedFinancialOperation);
        vm.TransactionAmount = 99m;
        vm.ExternalReference = "edited reference";
        vm.TransactionNote = "edited reason";
        await Invoke(vm, method, arguments);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0], requests[1]);
        Assert.False(vm.HasUnresolvedFinancialOperation);
    }

    [Fact]
    public async Task SupplierFinancialActionsShareAtomicGateAndFreezeFields()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = Proxy<IBackendOperationsService>((method, _) =>
        {
            if (method.Name == "GetSupplierWorkspaceAsync")
            {
                return Task.FromException<SupplierAccountWorkspaceDto>(new IOException("Unavailable read"));
            }
            Interlocked.Increment(ref calls);
            return completion.Task;
        });
        var vm = Detail(service);
        vm.TransactionAmount = 30m;
        vm.TransactionNote = "reason";
        var first = Invoke(vm, "PostPaymentAsync", SupplierPaymentPurpose.Advance);
        Assert.True(vm.IsSubmitting);
        vm.TransactionAmount = 99m;
        vm.TransactionNote = "edited";
        await Invoke(vm, "PostPaymentAsync", SupplierPaymentPurpose.Advance);
        await Invoke(vm, "ReceiveRefundAsync");
        Assert.Equal(1, calls);
        Assert.Equal(30m, vm.TransactionAmount);
        Assert.Equal("reason", vm.TransactionNote);
        completion.SetResult();
        await first;
        Assert.False(vm.IsSubmitting);
    }

    [Fact]
    public async Task PayloadConflictDoesNotForgetPossiblyCommittedIntentOrAllowOtherAction()
    {
        var calls = 0;
        var service = Proxy<IBackendOperationsService>((method, _) =>
        {
            if (method.Name == "GetSupplierWorkspaceAsync")
            {
                return Task.FromException<SupplierAccountWorkspaceDto>(new IOException("Unavailable read"));
            }
            calls++;
            return Task.FromException(new OperationException("idempotency.payload_mismatch", "Conflict"));
        });
        var vm = Detail(service);
        vm.TransactionAmount = 30m;
        await Invoke(vm, "PostPaymentAsync", SupplierPaymentPurpose.Advance);
        await Invoke(vm, "ReceiveRefundAsync");
        Assert.True(vm.HasUnresolvedFinancialOperation);
        Assert.False(vm.IsSubmitting);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SupplierEditCapturesSnapshotAndRejectsConcurrentProgrammaticSave()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        object?[]? captured = null;
        var backend = Proxy<IBackendBusinessOperationsService>((_, args) =>
        {
            calls++;
            captured = args!.ToArray();
            return completion.Task;
        });
        var vm = new SupplierEditViewModel(Proxy<IToastService>((_, _) => null), () => { }, backendService: backend);
        vm.Name = "Original";
        var first = Invoke(vm, "SaveAsync");
        vm.Name = "Edited";
        await Invoke(vm, "SaveAsync");
        Assert.Equal(1, calls);
        Assert.Equal("Original", captured![1]);
        Assert.Equal("Original", vm.Name);
        Assert.True(vm.IsBusy);
        completion.SetResult();
        await first;
        Assert.False(vm.IsBusy);
    }

    private static SupplierDetailViewModel Detail(IBackendOperationsService service) => new(
        new SupplierDirectoryRecord { Id = "test-supplier", BackendId = Guid.NewGuid(), Name = "Test supplier" },
        Proxy<IDrawerService>((_, _) => null), Proxy<IDialogService>((_, _) => null),
        Proxy<IToastService>((_, _) => null), operationsService: service);

    private static Task Invoke(object instance, string name, params object[] args) =>
        (Task)instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args)!;

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
}
