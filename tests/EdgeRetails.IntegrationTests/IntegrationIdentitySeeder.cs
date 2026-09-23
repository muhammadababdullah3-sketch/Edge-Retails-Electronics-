using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.IntegrationTests;

internal static class IntegrationIdentitySeeder
{
    private const string RoleName = "Integration Operator";

    private static readonly string[] PermissionKeysForOperations = typeof(PermissionKeys)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
        .Select(f => (string)f.GetValue(null)!)
        .ToArray();

    public static async Task<Guid> CreateActorAsync(
        EdgeRetailsDbContext db)
    {
        var role = await db.Roles
            .SingleOrDefaultAsync(x => x.Name == RoleName);

        if (role is null)
        {
            role = new Role
            {
                Name = RoleName,
                IsSystem = false,
                IsActive = true
            };
            db.Roles.Add(role);
        }

        var allDbPermissions = await db.Permissions.ToListAsync();
        var permissionsMap = allDbPermissions
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        var addedAny = false;
        foreach (var key in PermissionKeysForOperations)
        {
            if (!permissionsMap.TryGetValue(key, out var existing))
            {
                var permission = new Permission
                {
                    Key = key,
                    Description = "Integration permission " + key,
                    IsActive = true
                };
                db.Permissions.Add(permission);
                permissionsMap[key] = permission;
                addedAny = true;
            }
        }

        if (addedAny || db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync();
        }

        var assignedPermissionIds = await db.RolePermissions
            .Where(x => x.RoleId == role.Id)
            .Select(x => x.PermissionId)
            .ToHashSetAsync();

        foreach (var permission in permissionsMap.Values)
        {
            if (!assignedPermissionIds.Contains(permission.Id))
            {
                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = role.Id,
                    PermissionId = permission.Id
                });
            }
        }

        var now = DateTimeOffset.UtcNow;
        var actor = new User
        {
            DisplayName = "Integration User " + Guid.NewGuid().ToString("N")[..8],
            RoleId = role.Id,
            PinHash = [1, 2, 3, 4],
            PinSalt = [5, 6, 7, 8],
            PinIterations = 210_000,
            PinAlgorithm = "PBKDF2-SHA256",
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.Add(actor);

        await db.SaveChangesAsync();
        return actor.Id;
    }
}
