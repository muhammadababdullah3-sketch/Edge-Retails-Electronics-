using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

public sealed class PortalSupportAndQueueFixtureSeederTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SeedOwnedAuthenticatedSupportQueueBrowserFixture()
    {
        var fixturePath = Required("CENTRAL_PORTAL_BROWSER_FIXTURE_OUTPUT");
        var statePath = Required("CENTRAL_PORTAL_BROWSER_STORAGE_STATE");
        if (File.Exists(fixturePath) || File.Exists(statePath))
        {
            throw new InvalidOperationException("Browser fixture outputs must be absent; refusing to overwrite.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);

        var now = DateTimeOffset.UtcNow;
        var customer = Customer.Create("Synthetic browser customer", $"browser-{Guid.NewGuid():N}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"BR{Guid.NewGuid():N}"[..12], "Synthetic browser shop", now);
        var admin = AdminUser.Create($"browser-{Guid.NewGuid():N}", "synthetic-hash", "synthetic-salt", null, false, now);
        var capabilities = new[] { "sessions.view", "queues.view", "dashboard.view", "support.view", "support.create", "support.reply" };
        var role = AdminRole.Create($"browser-{Guid.NewGuid():N}", "Synthetic browser support", capabilities);
        var sessionId = Guid.NewGuid();
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString()))).ToLowerInvariant();
        var session = AdminSession.Create(admin.Id, tokenHash, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, admin.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, sessionId);
        var ticketIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();
        var queueMarker = $"BrowserQueueFixture-{Guid.NewGuid():N}";
        Guid[] openQueueIds;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.AdminUsers.Add(admin);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(admin.Id, role.Id, now));
            db.AdminSessions.Add(session);
            for (var i = 0; i < ticketIds.Length; i++)
            {
                db.SupportTickets.Add(SupportTicket.Create(ticketIds[i], shop.Id, admin.Id,
                    $"{queueMarker} {i:D2}", "Synthetic persisted support queue row.", [], 1, now.AddSeconds(i)));
            }
            await db.SaveChangesAsync();
            openQueueIds = await db.SupportTickets.AsNoTracking()
                .Where(x => x.Status == SupportTicket.StatusOpen)
                .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
                .Select(x => x.Id).ToArrayAsync();
        }
        if (openQueueIds.Length < 21)
        {
            throw new InvalidOperationException("The seeded database did not produce the required two-page synthetic support queue.");
        }

        var state = new
        {
            cookies = new[]
            {
                new { name = "central_session", value = sessionId.ToString("D"), domain = "127.0.0.1", path = "/", expires = -1, httpOnly = true, secure = false, sameSite = "Lax" }
            },
            origins = Array.Empty<object>()
        };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        var fixture = new
        {
            baseUrl = "https://127.0.0.1:18192/",
            storageStatePath = Path.GetFullPath(statePath),
            shopId = shop.Id.ToString("D"),
            queueKind = "SUPPORT_INBOX",
            queuePageOneEntityIds = openQueueIds.Take(20).Select(x => x.ToString("D")).ToArray(),
            queuePageTwoEntityIds = openQueueIds.Skip(20).Take(20).Select(x => x.ToString("D")).ToArray(),
            customer360ExpectedText = shop.ShopCode
        };
        await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(21, ticketIds.Length);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? Path.GetFullPath(value)
        : throw new InvalidOperationException($"Explicit owned browser fixture path {name} required.");
}
