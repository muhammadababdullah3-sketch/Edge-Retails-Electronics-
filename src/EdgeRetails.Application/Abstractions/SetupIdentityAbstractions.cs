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

    Task<IReadOnlyList<Role>> GetRolesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetRolePermissionKeysAsync(
        Guid roleId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Permission>> GetPermissionsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetEffectivePermissionKeysAsync(
        Guid userId,
        CancellationToken cancellationToken);
}

public sealed record PinRecoveryTarget(Guid UserId, string DisplayName);

public interface IIdentityCredentialRecoveryRepository
{
    Task<IReadOnlyList<PinRecoveryTarget>> GetActiveOwnerTargetsAsync(
        CancellationToken cancellationToken);

    Task<User?> GetUserForRecoveryUpdateAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<bool> HasRecoveryOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task RevokeActiveSessionsAsync(
        Guid userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    Task<bool> TrySaveRecoveryChangesAsync(CancellationToken cancellationToken);
}

public interface IIdentitySessionRepository
{
    void AddSession(UserSession session);

    /// <summary>
    /// Read-only (non-locking) session lookup used by the authentication
    /// middleware where a pessimistic lock would be wasteful.
    /// </summary>
    Task<UserSession?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

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
