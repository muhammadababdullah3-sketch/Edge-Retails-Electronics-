using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

public sealed class Journey7StorageFixtureSeederTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SeedOwnedStorageBackupAndUploadFixture()
    {
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE_OUTPUT")
            ?? throw new InvalidOperationException("Explicit Journey 7 fixture output path is required.");
        var statePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_STORAGE_STATE")
            ?? throw new InvalidOperationException("Explicit Journey 7 storage state path is required.");

        if (File.Exists(fixturePath) || File.Exists(statePath))
        {
            throw new InvalidOperationException("Journey 7 fixture outputs exist; refusing overwrite.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);

        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var customer = Customer.Create($"Synthetic Journey 7 Customer {runId[..8]}", $"j7-{runId[..8]}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"J7{runId[..10]}", $"Synthetic Journey 7 Shop {runId[..8]}", now);

        var admin = AdminUser.Create($"admin-j7-{runId[..8]}", "synthetic-hash", "synthetic-salt", null, false, now);
        var capabilities = new[]
        {
            "sessions.view", "dashboard.view", "queues.view",
            "storage.view", "storage.upload", "storage.download",
            "backups.view", "customer.view", "shop.view"
        };
        var role = AdminRole.Create($"role-j7-{runId[..8]}", "Synthetic storage operator role", capabilities);

        var sessionId = Guid.NewGuid();
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString())));
        var session = AdminSession.Create(admin.Id, tokenHash, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, admin.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, sessionId);

        var objectId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var retentionPolicyId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes($"SYNTHETIC_JOURNEY7_ENCRYPTED_BACKUP_{runId}");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));

        string storagePath;
        using (var scope = factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorageProvider>();
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();

            await using var stream = new MemoryStream(bytes);
            storagePath = await storage.SaveCiphertextAsync(shop.Id, objectId, stream);

            var backupRecord = EncryptedObjectRecord.Create(
                objectId,
                shop.Id,
                installationId,
                EncryptedObjectRecord.PurposeEncryptedBackup,
                bytes.Length,
                "application/octet-stream",
                digest,
                storagePath,
                retentionPolicyId,
                admin.Username,
                now);

            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.AdminUsers.Add(admin);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(admin.Id, role.Id, now));
            db.AdminSessions.Add(session);
            db.EncryptedObjects.Add(backupRecord);
            await db.SaveChangesAsync();

            Assert.True(await db.EncryptedObjects.AsNoTracking().AnyAsync(x => x.Id == objectId));
        }

        var state = new
        {
            cookies = new[]
            {
                new
                {
                    name = "central_session",
                    value = sessionId.ToString("D"),
                    domain = "127.0.0.1",
                    path = "/",
                    httpOnly = true,
                    secure = true,
                    sameSite = "Strict"
                }
            },
            origins = Array.Empty<object>()
        };

        var fixture = new
        {
            baseUrl = "https://127.0.0.1:18192/",
            adminUserId = admin.Id.ToString("D"),
            username = admin.Username,
            customerId = customer.Id.ToString("D"),
            shopId = shop.Id.ToString("D"),
            backupObjectId = objectId.ToString("D"),
            storagePath,
            ciphertextSha256 = digest,
            contentLength = bytes.Length
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, options));
        await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, options));
    }
}
