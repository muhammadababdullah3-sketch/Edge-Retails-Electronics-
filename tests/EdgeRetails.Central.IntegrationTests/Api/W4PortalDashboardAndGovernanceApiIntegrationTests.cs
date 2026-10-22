using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Support;
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
        var (_, dashboardSession) = await CreateTestAdminAndSessionAsync("dashboard_admin", new[] { AdminCapabilities.AdminManage, "dashboard.view", "queues.view" });
        using var client = CreateSessionClient(dashboardSession.Id);
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var lifetimeLicenseId = Guid.NewGuid();
        var fixedTermLicenseId = Guid.NewGuid();
        var queueMarker = $"Queue page {Guid.NewGuid():N}";
        var queueTicketIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

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
            typeof(Order).GetProperty("Status")!.SetValue(orderLifetime, Order.StatusPaidApproved);
            db.Orders.Add(orderLifetime);

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
}
