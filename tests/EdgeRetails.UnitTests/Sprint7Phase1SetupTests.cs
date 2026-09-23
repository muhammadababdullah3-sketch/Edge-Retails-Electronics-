using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Setup;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.UnitTests;

public sealed class Sprint7Phase1SetupTests
{
    [Fact]
    public async Task Bootstrap_Creates_Complete_Atomic_Setup_Model()
    {
        var setup = new FakeSetupRepository();
        var unitOfWork = new FakeUnitOfWork();
        var transactions = new FakeTransactionRunner();
        var handler = new FirstSetupBootstrapHandler(
            setup,
            new FakePinCredentialService(),
            transactions,
            unitOfWork,
            new FakeClock());

        var result = await handler.HandleAsync(
            new FirstSetupBootstrapCommand(
                "Edge Retails",
                "0300-0000000",
                "Bahawalnagar",
                "Owner",
                "1234", "Retail",
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, transactions.ExecuteCount);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.NotNull(setup.Installation);
        Assert.Equal(SetupStatus.Complete, setup.Installation!.SetupStatus);
        Assert.NotNull(setup.Installation.CompletedAt);

        var owner = Assert.Single(setup.Users);
        Assert.Equal(UserStatus.Active, owner.Status);
        Assert.Equal("PBKDF2-SHA256", owner.PinAlgorithm);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, owner.PinHash);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, owner.PinSalt);
        Assert.Null(typeof(User).GetProperty("Pin"));
        Assert.Null(typeof(User).GetProperty("PlainPin"));

        var walkIn = Assert.Single(setup.Customers);
        Assert.True(walkIn.IsWalkIn);
        Assert.Equal("Walk-in Customer", walkIn.Name);
        Assert.Equal(3, setup.Roles.Count);
        Assert.Equal(35, setup.Permissions.Count);
        Assert.Equal(68, setup.RolePermissions.Count);
        Assert.Single(setup.ShopProfiles);
        Assert.Single(setup.ReceiptSettings);
    }
    [Fact]
    public async Task Bootstrap_Rejects_Invalid_Pin_Before_Transaction()
    {
        var setup = new FakeSetupRepository();
        var unitOfWork = new FakeUnitOfWork();
        var transactions = new FakeTransactionRunner();
        var handler = new FirstSetupBootstrapHandler(
            setup,
            new FakePinCredentialService(),
            transactions,
            unitOfWork,
            new FakeClock());

        var result = await handler.HandleAsync(
            new FirstSetupBootstrapCommand(
                "Edge Retails",
                null,
                null,
                "Owner",
                "12A4",
                null,
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("setup.owner_pin_invalid", result.Error?.Code);
        Assert.Equal(0, transactions.ExecuteCount);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Bootstrap_Fails_Closed_When_Setup_Is_Already_Complete()
    {
        var setup = new FakeSetupRepository
        {
            Installation = new InstallationState
            {
                SetupStatus = SetupStatus.Complete,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            }
        };
        var unitOfWork = new FakeUnitOfWork();
        var handler = new FirstSetupBootstrapHandler(
            setup,
            new FakePinCredentialService(),
            new FakeTransactionRunner(),
            unitOfWork,
            new FakeClock());

        var result = await handler.HandleAsync(
            new FirstSetupBootstrapCommand(
                "Edge Retails",
                null,
                null,
                "Owner",
                "1234",
                null,
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("setup.already_complete", result.Error?.Code);
        Assert.Equal(0, unitOfWork.SaveCount);
    }
    private sealed class FakeSetupRepository : ISetupRepository
    {
        public InstallationState? Installation { get; set; }
        public List<ShopProfile> ShopProfiles { get; } = [];
        public List<Customer> Customers { get; } = [];
        public List<ReceiptTemplateSettings> ReceiptSettings { get; } = [];
        public List<Role> Roles { get; } = [];
        public List<Permission> Permissions { get; } = [];
        public List<RolePermission> RolePermissions { get; } = [];
        public List<User> Users { get; } = [];

        public Task<InstallationState?> GetInstallationStateForUpdateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Installation);

        public Task<bool> AnyUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Users.Count > 0);

        public Task<ShopProfile?> GetShopProfileAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ShopProfiles.SingleOrDefault());

        public Task<ShopProfile?> GetShopProfileForUpdateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ShopProfiles.SingleOrDefault());

        public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ReceiptSettings.SingleOrDefault());

        public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsForUpdateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ReceiptSettings.SingleOrDefault());

        public void AddInstallationState(InstallationState state) =>
            Installation = state;

        public void AddShopProfile(ShopProfile profile) =>
            ShopProfiles.Add(profile);

        public void AddCustomer(Customer customer) =>
            Customers.Add(customer); public void AddReceiptTemplateSettings(
            ReceiptTemplateSettings settings) =>
            ReceiptSettings.Add(settings);

        public void AddRole(Role role) =>
            Roles.Add(role);

        public void AddPermission(Permission permission) =>
            Permissions.Add(permission);

        public void AddRolePermission(RolePermission rolePermission) =>
            RolePermissions.Add(rolePermission);

        public void AddUser(User user) =>
            Users.Add(user);
    }

    private sealed class FakePinCredentialService : IPinCredentialService
    {
        public PinCredential Hash(string pin) =>
            new(
                [1, 2, 3, 4],
                [9, 8, 7, 6],
                210_000,
                "PBKDF2-SHA256");

        public bool Verify(string pin, PinCredential credential) => true;
    }
    private sealed class FakeTransactionRunner : ITransactionRunner
    {
        public int ExecuteCount { get; private set; }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return await operation(cancellationToken);
        }
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

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } =
            new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

        public DateOnly ShopDate => DateOnly.FromDateTime(UtcNow.UtcDateTime);
    }
}
