using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.DevTools;

internal static class PortalAdminProvisioner
{
    public static async Task<int> SyncAsync(CentralDbContext db, string username, string password, string contentRootPath)
    {
        var normalized = username.Trim().ToLowerInvariant();
        if (normalized.Length == 0 || password.Length == 0)
        {
            Console.Error.WriteLine("Username and password cannot be empty.");
            return 1;
        }

        var hasher = new PasswordHasher();
        var totp = new TotpService();
        var now = DateTimeOffset.UtcNow;
        var existing = await db.AdminUsers
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Username == normalized);

        string mfaSecret;
        if (existing is null)
        {
            mfaSecret = totp.GenerateSecret();
            var user = AdminUser.Create(normalized, hasher.HashPassword(password), "salt", mfaSecret, true, now);
            var role = AdminRole.Create(
                $"Local dev operator {Guid.NewGuid():N}",
                "Disposable development portal operator",
                CentralDevOperatorCapabilities.PortalOperator);
            db.AdminUsers.Add(user);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, now));
            Console.WriteLine("Created new local portal admin (MFA secret is new — enroll authenticator).");
        }
        else
        {
            existing.UpdatePassword(hasher.HashPassword(password), existing.Salt);
            if (!existing.IsActive)
            {
                Console.Error.WriteLine($"Admin user '{normalized}' exists but is inactive.");
                return 5;
            }

            if (string.IsNullOrWhiteSpace(existing.MfaSecret))
            {
                mfaSecret = totp.GenerateSecret();
                existing.SetMfa(true, mfaSecret);
                Console.WriteLine("Configured MFA secret for existing admin.");
            }
            else
            {
                mfaSecret = existing.MfaSecret!;
            }

            if (existing.UserRoles.Count == 0)
            {
                var role = AdminRole.Create(
                    $"Local dev operator {Guid.NewGuid():N}",
                    "Disposable development portal operator",
                    CentralDevOperatorCapabilities.PortalOperator);
                db.AdminRoles.Add(role);
                db.AdminUserRoles.Add(new AdminUserRole(existing.Id, role.Id, now));
                Console.WriteLine("Attached portal operator role to existing admin.");
            }
        }

        await db.SaveChangesAsync();

        CentralLocalPortalCredentialStore.Write(contentRootPath, new CentralLocalPortalCredentialRecord
        {
            Username = normalized,
            Password = password,
            MfaSecret = mfaSecret
        });

        var recordPath = CentralLocalPortalCredentialStore.ResolveRecordPath(contentRootPath);
        Console.WriteLine("Local portal sign-in record synchronized.");
        Console.WriteLine($"  username: {normalized}");
        Console.WriteLine($"  password: (same as {CentralLocalPortalCredentials.PasswordVariable} — see local/central-dev.portal.json)");
        Console.WriteLine($"  mfaSecret: {mfaSecret}");
        Console.WriteLine($"  record: {recordPath}");
        Console.WriteLine("  Portal login page loads these values automatically in Development.");
        return 0;
    }
}
