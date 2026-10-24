using System.Net;
using System.Net.Http.Json;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.Central.IntegrationTests.Api;

public class WebAdminPortalIntegrationTests : IClassFixture<WebAdminPortalIntegrationTests.PortalFactory>
{
    private readonly PortalFactory _factory;
    private readonly HttpClient _client;

    public WebAdminPortalIntegrationTests(PortalFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });
    }

    public sealed class PortalFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var connectionString = Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                builder.UseSetting("ConnectionStrings:CentralDatabase", connectionString);
            }
        }
    }

    [Fact]
    public async Task Portal_RootAndIndexHtml_ServesWebPortalShell()
    {
        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("EdgeRetails Central Admin Portal", html);
        Assert.Contains("id=\"app-shell\"", html);
        Assert.Contains("id=\"sidebar\"", html);
        Assert.Contains("id=\"dashboard-view\"", html);
        Assert.Contains("id=\"customer360-view\"", html);
        Assert.Contains("id=\"commercial-view\"", html);
        Assert.Contains("id=\"backups-view\"", html);
        Assert.Contains("id=\"audit-view\"", html);
        Assert.Contains("id=\"recovery-view\"", html);
    }

    [Fact]
    public async Task Portal_StaticAssets_ServesCssAndJs()
    {
        var cssResponse = await _client.GetAsync("/css/portal.css");
        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
        Assert.Equal("text/css", cssResponse.Content.Headers.ContentType?.MediaType);
        var css = await cssResponse.Content.ReadAsStringAsync();
        Assert.Contains("--badge-lifetime:", css);
        Assert.Contains("--bg-primary:", css);

        var jsResponse = await _client.GetAsync("/js/portal.js");
        Assert.Equal(HttpStatusCode.OK, jsResponse.StatusCode);
        var js = await jsResponse.Content.ReadAsStringAsync();
        Assert.Contains("AppState", js);
        Assert.Contains("METRIC_FIELDS", js);
        Assert.Contains("loadQueue", js);
        Assert.Contains("same-origin", js);
        Assert.Contains("async function loadRecovery()", js);
        Assert.Contains("function isCanonicalBackupCursor(cursor)", js);
        Assert.Contains("String(offset) === cursor", js);
        Assert.Contains("/api/admin/v1/notifications/status", js);
        Assert.Contains("A worker observation does not prove a backup, restore, host identity, or database target", js);

        var htmlResponse = await _client.GetAsync("/index.html");
        Assert.Equal(HttpStatusCode.OK, htmlResponse.StatusCode);
        var html = await htmlResponse.Content.ReadAsStringAsync();
        Assert.Contains("recovery-worker-status", html);
        Assert.Contains("id=\"recovery-refresh\"", html);
    }

    [Fact]
    public async Task Portal_BackupsView_StatesUnavailableWithoutImplyingRestoreSucceeded()
    {
        var response = await _client.GetAsync("/index.html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("This view reports persisted metadata only.", html);
        Assert.Contains("it is not a live integrity check", html);
        Assert.Contains("does not attest key custody or file availability", html);
        Assert.DoesNotContain("Zero-Knowledge Cryptographic Custody Enforced", html);
    }

    [Fact]
    public async Task Portal_ActionPreview_NonexistentTargetCannotReturnSuccessfulConsequences()
    {
        var sessionId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var now = DateTimeOffset.UtcNow;
            var user = AdminUser.Create($"portal_{Guid.NewGuid():N}", "fixture-password-hash", "fixture-salt", null, false, now);
            db.AdminUsers.Add(user);
            var role = AdminRole.Create($"portal_role_{Guid.NewGuid():N}", "Portal test role", new[] { "sensitive.approve" });
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, now));
            var tokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId.ToString()))).ToLowerInvariant();
            var session = AdminSession.Create(user.Id, tokenHash, now, TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), true, user.SecurityRevision, new[] { "sensitive.approve" });
            typeof(AdminSession).GetProperty("Id")!.SetValue(session, sessionId);
            db.AdminSessions.Add(session);
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });
        client.DefaultRequestHeaders.Add("X-Session-Id", sessionId.ToString());
        var targetEntityId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var request = new ActionPreviewRequestDto
        {
            TargetEntityId = targetEntityId,
            ShopId = shopId,
            ActionType = "REVOKE_LICENSE",
            ProposedParametersJson = "{}"
        };

        using (var anonymousClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        }))
        {
            var anonymousRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/v1/action-preview")
            {
                Content = JsonContent.Create(request)
            };
            anonymousRequest.Headers.Add("Origin", "https://localhost");
            using var anonymousResponse = await anonymousClient.SendAsync(anonymousRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
            var anonymousBody = await anonymousResponse.Content.ReadAsStringAsync();
            Assert.Contains("SESSION_REQUIRED", anonymousBody);
            Assert.DoesNotContain("consequenceSummary", anonymousBody);
        }

        var previewRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/v1/action-preview");
        previewRequest.Headers.Add("Origin", "https://localhost");
        previewRequest.Content = JsonContent.Create(request);
        var response = await client.SendAsync(previewRequest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SHOP_NOT_FOUND", body);
        Assert.DoesNotContain("consequenceSummary", body);
    }
}
