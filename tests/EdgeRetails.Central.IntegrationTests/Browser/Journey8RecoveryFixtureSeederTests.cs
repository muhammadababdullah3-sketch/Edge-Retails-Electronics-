using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

public sealed class Journey8RecoveryFixtureSeederTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SeedOwnedOutboxWorkerAndRecoveryFixture()
    {
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE_OUTPUT")
            ?? throw new InvalidOperationException("Explicit Journey 8 fixture output path is required.");
        var statePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_STORAGE_STATE")
            ?? throw new InvalidOperationException("Explicit Journey 8 storage state path is required.");

        if (File.Exists(fixturePath) || File.Exists(statePath))
        {
            throw new InvalidOperationException("Journey 8 fixture outputs exist; refusing overwrite.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);

        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var customer = Customer.Create($"Synthetic Journey 8 Customer {runId[..8]}", $"j8-{runId[..8]}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"J8{runId[..10]}", $"Synthetic Journey 8 Shop {runId[..8]}", now);

        var admin = AdminUser.Create($"admin-j8-{runId[..8]}", "synthetic-hash", "synthetic-salt", null, false, now);
        var capabilities = new[]
        {
            "sessions.view", "dashboard.view", "queues.view",
            "operations.view", "operations.outbox", "audit.view"
        };
        var role = AdminRole.Create($"role-j8-{runId[..8]}", "Synthetic operations recovery role", capabilities);

        var sessionId = Guid.NewGuid();
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString())));
        var session = AdminSession.Create(admin.Id, tokenHash, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, admin.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, sessionId);

        var pendingMessageId = Guid.NewGuid();
        var pendingMessage = CentralOutboxMessage.Create(
            pendingMessageId,
            "commercial.license_issued",
            "CommercialLicense",
            Guid.NewGuid().ToString("D"),
            $"{{\"shopId\":\"{shop.Id}\",\"purpose\":\"RECOVERY_TEST\"}}",
            now);

        var deadLetterMessageId = Guid.NewGuid();
        var deadLetterMessage = CentralOutboxMessage.Create(
            deadLetterMessageId,
            "notification.poison_event",
            "Notification",
            Guid.NewGuid().ToString("D"),
            "{\"error\":\"Simulated downstream recipient unreachable.\"}",
            now.AddMinutes(-10));
        deadLetterMessage.MarkFailed("Permanent downstream channel timeout", maxRetries: 1);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.AdminUsers.Add(admin);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(admin.Id, role.Id, now));
            db.AdminSessions.Add(session);
            db.CentralOutboxMessages.Add(pendingMessage);
            db.CentralOutboxMessages.Add(deadLetterMessage);
            await db.SaveChangesAsync();

            Assert.True(await db.CentralOutboxMessages.AsNoTracking().AnyAsync(x => x.Id == pendingMessageId));
            Assert.True(await db.CentralOutboxMessages.AsNoTracking().AnyAsync(x => x.Id == deadLetterMessageId));
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
            pendingMessageId = pendingMessageId.ToString("D"),
            deadLetterMessageId = deadLetterMessageId.ToString("D")
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, options));
        await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, options));
    }
}
