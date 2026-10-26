using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

public sealed class Journey2DirectoryFixtureSeederTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SeedOwnedDirectoryDashboardQueueFixture()
    {
        await new PortalSupportAndQueueFixtureSeederTests(factory).SeedOwnedAuthenticatedSupportQueueBrowserFixture();
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE_OUTPUT")!;
        var statePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_STORAGE_STATE")!;
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(fixturePath))!.AsObject();
        var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
        var cookie = state["cookies"]!.AsArray()[0]!.AsObject();
        var previousSessionId = Guid.Parse(cookie["value"]!.GetValue<string>());
        var now = DateTimeOffset.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var previous = await db.AdminSessions.SingleAsync(x => x.Id == previousSessionId);
            var admin = await db.AdminUsers.SingleAsync(x => x.Id == previous.AdminUserId);
            var roleIds = await db.AdminUserRoles.Where(x => x.AdminUserId == admin.Id).Select(x => x.RoleId).ToArrayAsync();
            var roles = await db.AdminRoles.Where(x => roleIds.Contains(x.Id)).ToArrayAsync();
            var capabilities = previous.GetCapabilities().Concat(new[] { "customer.create", "shop.register", "search.masked" })
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var role in roles)
            {
                role.UpdateCapabilities(capabilities);
            }
            var sessionId = Guid.NewGuid();
            var session = AdminSession.Create(admin.Id,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString()))),
                now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, admin.SecurityRevision, capabilities);
            typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, sessionId);
            previous.Revoke("Superseded by scoped Journey 2 synthetic session", now);
            db.AdminSessions.Add(session);
            await db.SaveChangesAsync();
            cookie["value"] = sessionId.ToString("D");
            fixture["expectedInitialActiveShops"] = await db.Shops.CountAsync(x => x.Status == Shop.StatusActive);
            fixture["expectedOpenSupportTickets"] = await db.SupportTickets.CountAsync(x => x.Status == "OPEN");
        }
        var runId = Guid.NewGuid().ToString("N");
        fixture["directoryCustomerName"] = $"Synthetic Journey2 customer {runId}";
        fixture["directoryShopName"] = $"Synthetic Journey2 shop {runId}";
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(statePath, state.ToJsonString(options));
        await File.WriteAllTextAsync(fixturePath, fixture.ToJsonString(options));
    }
}
