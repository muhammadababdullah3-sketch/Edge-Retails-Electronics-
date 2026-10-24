using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

public sealed class CanonicalEvidenceDownloadApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CanonicalEvidenceDownloadApiIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task CanonicalEvidenceDownload_RequiresCapabilityAndActivePaymentProofObject()
    {
        RateLimitingMiddleware.Reset();
        var shop = Shop.Create(Guid.NewGuid(), $"ED_{Guid.NewGuid():N}"[..10], "Synthetic evidence download shop", DateTimeOffset.UtcNow);
        using var key = RSA.Create(2048);
        var installation = Installation.Register(shop.Id, Guid.NewGuid(), "SYNTHETIC_EVIDENCE_FP", key.ExportRSAPublicKeyPem(),
            "evidence-download-host", "Windows 11", DateTimeOffset.UtcNow);
        installation.Approve(DateTimeOffset.UtcNow);

        var ciphertext = Encoding.UTF8.GetBytes("SYNTHETIC_PAYMENT_PROOF_CIPHERTEXT_033");
        var objectId = Guid.NewGuid();
        var retentionPolicyId = Guid.Parse("d0d8959b-278f-4cda-a81c-3eb88732f071");
        string storagePath;
        using (var content = new MemoryStream(ciphertext))
        {
            storagePath = await _factory.Services.GetRequiredService<IObjectStorageProvider>()
                .SaveCiphertextAsync(shop.Id, objectId, content);
        }
        var record = EncryptedObjectRecord.Create(objectId, shop.Id, installation.Id,
            EncryptedObjectRecord.PurposePaymentProof, ciphertext.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(ciphertext)), storagePath, retentionPolicyId,
            "synthetic-fixture", DateTimeOffset.UtcNow);
        Console.WriteLine($"EVIDENCE_DOWNLOAD_FIXTURE shop={shop.Id:D} installation={installation.Id:D} object={objectId:D} retentionPolicy={retentionPolicyId:D}");

        var tombstonedObjectId = Guid.NewGuid();
        var tombstonedPath = await _factory.Services.GetRequiredService<IObjectStorageProvider>()
            .SaveCiphertextAsync(shop.Id, tombstonedObjectId, new MemoryStream(ciphertext));
        var tombstoned = EncryptedObjectRecord.Create(tombstonedObjectId, shop.Id, installation.Id,
            EncryptedObjectRecord.PurposePaymentProof, ciphertext.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(ciphertext)), tombstonedPath, retentionPolicyId,
            "synthetic-fixture", DateTimeOffset.UtcNow);
        tombstoned.Tombstone(DateTimeOffset.UtcNow);
        Console.WriteLine($"EVIDENCE_DOWNLOAD_TOMBSTONE_FIXTURE object={tombstonedObjectId:D}");

        using (var db = GetDbContext())
        {
            db.Shops.Add(shop);
            db.Installations.Add(installation);
            db.EncryptedObjects.AddRange(record, tombstoned);
            await db.SaveChangesAsync();
        }

        using var permitted = await CreateAdminClientAsync("evidence.download");
        using var denied = await CreateAdminClientAsync();
        var route = $"/api/admin/v1/evidence/{objectId:D}/download?objectId={objectId:D}&purpose=PAYMENT_PROOF&retentionPolicyId={retentionPolicyId:D}";

        using (var deniedResponse = await denied.GetAsync(route))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        }

        using (var pathMismatch = await permitted.GetAsync(
            $"/api/admin/v1/evidence/{objectId:D}/download?objectId={Guid.NewGuid():D}&purpose=PAYMENT_PROOF"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, pathMismatch.StatusCode);
        }

        using (var purposeMismatch = await permitted.GetAsync(
            $"/api/admin/v1/evidence/{objectId:D}/download?objectId={objectId:D}&purpose=ENCRYPTED_BACKUP"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, purposeMismatch.StatusCode);
        }

        using (var policyMismatch = await permitted.GetAsync(
            $"/api/admin/v1/evidence/{objectId:D}/download?objectId={objectId:D}&purpose=PAYMENT_PROOF&retentionPolicyId={Guid.NewGuid():D}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, policyMismatch.StatusCode);
        }

        using (var tombstoneResponse = await permitted.GetAsync(
            $"/api/admin/v1/evidence/{tombstonedObjectId:D}/download?objectId={tombstonedObjectId:D}&purpose=PAYMENT_PROOF"))
        {
            Assert.Equal(HttpStatusCode.NotFound, tombstoneResponse.StatusCode);
        }

        using (var response = await permitted.GetAsync(route))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(ciphertext, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(objectId.ToString("D"), response.Headers.GetValues("X-Object-Id").Single());
            Assert.Equal(ciphertext.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                response.Content.Headers.ContentLength?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal($"sha-256={Convert.ToBase64String(SHA256.HashData(ciphertext))}",
                response.Headers.GetValues("Digest").Single());
        }

        using (var db = GetDbContext())
        {
            Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x =>
                x.Action == "EVIDENCE_DOWNLOADED" && x.ResourceId == objectId.ToString("D")));
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
        var username = $"evidence_download_{Guid.NewGuid():N}";
        var totp = _factory.Services.GetRequiredService<ITotpService>();
        var hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        var secret = totp.GenerateSecret();
        using (var db = GetDbContext())
        {
            var user = AdminUser.Create(username, hasher.HashPassword(password), "salt", secret, true, DateTimeOffset.UtcNow);
            var role = AdminRole.Create($"evidence_download_role_{Guid.NewGuid():N}", "Evidence download", capabilities);
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
        var challengeId = Guid.Parse((await login.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["challengeId"]!.GetValue<string>());
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
