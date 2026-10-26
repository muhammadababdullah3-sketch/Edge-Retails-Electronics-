using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

public sealed class Journey4TransferFixtureSeederTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SeedOwnedTransferAndHardwareReviewFixture()
    {
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE_OUTPUT")
            ?? throw new InvalidOperationException("Explicit Journey 4 fixture output path is required.");
        var statePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_STORAGE_STATE")
            ?? throw new InvalidOperationException("Explicit Journey 4 storage state path is required.");

        if (File.Exists(fixturePath) || File.Exists(statePath))
        {
            throw new InvalidOperationException("Journey 4 fixture outputs exist; refusing overwrite.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);

        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var customer = Customer.Create($"Synthetic Journey 4 Customer {runId[..8]}", $"j4-{runId[..8]}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"J4{runId[..10]}", $"Synthetic Journey 4 Shop {runId[..8]}", now);

        var admin = AdminUser.Create($"admin-j4-{runId[..8]}", "synthetic-hash", "synthetic-salt", null, false, now);
        var capabilities = new[]
        {
            "sessions.view", "dashboard.view", "queues.view",
            "hardware.review", "primary.transfer", "sensitive.approve",
            "auth.stepup", "customer.view", "shop.view"
        };
        var role = AdminRole.Create($"role-j4-{runId[..8]}", "Synthetic transfer operator role", capabilities);
        var sessionId = Guid.NewGuid();
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString())));
        var session = AdminSession.Create(admin.Id, tokenHash, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, admin.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, sessionId);

        var licenseId = Guid.NewGuid();
        const string pubKeyPem = "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE7n0y7ZqF7Q21l4F8p\n-----END PUBLIC KEY-----";

        var primaryInstall = Installation.Register(
            shop.Id,
            licenseId,
            $"FINGERPRINT-PRIMARY-{runId[..8]}",
            pubKeyPem,
            "PRIMARY-POS-01",
            "WINDOWS",
            now);

        var secondaryInstall = Installation.Register(
            shop.Id,
            licenseId,
            $"FINGERPRINT-SECONDARY-{runId[..8]}",
            pubKeyPem,
            "SECONDARY-POS-02",
            "WINDOWS",
            now);

        var hardwareReview = HardwareReview.Create(
            shop.Id,
            primaryInstall.Id,
            $"REPORTED-FP-{runId[..8]}",
            $"STORED-FP-{runId[..8]}",
            "Motherboard replacement fingerprint discrepancy.",
            now);

        var transferId = Guid.NewGuid();
        var primaryTransfer = PrimaryTransfer.Initiate(
            transferId,
            shop.Id,
            primaryInstall.Id,
            secondaryInstall.Id,
            "Hardware upgrade and primary device replacement.",
            admin.Id,
            now);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.AdminUsers.Add(admin);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(admin.Id, role.Id, now));
            db.AdminSessions.Add(session);
            db.Installations.Add(primaryInstall);
            db.Installations.Add(secondaryInstall);
            db.HardwareReviews.Add(hardwareReview);
            db.PrimaryTransfers.Add(primaryTransfer);
            await db.SaveChangesAsync();

            Assert.True(await db.HardwareReviews.AsNoTracking().AnyAsync(x => x.Id == hardwareReview.Id));
            Assert.True(await db.PrimaryTransfers.AsNoTracking().AnyAsync(x => x.Id == transferId));
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
            licenseId = licenseId.ToString("D"),
            primaryInstallationId = primaryInstall.Id.ToString("D"),
            secondaryInstallationId = secondaryInstall.Id.ToString("D"),
            hardwareReviewId = hardwareReview.Id.ToString("D"),
            primaryTransferId = transferId.ToString("D")
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, options));
        await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, options));
    }
}
