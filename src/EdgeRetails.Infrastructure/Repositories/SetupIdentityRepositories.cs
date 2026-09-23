using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class SetupRepository : ISetupRepository
{
    private readonly EdgeRetailsDbContext _db;

    public SetupRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<InstallationState?> GetInstallationStateForUpdateAsync(
        CancellationToken cancellationToken)
    {
        var tracked = _db.InstallationStates.Local.SingleOrDefault();
        return tracked is not null
            ? Task.FromResult<InstallationState?>(tracked)
            : _db.InstallationStates
                .FromSqlRaw(
                    "SELECT * FROM system.installation_state WHERE singleton_key = 'PRIMARY' FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
    }
    public Task<bool> AnyUsersAsync(CancellationToken cancellationToken) =>
        _db.Users.AnyAsync(cancellationToken);

    public Task<ShopProfile?> GetShopProfileAsync(CancellationToken cancellationToken) =>
        _db.ShopProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProfileKey == "PRIMARY", cancellationToken);

    public Task<ShopProfile?> GetShopProfileForUpdateAsync(CancellationToken cancellationToken) =>
        _db.ShopProfiles
            .FromSqlRaw("SELECT * FROM system.shop_profile WHERE profile_key = 'PRIMARY' FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsAsync(CancellationToken cancellationToken) =>
        _db.ReceiptTemplateSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TemplateKey == "PRIMARY", cancellationToken);

    public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsForUpdateAsync(CancellationToken cancellationToken) =>
        _db.ReceiptTemplateSettings
            .FromSqlRaw("SELECT * FROM system.receipt_template_settings WHERE template_key = 'PRIMARY' FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public void AddInstallationState(InstallationState state) =>
        _db.InstallationStates.Add(state);

    public void AddShopProfile(ShopProfile profile) =>
        _db.ShopProfiles.Add(profile);

    public void AddCustomer(Customer customer) =>
        _db.Customers.Add(customer);

    public void AddReceiptTemplateSettings(ReceiptTemplateSettings settings) =>
        _db.ReceiptTemplateSettings.Add(settings);

    public void AddRole(Role role) =>
        _db.Roles.Add(role);

    public void AddPermission(Permission permission) =>
        _db.Permissions.Add(permission);

    public void AddRolePermission(RolePermission rolePermission) =>
        _db.RolePermissions.Add(rolePermission);

    public void AddUser(User user) =>
        _db.Users.Add(user);
}
public sealed class IdentityReadRepository : IIdentityReadRepository
{
    private readonly EdgeRetailsDbContext _db;

    public IdentityReadRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<User>> GetActiveUsersAsync(
        CancellationToken cancellationToken) =>
        await _db.Users
            .AsNoTracking()
            .Where(x => x.Status == UserStatus.Active)
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public Task<User?> GetUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        _db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);

    public Task<Role?> GetRoleAsync(
        Guid roleId,
        CancellationToken cancellationToken) =>
        _db.Roles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == roleId, cancellationToken); public async Task<IReadOnlySet<string>> GetEffectivePermissionKeysAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var keys = await (
            from rolePermission in _db.RolePermissions
            join permission in _db.Permissions
                on rolePermission.PermissionId equals permission.Id
            where rolePermission.RoleId == user.RoleId && permission.IsActive
            select permission.Key)
            .ToListAsync(cancellationToken);

        var result = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrides = await (
            from permissionOverride in _db.UserPermissionOverrides
            join permission in _db.Permissions
                on permissionOverride.PermissionId equals permission.Id
            where permissionOverride.UserId == user.Id && permission.IsActive
            select new { permission.Key, permissionOverride.IsAllowed })
            .ToListAsync(cancellationToken); foreach (var permissionOverride in overrides)
        {
            if (permissionOverride.IsAllowed)
            {
                result.Add(permissionOverride.Key);
            }
            else
            {
                result.Remove(permissionOverride.Key);
            }
        }

        return result;
    }
}

public sealed class IdentitySessionRepository : IIdentitySessionRepository
{
    private readonly EdgeRetailsDbContext _db;

    public IdentitySessionRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public void AddSession(UserSession session) =>
        _db.UserSessions.Add(session);

    public Task<UserSession?> GetSessionForUpdateAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        _db.UserSessions
            .FromSqlInterpolated(
                $"SELECT * FROM identity.user_sessions WHERE id = {sessionId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
}
