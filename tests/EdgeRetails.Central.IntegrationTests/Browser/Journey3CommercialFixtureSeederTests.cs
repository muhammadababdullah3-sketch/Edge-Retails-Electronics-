using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

public sealed class Journey3CommercialFixtureSeederTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task SeedOwnedCommercialOrderAndCatalogFixture()
    {
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE_OUTPUT")
            ?? throw new InvalidOperationException("Explicit Journey 3 fixture output path is required.");
        var statePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_STORAGE_STATE")
            ?? throw new InvalidOperationException("Explicit Journey 3 storage state path is required.");

        if (File.Exists(fixturePath) || File.Exists(statePath))
        {
            throw new InvalidOperationException("Journey 3 fixture outputs exist; refusing overwrite.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);

        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var customer = Customer.Create($"Synthetic Journey 3 Customer {runId[..8]}", $"j3-{runId[..8]}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"J3{runId[..10]}", $"Synthetic Journey 3 Shop {runId[..8]}", now);

        var admin = AdminUser.Create($"admin-j3-{runId[..8]}", "synthetic-hash", "synthetic-salt", null, false, now);
        var capabilities = new[]
        {
            "sessions.view", "dashboard.view", "queues.view",
            "commercial.orders", "commercial.view", "commercial.licenses",
            "commercial.payment_evidence", "commercial.renew", "customer.view", "shop.view"
        };
        var role = AdminRole.Create($"role-j3-{runId[..8]}", "Synthetic commercial operator role", capabilities);
        var sessionId = Guid.NewGuid();
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString())));
        var session = AdminSession.Create(admin.Id, tokenHash, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, admin.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, sessionId);

        var catalogItem = PlanCatalogItem.Create(
            $"PLAN-J3-{runId[..6].ToUpperInvariant()}",
            "Synthetic Standard License Plan",
            PlanCatalogItem.TargetBaseLicense,
            PlanCatalogItem.TermFixed,
            "YEARLY",
            1200.00m,
            "USD");

        var snapshot = PricingSnapshot.Create(
            shop.Id,
            catalogItem,
            1,
            now);

        var order = Order.CreateFromSnapshot(snapshot, customer.Id, now);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.AdminUsers.Add(admin);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(admin.Id, role.Id, now));
            db.AdminSessions.Add(session);
            db.PlanCatalogItems.Add(catalogItem);
            db.PricingSnapshots.Add(snapshot);
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            Assert.True(await db.Orders.AsNoTracking().AnyAsync(x => x.Id == order.Id));
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
            orderId = order.Id.ToString("D"),
            catalogItemId = catalogItem.Id.ToString("D"),
            orderTotal = order.TotalAmount,
            currency = order.Currency
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, options));
        await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, options));
    }
}
