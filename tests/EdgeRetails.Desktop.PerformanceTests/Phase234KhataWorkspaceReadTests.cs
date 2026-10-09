using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase234KhataWorkspaceReadTests
{
    [Fact]
    public void FirstReadFailure_IsUnavailableAndReadOnlyRetryRestoresAuthoritativeSnapshot()
    {
        var projectId = Guid.NewGuid();
        var backend = new ReadBackend(projectId, failFirst: true);
        var workspace = new ThakaWorkspaceViewModel(Project(projectId), backendService: backend);
        AssertUnavailable(workspace);
        Assert.Equal(1, backend.ReadCount);
        Assert.True(workspace.RefreshCommand.CanExecute(null));

        workspace.RefreshCommand.Execute(null);

        Assert.Equal(2, backend.ReadCount);
        Assert.False(workspace.IsLoading);
        Assert.False(workspace.HasLoadError);
        Assert.NotEqual("Unavailable", workspace.MaterialValueFormatted);
        Assert.Equal(150m, workspace.MaterialValue);
        Assert.Equal(25m, workspace.TotalPaid);
        Assert.Equal(125m, workspace.Balance);
        Assert.Single(workspace.MaterialLedger);
        Assert.Single(workspace.PaymentLedger);
        Assert.True(workspace.AddMaterialCommand.CanExecute(null));
        Assert.True(workspace.RecordPaymentCommand.CanExecute(null));
    }

    [Fact]
    public void RefreshFailure_AfterSuccessfulSnapshotMarksValuesUnavailableAndBlocksMutation()
    {
        var projectId = Guid.NewGuid();
        var backend = new ReadBackend(projectId, failFirst: false);
        var workspace = new ThakaWorkspaceViewModel(Project(projectId), backendService: backend);
        Assert.Equal(150m, workspace.MaterialValue);
        backend.FailNext = true;

        workspace.RefreshCommand.Execute(null);

        AssertUnavailable(workspace);
        Assert.Equal(2, backend.ReadCount);
        Assert.True(workspace.RefreshCommand.CanExecute(null));
    }

    private static void AssertUnavailable(ThakaWorkspaceViewModel workspace)
    {
        Assert.False(workspace.IsLoading);
        Assert.True(workspace.HasLoadError);
        Assert.NotEmpty(workspace.LoadError);
        Assert.Equal("Unavailable", workspace.MaterialValueFormatted);
        Assert.Equal("Unavailable", workspace.TotalPaidFormatted);
        Assert.Equal("Unavailable", workspace.BalanceFormatted);
        Assert.False(workspace.AddMaterialCommand.CanExecute(null));
        Assert.False(workspace.RecordPaymentCommand.CanExecute(null));
        Assert.False(workspace.FinalSettlementCommand.CanExecute(null));
        Assert.False(workspace.ToggleSuspensionCommand.CanExecute(null));
        Assert.False(workspace.ReverseMaterialCommand.CanExecute(null));
    }

    private static ThakaProjectListItemViewModel Project(Guid id) => new(
        "THK-READ", "Read fixture", "Customer", "N/A", "N/A", new DateTime(2026, 10, 8),
        materialValue: 150m, paid: 25m, backendProjectId: id);

    // Completed task responses make constructor/async-void command continuations
    // deterministic. All mutation methods fail: the test permits only reads.
    private sealed class ReadBackend(Guid projectId, bool failFirst) : IBackendThakaService
    {
        public int ReadCount { get; private set; }
        public bool FailNext { get; set; } = failFirst;

        public Task<BackendThakaWorkspaceSnapshot> GetWorkspaceAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Assert.Equal(projectId, id);
            ReadCount++;
            if (FailNext)
            {
                FailNext = false;
                return Task.FromException<BackendThakaWorkspaceSnapshot>(new InvalidOperationException("Controlled read failure"));
            }
            return Task.FromResult(new BackendThakaWorkspaceSnapshot(Project(id),
                [new MaterialLedgerEntry { ProductName = "Authoritative item", TotalValue = 150m }],
                [new PaymentLedgerEntry { ReceiptNumber = "Authoritative receipt", Amount = 25m }]));
        }

        public Task<IReadOnlyList<ThakaProjectListItemViewModel>> GetProjectsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendThakaProjectPage> GetProjectsPageAsync(string? search, string filter, int pageSize = 200,
            DateOnly? beforeStartedOn = null, Guid? beforeProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThakaProjectListItemViewModel> CreateProjectAsync(string customerName, string? phone,
            string projectName, string? location, string? note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BackendThakaMaterialCatalogItem>> GetMaterialCatalogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendThakaIssueResult> IssueMaterialAsync(ThakaProjectListItemViewModel project,
            PosProductItemViewModel product, decimal quantity, IReadOnlyList<Guid> inventoryUnitIds,
            Guid clientOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReverseMaterialAsync(ThakaProjectListItemViewModel project, Guid materialIssueId,
            string reason, Guid clientOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackendThakaPaymentResult> RecordPaymentAsync(ThakaProjectListItemViewModel project,
            decimal amount, string paymentMethod, string? reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SettleAsync(ThakaProjectListItemViewModel project, decimal amount,
            string paymentMethod, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
