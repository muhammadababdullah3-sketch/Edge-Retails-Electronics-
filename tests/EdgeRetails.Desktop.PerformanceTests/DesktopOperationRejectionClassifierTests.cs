using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Reflection;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class DesktopOperationRejectionClassifierTests : IDisposable
{
    private readonly string _tempDirectory;

    public DesktopOperationRejectionClassifierTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"EdgeRetails_RejectionClassifier_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure in test teardown
        }
    }

    private string CreateJournalPath() => Path.Combine(_tempDirectory, "operation-intents.json");

    [Theory]
    [InlineData("network.timeout")]
    [InlineData("network.server_unavailable")]
    [InlineData("gateway.bad_gateway")]
    [InlineData("gateway.timeout")]
    [InlineData("http.communication_error")]
    [InlineData("system.internal_error")]
    public void Classifier_RejectsSynthesizedTransportErrors(string code)
    {
        var ex = new DesktopApiException(code, "Transport failure", HttpStatusCode.BadRequest);
        var result = DesktopOperationRejectionClassifier.IsAuthoritativeDeterministicBusinessRejection(ex);
        Assert.False(result, $"Expected code '{code}' to not be classified as authoritative business rejection.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void Classifier_RejectsAmbiguousOrRetryableStatusCodes(HttpStatusCode statusCode)
    {
        var ex = new DesktopApiException("thaka.credit_exceeds_available", "Business code with retryable status", statusCode);
        var result = DesktopOperationRejectionClassifier.IsAuthoritativeDeterministicBusinessRejection(ex);
        Assert.False(result, $"Expected status code '{statusCode}' to retain intent (not deterministic rejection).");
    }

    [Fact]
    public void Classifier_RejectsNullExceptionStatusCode()
    {
        var ex = new DesktopApiException("thaka.credit_exceeds_available", "Business code with null status", null);
        var result = DesktopOperationRejectionClassifier.IsAuthoritativeDeterministicBusinessRejection(ex);
        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classifier_RejectsNullOrWhitespaceErrorCode(string? code)
    {
        var ex = new DesktopApiException(code!, "No error code", HttpStatusCode.BadRequest);
        var result = DesktopOperationRejectionClassifier.IsAuthoritativeDeterministicBusinessRejection(ex);
        Assert.False(result);
    }

    [Theory]
    [InlineData("concurrency.conflict")]
    [InlineData("idempotency.concurrent_execution")]
    [InlineData("idempotency.payload_mismatch")]
    [InlineData("idempotency.operation_type_mismatch")]
    [InlineData("idempotency.context_mismatch")]
    [InlineData("database.deadlock")]
    public void Classifier_RejectsConcurrencyAndIdempotencyConflictErrors(string code)
    {
        var ex = new DesktopApiException(code, "Concurrency or idempotency conflict", HttpStatusCode.BadRequest);
        var result = DesktopOperationRejectionClassifier.IsAuthoritativeDeterministicBusinessRejection(ex);
        Assert.False(result, $"Expected code '{code}' to preserve intent (not deterministic rejection).");
    }

    [Theory]
    [InlineData("thaka.credit_exceeds_available")]
    [InlineData("thaka.payment_already_refunded")]
    [InlineData("thaka.project_has_credit_balance")]
    [InlineData("thaka.project_not_found")]
    [InlineData("validation.invalid_argument")]
    [InlineData("cash.session_required")]
    [InlineData("cash.insufficient_balance")]
    [InlineData("inventory.insufficient_stock")]
    [InlineData("catalog.product_not_found")]
    [InlineData("sales.sale_not_found")]
    [InlineData("parties.customer_suspended")]
    public void Classifier_AcceptsAuthoritativeDeterministicBusinessRejectionCodes(string code)
    {
        var ex = new DesktopApiException(code, "Deterministic business failure", HttpStatusCode.BadRequest);
        var result = DesktopOperationRejectionClassifier.IsAuthoritativeDeterministicBusinessRejection(ex);
        Assert.True(result, $"Expected business code '{code}' with 400 Bad Request to be classified as authoritative business rejection.");
    }

    [Fact]
    public async Task ThakaWorkspaceViewModel_CreditRefund_DefinitiveRejection_CompletesIntent_While_Retryable_Retains()
    {
        var journalPath = CreateJournalPath();
        var projectId = Guid.NewGuid();
        var project = new ThakaProjectListItemViewModel(
            "THK-001", "Credit Project", "Customer 1", "03001234567", "Rawalpindi", DateTime.Today,
            materialValue: 500m, paid: 800m, backendProjectId: projectId);

        var throwStatusCode = HttpStatusCode.Conflict;
        var throwCode = "thaka.credit_exceeds_available";

        var backend = ThakaMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendThakaService.RefundCreditAsync))
            {
                throw new DesktopApiException(throwCode, "Simulated rejection", throwStatusCode);
            }
            if (method.Name == nameof(IBackendThakaService.GetWorkspaceAsync))
            {
                return Task.FromResult(new BackendThakaWorkspaceSnapshot(project, [], []));
            }
            return null;
        });

        var store = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm = new ThakaWorkspaceViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: store);

        // Open refund panel and set valid refund amount
        vm.OpenRefundCreditCommand.Execute(null);
        Assert.True(vm.IsRefundPanelVisible);
        vm.RefundAmountInput = 100m;
        vm.RefundReasonInput = "Customer overpayment refund";
        Assert.True(vm.CanConfirmRefund);

        // 1. Retryable 409 Conflict: Intent must be retained for reconciliation
        await vm.ConfirmRefundCreditAsync();
        var pendingKey = $"thaka:credit-refund:{projectId:D}";
        var pendingIntents = store.FindPendingByPrefix(pendingKey);
        Assert.Single(pendingIntents);
        Assert.True(vm.HasRefundErrorMessage);

        // 2. Authoritative 400 Bad Request business rejection: Intent must be completed/released
        throwStatusCode = HttpStatusCode.BadRequest;
        await vm.ConfirmRefundCreditAsync();

        var afterAuthoritative = store.FindPendingByPrefix(pendingKey);
        Assert.Empty(afterAuthoritative);
        Assert.True(vm.HasRefundErrorMessage);
    }

    [Fact]
    public async Task ThakaWorkspaceViewModel_IdempotencyMismatch400_PreservesIntent_BecauseConflictingExistingOperation()
    {
        var journalPath = CreateJournalPath();
        var projectId = Guid.NewGuid();
        var project = new ThakaProjectListItemViewModel(
            "THK-001B", "Credit Project B", "Customer B", "03001234567", "Rawalpindi", DateTime.Today,
            materialValue: 500m, paid: 800m, backendProjectId: projectId);

        var backend = ThakaMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendThakaService.RefundCreditAsync))
            {
                // Even if returned as 400 Bad Request, idempotency.payload_mismatch is evidence of existing operation conflict
                throw new DesktopApiException("idempotency.payload_mismatch", "Existing operation conflict", HttpStatusCode.BadRequest);
            }
            if (method.Name == nameof(IBackendThakaService.GetWorkspaceAsync))
            {
                return Task.FromResult(new BackendThakaWorkspaceSnapshot(project, [], []));
            }
            return null;
        });

        var store = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm = new ThakaWorkspaceViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: store);

        vm.OpenRefundCreditCommand.Execute(null);
        vm.RefundAmountInput = 100m;
        vm.RefundReasonInput = "Customer overpayment refund";

        await vm.ConfirmRefundCreditAsync();

        var pendingKey = $"thaka:credit-refund:{projectId:D}";
        var pendingIntents = store.FindPendingByPrefix(pendingKey);
        Assert.Single(pendingIntents); // MUST BE PRESERVED! Not completed!
        Assert.True(vm.HasRefundErrorMessage);
    }

    [Fact]
    public async Task ThakaWorkspaceViewModel_CreditRefund_ColdRestart_ReusesOperationId()
    {
        var journalPath = CreateJournalPath();
        var projectId = Guid.NewGuid();
        var project = new ThakaProjectListItemViewModel(
            "THK-002", "Credit Project 2", "Customer 2", "03007654321", "Islamabad", DateTime.Today,
            materialValue: 200m, paid: 600m, backendProjectId: projectId);

        var operationIds = new List<Guid>();
        var simulateFailure = true;

        var backend = ThakaMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendThakaService.RefundCreditAsync))
            {
                var opId = (Guid)args[3]!;
                operationIds.Add(opId);

                if (simulateFailure)
                {
                    throw new DesktopApiException("network.timeout", "Simulated network timeout");
                }

                return Task.CompletedTask;
            }
            if (method.Name == nameof(IBackendThakaService.GetWorkspaceAsync))
            {
                return Task.FromResult(new BackendThakaWorkspaceSnapshot(project, [], []));
            }
            return null;
        });

        var store1 = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm1 = new ThakaWorkspaceViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: store1);

        vm1.OpenRefundCreditCommand.Execute(null);
        vm1.RefundAmountInput = 150m;
        vm1.RefundReasonInput = "Authorized refund";

        // First attempt fails with network timeout
        await vm1.ConfirmRefundCreditAsync();
        Assert.Single(operationIds);
        var originalOpId = operationIds[0];
        Assert.NotEqual(Guid.Empty, originalOpId);

        // Cold restart: fresh ViewModel and fresh store pointing to same journal
        simulateFailure = false;
        var storeRestart = new FileClientOperationIntentStore(journalPath);
        var vmRestart = new ThakaWorkspaceViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: storeRestart);

        vmRestart.OpenRefundCreditCommand.Execute(null);
        vmRestart.RefundAmountInput = 150m;
        vmRestart.RefundReasonInput = "Authorized refund";

        // Second attempt succeeds using preserved operation intent
        await vmRestart.ConfirmRefundCreditAsync();

        Assert.Equal(2, operationIds.Count);
        Assert.Equal(originalOpId, operationIds[1]); // Reused identical ClientOperationId!
        Assert.Empty(storeRestart.FindPendingByPrefix($"thaka:credit-refund:{projectId:D}")); // Intent completed
    }

    private sealed class TestToastService : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null)
        {
            _messages.Add(new ToastMessage(Guid.NewGuid(), message, tone));
        }
    }

    private class ThakaMockProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[], object?> _handler = null!;

        public static IBackendThakaService Create(Func<MethodInfo, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create<IBackendThakaService, ThakaMockProxy>();
            ((ThakaMockProxy)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!, args ?? []);
    }
}
