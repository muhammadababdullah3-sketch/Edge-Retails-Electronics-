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

public sealed class Journey5SecurityFixtureSeederTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SeedOwnedSecurityAndDualControlApprovalFixture()
    {
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE_OUTPUT")
            ?? throw new InvalidOperationException("Explicit Journey 5 fixture output path is required.");
        var statePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_STORAGE_STATE")
            ?? throw new InvalidOperationException("Explicit Journey 5 storage state path is required.");

        if (File.Exists(fixturePath) || File.Exists(statePath))
        {
            throw new InvalidOperationException("Journey 5 fixture outputs exist; refusing overwrite.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);

        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var customer = Customer.Create($"Synthetic Journey 5 Customer {runId[..8]}", $"j5-{runId[..8]}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"J5{runId[..10]}", $"Synthetic Journey 5 Shop {runId[..8]}", now);

        var requester = AdminUser.Create($"requester-j5-{runId[..8]}", "synthetic-hash", "synthetic-salt", null, false, now);
        var decider = AdminUser.Create($"decider-j5-{runId[..8]}", "synthetic-hash", "synthetic-salt", null, false, now);

        var capabilities = new[]
        {
            "sessions.view", "dashboard.view", "queues.view",
            "sensitive.approve", "auth.stepup", "audit.view",
            "customer.view", "shop.view", "search.masked", "customer.unmask"
        };
        var role = AdminRole.Create($"role-j5-{runId[..8]}", "Synthetic security review role", capabilities);

        var sessionId = Guid.NewGuid();
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString())));
        var session = AdminSession.Create(decider.Id, tokenHash, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, decider.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, sessionId);

        var dummyPayload = Encoding.UTF8.GetBytes($"{{\"action\":\"REBIND\",\"shopId\":\"{shop.Id}\"}}");
        var targetSha256 = Convert.ToHexStringLower(SHA256.HashData(dummyPayload));

        var approvalRequest = SensitiveApprovalRequest.Request(
            "HARDWARE_REBIND",
            shop.Id,
            targetSha256,
            requester.Id,
            "hardware.review",
            Guid.NewGuid(),
            now,
            now.AddHours(2),
            "Rebinding primary installation after audited physical terminal motherboard replacement.");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.AdminUsers.Add(requester);
            db.AdminUsers.Add(decider);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(decider.Id, role.Id, now));
            db.AdminSessions.Add(session);
            db.SensitiveApprovalRequests.Add(approvalRequest);
            await db.SaveChangesAsync();

            Assert.True(await db.SensitiveApprovalRequests.AsNoTracking().AnyAsync(x => x.Id == approvalRequest.Id));
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
            deciderAdminUserId = decider.Id.ToString("D"),
            requesterAdminUserId = requester.Id.ToString("D"),
            username = decider.Username,
            customerId = customer.Id.ToString("D"),
            shopId = shop.Id.ToString("D"),
            approvalRequestId = approvalRequest.Id.ToString("D"),
            targetPayloadSha256 = targetSha256
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, options));
        await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, options));
    }
}
