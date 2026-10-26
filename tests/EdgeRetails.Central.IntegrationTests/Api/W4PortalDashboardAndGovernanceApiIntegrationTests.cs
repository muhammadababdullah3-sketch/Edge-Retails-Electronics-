using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.Central.IntegrationTests.Api;

public class W4PortalDashboardAndGovernanceApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public W4PortalDashboardAndGovernanceApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateDefaultClient();
        _client.BaseAddress = new Uri("https://localhost");

        RateLimitingMiddleware.Reset();
    }

    private CentralDbContext GetDbContext()
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CentralDbContext>();
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(content) ?? throw new InvalidOperationException("Failed to parse JSON response: " + content);
    }

    private HttpClient CreateSessionClient(Guid sessionId)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });
        client.DefaultRequestHeaders.Add("X-Session-Id", sessionId.ToString());
        return client;
    }

    private async Task<HttpClient> CreateSessionClientAsync(string[] capabilities)
    {
        var (_, session) = await CreateTestAdminAndSessionAsync("portal_client", capabilities);
        return CreateSessionClient(session.Id);
    }

    private async Task<(AdminUser user, AdminSession session)> CreateTestAdminAndSessionAsync(
        string prefix = "w4admin",
        string[]? capabilities = null)
    {
        using var db = GetDbContext();
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create(
            $"{prefix}_{Guid.NewGuid():N}",
            "hashed_pass",
            "salt",
            null,
            false,
            now);
        db.AdminUsers.Add(user);

        var sessionId = Guid.NewGuid();
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString()))).ToLowerInvariant();
        var session = AdminSession.Create(
            adminUserId: user.Id,
            tokenHash: tokenHash,
            createdAtUtc: now,
            idleTimeout: TimeSpan.FromMinutes(30),
            absoluteLifetime: TimeSpan.FromHours(8),
            mfaSatisfied: true,
            securityRevision: user.SecurityRevision,
            capabilities: capabilities ?? new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuditRead });

        typeof(AdminSession).GetProperty("Id")!.SetValue(session, sessionId);
        db.AdminSessions.Add(session);
        await db.SaveChangesAsync();

        return (user, session);
    }

    [Fact]
    public async Task CONE_T20_PortalSessionContext_And_Revocation_EndToEnd()
    {
        RateLimitingMiddleware.Reset();
        var (user, session1) = await CreateTestAdminAndSessionAsync(
            "portal_admin",
            new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuditRead, "sessions.view", "sessions.revoke" });

        // Create a second active session for the same user
        Guid session2Id;
        using (var db = GetDbContext())
        {
            session2Id = Guid.NewGuid();
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session2Id.ToString()))).ToLowerInvariant();
            var session2 = AdminSession.Create(
                adminUserId: user.Id,
                tokenHash: tokenHash,
                createdAtUtc: DateTimeOffset.UtcNow,
                idleTimeout: TimeSpan.FromMinutes(30),
                absoluteLifetime: TimeSpan.FromHours(8),
                mfaSatisfied: true,
                securityRevision: user.SecurityRevision,
                capabilities: new[] { AdminCapabilities.AdminManage });

            typeof(AdminSession).GetProperty("Id")!.SetValue(session2, session2Id);
            db.AdminSessions.Add(session2);
            await db.SaveChangesAsync();
        }

        // 1. Get Portal Session Context via session1
        using var sessionClient = CreateSessionClient(session1.Id);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/v1/portal/session");
        sessionClient.DefaultRequestHeaders.Remove("X-Session-Id");
        request.Headers.Add("Cookie", $"central_session={session1.Id:D}");
        var response = await sessionClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await ReadJsonAsync(response);
        Assert.Null(json["sessionId"]);
        Assert.Equal(user.Id.ToString(), json["adminUserId"]?.GetValue<string>());
        Assert.Equal(2, json["activeSessionsCount"]?.GetValue<int>());

        using (var sessionsListClient = CreateSessionClient(session1.Id))
        using (var sessionsPageResponse = await sessionsListClient.GetAsync("/api/admin/v1/sessions?pageSize=1"))
        {
            Assert.Equal(HttpStatusCode.OK, sessionsPageResponse.StatusCode);
            var sessionsPage = await ReadJsonAsync(sessionsPageResponse);
            Assert.Single(sessionsPage["items"]!.AsArray());
            Assert.True(sessionsPage["authorizedCount"]!.GetValue<int>() >= 2);
            Assert.Equal("1", sessionsPage["nextCursor"]?.GetValue<string>());
            Assert.NotNull(sessionsPage["asOfVersion"]);
            Assert.NotNull(sessionsPage["projectionRevision"]);
            Assert.DoesNotContain("tokenHash", sessionsPage.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("capabilities", sessionsPage.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }

        // Cookie/header authenticated mutations require an exact same-origin assertion.
        using (var csrfClient = CreateSessionClient(session1.Id))
        using (var csrfRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/portal/sessions/revoke?targetSessionId={session2Id:D}"))
        {
            using var csrfResponse = await csrfClient.SendAsync(csrfRequest);
            Assert.Equal(HttpStatusCode.Forbidden, csrfResponse.StatusCode);
            Assert.Contains("CSRF_REJECTED", await csrfResponse.Content.ReadAsStringAsync());
        }

        // 2. Revoke other sessions (targetSessionId = null/empty revokes all other sessions)
        var revokeRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/portal/sessions/revoke?targetSessionId={session2Id:D}");
        revokeRequest.Headers.Add("X-Session-Id", session1.Id.ToString());
        revokeRequest.Headers.Add("Origin", "https://localhost");

        var revokeResponse = await sessionClient.SendAsync(revokeRequest);
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        // 3. Verify session2 is now revoked in database
        using (var db = GetDbContext())
        {
            var dbSession2 = await db.AdminSessions.FirstOrDefaultAsync(s => s.Id == session2Id);
            Assert.NotNull(dbSession2);
            Assert.True(dbSession2.IsRevoked);
            var dbCaller = await db.AdminSessions.AsNoTracking().FirstAsync(s => s.Id == session1.Id);
            Assert.False(dbCaller.IsRevoked);
            var audit = await db.CentralAuditRecords.AsNoTracking()
                .SingleAsync(record => record.Action == "ADMIN_SESSION_REVOKED" && record.ResourceId == session2Id.ToString("D"));
            Assert.Equal(session1.Id, audit.SessionId);
            Assert.Equal(user.Id.ToString("D"), audit.ActorId);
            Assert.True(audit.IsSuccess);
        }

        using (var survivingClient = CreateSessionClient(session1.Id))
        using (var survivingResponse = await survivingClient.GetAsync("/api/admin/v1/portal/session"))
        {
            Assert.Equal(HttpStatusCode.OK, survivingResponse.StatusCode);
        }
        using (var revokedClient = CreateSessionClient(session2Id))
        using (var revokedResponse = await revokedClient.GetAsync("/api/admin/v1/portal/session"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
            Assert.Contains("SESSION_INVALID", await revokedResponse.Content.ReadAsStringAsync());
        }

        // `sessions.view` alone cannot revoke; denial is audited and has no target effect.
        var (_, viewOnlySession) = await CreateTestAdminAndSessionAsync("portal_view_only", new[] { "sessions.view" });
        var (_, viewOnlyTarget) = await CreateTestAdminAndSessionAsync("portal_view_only_target", new[] { "sessions.view" });
        using (var ownSessionsClient = CreateSessionClient(viewOnlySession.Id))
        using (var ownSessionsResponse = await ownSessionsClient.GetAsync("/api/admin/v1/sessions"))
        {
            Assert.Equal(HttpStatusCode.OK, ownSessionsResponse.StatusCode);
            var ownSessions = await ReadJsonAsync(ownSessionsResponse);
            Assert.Equal(1, ownSessions["authorizedCount"]?.GetValue<int>());
            Assert.Single(ownSessions["items"]!.AsArray());
            Assert.Equal(viewOnlySession.Id.ToString("D"), ownSessions["items"]![0]!["sessionId"]?.GetValue<string>());
            Assert.DoesNotContain(viewOnlySession.AdminUserId.ToString("D"), ownSessions.ToJsonString());
        }
        using (var viewOnlyClient = CreateSessionClient(viewOnlySession.Id))
        using (var deniedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/portal/sessions/revoke?targetSessionId={viewOnlyTarget.Id:D}"))
        {
            deniedRequest.Headers.Add("Origin", "https://localhost");
            using var deniedResponse = await viewOnlyClient.SendAsync(deniedRequest);
            Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        }
        using (var db = GetDbContext())
        {
            Assert.False((await db.AdminSessions.AsNoTracking().SingleAsync(s => s.Id == viewOnlyTarget.Id)).IsRevoked);
            Assert.True(await db.CentralAuditRecords.AsNoTracking().AnyAsync(a =>
                a.Action == "security.request.denied" && a.Resource == "ApiRoute" &&
                a.ResourceId == "/api/admin/v1/portal/sessions/revoke" && !a.IsSuccess));
        }

        // A caller with sessions.revoke but without cross-admin authority cannot revoke another admin's session.
        var (_, revokeOnlySession) = await CreateTestAdminAndSessionAsync("portal_revoke_only", new[] { "sessions.revoke" });
        var (_, crossAdminTarget) = await CreateTestAdminAndSessionAsync("portal_cross_admin_target", new[] { "sessions.view" });
        using (var viewDeniedClient = CreateSessionClient(revokeOnlySession.Id))
        using (var viewDeniedResponse = await viewDeniedClient.GetAsync("/api/admin/v1/sessions"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, viewDeniedResponse.StatusCode);
        }
        using (var revokeOnlyClient = CreateSessionClient(revokeOnlySession.Id))
        using (var deniedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/portal/sessions/revoke?targetSessionId={crossAdminTarget.Id:D}"))
        {
            deniedRequest.Headers.Add("Origin", "https://localhost");
            using var deniedResponse = await revokeOnlyClient.SendAsync(deniedRequest);
            Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
            Assert.Contains("FORBIDDEN", await deniedResponse.Content.ReadAsStringAsync());
        }
        using (var db = GetDbContext())
        {
            Assert.False((await db.AdminSessions.AsNoTracking().SingleAsync(s => s.Id == crossAdminTarget.Id)).IsRevoked);
            Assert.True(await db.CentralAuditRecords.AsNoTracking().AnyAsync(a =>
                a.Action == "security.request.denied" && a.Resource == "ApiRoute" &&
                a.ResourceId == "/api/admin/v1/portal/sessions/revoke" && !a.IsSuccess));
        }
    }

    [Fact]
    public async Task CONE_T21_Authoritative_Metrics_And_Customer360_EndToEnd()
    {
        RateLimitingMiddleware.Reset();
        var (_, dashboardSession) = await CreateTestAdminAndSessionAsync("dashboard_admin", new[] { AdminCapabilities.AdminManage, "dashboard.view", "queues.view", "payment.review" });
        using var client = CreateSessionClient(dashboardSession.Id);
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var lifetimeLicenseId = Guid.NewGuid();
        var fixedTermLicenseId = Guid.NewGuid();
        var queueMarker = $"Queue page {Guid.NewGuid():N}";
        var queueTicketIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var paymentProofId = Guid.NewGuid();
        var paymentProofOrderId = Guid.Empty;
        var paymentProofBytes = Encoding.UTF8.GetBytes("SYNTHETIC_ENCRYPTED_PAYMENT_PROOF_FIXTURE_V1");
        var paymentProofDigest = Convert.ToHexStringLower(SHA256.HashData(paymentProofBytes));
        var paymentProofStoragePath = $"fixture/{Guid.NewGuid():N}.bin";
        var paymentStorageRoot = Environment.GetEnvironmentVariable("CENTRAL_PAYMENT_STORAGE_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(paymentStorageRoot), "The owned payment-storage fixture root must be configured.");
        var paymentProofFile = Path.GetFullPath(Path.Combine(paymentStorageRoot!, paymentProofStoragePath.Replace('/', Path.DirectorySeparatorChar)));
        var paymentRootFullPath = Path.GetFullPath(paymentStorageRoot!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.StartsWith(paymentRootFullPath + Path.DirectorySeparatorChar, paymentProofFile, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(paymentProofFile)!);
        await File.WriteAllBytesAsync(paymentProofFile, paymentProofBytes);

        using (var db = GetDbContext())
        {
            var shop = Shop.Create(customerId, $"S4_{Guid.NewGuid():N}"[..8], "Wave 4 Authoritative Shop", now);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            db.Shops.Add(shop);

            // 1. Setup Lifetime Base License (validUntilUtc == null)
            var catalogItemLifetime = PlanCatalogItem.Create($"BL_{Guid.NewGuid():N}"[..12], "Base Lifetime", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1500m, "USD");
            db.PlanCatalogItems.Add(catalogItemLifetime);

            var snapshotLifetime = PricingSnapshot.Create(shopId, catalogItemLifetime, 1, now);
            db.PricingSnapshots.Add(snapshotLifetime);

            var orderLifetime = Order.CreateFromSnapshot(snapshotLifetime, customerId, now);
            paymentProofOrderId = orderLifetime.Id;
            typeof(Order).GetProperty("Status")!.SetValue(orderLifetime, Order.StatusPaidApproved);
            db.Orders.Add(orderLifetime);

            var proofSession = UploadSession.Create(UploadSession.PurposePaymentProof, shopId, Guid.NewGuid(), orderLifetime.Id,
                paymentProofBytes.Length, paymentProofDigest, paymentProofStoragePath, now.AddHours(1), now,
                "application/octet-stream", Guid.NewGuid());
            proofSession.AppendChunk(paymentProofBytes.Length);
            proofSession.FinalizeUpload(paymentProofDigest);
            var paymentProof = PaymentEvidence.CreateFromSession(proofSession, orderLifetime.Id, "application/octet-stream", now);
            typeof(PaymentEvidence).GetProperty("Id")!.SetValue(paymentProof, paymentProofId);
            db.UploadSessions.Add(proofSession);
            db.PaymentEvidences.Add(paymentProof);

            var lifetimeLicense = CommercialLicense.Issue(orderLifetime, now, null, now);
            typeof(CommercialLicense).GetProperty("Id")!.SetValue(lifetimeLicense, lifetimeLicenseId);
            db.CommercialLicenses.Add(lifetimeLicense);

            // 2. Setup Fixed-Term Base License expiring in 15 days
            var catalogItemFixed = PlanCatalogItem.Create($"BF_{Guid.NewGuid():N}"[..12], "Base Fixed", PlanCatalogItem.TargetBaseLicense, CommercialTerm.FixedTerm, "P1M", 200m, "USD");
            db.PlanCatalogItems.Add(catalogItemFixed);

            var snapshotFixed = PricingSnapshot.Create(shopId, catalogItemFixed, 1, now);
            db.PricingSnapshots.Add(snapshotFixed);

            var orderFixed = Order.CreateFromSnapshot(snapshotFixed, customerId, now);
            typeof(Order).GetProperty("Status")!.SetValue(orderFixed, Order.StatusPaidApproved);
            db.Orders.Add(orderFixed);

            var fixedLicense = CommercialLicense.Issue(orderFixed, now, now.AddDays(15), now);
            typeof(CommercialLicense).GetProperty("Id")!.SetValue(fixedLicense, fixedTermLicenseId);
            db.CommercialLicenses.Add(fixedLicense);

            // 3. Setup Addon Entitlement expiring in 10 days
            var addon = CommercialEntitlement.Create(lifetimeLicenseId, CommercialEntitlement.KindSupport, now.AddDays(10), orderLifetime.Id);
            db.CommercialEntitlements.Add(addon);

            // 4. Setup Support Ticket
            db.SupportTickets.Add(SupportTicket.Create(queueTicketIds[0], shopId, Guid.NewGuid(), $"{queueMarker} first", "Checking dashboard queues", new List<Guid>(), 1, now));
            db.SupportTickets.Add(SupportTicket.Create(queueTicketIds[1], shopId, Guid.NewGuid(), $"{queueMarker} second", "Checking dashboard queues", new List<Guid>(), 1, now.AddTicks(1)));

            await db.SaveChangesAsync();
        }

        // Query Dashboard Metrics
        var metricsResponse = await client.GetAsync("/api/admin/v1/dashboard/metrics");
        Assert.Equal(HttpStatusCode.OK, metricsResponse.StatusCode);

        var metrics = await ReadJsonAsync(metricsResponse);
        Assert.True(metrics["activeShopsCount"]?.GetValue<int>() >= 1);
        Assert.True(metrics["lifetimeBaseCount"]?.GetValue<int>() >= 1);
        Assert.True(metrics["fixedTermBaseCount"]?.GetValue<int>() >= 1);
        Assert.True(metrics["expiringAddonsCount"]?.GetValue<int>() >= 1);
        Assert.True(metrics["expiringFixedTermBasesCount"]?.GetValue<int>() >= 1);

        // V2 section 134 lists LicenseExpiring as an example. Current design derives expiry
        // metrics from persisted timestamps when queried; it does not publish an expiry event.
        using (var db = GetDbContext())
        {
            Assert.False(await db.CentralOutboxMessages.AnyAsync(x =>
                x.EventType == "commercial.license_expiring" &&
                x.AggregateId == fixedTermLicenseId.ToString()));
        }

        using var contractDashboardResponse = await client.GetAsync("/api/admin/v1/dashboard");
        Assert.Equal(HttpStatusCode.OK, contractDashboardResponse.StatusCode);
        var contractDashboard = await ReadJsonAsync(contractDashboardResponse);
        Assert.True(JsonNode.DeepEquals(metrics, contractDashboard));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/v1/dashboard?unsupported=1")).StatusCode);

        // Query Customer 360 for this shop
        var customer360Response = await client.GetAsync($"/api/admin/v1/dashboard/customer360/{shopId}");
        Assert.Equal(HttpStatusCode.OK, customer360Response.StatusCode);

        var c360 = await ReadJsonAsync(customer360Response);
        Assert.Equal(shopId.ToString(), c360["shopId"]?.GetValue<string>());
        Assert.NotNull(c360["commercialLicense"]);
        Assert.True(c360["addons"]?.AsArray().Count >= 1);
        Assert.True(c360["recentOrders"]?.AsArray().Count >= 1);
        Assert.True(c360["recentTickets"]?.AsArray().Count >= 1);

        // Query the effective040 cursor-paged SUPPORT_INBOX contract.
        var filter = Uri.EscapeDataString(queueMarker);
        using var queueResponse = await client.GetAsync($"/api/admin/v1/queues/SUPPORT_INBOX?pageSize=1&filter={filter}");
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);
        var queue = await ReadJsonAsync(queueResponse);
        Assert.Equal(2, queue["authorizedCount"]!.GetValue<int>());
        Assert.Contains(Guid.Parse(queue["items"]![0]!["entityId"]!.GetValue<string>()), queueTicketIds);
        Assert.Equal("1", queue["nextCursor"]!.GetValue<string>());
        Assert.NotNull(queue["asOfVersion"]);
        Assert.NotNull(queue["projectionRevision"]);
        var firstQueueId = queue["items"]![0]!["entityId"]!.GetValue<string>();
        var queueVersion = queue["asOfVersion"]!.GetValue<long>();
        using var nextQueueResponse = await client.GetAsync($"/api/admin/v1/queues/SUPPORT_INBOX?pageSize=1&filter={filter}&cursor=1&asOfVersion={queueVersion}");
        Assert.Equal(HttpStatusCode.OK, nextQueueResponse.StatusCode);
        var nextQueue = await ReadJsonAsync(nextQueueResponse);
        Assert.NotEqual(firstQueueId, nextQueue["items"]![0]!["entityId"]!.GetValue<string>());
        Assert.Null(nextQueue["nextCursor"]);
        using var staleQueueResponse = await client.GetAsync($"/api/admin/v1/queues/SUPPORT_INBOX?pageSize=1&filter={filter}&cursor=1&asOfVersion={queueVersion + 1}");
        Assert.Equal(HttpStatusCode.Conflict, staleQueueResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/v1/queues/SUPPORT_INBOX?page=1")).StatusCode);
        using var deniedQueueClient = await CreateSessionClientAsync(new[] { "dashboard.view" });
        Assert.Equal(HttpStatusCode.Forbidden, (await deniedQueueClient.GetAsync("/api/admin/v1/queues/SUPPORT_INBOX")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await deniedQueueClient.GetAsync("/api/admin/v1/dashboard")).StatusCode);

        using var queueOnlyClient = await CreateSessionClientAsync(new[] { "queues.view" });
        Assert.Equal(HttpStatusCode.OK, (await queueOnlyClient.GetAsync("/api/admin/v1/queues/SUPPORT_INBOX")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await queueOnlyClient.GetAsync("/api/admin/v1/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await queueOnlyClient.GetAsync($"/api/admin/v1/dashboard/customer360/{shopId}")).StatusCode);

        // Payment reviewers receive the persisted order relationship and proof revision needed
        // to submit the canonical payment.review command; read-only capabilities do not get it.
        using var paymentQueueResponse = await client.GetAsync("/api/admin/v1/queues/PAYMENT_REVIEW?pageSize=20");
        Assert.Equal(HttpStatusCode.OK, paymentQueueResponse.StatusCode);
        var paymentQueue = await ReadJsonAsync(paymentQueueResponse);
        var proofQueueItem = paymentQueue["items"]!.AsArray().Single(x => x!["entityId"]!.GetValue<string>() == paymentProofId.ToString("D"));
        Assert.Equal(paymentProofOrderId.ToString("D"), proofQueueItem!["orderId"]!.GetValue<string>());
        Assert.Equal(1, proofQueueItem["resourceVersion"]!.GetValue<int>());
        using var paymentQueueViewer = await CreateSessionClientAsync(new[] { "queues.view" });
        using var viewerPaymentQueueResponse = await paymentQueueViewer.GetAsync("/api/admin/v1/queues/PAYMENT_REVIEW?pageSize=20");
        Assert.Equal(HttpStatusCode.OK, viewerPaymentQueueResponse.StatusCode);
        var viewerPaymentQueue = await ReadJsonAsync(viewerPaymentQueueResponse);
        var viewerProof = viewerPaymentQueue["items"]!.AsArray().Single(x => x!["entityId"]!.GetValue<string>() == paymentProofId.ToString("D"));
        Assert.Null(viewerProof!["orderId"]);
        Assert.Null(viewerProof["resourceVersion"]);
    }

    [Fact]
    public async Task CONE_T22_ActionPreview_LifecycleConsequences_EndToEnd()
    {
        RateLimitingMiddleware.Reset();
        var (_, previewSession) = await CreateTestAdminAndSessionAsync("preview_admin", new[] { AdminCapabilities.AdminManage, "sensitive.approve" });
        using var client = CreateSessionClient(previewSession.Id);
        var shopId = Guid.NewGuid();
        Guid licenseId;
        Guid entitlementId;

        using (var db = GetDbContext())
        {
            var shop = Shop.Create(Guid.NewGuid(), $"PR_{Guid.NewGuid():N}"[..8], "Preview Shop", DateTimeOffset.UtcNow);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            db.Shops.Add(shop);

            var now = DateTimeOffset.UtcNow;
            var item = PlanCatalogItem.Create("PREVIEW_" + Guid.NewGuid().ToString("N"), "Synthetic preview", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1m, "USD");
            var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
            var order = Order.CreateFromSnapshot(snapshot, shop.CustomerId, now);
            typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
            var license = CommercialLicense.Issue(order, now, null, now);
            var entitlement = CommercialEntitlement.Create(license.Id, CommercialEntitlement.KindSupport, now.AddDays(5), order.Id);
            db.PlanCatalogItems.Add(item); db.PricingSnapshots.Add(snapshot); db.Orders.Add(order);
            db.CommercialLicenses.Add(license); db.CommercialEntitlements.Add(entitlement);
            licenseId = license.Id;
            entitlementId = entitlement.Id;
            var install = Installation.Register(shopId, licenseId, "FP_PREV_1", "-----BEGIN PUBLIC KEY-----\nMIIB\n-----END PUBLIC KEY-----", "pos-1", "windows", now);
            db.Installations.Add(install);
            await db.SaveChangesAsync();
        }

        // Preview REVOKE_LICENSE
        var revokeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/v1/action-preview");
        revokeRequest.Headers.Add("Origin", "https://localhost");
        revokeRequest.Content = JsonContent.Create(new ActionPreviewRequestDto
        {
            ActionType = "REVOKE_LICENSE",
            ShopId = shopId,
            TargetEntityId = licenseId
        });
        var revokePreviewResponse = await client.SendAsync(revokeRequest);
        Assert.Equal(HttpStatusCode.OK, revokePreviewResponse.StatusCode);
        var revokeJson = await ReadJsonAsync(revokePreviewResponse);
        Assert.True(revokeJson["isDestructive"]?.GetValue<bool>());
        Assert.True(revokeJson["requiresStepUp"]?.GetValue<bool>());
        Assert.True(revokeJson["requiresDualApproval"]?.GetValue<bool>());
        Assert.True(revokeJson["affectedInstallationsCount"]?.GetValue<int>() >= 1);
        Assert.Contains("Revoking license", revokeJson["consequenceSummary"]?.GetValue<string>() ?? "");

        // Preview RENEW_ADDON
        var renewRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/v1/action-preview");
        renewRequest.Headers.Add("Origin", "https://localhost");
        renewRequest.Content = JsonContent.Create(new ActionPreviewRequestDto
        {
            ActionType = "RENEW_ADDON",
            ShopId = shopId,
            TargetEntityId = entitlementId
        });
        var renewPreviewResponse = await client.SendAsync(renewRequest);
        Assert.Equal(HttpStatusCode.OK, renewPreviewResponse.StatusCode);
        var renewJson = await ReadJsonAsync(renewPreviewResponse);
        Assert.False(renewJson["isDestructive"]?.GetValue<bool>());
        Assert.False(renewJson["requiresDualApproval"]?.GetValue<bool>());
        Assert.Contains("LIFETIME base license remains completely unaffected", renewJson["consequenceSummary"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public async Task CONE_T23_TwoPerson_DualApproval_And_PiiUnmasking_EndToEnd()
    {
        RateLimitingMiddleware.Reset();
        var approvalCapabilities = new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuditRead, "sensitive.approve", "customer.view_sensitive" };
        var (proposerUser, proposerSession) = await CreateTestAdminAndSessionAsync("proposer", approvalCapabilities);
        var (deciderUser, deciderSession) = await CreateTestAdminAndSessionAsync("decider", approvalCapabilities);

        var approvalId = Guid.NewGuid();
        var targetEntityId = Guid.NewGuid();
        var payloadSha = new string('c', 64);
        var now = DateTimeOffset.UtcNow;

        using (var db = GetDbContext())
        {
            var approvalReq = SensitiveApprovalRequest.Request(
                operationType: "COMMERCIAL_POLICY_CHANGE",
                targetEntityId: targetEntityId,
                targetPayloadSha256: payloadSha,
                requesterAdminId: proposerUser.Id,
                actionScope: "policy.change",
                stepUpSessionId: Guid.NewGuid(),
                requestedAtUtc: now,
                expiresAtUtc: now.AddHours(2));

            typeof(SensitiveApprovalRequest).GetProperty("Id")!.SetValue(approvalReq, approvalId);
            db.SensitiveApprovalRequests.Add(approvalReq);

            var customer = Customer.Create("Synthetic PII fixture", "permitted-contact@example.invalid", "synthetic-contact", now);
            typeof(Customer).GetProperty("Id")!.SetValue(customer, targetEntityId);
            db.Customers.Add(customer);

            // Also seed an audit record for PII unmask test
            var audit = CentralAuditRecord.Create(
                actorId: proposerUser.Id.ToString(),
                actorUsername: proposerUser.Username,
                sessionId: proposerSession.Id,
                action: "CUSTOMER_RECORD_MUTATED",
                resource: "Customer",
                resourceId: targetEntityId.ToString(),
                reason: "Initial customer enrollment",
                correlationId: Guid.NewGuid().ToString(),
                clientIp: "127.0.0.1",
                userAgent: "integration-test",
                payloadSha256: null,
                detailsJson: "{\"maskedPhone\":\"+1***1234\"}",
                isSuccess: true,
                timestampUtc: now);

            db.CentralAuditRecords.Add(audit);
            await db.SaveChangesAsync();
        }

        // 1. Proposer attempts SELF-APPROVAL -> Must be blocked fail-closed!
        var selfApprovalRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/sensitive-approvals/{approvalId}/decision");
        selfApprovalRequest.Headers.Add("X-Session-Id", proposerSession.Id.ToString());
        selfApprovalRequest.Headers.Add("Origin", "https://localhost");
        selfApprovalRequest.Content = JsonContent.Create(new SensitiveApprovalDecisionDto
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = payloadSha
        });

        var selfApprovalResponse = await _client.SendAsync(selfApprovalRequest);
        Assert.Equal(HttpStatusCode.BadRequest, selfApprovalResponse.StatusCode);

        // 2. Decider (distinct admin) approves request -> Succeeded!
        var validApprovalRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/sensitive-approvals/{approvalId}/decision");
        validApprovalRequest.Headers.Add("X-Session-Id", deciderSession.Id.ToString());
        validApprovalRequest.Headers.Add("Origin", "https://localhost");
        validApprovalRequest.Content = JsonContent.Create(new SensitiveApprovalDecisionDto
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = payloadSha
        });

        var validApprovalResponse = await _client.SendAsync(validApprovalRequest);
        Assert.Equal(HttpStatusCode.OK, validApprovalResponse.StatusCode);

        // Verify status in DB
        using (var db = GetDbContext())
        {
            var approval = await db.SensitiveApprovalRequests.FirstOrDefaultAsync(a => a.Id == approvalId);
            Assert.NotNull(approval);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, approval.Status);
            Assert.Equal(deciderUser.Id, approval.ApproverAdminId);
        }

        // 3. Unmask PII with reason -> Audit logged
        Guid auditRecordId;
        using (var db = GetDbContext())
        {
            var audit = await db.CentralAuditRecords.SingleAsync(a => a.Action == "CUSTOMER_RECORD_MUTATED" && a.ResourceId == targetEntityId.ToString());
            auditRecordId = audit.Id;
        }

        var unmaskRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/v1/sensitive-approvals/unmask-pii");
        unmaskRequest.Headers.Add("X-Session-Id", deciderSession.Id.ToString());
        unmaskRequest.Headers.Add("Origin", "https://localhost");
        unmaskRequest.Content = JsonContent.Create(new UnmaskPiiRequestDto
        {
            AuditRecordId = auditRecordId,
            Reason = "Court-ordered compliance investigation audit reference #994"
        });

        var unmaskResponse = await _client.SendAsync(unmaskRequest);
        Assert.Equal(HttpStatusCode.OK, unmaskResponse.StatusCode);

        var unmaskJson = await ReadJsonAsync(unmaskResponse);
        Assert.Contains("permitted-contact@example.invalid", unmaskJson["unmaskedData"]?.GetValue<string>() ?? "");
        Assert.DoesNotContain("UNMASKED-VERIFIED", unmaskJson["unmaskedData"]?.GetValue<string>() ?? "");

        // 4. Verify append-only audit record created
        using (var db = GetDbContext())
        {
            var unmaskAudit = await db.CentralAuditRecords.OrderByDescending(a => a.TimestampUtc).FirstOrDefaultAsync(a => a.Action == "AUDIT_PII_UNMASKED" && a.ActorId == deciderUser.Id.ToString());
            Assert.NotNull(unmaskAudit);
            Assert.Equal("Court-ordered compliance investigation audit reference #994", unmaskAudit.Reason);
            Assert.Equal(deciderUser.Id.ToString(), unmaskAudit.ActorId);
        }
    }

    [Fact]
    public async Task CanonicalSensitiveApproval_Decide_BindsPersistedRequestAndReturnsReplayableOutcome()
    {
        RateLimitingMiddleware.Reset();
        var approvalCapabilities = new[] { "sensitive.approve" };
        var (requester, _) = await CreateTestAdminAndSessionAsync("canonical_approval_requester", approvalCapabilities);
        var (approver, approverSession) = await CreateTestAdminAndSessionAsync("canonical_approval_approver", approvalCapabilities);
        var (_, noCapabilitySession) = await CreateTestAdminAndSessionAsync("canonical_approval_denied", Array.Empty<string>());
        var requestId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targetDigest = new string('a', 64);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        using (var db = GetDbContext())
        {
            var approval = SensitiveApprovalRequest.Request(
                "COMMERCIAL_POLICY_CHANGE", targetId, targetDigest, requester.Id, "policy.change",
                Guid.NewGuid(), DateTimeOffset.UtcNow, expiresAt);
            typeof(SensitiveApprovalRequest).GetProperty("Id")!.SetValue(approval, requestId);
            db.SensitiveApprovalRequests.Add(approval);
            await db.SaveChangesAsync();
        }

        var serializer = new CanonicalJsonSerializer();
        var payload = new CanonicalSensitiveApprovalDecisionPayload
        {
            TargetActionType = "COMMERCIAL_POLICY_CHANGE",
            TargetId = targetId,
            TargetPayloadSha256 = targetDigest,
            RequesterAdminId = requester.Id,
            ApproverAdminId = approver.Id,
            ExpiresAtUtc = expiresAt,
            Decision = "APPROVE"
        };
        var operationId = Guid.NewGuid();
        HttpRequestMessage CreateDecisionRequest(Guid sessionId, CanonicalSensitiveApprovalDecisionPayload submitted, Guid clientOperationId) =>
            new(HttpMethod.Post, $"/api/admin/v1/sensitive-approvals/{requestId:D}/decide")
            {
                Content = JsonContent.Create(new
                {
                    meta = new
                    {
                        clientOperationId,
                        operationType = "sensitive.approve",
                        fingerprintVersion = 1,
                        payloadSha256 = serializer.SerializeAndHash(submitted).Sha256Hex,
                        expectedVersion = 1,
                        policyVersion = 0,
                        reason = "synthetic independent approval fixture",
                        sensitiveApprovalId = requestId
                    },
                    payload = submitted
                })
            };

        using (var denied = CreateDecisionRequest(noCapabilitySession.Id, payload, Guid.NewGuid()))
        {
            denied.Headers.Add("X-Session-Id", noCapabilitySession.Id.ToString());
            denied.Headers.Add("Origin", "https://localhost");
            var deniedResponse = await _client.SendAsync(denied);
            Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        }

        using (var db = GetDbContext())
        {
            Assert.Equal(SensitiveApprovalRequest.StatusPending,
                (await db.SensitiveApprovalRequests.SingleAsync(item => item.Id == requestId)).Status);
        }

        var forgedPayload = new CanonicalSensitiveApprovalDecisionPayload
        {
            TargetActionType = payload.TargetActionType,
            TargetId = payload.TargetId,
            TargetPayloadSha256 = payload.TargetPayloadSha256,
            RequesterAdminId = Guid.NewGuid(),
            ApproverAdminId = approver.Id,
            ExpiresAtUtc = expiresAt,
            Decision = "APPROVE"
        };
        using (var forged = CreateDecisionRequest(approverSession.Id, forgedPayload, Guid.NewGuid()))
        {
            forged.Headers.Add("X-Session-Id", approverSession.Id.ToString());
            forged.Headers.Add("Origin", "https://localhost");
            var forgedResponse = await _client.SendAsync(forged);
            Assert.Equal(HttpStatusCode.BadRequest, forgedResponse.StatusCode);
        }

        using var accepted = CreateDecisionRequest(approverSession.Id, payload, operationId);
        accepted.Headers.Add("X-Session-Id", approverSession.Id.ToString());
        accepted.Headers.Add("Origin", "https://localhost");
        var acceptedResponse = await _client.SendAsync(accepted);
        Assert.True(acceptedResponse.StatusCode == HttpStatusCode.OK,
            $"Expected canonical decision success; received {(int)acceptedResponse.StatusCode}: {await acceptedResponse.Content.ReadAsStringAsync()}");
        var outcome = await ReadJsonAsync(acceptedResponse);
        Assert.Equal("sensitive.approve", outcome["operationType"]?.GetValue<string>());
        Assert.Equal("SUCCEEDED", outcome["state"]?.GetValue<string>());
        Assert.Equal(operationId, outcome["clientOperationId"]?.GetValue<Guid>());

        using var replay = CreateDecisionRequest(approverSession.Id, payload, operationId);
        replay.Headers.Add("X-Session-Id", approverSession.Id.ToString());
        replay.Headers.Add("Origin", "https://localhost");
        var replayResponse = await _client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayOutcome = await ReadJsonAsync(replayResponse);
        Assert.Equal(outcome["outcomeId"]?.GetValue<Guid>(), replayOutcome["outcomeId"]?.GetValue<Guid>());

        var stalePayload = new CanonicalSensitiveApprovalDecisionPayload
        {
            TargetActionType = payload.TargetActionType,
            TargetId = payload.TargetId,
            TargetPayloadSha256 = payload.TargetPayloadSha256,
            RequesterAdminId = payload.RequesterAdminId,
            ApproverAdminId = payload.ApproverAdminId,
            ExpiresAtUtc = payload.ExpiresAtUtc,
            Decision = "REJECT"
        };
        using var stale = CreateDecisionRequest(approverSession.Id, stalePayload, Guid.NewGuid());
        stale.Headers.Add("X-Session-Id", approverSession.Id.ToString());
        stale.Headers.Add("Origin", "https://localhost");
        var staleResponse = await _client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);

        using (var db = GetDbContext())
        {
            var persisted = await db.SensitiveApprovalRequests.SingleAsync(item => item.Id == requestId);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, persisted.Status);
            Assert.Equal(approver.Id, persisted.ApproverAdminId);
            Assert.Equal(2, persisted.RowVersion);
        }
    }

    [Fact]
    public async Task SensitiveApprovalQueue_ExposesDecisionEnvelopeOnlyToApprovers_AndSupportsPersistedDecision()
    {
        RateLimitingMiddleware.Reset();
        var requesterCapabilities = new[] { "queues.view", "sensitive.approve" };
        var approverCapabilities = new[] { "queues.view", "sensitive.approve" };
        var viewerCapabilities = new[] { "queues.view" };
        var (requester, _) = await CreateTestAdminAndSessionAsync("approval_queue_requester", requesterCapabilities);
        var (approver, approverSession) = await CreateTestAdminAndSessionAsync("approval_queue_approver", approverCapabilities);
        var (_, viewerSession) = await CreateTestAdminAndSessionAsync("approval_queue_viewer", viewerCapabilities);
        var requestId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targetDigest = new string('b', 64);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(20);

        using (var db = GetDbContext())
        {
            var request = SensitiveApprovalRequest.Request(
                "COMMERCIAL_POLICY_CHANGE", targetId, targetDigest, requester.Id, "policy.change",
                Guid.NewGuid(), DateTimeOffset.UtcNow, expiresAt);
            typeof(SensitiveApprovalRequest).GetProperty("Id")!.SetValue(request, requestId);
            db.SensitiveApprovalRequests.Add(request);
            await db.SaveChangesAsync();
        }

        using var viewer = CreateSessionClient(viewerSession.Id);
        var viewerResponse = await viewer.GetAsync("/api/admin/v1/queues/DUAL_APPROVAL?pageSize=20");
        Assert.Equal(HttpStatusCode.OK, viewerResponse.StatusCode);
        var viewerPage = await ReadJsonAsync(viewerResponse);
        var redacted = viewerPage["items"]!.AsArray().Single(item => item!["entityId"]!.GetValue<Guid>() == requestId)!;
        Assert.Null(redacted["approvalRequesterAdminId"]);
        Assert.Null(redacted["approvalTargetActionType"]);
        Assert.Null(redacted["approvalTargetId"]);
        Assert.Null(redacted["approvalTargetPayloadSha256"]);
        Assert.Null(redacted["approvalExpiresAtUtc"]);

        using var approverClient = CreateSessionClient(approverSession.Id);
        var approverResponse = await approverClient.GetAsync("/api/admin/v1/queues/DUAL_APPROVAL?pageSize=20");
        Assert.Equal(HttpStatusCode.OK, approverResponse.StatusCode);
        var approverPage = await ReadJsonAsync(approverResponse);
        var item = approverPage["items"]!.AsArray().Single(value => value!["entityId"]!.GetValue<Guid>() == requestId)!;
        Assert.Equal(requester.Id, item["approvalRequesterAdminId"]!.GetValue<Guid>());
        Assert.Equal("COMMERCIAL_POLICY_CHANGE", item["approvalTargetActionType"]!.GetValue<string>());
        Assert.Equal(targetId, item["approvalTargetId"]!.GetValue<Guid>());
        Assert.Equal(targetDigest, item["approvalTargetPayloadSha256"]!.GetValue<string>());
        Assert.True(Math.Abs((expiresAt - item["approvalExpiresAtUtc"]!.GetValue<DateTimeOffset>()).Ticks) <= TimeSpan.TicksPerMicrosecond);
        Assert.Equal(1, item["resourceVersion"]!.GetValue<int>());

        var payload = new CanonicalSensitiveApprovalDecisionPayload
        {
            TargetActionType = item["approvalTargetActionType"]!.GetValue<string>(),
            TargetId = item["approvalTargetId"]!.GetValue<Guid>(),
            TargetPayloadSha256 = item["approvalTargetPayloadSha256"]!.GetValue<string>(),
            RequesterAdminId = item["approvalRequesterAdminId"]!.GetValue<Guid>(),
            ApproverAdminId = approver.Id,
            ExpiresAtUtc = item["approvalExpiresAtUtc"]!.GetValue<DateTimeOffset>(),
            Decision = "APPROVE"
        };
        var serializer = new CanonicalJsonSerializer();
        var operationId = Guid.NewGuid();
        using var decision = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/sensitive-approvals/{requestId:D}/decide")
        {
            Content = JsonContent.Create(new
            {
                meta = new
                {
                    clientOperationId = operationId,
                    operationType = "sensitive.approve",
                    fingerprintVersion = 1,
                    payloadSha256 = serializer.SerializeAndHash(payload).Sha256Hex,
                    expectedVersion = item["resourceVersion"]!.GetValue<int>(),
                    policyVersion = 0,
                    reason = "Synthetic independent approval queue test",
                    sensitiveApprovalId = requestId
                },
                payload
            })
        };
        decision.Headers.Add("Origin", "https://localhost");
        var decisionResponse = await approverClient.SendAsync(decision);
        Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
        var outcome = await ReadJsonAsync(decisionResponse);
        Assert.Equal("SUCCEEDED", outcome["state"]?.GetValue<string>());
        Assert.Equal(operationId, outcome["clientOperationId"]?.GetValue<Guid>());

        using (var db = GetDbContext())
        {
            var persisted = await db.SensitiveApprovalRequests.SingleAsync(value => value.Id == requestId);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, persisted.Status);
            Assert.Equal(approver.Id, persisted.ApproverAdminId);
            Assert.Equal(2, persisted.RowVersion);
            Assert.True(await db.CentralOperationRecords.AnyAsync(value => value.ClientOperationId == operationId));
            Assert.True(await db.CentralOutboxMessages.AnyAsync(value => value.Id == operationId && value.EventType == "approval.decided"));
            Assert.True(await db.CentralAuditRecords.AnyAsync(value => value.ResourceId == requestId.ToString("D") && value.Action == "SENSITIVE_APPROVAL_DECIDED"));
        }
    }

    [Fact]
    public async Task CanonicalSensitiveApproval_Decide_RejectsWithPersistedOutcome()
    {
        RateLimitingMiddleware.Reset();
        var capabilities = new[] { "sensitive.approve" };
        var (requester, _) = await CreateTestAdminAndSessionAsync("canonical_reject_requester", capabilities);
        var (approver, approverSession) = await CreateTestAdminAndSessionAsync("canonical_reject_approver", capabilities);
        var requestId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targetDigest = new string('b', 64);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        using (var db = GetDbContext())
        {
            var approval = SensitiveApprovalRequest.Request(
                "COMMERCIAL_POLICY_CHANGE", targetId, targetDigest, requester.Id, "policy.change",
                Guid.NewGuid(), DateTimeOffset.UtcNow, expiresAt);
            typeof(SensitiveApprovalRequest).GetProperty("Id")!.SetValue(approval, requestId);
            db.SensitiveApprovalRequests.Add(approval);
            await db.SaveChangesAsync();
        }

        var serializer = new CanonicalJsonSerializer();
        var payload = new CanonicalSensitiveApprovalDecisionPayload
        {
            TargetActionType = "COMMERCIAL_POLICY_CHANGE",
            TargetId = targetId,
            TargetPayloadSha256 = targetDigest,
            RequesterAdminId = requester.Id,
            ApproverAdminId = approver.Id,
            ExpiresAtUtc = expiresAt,
            Decision = "REJECT"
        };
        var clientOperationId = Guid.NewGuid();
        using var decision = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/sensitive-approvals/{requestId:D}/decide")
        {
            Content = JsonContent.Create(new
            {
                meta = new
                {
                    clientOperationId,
                    operationType = "sensitive.approve",
                    fingerprintVersion = 1,
                    payloadSha256 = serializer.SerializeAndHash(payload).Sha256Hex,
                    expectedVersion = 1,
                    policyVersion = 0,
                    reason = "synthetic rejection fixture"
                },
                payload
            })
        };
        decision.Headers.Add("X-Session-Id", approverSession.Id.ToString());
        decision.Headers.Add("Origin", "https://localhost");
        var response = await _client.SendAsync(decision);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = await ReadJsonAsync(response);
        Assert.Equal("sensitive.approve", outcome["operationType"]?.GetValue<string>());
        Assert.Equal("SUCCEEDED", outcome["state"]?.GetValue<string>());

        using (var db = GetDbContext())
        {
            var persisted = await db.SensitiveApprovalRequests.SingleAsync(item => item.Id == requestId);
            Assert.Equal(SensitiveApprovalRequest.StatusRejected, persisted.Status);
            Assert.Equal(approver.Id, persisted.ApproverAdminId);
            Assert.Equal(2, persisted.RowVersion);
        }
    }
}
