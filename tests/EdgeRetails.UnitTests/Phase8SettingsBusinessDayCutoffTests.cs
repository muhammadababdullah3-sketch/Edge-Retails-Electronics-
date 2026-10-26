using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Settings;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Services;

namespace EdgeRetails.UnitTests;

public sealed class Phase8SettingsBusinessDayCutoffTests
{
    [Fact]
    public async Task UpdateShopProfile_AppliesBusinessDayCutoffAndRefreshesCache()
    {
        var setup = new FakeSetupRepository();
        var profile = new ShopProfile
        {
            ShopName = "Test Shop",
            TimeZoneId = "Asia/Karachi",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        setup.ShopProfile = profile;

        var cache = new ShopTimeZoneCache();
        var handler = new UpdateShopProfileHandler(
            setup,
            new AllowSettingsAuthorizer(),
            new Phase2TestDoubles().Transactions,
            new Phase2TestDoubles().Audit,
            new Phase2TestDoubles().Clock,
            new Phase2TestDoubles().UnitOfWork,
            cache);

        var result = await handler.HandleAsync(
            new UpdateShopProfileCommand(
                "Test Shop",
                null,
                null,
                null,
                "03:30",
                Guid.CreateVersion7(),
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new TimeOnly(3, 30), profile.BusinessDayCutoffLocalTime);
        Assert.Equal(new TimeOnly(3, 30), cache.BusinessDayCutoffLocalTime);
    }

    private sealed class AllowSettingsAuthorizer : IApplicationPermissionAuthorizer
    {
        public Task<Result> AuthorizeAsync(Guid actorId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeSetupRepository : ISetupRepository
    {
        public ShopProfile? ShopProfile { get; set; }

        public Task<ShopProfile?> GetShopProfileAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ShopProfile);

        public Task<ShopProfile?> GetShopProfileForUpdateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ShopProfile);

        public void AddShopProfile(ShopProfile profile) => ShopProfile = profile;

        public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ReceiptTemplateSettings?>(null);

        public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsForUpdateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ReceiptTemplateSettings?>(null);

        public void AddReceiptTemplateSettings(ReceiptTemplateSettings settings) { }

        public Task<InstallationState?> GetInstallationStateForUpdateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<InstallationState?>(null);

        public void AddInstallationState(InstallationState state) { }

        public void AddRole(Role role) { }

        public void AddPermission(Permission permission) { }

        public void AddRolePermission(RolePermission rolePermission) { }

        public void AddUser(User user) { }

        public void AddCustomer(Domain.Parties.Customer customer) { }

        public Task<bool> HasCatalogReferenceUpgradeAsync(Guid installationId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> AnyUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
