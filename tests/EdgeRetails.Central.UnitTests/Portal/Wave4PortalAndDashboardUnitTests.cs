using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Portal;

public class Wave4PortalAndDashboardUnitTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly FakeAdminAuthService _authService;
    private readonly Wave4DashboardAndPortalService _service;

    public Wave4PortalAndDashboardUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(databaseName: $"CentralWave4UnitTestDb_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbContext = new CentralDbContext(options);
        _authService = new FakeAdminAuthService();
        _service = new Wave4DashboardAndPortalService(_dbContext, _authService);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private class FakeAdminAuthService : IAdminAuthService
    {
        private readonly Dictionary<Guid, AdminSessionDto> _sessions = new();

        public void RegisterSession(AdminSessionDto session)
        {
            _sessions[session.SessionId] = session;
        }

        public Task<PreAuthChallengeDto> StartLoginAsync(LoginRequestDto request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AdminSessionDto> CompleteMfaAsync(MfaRequestDto request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AdminSessionDto?> ValidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            _sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session);
        }

        public Task<bool> LogoutAsync(Guid sessionId, string reason, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_sessions.Remove(sessionId));
        }
    }

    private async Task<(AdminUser User, AdminSession Session, Guid SessionId)> CreateAdminAndSessionAsync(
        string username = "testadmin",
        bool mfaSatisfied = true,
        string[]? capabilities = null)
    {
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create(username, "hash", "salt", null, false, now);
        _dbContext.AdminUsers.Add(user);

        var sessionId = Guid.NewGuid();
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString()))).ToLowerInvariant();
        var session = AdminSession.Create(
            adminUserId: user.Id,
            tokenHash: tokenHash,
            createdAtUtc: now,
            idleTimeout: TimeSpan.FromMinutes(30),
            absoluteLifetime: TimeSpan.FromHours(8),
            mfaSatisfied: mfaSatisfied,
            securityRevision: user.SecurityRevision,
            capabilities: capabilities ?? new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuditRead });

        typeof(AdminSession).GetProperty("Id")!.SetValue(session, sessionId);
        _dbContext.AdminSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        _authService.RegisterSession(new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = user.Id,
            ExpiresAtUtc = now.AddMinutes(30),
            AbsoluteExpiresAtUtc = now.AddHours(8),
            AdminSecurityRevision = user.SecurityRevision,
            Capabilities = (capabilities ?? new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuditRead }).ToList()
        });

        return (user, session, sessionId);
    }

    // ==========================================
    // CONE-T20: Portal Session Context & Administration
    // ==========================================

    [Fact]
    public async Task CONE_T20_PortalSessionContext_ValidSession_ReturnsFullContext()
    {
        var (user, _, sessionId) = await CreateAdminAndSessionAsync("portal_t20_user");

        var context = await _service.GetPortalSessionContextAsync(sessionId);

        Assert.NotNull(context);
        var serializedContext = System.Text.Json.JsonSerializer.Serialize(context);
        Assert.DoesNotContain("SessionId", serializedContext, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(user.Id, context.AdminUserId);
        Assert.Equal("portal_t20_user", context.Username);
        Assert.Contains(AdminCapabilities.AdminManage, context.Capabilities);
        Assert.Equal(1, context.ActiveSessionsCount);
    }

    [Fact]
    public async Task CONE_T20_PortalSessionContext_InvalidOrExpiredSession_ThrowsCentralDomainException()
    {
        var invalidSessionId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.GetPortalSessionContextAsync(invalidSessionId));

        Assert.Equal("SESSION_INVALID", ex.Code);
    }

    [Fact]
    public async Task CONE_T20_RevokeOtherSessions_RevokesAllExceptCurrent()
    {
        var (user, _, currentSessionId) = await CreateAdminAndSessionAsync("multi_session_user");

        // Add 2 more active sessions for the same user
        var session2Id = Guid.NewGuid();
        var session2 = AdminSession.Create(user.Id, "hash2", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), true, user.SecurityRevision, new[] { AdminCapabilities.AdminManage });
        typeof(AdminSession).GetProperty("Id")!.SetValue(session2, session2Id);
        _dbContext.AdminSessions.Add(session2);

        var session3Id = Guid.NewGuid();
        var session3 = AdminSession.Create(user.Id, "hash3", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), true, user.SecurityRevision, new[] { AdminCapabilities.AdminManage });
        typeof(AdminSession).GetProperty("Id")!.SetValue(session3, session3Id);
        _dbContext.AdminSessions.Add(session3);
        await _dbContext.SaveChangesAsync();

        var result = await _service.RevokeSessionAsync(currentSessionId, Guid.Empty);
        Assert.True(result);

        var dbSession2 = await _dbContext.AdminSessions.FindAsync(session2Id);
        var dbSession3 = await _dbContext.AdminSessions.FindAsync(session3Id);
        var currentDbSession = await _dbContext.AdminSessions.FindAsync(currentSessionId);

        Assert.True(dbSession2!.IsRevoked);
        Assert.True(dbSession3!.IsRevoked);
        Assert.False(currentDbSession!.IsRevoked);
    }

    [Fact]
    public async Task CONE_T20_RevokeTargetSession_WithoutAdminManage_ThrowsForbidden()
    {
        var (_, _, callerSessionId) = await CreateAdminAndSessionAsync("caller_without_manage", true, new[] { AdminCapabilities.AuditRead });
        var (_, _, targetSessionId) = await CreateAdminAndSessionAsync("target_other_user", true, new[] { AdminCapabilities.AuditRead });

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.RevokeSessionAsync(callerSessionId, targetSessionId));

        Assert.Equal("FORBIDDEN", ex.Code);
    }

    // ==========================================
    // CONE-T21: Authoritative Metrics & Customer 360
    // ==========================================

    [Fact]
    public async Task CONE_T21_DashboardMetrics_SeparatesLifetimeBase_FromFixedTerm_AndAddons()
    {
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();

        var shop = Shop.Create(customerId, "S4_METRIC", "Metrics Shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        _dbContext.Shops.Add(shop);

        // 1. Lifetime License (ValidUntilUtc == null)
        var itemLifetime = PlanCatalogItem.Create("PL_LIFE_1", "Lifetime Item", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1000m, "USD");
        _dbContext.PlanCatalogItems.Add(itemLifetime);
        var snapLifetime = PricingSnapshot.Create(shopId, itemLifetime, 1, now);
        _dbContext.PricingSnapshots.Add(snapLifetime);
        var orderLifetime = Order.CreateFromSnapshot(snapLifetime, customerId, now);
        typeof(Order).GetProperty("Status")!.SetValue(orderLifetime, Order.StatusPaidApproved);
        _dbContext.Orders.Add(orderLifetime);
        var licenseLifetime = CommercialLicense.Issue(orderLifetime, now, null, now);
        _dbContext.CommercialLicenses.Add(licenseLifetime);

        // 2. Fixed-Term License expiring in 15 days
        var itemFixed = PlanCatalogItem.Create("PL_FIX_1", "Fixed Item", PlanCatalogItem.TargetBaseLicense, CommercialTerm.FixedTerm, "P1M", 100m, "USD");
        _dbContext.PlanCatalogItems.Add(itemFixed);
        var snapFixed = PricingSnapshot.Create(shopId, itemFixed, 1, now);
        _dbContext.PricingSnapshots.Add(snapFixed);
        var orderFixed = Order.CreateFromSnapshot(snapFixed, customerId, now);
        typeof(Order).GetProperty("Status")!.SetValue(orderFixed, Order.StatusPaidApproved);
        _dbContext.Orders.Add(orderFixed);
        var licenseFixed = CommercialLicense.Issue(orderFixed, now, now.AddDays(15), now);
        _dbContext.CommercialLicenses.Add(licenseFixed);

        // 3. Addon expiring in 5 days
        var addon = CommercialEntitlement.Create(licenseLifetime.Id, CommercialEntitlement.KindSupport, now.AddDays(5), orderLifetime.Id);
        _dbContext.CommercialEntitlements.Add(addon);

        await _dbContext.SaveChangesAsync();

        var metrics = await _service.GetDashboardMetricsAsync();

        Assert.Equal(1, metrics.ActiveShopsCount);
        Assert.Equal(1, metrics.LifetimeBaseCount);
        Assert.Equal(1, metrics.FixedTermBaseCount);
        Assert.Equal(1, metrics.ExpiringFixedTermBasesCount);
        Assert.Equal(1, metrics.ExpiringAddonsCount);
    }

    [Fact]
    public async Task CONE_T21_Customer360_AggregatesLicense_Addons_AndInstallations()
    {
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();

        var shop = Shop.Create(customerId, "S4_C360", "Customer 360 Shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        _dbContext.Shops.Add(shop);

        var install = Installation.Register(shopId, Guid.NewGuid(), "FP_C360_1", "PUBKEY", "pos-terminal", "windows", now);
        _dbContext.Installations.Add(install);

        var item = PlanCatalogItem.Create("PL_C360_1", "Item", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1000m, "USD");
        _dbContext.PlanCatalogItems.Add(item);
        var snap = PricingSnapshot.Create(shopId, item, 1, now);
        _dbContext.PricingSnapshots.Add(snap);
        var order = Order.CreateFromSnapshot(snap, customerId, now);
        typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
        _dbContext.Orders.Add(order);
        var license = CommercialLicense.Issue(order, now, null, now);
        _dbContext.CommercialLicenses.Add(license);

        var addon = CommercialEntitlement.Create(license.Id, CommercialEntitlement.KindSupport, now.AddDays(10), order.Id);
        _dbContext.CommercialEntitlements.Add(addon);

        await _dbContext.SaveChangesAsync();

        var c360 = await _service.GetCustomer360Async(shopId);

        Assert.Equal(shopId, c360.ShopId);
        Assert.Equal("S4_C360", c360.ShopCode);
        Assert.NotNull(c360.CommercialLicense);
        Assert.Equal(CommercialTerm.Lifetime, c360.CommercialLicense!.TermKind);
        Assert.Null(c360.CommercialLicense.ValidUntilUtc);
        Assert.Single(c360.Addons);
        Assert.True(c360.Addons[0].IsExpiringSoon);
        Assert.Single(c360.Installations);
    }

    [Fact]
    public async Task CONE_T21_DashboardQueues_PagingAndFiltering_MatchesPersistedDecisions()
    {
        var now = DateTimeOffset.UtcNow;
        var customer = Customer.Create("Queue Customer", "queue@example.test", "", now);
        var shop = Shop.Create(customer.Id, "QUEUE_SHOP", "Queue Shop", now);
        _dbContext.Customers.Add(customer);
        _dbContext.Shops.Add(shop);

        for (int i = 0; i < 5; i++)
        {
            var ticket = SupportTicket.Create(Guid.NewGuid(), shop.Id, Guid.NewGuid(), $"Subject {i}", $"Summary {i}", new List<Guid>(), 1, now.AddMinutes(i));
            _dbContext.SupportTickets.Add(ticket);
        }
        await _dbContext.SaveChangesAsync();

        var page1 = await _service.GetDashboardQueueAsync("SUPPORT_INBOX", pageNumber: 1, pageSize: 2);
        var page2 = await _service.GetDashboardQueueAsync("SUPPORT_INBOX", pageNumber: 2, pageSize: 2);

        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.NotEqual(page1[0].EntityId, page2[0].EntityId);
        Assert.Equal("Queue Customer", page1[0].CustomerName);
        Assert.Equal("Queue Shop", page1[0].ShopName);
        Assert.Equal("Not set", page1[0].Priority);
        Assert.Equal("Unassigned", page1[0].AssignedAdmin);
        Assert.Equal("No marker", page1[0].RiskAttentionMarker);
    }

    [Fact]
    public async Task CONE_T21_DashboardQueues_InvalidQueueType_ThrowsInvalidQueueException()
    {
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.GetDashboardQueueAsync("NON_EXISTENT_QUEUE"));

        Assert.Equal("INVALID_QUEUE_TYPE", ex.Code);
    }

    [Fact]
    public async Task CONE_T21_HardwareReviewQueue_ExposesPersistedResourceVersion()
    {
        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), "HW_QUEUE_VERSION", "Hardware Queue Version", now);
        var installationId = Guid.NewGuid();
        var review = HardwareReview.Create(
            shop.Id,
            installationId,
            "reported-fingerprint-v2",
            "stored-fingerprint-v1",
            "hardware discrepancy",
            now);
        _dbContext.Shops.Add(shop);
        _dbContext.HardwareReviews.Add(review);
        await _dbContext.SaveChangesAsync();

        var queue = await _service.GetDashboardQueueAsync("HARDWARE_REVIEW");

        var item = Assert.Single(queue);
        Assert.Equal(review.Id, item.EntityId);
        Assert.Equal(shop.Id, item.ShopId);
        Assert.Equal(review.RowVersion, item.ResourceVersion);
    }

    [Fact]
    public async Task CONE_T21_PrimaryTransferQueue_ShowsActiveCasesAndPersistedVersionOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), "TRANSFER_QUEUE", "Transfer Queue", now);
        var initiator = Guid.NewGuid();
        var pending = PrimaryTransfer.Initiate(shop.Id, Guid.NewGuid(), Guid.NewGuid(), "pending transfer", initiator, now);
        var approved = PrimaryTransfer.Initiate(shop.Id, Guid.NewGuid(), Guid.NewGuid(), "approved transfer", initiator, now.AddMinutes(1));
        approved.Approve(Guid.NewGuid(), Guid.NewGuid());
        var completed = PrimaryTransfer.Initiate(shop.Id, Guid.NewGuid(), Guid.NewGuid(), "completed transfer", initiator, now.AddMinutes(2));
        completed.Approve(Guid.NewGuid(), Guid.NewGuid());
        completed.Complete(now.AddMinutes(3));
        var rejected = PrimaryTransfer.Initiate(shop.Id, Guid.NewGuid(), Guid.NewGuid(), "rejected transfer", initiator, now.AddMinutes(4));
        rejected.Reject(Guid.NewGuid());
        _dbContext.Shops.Add(shop);
        _dbContext.PrimaryTransfers.AddRange(pending, approved, completed, rejected);
        await _dbContext.SaveChangesAsync();

        var queue = await _service.GetDashboardQueueAsync("PRIMARY_TRANSFER");

        Assert.Equal(2, queue.Count);
        Assert.Contains(queue, item => item.EntityId == pending.Id && item.Status == PrimaryTransfer.StatusPendingApproval && item.ResourceVersion == pending.RowVersion);
        Assert.Contains(queue, item => item.EntityId == approved.Id && item.Status == PrimaryTransfer.StatusApproved && item.ResourceVersion == approved.RowVersion);
        Assert.DoesNotContain(queue, item => item.EntityId == completed.Id || item.EntityId == rejected.Id);
        Assert.All(queue, item => Assert.Equal(shop.Id, item.ShopId));
    }

    // ==========================================
    // CONE-T22: Action Consequence Preview
    // ==========================================

    [Fact]
    public async Task CONE_T22_ActionPreview_RevokeLicense_MarksDestructiveAndRequiresDualApproval()
    {
        var shopId = Guid.NewGuid();
        var shop = Shop.Create(Guid.NewGuid(), "PREV_SH", "Preview Shop", DateTimeOffset.UtcNow);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        _dbContext.Shops.Add(shop);

        var license = SeedPreviewLicense(shop);
        var install = Installation.Register(shopId, license.Id, "FP_PREV", "PUBKEY", "pos1", "linux", DateTimeOffset.UtcNow);
        _dbContext.Installations.Add(install);
        await _dbContext.SaveChangesAsync();

        var preview = await _service.PreviewActionAsync(new ActionPreviewRequestDto
        {
            ActionType = "REVOKE_LICENSE",
            ShopId = shopId,
            TargetEntityId = license.Id
        });

        Assert.True(preview.IsDestructive);
        Assert.True(preview.RequiresStepUp);
        Assert.True(preview.RequiresDualApproval);
        Assert.Equal(1, preview.AffectedInstallationsCount);
        Assert.Contains("Revoking license", preview.ConsequenceSummary);
        Assert.NotEmpty(preview.Warnings);
    }

    [Fact]
    public async Task CONE_T22_ActionPreview_RenewAddon_ConfirmsLifetimeBaseUnaffected()
    {
        var shop = Shop.Create(Guid.NewGuid(), "ADDON_PREVIEW", "Addon preview", DateTimeOffset.UtcNow);
        _dbContext.Shops.Add(shop);
        var license = SeedPreviewLicense(shop);
        var addon = CommercialEntitlement.Create(license.Id, CommercialEntitlement.KindSupport, DateTimeOffset.UtcNow.AddDays(5), license.InitialOrderId);
        _dbContext.CommercialEntitlements.Add(addon);
        await _dbContext.SaveChangesAsync();
        var preview = await _service.PreviewActionAsync(new ActionPreviewRequestDto
        {
            ActionType = "RENEW_ADDON",
            ShopId = shop.Id,
            TargetEntityId = addon.Id
        });

        Assert.False(preview.IsDestructive);
        Assert.False(preview.RequiresDualApproval);
        Assert.Contains("LIFETIME base license remains completely unaffected", preview.ConsequenceSummary);
    }

    [Fact]
    public async Task CONE_T22_ActionPreview_WatermarkPolicy_ConsequencesReflectConfiguredPolicy()
    {
        var previousApprovalMatrix = Environment.GetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS");
        Environment.SetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS", "NONE");
        try
        {
        var shop = Shop.Create(Guid.NewGuid(), "POLICY_PREVIEW", "Policy preview", DateTimeOffset.UtcNow);
        _dbContext.Shops.Add(shop);
        var license = SeedPreviewLicense(shop);
        _dbContext.ShopEnforcementPolicies.Add(ShopEnforcementPolicy.Create(shop.Id, license.Id, 1,
            ShopEnforcementPolicy.FullStop, null, Guid.Empty, 86400, 86400, 300, Guid.NewGuid(), "synthetic", "synthetic", "synthetic", Guid.NewGuid(), DateTimeOffset.UtcNow));
        await _dbContext.SaveChangesAsync();
        var preview = await _service.PreviewActionAsync(new ActionPreviewRequestDto
        {
            ActionType = "WATERMARK_POLICY",
            ShopId = shop.Id,
            TargetEntityId = license.Id
        });

        Assert.True(preview.IsDestructive);
        Assert.Contains("FULL_STOP", preview.ConsequenceSummary);
        Assert.DoesNotContain("CONFIGURABLE_EXTENSION permits", preview.ConsequenceSummary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS", previousApprovalMatrix);
        }
    }

    [Fact]
    public async Task CONE_T22_ActionPreview_RejectsUnsupportedActionAndMissingPersistedIdentity()
    {
        var unsupported = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.PreviewActionAsync(new ActionPreviewRequestDto
            {
                ActionType = "UNSUPPORTED_ACTION",
                ShopId = Guid.NewGuid(),
                TargetEntityId = Guid.NewGuid()
            }));
        Assert.Equal("UNSUPPORTED_ACTION", unsupported.Code);

        var missingTarget = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.PreviewActionAsync(new ActionPreviewRequestDto
            {
                ActionType = "REVOKE_LICENSE",
                ShopId = Guid.NewGuid(),
                TargetEntityId = Guid.Empty
            }));
        Assert.Equal("INVALID_PREVIEW_TARGET", missingTarget.Code);
    }

    [Fact]
    public async Task CONE_T22_ActionPreview_RejectsLicenseTargetOutsideRequestedShop()
    {
        var owningShop = Shop.Create(Guid.NewGuid(), "OWNER_PREVIEW", "Owner preview", DateTimeOffset.UtcNow);
        var otherShop = Shop.Create(Guid.NewGuid(), "OTHER_PREVIEW", "Other preview", DateTimeOffset.UtcNow);
        _dbContext.Shops.AddRange(owningShop, otherShop);
        var license = SeedPreviewLicense(owningShop);
        await _dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.PreviewActionAsync(new ActionPreviewRequestDto
            {
                ActionType = "REVOKE_LICENSE",
                ShopId = otherShop.Id,
                TargetEntityId = license.Id
            }));

        Assert.Equal("TARGET_SHOP_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task CONE_T22_HardwarePreview_BindsApprovalRequirementsToRebindDecision()
    {
        var shop = Shop.Create(Guid.NewGuid(), "HW_PREVIEW", "Hardware preview", DateTimeOffset.UtcNow);
        _dbContext.Shops.Add(shop);
        var license = SeedPreviewLicense(shop);
        var installation = Installation.Register(shop.Id, license.Id, "FP_HW_PREVIEW", "PUBKEY", "pos-hw", "linux", DateTimeOffset.UtcNow);
        var review = HardwareReview.Create(shop.Id, installation.Id, "reported-fingerprint", "stored-fingerprint", "replacement", DateTimeOffset.UtcNow);
        _dbContext.Installations.Add(installation);
        _dbContext.HardwareReviews.Add(review);
        await _dbContext.SaveChangesAsync();

        async Task<ActionPreviewResponseDto> Preview(string decision) => await _service.PreviewActionAsync(new ActionPreviewRequestDto
        {
            ActionType = "REPLACE_HARDWARE",
            ShopId = shop.Id,
            TargetEntityId = review.Id,
            ProposedParametersJson = JsonSerializer.Serialize(new { shopId = shop.Id, targetEntityId = review.Id, expectedVersion = review.RowVersion, decision })
        });

        var rebind = await Preview("APPROVE_REBIND");
        var reject = await Preview("REJECT");

        Assert.True(rebind.RequiresStepUp);
        Assert.True(rebind.RequiresDualApproval);
        Assert.False(reject.RequiresStepUp);
        Assert.False(reject.RequiresDualApproval);
        Assert.Contains("reported fingerprint", rebind.ConsequenceSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not change", reject.ConsequenceSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CONE_T22_TransferPreview_OnlyRequiresDualApprovalForApproveDecision()
    {
        var shop = Shop.Create(Guid.NewGuid(), "TR_PREVIEW", "Transfer preview", DateTimeOffset.UtcNow);
        _dbContext.Shops.Add(shop);
        var license = SeedPreviewLicense(shop);
        var source = Installation.Register(shop.Id, license.Id, "FP_TR_SOURCE", "PUBKEY1", "pos-source", "linux", DateTimeOffset.UtcNow);
        var target = Installation.Register(shop.Id, license.Id, "FP_TR_TARGET", "PUBKEY2", "pos-target", "linux", DateTimeOffset.UtcNow);
        var transfer = PrimaryTransfer.Initiate(shop.Id, source.Id, target.Id, "synthetic transfer", Guid.NewGuid(), DateTimeOffset.UtcNow);
        _dbContext.Installations.AddRange(source, target);
        _dbContext.PrimaryTransfers.Add(transfer);
        await _dbContext.SaveChangesAsync();

        async Task<ActionPreviewResponseDto> Preview(string decision) => await _service.PreviewActionAsync(new ActionPreviewRequestDto
        {
            ActionType = "TRANSFER_PRIMARY",
            ShopId = shop.Id,
            TargetEntityId = transfer.Id,
            ProposedParametersJson = JsonSerializer.Serialize(new { shopId = shop.Id, targetEntityId = transfer.Id, expectedVersion = transfer.RowVersion, decision })
        });

        var approve = await Preview("APPROVE");
        var cancel = await Preview("CANCEL");

        Assert.True(approve.RequiresStepUp);
        Assert.True(approve.RequiresDualApproval);
        Assert.False(cancel.RequiresStepUp);
        Assert.False(cancel.RequiresDualApproval);
        Assert.Contains("Approving transfer", approve.ConsequenceSummary, StringComparison.Ordinal);
        Assert.Contains("does not complete", cancel.ConsequenceSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CONE_T22_TransferPreview_RejectsDecisionsThatCannotApplyToPersistedState()
    {
        var shop = Shop.Create(Guid.NewGuid(), "TR_PREVIEW_STATE", "Transfer preview state", DateTimeOffset.UtcNow);
        _dbContext.Shops.Add(shop);
        var license = SeedPreviewLicense(shop);
        var source = Installation.Register(shop.Id, license.Id, "FP_TR_STATE_SOURCE", "PUBKEY_STATE_1", "pos-state-source", "linux", DateTimeOffset.UtcNow);
        var target = Installation.Register(shop.Id, license.Id, "FP_TR_STATE_TARGET", "PUBKEY_STATE_2", "pos-state-target", "linux", DateTimeOffset.UtcNow);
        var transfer = PrimaryTransfer.Initiate(shop.Id, source.Id, target.Id, "synthetic transfer", Guid.NewGuid(), DateTimeOffset.UtcNow);
        transfer.Approve(Guid.NewGuid(), Guid.NewGuid());
        _dbContext.Installations.AddRange(source, target);
        _dbContext.PrimaryTransfers.Add(transfer);
        await _dbContext.SaveChangesAsync();

        foreach (var decision in new[] { "REQUEST", "APPROVE", "REJECT", "CANCEL" })
        {
            var error = await Assert.ThrowsAsync<CentralDomainException>(() => _service.PreviewActionAsync(new ActionPreviewRequestDto
            {
                ActionType = "TRANSFER_PRIMARY",
                ShopId = shop.Id,
                TargetEntityId = transfer.Id,
                ProposedParametersJson = JsonSerializer.Serialize(new { shopId = shop.Id, targetEntityId = transfer.Id, expectedVersion = transfer.RowVersion, decision })
            }));

            Assert.Equal("INVALID_STATE_TRANSITION", error.Code);
        }

        var requestRecorded = PrimaryTransfer.Initiate(shop.Id, source.Id, target.Id, "synthetic requested transfer", Guid.NewGuid(), DateTimeOffset.UtcNow);
        requestRecorded.ApplyContractDecision("REQUEST", "STANDARD_TRANSFER", Guid.NewGuid(), Guid.NewGuid());
        _dbContext.PrimaryTransfers.Add(requestRecorded);
        await _dbContext.SaveChangesAsync();
        var repeatedRequest = await Assert.ThrowsAsync<CentralDomainException>(() => _service.PreviewActionAsync(new ActionPreviewRequestDto
        {
            ActionType = "TRANSFER_PRIMARY",
            ShopId = shop.Id,
            TargetEntityId = requestRecorded.Id,
            ProposedParametersJson = JsonSerializer.Serialize(new { shopId = shop.Id, targetEntityId = requestRecorded.Id, expectedVersion = requestRecorded.RowVersion, decision = "REQUEST" })
        }));
        Assert.Equal("INVALID_STATE_TRANSITION", repeatedRequest.Code);
    }

    private CommercialLicense SeedPreviewLicense(Shop shop)
    {
        var now = DateTimeOffset.UtcNow;
        var item = PlanCatalogItem.Create("PREVIEW_" + Guid.NewGuid().ToString("N"), "Synthetic preview", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, shop.CustomerId, now);
        typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
        var license = CommercialLicense.Issue(order, now, null, now);
        _dbContext.PlanCatalogItems.Add(item); _dbContext.PricingSnapshots.Add(snapshot);
        _dbContext.Orders.Add(order); _dbContext.CommercialLicenses.Add(license);
        return license;
    }

    // ==========================================
    // CONE-T23: Dual-Approval & PII Unmasking Audit
    // ==========================================

    [Fact]
    public async Task CONE_T23_DecideSensitiveApproval_SelfApproval_StrictlyProhibitedFailClosed()
    {
        var (proposer, _, _) = await CreateAdminAndSessionAsync("proposer_admin");

        var approvalId = Guid.NewGuid();
        var req = SensitiveApprovalRequest.Request(
            "TRANSFER_PRIMARY",
            Guid.NewGuid(),
            new string('a', 64),
            proposer.Id,
            "lifecycle.transfer",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(2));

        typeof(SensitiveApprovalRequest).GetProperty("Id")!.SetValue(req, approvalId);
        _dbContext.SensitiveApprovalRequests.Add(req);
        await _dbContext.SaveChangesAsync();

        // Proposer attempts to approve their own request
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.DecideSensitiveApprovalAsync(approvalId, proposer.Id, new SensitiveApprovalDecisionDto
            {
                Decision = "APPROVE"
            }));

        Assert.Equal("SELF_APPROVAL_PROHIBITED", ex.Code);
    }

    [Fact]
    public async Task CONE_T23_DecideSensitiveApproval_DistinctAdmin_ApprovesSuccessfully()
    {
        var (proposer, _, _) = await CreateAdminAndSessionAsync("proposer_1");
        var (decider, _, _) = await CreateAdminAndSessionAsync("decider_2");

        var approvalId = Guid.NewGuid();
        var req = SensitiveApprovalRequest.Request(
            "COMMERCIAL_POLICY_CHANGE",
            Guid.NewGuid(),
            new string('b', 64),
            proposer.Id,
            "policy.change",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(2));

        typeof(SensitiveApprovalRequest).GetProperty("Id")!.SetValue(req, approvalId);
        _dbContext.SensitiveApprovalRequests.Add(req);
        await _dbContext.SaveChangesAsync();

        var success = await _service.DecideSensitiveApprovalAsync(approvalId, decider.Id, new SensitiveApprovalDecisionDto
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = new string('b', 64)
        });

        Assert.True(success);

        var approvalInDb = await _dbContext.SensitiveApprovalRequests.FindAsync(approvalId);
        Assert.Equal(SensitiveApprovalRequest.StatusApproved, approvalInDb!.Status);
        Assert.Equal(decider.Id, approvalInDb.ApproverAdminId);
    }

    [Fact]
    public async Task CONE_T23_DecideSensitiveApproval_AlreadyDecided_ThrowsException()
    {
        var (proposer, _, _) = await CreateAdminAndSessionAsync("proposer_3");
        var (decider, _, _) = await CreateAdminAndSessionAsync("decider_3");

        var approvalId = Guid.NewGuid();
        var req = SensitiveApprovalRequest.Request(
            "COMMERCIAL_POLICY_CHANGE",
            Guid.NewGuid(),
            new string('c', 64),
            proposer.Id,
            "policy.change",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(2));

        req.Approve(decider.Id, new string('c', 64), DateTimeOffset.UtcNow);
        typeof(SensitiveApprovalRequest).GetProperty("Id")!.SetValue(req, approvalId);
        _dbContext.SensitiveApprovalRequests.Add(req);
        await _dbContext.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.DecideSensitiveApprovalAsync(approvalId, decider.Id, new SensitiveApprovalDecisionDto
            {
                Decision = "APPROVE"
            }));

        Assert.Equal("APPROVAL_ALREADY_DECIDED", ex.Code);
    }

    [Fact]
    public async Task CONE_T23_UnmaskPii_MissingReason_ThrowsReasonRequired()
    {
        var (admin, _, sessionId) = await CreateAdminAndSessionAsync("pii_admin_no_reason");

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.UnmaskPiiAsync(new UnmaskPiiRequestDto
            {
                AuditRecordId = Guid.NewGuid(),
                Reason = ""
            }, admin.Id, admin.Username, sessionId));

        Assert.Equal("REASON_REQUIRED", ex.Code);
    }

    [Fact]
    public async Task CONE_T23_UnmaskPii_ValidReason_LogsAppendOnlyAuditRecord()
    {
        var (admin, _, sessionId) = await CreateAdminAndSessionAsync("pii_admin_with_reason",
            capabilities: new[] { "customer.view_sensitive", AdminCapabilities.AuditRead });
        var customer = Customer.Create("Synthetic contact", "contact@example.invalid", "fixture-contact", DateTimeOffset.UtcNow);
        _dbContext.Customers.Add(customer);

        var originalAudit = CentralAuditRecord.Create(
            actorId: admin.Id.ToString(),
            actorUsername: admin.Username,
            sessionId: sessionId,
            action: "CUSTOMER_LOOKUP",
            resource: "Customer",
            resourceId: customer.Id.ToString(),
            reason: "Initial customer lookup",
            correlationId: Guid.NewGuid().ToString(),
            clientIp: "127.0.0.1",
            userAgent: "unit-test",
            payloadSha256: null,
            detailsJson: "{\"masked\":\"+1***9999\"}",
            isSuccess: true,
            timestampUtc: DateTimeOffset.UtcNow);

        _dbContext.CentralAuditRecords.Add(originalAudit);
        await _dbContext.SaveChangesAsync();

        var response = await _service.UnmaskPiiAsync(new UnmaskPiiRequestDto
        {
            AuditRecordId = originalAudit.Id,
            Reason = "Explicit fraud investigation reference #771"
        }, admin.Id, admin.Username, sessionId);

        Assert.NotNull(response);
        Assert.Contains(customer.ContactEmail, response.UnmaskedData);
        Assert.DoesNotContain("[UNMASKED-VERIFIED]", response.UnmaskedData);

        var auditLog = await _dbContext.CentralAuditRecords
            .OrderByDescending(a => a.TimestampUtc)
            .FirstOrDefaultAsync(a => a.Action == "AUDIT_PII_UNMASKED");

        Assert.NotNull(auditLog);
        Assert.Equal("Explicit fraud investigation reference #771", auditLog.Reason);
        Assert.Equal(admin.Id.ToString(), auditLog.ActorId);
    }
}
