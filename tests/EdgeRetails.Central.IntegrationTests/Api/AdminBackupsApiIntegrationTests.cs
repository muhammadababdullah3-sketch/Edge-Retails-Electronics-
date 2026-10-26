using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Api;

public sealed class AdminBackupsApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AdminBackupsApiIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task BackupMetadata_RequiresCapabilityReturnsOnlyPersistedBackupMetadataAndRejectsInvalidQueries()
    {
        RateLimitingMiddleware.Reset();
        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), $"BM_{Guid.NewGuid():N}"[..10], "Synthetic backup metadata shop", now);
        using var key = RSA.Create(2048);
        var installation = Installation.Register(shop.Id, Guid.NewGuid(), "SYNTHETIC_BACKUP_METADATA_FP",
            key.ExportRSAPublicKeyPem(), "backup-metadata-host", "Windows 11", now);
        var ciphertext = Encoding.UTF8.GetBytes("SYNTHETIC_ENCRYPTED_BACKUP_METADATA_001");
        var objectId = Guid.NewGuid();
        var path = await _factory.Services.GetRequiredService<IObjectStorageProvider>()
            .SaveCiphertextAsync(shop.Id, objectId, new MemoryStream(ciphertext));
        var record = EncryptedObjectRecord.Create(objectId, shop.Id, installation.Id,
            EncryptedObjectRecord.PurposeEncryptedBackup, ciphertext.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(ciphertext)), path,
            Guid.Parse("d0d8959b-278f-4cda-a81c-3eb88732f071"), "synthetic-private-manifest", now);
        var secondBackupId = Guid.NewGuid();
        var secondBackupPath = await _factory.Services.GetRequiredService<IObjectStorageProvider>()
            .SaveCiphertextAsync(shop.Id, secondBackupId, new MemoryStream(ciphertext));
        var secondBackup = EncryptedObjectRecord.Create(secondBackupId, shop.Id, installation.Id,
            EncryptedObjectRecord.PurposeEncryptedBackup, ciphertext.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(ciphertext)), secondBackupPath,
            Guid.Parse("d0d8959b-278f-4cda-a81c-3eb88732f071"), "synthetic-second-manifest", now);
        var nonBackupId = Guid.NewGuid();
        var nonBackupPath = await _factory.Services.GetRequiredService<IObjectStorageProvider>()
            .SaveCiphertextAsync(shop.Id, nonBackupId, new MemoryStream(ciphertext));
        var nonBackup = EncryptedObjectRecord.Create(nonBackupId, shop.Id, installation.Id,
            EncryptedObjectRecord.PurposePaymentProof, ciphertext.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(ciphertext)), nonBackupPath,
            Guid.Parse("d0d8959b-278f-4cda-a81c-3eb88732f071"), "synthetic-payment-manifest", now);

        using (var db = GetDbContext())
        {
            db.Shops.Add(shop);
            db.Installations.Add(installation);
            db.EncryptedObjects.AddRange(record, secondBackup, nonBackup);
            await db.SaveChangesAsync();
        }

        using var permitted = await CreateAdminClientAsync("backup.download");
        using var denied = await CreateAdminClientAsync();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            }).GetAsync("/api/admin/v1/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await denied.GetAsync("/api/admin/v1/backups")).StatusCode);

        foreach (var invalidQuery in new[]
        {
            "pageSize=1&unexpected=value",
            "pageSize=1&cursor=00",
            "pageSize=1&cursor=100001"
        })
        {
            using var invalid = await permitted.GetAsync($"/api/admin/v1/backups?{invalidQuery}");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var duplicate = await permitted.GetAsync("/api/admin/v1/backups?pageSize=1&pageSize=2"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        }

        using var response = await permitted.GetAsync("/api/admin/v1/backups?pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.True(root.GetProperty("authorizedCount").GetInt32() >= 2);
        Assert.Equal(1, root.GetProperty("items").GetArrayLength());
        var nextCursor = root.GetProperty("nextCursor").GetString();
        Assert.NotNull(nextCursor);
        Assert.False(root.GetProperty("isTruncated").GetBoolean());
        Assert.Equal(root.GetProperty("asOfVersion").GetInt64(), root.GetProperty("projectionRevision").GetInt64());
        var item = root.GetProperty("items")[0];
        var firstId = item.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, firstId);
        Assert.NotEqual(Guid.Empty, item.GetProperty("shopId").GetGuid());
        Assert.True(item.GetProperty("contentLength").GetInt64() > 0);
        Assert.Matches("^[A-Fa-f0-9]{64}$", item.GetProperty("ciphertextSha256").GetString());
        Assert.True(item.GetProperty("resourceVersion").GetInt32() >= 1);
        Assert.False(item.TryGetProperty("storagePath", out _));
        Assert.False(item.TryGetProperty("localManifestAuthenticationOpaque", out _));
        Assert.False(item.TryGetProperty("installationId", out _));

        var snapshot = root.GetProperty("asOfVersion").GetInt64();
        using (var pageTwo = await permitted.GetAsync($"/api/admin/v1/backups?pageSize=1&cursor={Uri.EscapeDataString(nextCursor!)}&asOfVersion={snapshot}"))
        {
            Assert.Equal(HttpStatusCode.OK, pageTwo.StatusCode);
            using var pageTwoBody = JsonDocument.Parse(await pageTwo.Content.ReadAsStringAsync());
            Assert.Equal(snapshot, pageTwoBody.RootElement.GetProperty("asOfVersion").GetInt64());
            Assert.Equal(1, pageTwoBody.RootElement.GetProperty("items").GetArrayLength());
            Assert.NotEqual(firstId, pageTwoBody.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        }

        using (var db = GetDbContext())
        {
            var changed = await db.EncryptedObjects.SingleAsync(x => x.Id == objectId);
            changed.Tombstone(now.AddSeconds(1));
            await db.SaveChangesAsync();
        }
        using (var stalePage = await permitted.GetAsync($"/api/admin/v1/backups?pageSize=1&cursor={Uri.EscapeDataString(nextCursor!)}&asOfVersion={snapshot}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, stalePage.StatusCode);
        }
    }

    private CentralDbContext GetDbContext()
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CentralDbContext>();
    }

    private async Task<HttpClient> CreateAdminClientAsync(params string[] capabilities)
    {
        const string password = "AdminPassword#123";
        var username = $"backup_metadata_{Guid.NewGuid():N}";
        var totp = _factory.Services.GetRequiredService<ITotpService>();
        var hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        var secret = totp.GenerateSecret();
        using (var db = GetDbContext())
        {
            var user = AdminUser.Create(username, hasher.HashPassword(password), "salt", secret, true, DateTimeOffset.UtcNow);
            var role = AdminRole.Create($"backup_metadata_role_{Guid.NewGuid():N}", "Backup metadata", capabilities);
            db.AdminUsers.Add(user);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateDefaultClient(new CanonicalFingerprintDelegatingHandler());
        client.BaseAddress = new Uri("https://localhost");
        var login = await client.PostAsJsonAsync("/api/admin/v1/auth/login", new LoginRequestDto
        {
            Username = username,
            PasswordOrProviderAssertion = password
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var challengeId = Guid.Parse((await login.Content.ReadFromJsonAsync<JsonObject>())!["challengeId"]!.GetValue<string>());
        var mfa = await client.PostAsJsonAsync("/api/admin/v1/auth/mfa", new MfaRequestDto
        {
            LoginChallengeId = challengeId,
            Proof = totp.GenerateCode(secret, DateTimeOffset.UtcNow)
        });
        Assert.Equal(HttpStatusCode.OK, mfa.StatusCode);
        Assert.True(mfa.Headers.TryGetValues("Set-Cookie", out var cookies));
        client.DefaultRequestHeaders.Add("Cookie", cookies.Select(x => x.Split(';', 2)[0])
            .Single(x => x.StartsWith("central_session=", StringComparison.Ordinal)));
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return client;
    }
}
