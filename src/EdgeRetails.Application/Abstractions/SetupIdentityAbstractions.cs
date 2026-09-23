using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Abstractions;

public sealed record PinCredential(
    byte[] Hash,
    byte[] Salt,
    int Iterations,
    string Algorithm);

public interface IPinCredentialService
{
    PinCredential Hash(string pin);
    bool Verify(string pin, PinCredential credential);
}

public interface ISetupRepository
{
    Task<InstallationState?> GetInstallationStateForUpdateAsync(
        CancellationToken cancellationToken);

    Task<bool> AnyUsersAsync(CancellationToken cancellationToken);

    Task<ShopProfile?> GetShopProfileAsync(CancellationToken cancellationToken);
    Task<ShopProfile?> GetShopProfileForUpdateAsync(CancellationToken cancellationToken);
    Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsAsync(CancellationToken cancellationToken);
    Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsForUpdateAsync(CancellationToken cancellationToken);

    void AddInstallationState(InstallationState state);
    void AddShopProfile(ShopProfile profile);
    void AddCustomer(Customer customer); void AddReceiptTemplateSettings(ReceiptTemplateSettings settings);
    void AddRole(Role role);
    void AddPermission(Permission permission);
    void AddRolePermission(RolePermission rolePermission);
    void AddUser(User user);
}

public interface IIdentityReadRepository
{
    Task<IReadOnlyList<User>> GetActiveUsersAsync(
        CancellationToken cancellationToken);

    Task<User?> GetUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<Role?> GetRoleAsync(
        Guid roleId,
        CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> GetEffectivePermissionKeysAsync(
        Guid userId,
        CancellationToken cancellationToken);
}

public interface IIdentitySessionRepository
{
    void AddSession(UserSession session);

    Task<UserSession?> GetSessionForUpdateAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
}

public interface IInstallationStateReadService
{
    Task<InstallationState?> GetAsync(
        CancellationToken cancellationToken);
}

public sealed record DatabaseReadinessResult(
    bool CanConnect,
    bool HasPendingMigrations,
    IReadOnlyList<string> PendingMigrations,
    string? FailureReason)
{
    public bool IsReady => CanConnect && !HasPendingMigrations;
}

public interface IDatabaseReadinessService
{
    Task<DatabaseReadinessResult> CheckAsync(
        CancellationToken cancellationToken);
}
