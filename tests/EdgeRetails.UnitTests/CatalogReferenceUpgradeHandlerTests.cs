using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Setup;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.UnitTests;

namespace EdgeRetails.UnitTests;

public sealed class CatalogReferenceUpgradeHandlerTests
{
    [Fact]
    public async Task HandleAsync_SeedsFixedUnitsOnce_AndReturnsAlreadyAppliedOnReplay()
    {
        var fixture = new UpgradeFixture();

        var first = await fixture.Handler.HandleAsync(fixture.ActorId, CancellationToken.None);
        var second = await fixture.Handler.HandleAsync(fixture.ActorId, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(10, first.Value!.UnitsAdded);
        Assert.Equal(TaxonomyV3Store.Departments.Count, first.Value.CategoriesAdded);
        Assert.Equal(TaxonomyV3Store.UnitDefinitions.Count - 10, first.Value.TaxonomyUnitsAdded);
        Assert.False(first.Value.AlreadyApplied);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.True(second.Value!.AlreadyApplied);
        Assert.Equal(TaxonomyV3Store.UnitDefinitions.Count, fixture.Catalog.Units.Count);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);
        Assert.Single(fixture.Audit.Events);
    }

    [Fact]
    public async Task HandleAsync_RejectsExistingSymbolConflictWithoutSavingOrMarkingApplied()
    {
        var fixture = new UpgradeFixture();
        fixture.Catalog.Units.Add(Guid.NewGuid(), new Unit
        {
            Name = "Retail Piece",
            Symbol = "PCS",
            Dimension = PhysicalDimension.Count,
            FactorToReference = 1m,
            ConversionKind = "FIXED",
            IsActive = true
        });

        var result = await fixture.Handler.HandleAsync(fixture.ActorId, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.upgrade_unit_symbol_conflict", result.Error?.Code);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
        Assert.Empty(fixture.Audit.Events);
        Assert.Single(fixture.Catalog.Units);
    }

    private sealed class UpgradeFixture
    {
        public Guid ActorId { get; } = Guid.NewGuid();
        public FakeCatalogRepository Catalog { get; } = new();
        public FakeSetupRepository Setup { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public RecordingAuditWriter Audit { get; }
        public CatalogReferenceUpgradeHandler Handler { get; }

        public UpgradeFixture()
        {
            Setup.Installation = new InstallationState
            {
                SetupStatus = SetupStatus.Complete,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            };
            Audit = new RecordingAuditWriter(Setup);
            Handler = new CatalogReferenceUpgradeHandler(
                Catalog,
                new AllowAllAuthorizer(),
                new PassThroughTransactionRunner(),
                UnitOfWork,
                Setup,
                new TestClock(),
                Audit);
        }
    }

    private sealed class FakeSetupRepository : ISetupRepository
    {
        public InstallationState? Installation { get; set; }
        public bool CatalogReferenceUpgradeApplied { get; set; }

        public Task<InstallationState?> GetInstallationStateForUpdateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Installation);

        public Task<bool> HasCatalogReferenceUpgradeAsync(Guid installationId, CancellationToken cancellationToken) =>
            Task.FromResult(CatalogReferenceUpgradeApplied);

        public Task<bool> AnyUsersAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<ShopProfile?> GetShopProfileAsync(CancellationToken cancellationToken) => Task.FromResult<ShopProfile?>(null);
        public Task<ShopProfile?> GetShopProfileForUpdateAsync(CancellationToken cancellationToken) => Task.FromResult<ShopProfile?>(null);
        public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsAsync(CancellationToken cancellationToken) => Task.FromResult<ReceiptTemplateSettings?>(null);
        public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsForUpdateAsync(CancellationToken cancellationToken) => Task.FromResult<ReceiptTemplateSettings?>(null);
        public void AddInstallationState(InstallationState state) => Installation = state;
        public void AddShopProfile(ShopProfile profile) { }
        public void AddCustomer(Customer customer) { }
        public void AddReceiptTemplateSettings(ReceiptTemplateSettings settings) { }
        public void AddRole(Role role) { }
        public void AddPermission(Permission permission) { }
        public void AddRolePermission(RolePermission rolePermission) { }
        public void AddUser(User user) { }
    }

    private sealed class RecordingAuditWriter(FakeSetupRepository setup) : IBusinessAuditWriter
    {
        public List<(string Action, string EntityType, Guid? EntityId)> Events { get; } = [];

        public void Record(string action, string entityType, Guid? entityId, Guid actorId, Guid correlationId, string? summary = null)
        {
            Events.Add((action, entityType, entityId));
            if (entityType == "CATALOG_REFERENCE_UPGRADE" && action == "FIXED_UNITS_SEEDED")
            {
                setup.CatalogReferenceUpgradeApplied = true;
            }
        }
    }

    private sealed class AllowAllAuthorizer : IApplicationPermissionAuthorizer
    {
        public Task<Result> AuthorizeAsync(Guid actorId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class PassThroughTransactionRunner : ITransactionRunner
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            operation(cancellationToken);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public DateOnly ShopDate => DateOnly.FromDateTime(UtcNow.UtcDateTime);
    }
}
