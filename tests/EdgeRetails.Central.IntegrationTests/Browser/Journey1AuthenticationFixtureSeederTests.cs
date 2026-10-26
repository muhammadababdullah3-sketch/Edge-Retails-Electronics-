using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

public sealed class Journey1AuthenticationFixtureSeederTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SeedOwnedPasswordMfaAndExpiredSessionFixture()
    {
        var path = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE_OUTPUT")
            ?? throw new InvalidOperationException("Explicit Journey 1 fixture output is required.");
        if (File.Exists(path))
        {
            throw new InvalidOperationException("Journey 1 fixture exists; refusing reuse.");
        }
        var now = DateTimeOffset.UtcNow;
        var password = $"Synthetic-Journey1-{Guid.NewGuid():N}!";
        const string mfaSecret = "JBSWY3DPEHPK3PXP";
        var user = AdminUser.Create($"journey1-{Guid.NewGuid():N}", new PasswordHasher().HashPassword(password),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), mfaSecret, true, now);
        string[] capabilities = ["sessions.view", "dashboard.view", "queues.view", "support.view"];
        var role = AdminRole.Create($"journey1-{Guid.NewGuid():N}", "Synthetic scoped authentication role", capabilities);
        var expired = AdminSession.Create(user.Id, Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))),
            now.AddDays(-1), TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, user.SecurityRevision, capabilities);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            db.AdminUsers.Add(user);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, now));
            db.AdminSessions.Add(expired);
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            baseUrl = "https://127.0.0.1:18192/", adminUserId = user.Id, username = user.Username,
            password, mfaSecret, expiredSessionId = expired.Id
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
