using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Operations;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Browser;

/// <summary>
/// Runs the real Central portal JavaScript in headless Chromium while a loopback
/// HTTP listener forwards every browser request into WebApplicationFactory's
/// TestServer handler. The database must be a freshly provisioned, owned,
/// disposable PostgreSQL 18 target; this test never creates, migrates, or drops it.
/// </summary>
public sealed class AuthenticatedPortalBrowserEndToEndTests
{
    private static readonly string[] RequiredCapabilities =
    [
        "dashboard.view", "queues.view", "activation.review", "hardware.review", "primary.transfer", "sessions.view", "sessions.revoke", "support.view", "support.create", "support.reply",
        "security.manage", "notifications.view", "search.masked", "customer.create", "shop.register",
        "licensing.read", "price.snapshot", "order.create", "payment.review", "license.issue", "addon.renew", "evidence.upload.init", "evidence.upload.finalize",
        "audit.view", "audit.read", "customer.view_sensitive", "license.view", "license.revoke", "sensitive.approve", "auth.stepup"
    ];

    [Fact]
    public async Task AuthenticatedPortal_PagesQueue_ReadsCustomer360_CreatesRepliesAndPersistsAfterReload()
    {
        var evidenceDirectory = RequiredPath("CENTRAL_PORTAL_BROWSER_EVIDENCE_DIR");
        var python = Path.GetFullPath(Required("CENTRAL_PLAYWRIGHT_PYTHON"));
        Assert.True(File.Exists(python), $"SFR-11 requires an existing Playwright Python executable: {python}");
        var dbConnection = Required("CENTRAL_DATABASE_CONNECTION_STRING");
        ValidateOwnedDatabaseConfiguration(dbConnection);
        ValidateRuntimeConfiguration();

        Directory.CreateDirectory(evidenceDirectory);
        evidenceDirectory = Path.GetFullPath(evidenceDirectory);
        var manifestPath = Path.Combine(evidenceDirectory, "run-manifest.json");
        var runId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var directoryCustomerName = $"Synthetic browser-created customer {runId:N}";
        var directoryShopName = $"Synthetic browser-created shop {runId:N}";
        var customer = Customer.CreateFromContract($"Synthetic SFR11 Browser Customer {runId:N}", null, createdAt);
        var customerId = customer.Id;
        var sensitiveCustomerName = $"Synthetic <img src=x onerror=\"window.__piiInjected=true\"> customer {runId:N}";
        var sensitiveCustomerEmail = $"pii-{runId:N}@example.invalid";
        var sensitiveCustomer = Customer.Create(sensitiveCustomerName, sensitiveCustomerEmail, "+1-555-0100", createdAt);
        var sensitiveAuditRecord = CentralAuditRecord.Create(
            actorId: "synthetic-browser-fixture",
            actorUsername: "synthetic-browser-fixture",
            sessionId: null,
            action: "SYNTHETIC_BROWSER_CUSTOMER_CREATED",
            resource: nameof(Customer),
            resourceId: sensitiveCustomer.Id.ToString("D"),
            reason: "Synthetic browser test fixture",
            correlationId: Guid.NewGuid().ToString("D"),
            clientIp: null,
            userAgent: null,
            payloadSha256: null,
            detailsJson: "{\"fixture\":\"synthetic-browser\"}",
            isSuccess: true,
            timestampUtc: createdAt);
        var shopId = Guid.NewGuid();
        var foreignShopId = Guid.NewGuid();
        var plan = PlanCatalogItem.Create($"SFR11-{Guid.NewGuid():N}"[..17].ToUpperInvariant(),
            "Synthetic browser lifetime plan", PlanCatalogItem.TargetBaseLicense,
            PlanCatalogItem.TermLifetime, null, 125m, "USD");
        var planId = plan.Id;
        var shopCode = $"E2E{Guid.NewGuid():N}"[..11].ToUpperInvariant();
        var paymentReviewBytes = Encoding.UTF8.GetBytes("SFR11_SYNTHETIC_ENCRYPTED_PAYMENT_PROOF_V1");
        var paymentReviewSha256 = Convert.ToHexStringLower(SHA256.HashData(paymentReviewBytes));
        var paymentEvidenceId = Guid.NewGuid();
        var paymentStoragePath = $"uploads/{shopId:N}/{Guid.NewGuid():N}.dat";
        var paymentStorageRoot = Required("CENTRAL_PAYMENT_STORAGE_ROOT");
        var paymentRootFullPath = Path.GetFullPath(paymentStorageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var paymentEvidenceFile = Path.GetFullPath(Path.Combine(paymentRootFullPath, paymentStoragePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!paymentEvidenceFile.StartsWith(paymentRootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Synthetic payment evidence escaped its explicitly configured local payment-storage root.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(paymentEvidenceFile)!);
        await File.WriteAllBytesAsync(paymentEvidenceFile, paymentReviewBytes);
        var addonPaymentBytes = Encoding.UTF8.GetBytes("SFR11_SYNTHETIC_SUPPORT_RENEWAL_PAYMENT_PROOF_V1");
        var addonPaymentSha256 = Convert.ToHexStringLower(SHA256.HashData(addonPaymentBytes));
        var addonPaymentStoragePath = $"uploads/{shopId:N}/{Guid.NewGuid():N}.dat";
        var addonPaymentFile = Path.GetFullPath(Path.Combine(paymentRootFullPath, addonPaymentStoragePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!addonPaymentFile.StartsWith(paymentRootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Synthetic add-on payment evidence escaped its explicitly configured local payment-storage root.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(addonPaymentFile)!);
        await File.WriteAllBytesAsync(addonPaymentFile, addonPaymentBytes);
        var username = $"sfr11_e2e_{Guid.NewGuid():N}";
        const string syntheticPassword = "Central-browser-dev-credential-2026!";
        var mfaSecret = new TotpService().GenerateSecret();
        var lifecycleApproverUsername = $"sfr11_lifecycle_approver_{Guid.NewGuid():N}";
        const string lifecycleApproverPassword = "Central-lifecycle-approver-dev-2026!";
        var lifecycleApproverMfaSecret = new TotpService().GenerateSecret();
        var initialQueueIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();
        var queueMarker = $"SFR11 synthetic queue {runId:N}";
        var ticketSubjectPrefix = $"SFR11 <img src=x onerror=\"window.__supportInjected=true\"> <a href=\"javascript:window.__supportTargetInjected=true\">Open</a> <button id=\"forged-transfer-action\">Transfer</button> {runId:N}";
        var replyText = $"Synthetic staff reply persisted for browser run {runId:D}";
        var attachmentObjectId = Guid.NewGuid();
        var attachmentBytes = Encoding.UTF8.GetBytes("SYNTHETIC_ENCRYPTED_SUPPORT_CIPHERTEXT_FIXTURE_V1");
        var attachmentSha256 = Convert.ToHexStringLower(SHA256.HashData(attachmentBytes));
        var attachmentRetentionPolicyId = Guid.Parse("d0d8959b-278f-4cda-a81c-3eb88732f071");
        var browserUploadObjectId = attachmentObjectId;
        var browserUploadBytes = attachmentBytes;
        var browserUploadSha256 = attachmentSha256;
        var unlinkedTicketId = Guid.NewGuid();
        var targetSessionId = Guid.NewGuid();
        var expiredSessionId = Guid.NewGuid();
        var expiredSessionMutationMarker = $"SFR15 expired-session mutation {runId:N}";
        using var testDatabase = CreateFactory(dbConnection);
        var manifest = new
        {
            schema = "CENTRAL_PORTAL_BROWSER_E2E_V1",
            runId,
            status = "STARTED",
            startedAtUtc = createdAt,
            evidenceDirectory,
            browser = "Playwright Python / Chromium",
            transport = "loopback HttpListener -> WebApplicationFactory TestServer handler",
            database = new
            {
                host = Environment.GetEnvironmentVariable("CENTRAL_PG_HOST"),
                port = Environment.GetEnvironmentVariable("CENTRAL_PG_PORT"),
                database = Environment.GetEnvironmentVariable("CENTRAL_PG_DATABASE"),
                runtimeRole = Environment.GetEnvironmentVariable("CENTRAL_PG_ROLE"),
                migrationRole = Environment.GetEnvironmentVariable("CENTRAL_PG_MIGRATION_ROLE"),
                ownershipMarker = Environment.GetEnvironmentVariable("CENTRAL_PG_OWNERSHIP_MARKER"),
                dataDirectory = Environment.GetEnvironmentVariable("CENTRAL_PG_DATA_DIR")
            },
            seeded = new { customerId, shopId, shopCode, username, queueTicketIds = initialQueueIds, queueMarker,
                directoryCustomerName, directoryShopName, targetSessionId, expiredSessionId },
            evidenceFiles = new[]
            {
                "run-manifest.json", "db-before.json", "db-after.json", "browser-fixture.json",
                "browser-events.json", "browser-result.json", "browser-run.log", "portal-trace.zip",
                "worker-probe-trigger.json", "worker-probe-result.json", "worker-probe-database-readback.json",
                "screenshots/01-authenticated-dashboard.png", "screenshots/02-queue-page-1.png",
                "screenshots/03-queue-page-2.png", "screenshots/04-customer-360.png",
                "screenshots/05-support-created.png", "screenshots/06-support-reply.png",
                "screenshots/07-reloaded-support-history.png", "screenshots/08-recovery-worker-status.png",
                "screenshots/09-login-mfa-session.png", "screenshots/10-logout-revoked.png",
                "screenshots/12-customer-created.png", "screenshots/13-shop-registered.png",
                "screenshots/14-masked-directory-search.png",
                "screenshots/15-pending-approval-queue.png", "screenshots/16-audited-pii-reveal.png",
                "screenshots/17-pii-reveal-cleared.png", "screenshots/18-pii-reveal-cleared-on-navigation.png",
                "screenshots/20-activation-review-persisted.png", "screenshots/21-sensitive-approval-persisted.png",
                "screenshots/19-admin-session-revocation.png",
                "screenshots/11-commercial-order.png", "screenshots/22-payment-review-persisted.png",
                "screenshots/24-expired-session-denied.png",
                "screenshots/23-manual-license-issue-persisted.png", "screenshots/25-license-revocation-expired-approval-repreview.png",
                "screenshots/26-license-revocation-pending-approval.png", "screenshots/27-license-revocation-separate-approval.png",
                "screenshots/28-license-revocation-confirmed.png", "screenshots/31-hardware-information-persisted.png",
                "screenshots/32-transfer-cancellation-persisted.png", "screenshots/33-closed-preview-cancelled.png",
                "screenshots/36-primary-transfer-completed.png", "screenshots/41-worker-degraded-after-dead-letter.png",
                "proxy-events.json"
            }
        };
        await WriteJsonAsync(manifestPath, manifest);

        try
        {
            var factory = testDatabase;
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.NotEmpty(appliedMigrations);
            Assert.Equal(0, await db.SupportTickets.CountAsync());
            Assert.Equal(0, await db.Orders.CountAsync());
            Assert.Equal(0, await db.PricingSnapshots.CountAsync());
            await WriteJsonAsync(Path.Combine(evidenceDirectory, "db-before.json"), new
            {
                runId,
                capturedAtUtc = DateTimeOffset.UtcNow,
                appliedMigrations,
                supportTicketCount = await db.SupportTickets.CountAsync(),
                operationRecordCount = await db.CentralOperationRecords.CountAsync(),
                shopCount = await db.Shops.CountAsync(),
                orderCount = await db.Orders.CountAsync(),
                pricingSnapshotCount = await db.PricingSnapshots.CountAsync()
            });

            var protectedSigner = scope.ServiceProvider.GetRequiredService<IProtectedSigningEngine>();
            var trustSet = await protectedSigner.GetTrustSetAsync();
            Assert.True(trustSet.TrustSetVersion > 0);
            Assert.Equal(1, await db.CentralTrustSetRecords.CountAsync());
            await WriteJsonAsync(Path.Combine(evidenceDirectory, "protected-trust-set-fixture.json"), new
            {
                runId,
                trustSet.TrustSetVersion,
                trustSet.EmergencyRolloverRevision,
                trustSet.SignedByTrustedRootKeyId,
                keyCount = trustSet.Keys.Count,
                persisted = true,
                privateKeyExported = false
            });

            var user = AdminUser.Create(username, new PasswordHasher().HashPassword(syntheticPassword),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), mfaSecret, true, createdAt);
            var lifecycleApprover = AdminUser.Create(lifecycleApproverUsername,
                new PasswordHasher().HashPassword(lifecycleApproverPassword),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), lifecycleApproverMfaSecret, true, createdAt);
            var lifecycleApproverRole = AdminRole.Create($"SFR11 lifecycle approver {runId:N}",
                "Synthetic independent lifecycle approver", ["dashboard.view", "queues.view", "sessions.view", "sensitive.approve", "hardware.review", "primary.transfer", "auth.stepup"]);
            var lifecycleApproverAssignment = new AdminUserRole(lifecycleApprover.Id, lifecycleApproverRole.Id, createdAt);
            var approvalRequester = AdminUser.Create(
                $"sfr11_approval_requester_{runId:N}", "synthetic-browser-test-hash", "synthetic-browser-test-salt", null, false, createdAt);
            var targetSession = AdminSession.Create(
                adminUserId: user.Id,
                tokenHash: Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))),
                createdAtUtc: createdAt,
                idleTimeout: TimeSpan.FromMinutes(15),
                absoluteLifetime: TimeSpan.FromHours(8),
                mfaSatisfied: true,
                securityRevision: user.SecurityRevision,
                capabilities: RequiredCapabilities);
            typeof(AdminSession).GetProperty("Id")!.SetValue(targetSession, targetSessionId);
            var expiredSession = AdminSession.Create(
                adminUserId: user.Id,
                tokenHash: Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))),
                createdAtUtc: createdAt.AddDays(-1),
                idleTimeout: TimeSpan.FromMinutes(15),
                absoluteLifetime: TimeSpan.FromHours(8),
                mfaSatisfied: true,
                securityRevision: user.SecurityRevision,
                capabilities: RequiredCapabilities);
            typeof(AdminSession).GetProperty("Id")!.SetValue(expiredSession, expiredSessionId);
            var pendingApproval = SensitiveApprovalRequest.Request(
                "SYNTHETIC_BROWSER_REVIEW",
                sensitiveCustomer.Id,
                new string('a', 64),
                approvalRequester.Id,
                "SYNTHETIC_BROWSER_REVIEW",
                Guid.NewGuid(),
                createdAt,
                createdAt.AddHours(2));
            var role = AdminRole.Create($"SFR11 browser {runId:N}", "Synthetic least-privilege browser role", RequiredCapabilities);
            var assignment = new AdminUserRole(user.Id, role.Id, createdAt);
            var shop = Shop.Create(customerId, shopCode, $"Synthetic SFR11 Browser Shop {runId:N}", createdAt);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            var foreignShop = Shop.Create(Guid.NewGuid(), $"F{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
                $"Synthetic Foreign Attachment Shop {runId:N}", createdAt);
            typeof(Shop).GetProperty("Id")!.SetValue(foreignShop, foreignShopId);
            var fixedTermPlan = PlanCatalogItem.Create($"SF-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
                "Synthetic browser fixed-term plan", PlanCatalogItem.TargetBaseLicense,
                CommercialTerm.FixedTerm, "P1M", 100m, "USD");
            var lifetimeSnapshot = PricingSnapshot.Create(shopId, plan, 1, createdAt);
            var lifetimeOrder = Order.CreateFromSnapshot(lifetimeSnapshot, customerId, createdAt);
            typeof(Order).GetProperty(nameof(Order.Status))!.SetValue(lifetimeOrder, Order.StatusPaidApproved);
            var lifetimeLicense = CommercialLicense.Issue(lifetimeOrder, createdAt, null, createdAt.AddMilliseconds(5));
        var lifecycleCapabilityPolicyId = Guid.NewGuid();
        var lifecyclePolicy = ShopEnforcementPolicy.Create(shopId, lifetimeLicense.Id, 1, ShopEnforcementPolicy.FullStop,
            null, Guid.NewGuid(), 86400, 86400, 300, lifecycleCapabilityPolicyId,
            "{\"fixture\":\"ER-CHANGE-20261024-020\"}", "synthetic-browser-fixture-signature", "synthetic-browser-fixture-key",
            user.Id, createdAt);
        var expiredLifecycleApprovalId = Guid.NewGuid();
            using var browserUploadKey = RSA.Create(2048);
            var browserUploadPublicKey = browserUploadKey.ExportRSAPublicKeyPem();
            var browserUploadPrivateKeyPkcs8Base64 = Convert.ToBase64String(browserUploadKey.ExportPkcs8PrivateKey());
            var browserUploadKeyId = Installation.ComputePublicKeyFingerprintSha256(browserUploadPublicKey);
            var browserUploadInstallation = Installation.Register(shopId, lifetimeLicense.Id,
                $"synthetic-upload-machine-{runId:N}", browserUploadPublicKey, "browser-upload-fixture", "Windows", createdAt);
            browserUploadInstallation.Approve(createdAt);
            var transferTarget = Installation.Register(shopId, lifetimeLicense.Id,
                $"synthetic-transfer-target-{runId:N}", "synthetic-transfer-public-key", "fixture", "Windows", createdAt);
            transferTarget.MoveToPendingApproval();
            var hardwareReview = HardwareReview.Create(shopId, browserUploadInstallation.Id,
                $"synthetic-changed-hardware-{runId:N}", browserUploadInstallation.MachineFingerprint, "Synthetic browser hardware discrepancy", createdAt);
            var primaryTransfer = PrimaryTransfer.Initiate(shopId, browserUploadInstallation.Id, transferTarget.Id,
                "Synthetic browser transfer cancellation", user.Id, createdAt);
            var approvalHardwareReview = HardwareReview.Create(shopId, browserUploadInstallation.Id,
                $"synthetic-approved-hardware-{runId:N}", browserUploadInstallation.MachineFingerprint, "Synthetic browser approved rebind", createdAt);
            var approvalPrimaryTransfer = PrimaryTransfer.Initiate(shopId, browserUploadInstallation.Id, transferTarget.Id,
                "Synthetic browser independently approved transfer", user.Id, createdAt);
            var activationInstallation = Installation.Register(shopId, lifetimeLicense.Id,
                $"synthetic-activation-machine-{runId:N}", "synthetic-activation-public-key", "fixture", "Windows", createdAt);
            activationInstallation.MoveToPendingApproval();
            var activationToken = ActivationToken.Issue(lifetimeLicense.Id, shopId,
                Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))), createdAt.AddDays(1), user.Id, createdAt);
            activationToken.MarkRedeemed(createdAt);
            activationToken.MoveToPendingApproval();
            var activationSnapshot = new CanonicalJsonSerializer().SerializeCanonical(new
            {
                version = 1,
                tokenId = activationToken.Id,
                tokenStatus = activationToken.Status,
                tokenRowVersion = activationToken.RowVersion,
                tokenExpiresAtUtc = new DateTimeOffset(activationToken.FirstRedemptionExpiresAtUtc.ToUniversalTime().Ticks -
                    (activationToken.FirstRedemptionExpiresAtUtc.ToUniversalTime().Ticks % 10), TimeSpan.Zero),
                installationId = activationInstallation.Id,
                installationStatus = activationInstallation.Status,
                installationRowVersion = activationInstallation.RowVersion,
                installationShopId = activationInstallation.ShopId,
                installationLicenseId = activationInstallation.LicenseId,
                installationPublicKeyFingerprint = activationInstallation.PublicKeyFingerprintSha256,
                hardwareFingerprintVersion = activationInstallation.HardwareFingerprintVersion,
                reportedHardwareFingerprintHash = activationInstallation.HardwareFingerprintHash,
                hardwareFingerprintHash = activationInstallation.HardwareFingerprintHash,
                licenseId = lifetimeLicense.Id,
                licenseShopId = lifetimeLicense.ShopId,
                licenseStatus = lifetimeLicense.Status,
                licenseAuthorityRevision = lifetimeLicense.AuthorityRevision,
                licenseRowVersion = lifetimeLicense.RowVersion
            });
            var pendingActivation = ActivationRedemption.Create(activationToken.Id, activationInstallation.Id,
                $"synthetic-browser-nonce-{runId:N}", "synthetic-browser-proof", createdAt, activationSnapshot, 1);
            var activationRequestId = pendingActivation.Id;
            var activationReasonCode = $"SFR11_BROWSER_{runId:N}";
            var fixedSnapshot = PricingSnapshot.Create(shopId, fixedTermPlan, 1, createdAt);
            var fixedOrder = Order.CreateFromSnapshot(fixedSnapshot, customerId, createdAt);
            typeof(Order).GetProperty(nameof(Order.Status))!.SetValue(fixedOrder, Order.StatusPaidApproved);
            var paymentReviewSnapshot = PricingSnapshot.Create(shopId, plan, 1, createdAt.AddMilliseconds(1));
            var paymentReviewOrder = Order.CreateFromSnapshot(paymentReviewSnapshot, customerId, createdAt.AddMilliseconds(1));
            var paymentUploadSession = UploadSession.Create(
                UploadSession.PurposePaymentProof, shopId, user.Id, paymentReviewOrder.Id,
                paymentReviewBytes.Length, paymentReviewSha256, paymentStoragePath,
                createdAt.AddHours(1), createdAt, "application/octet-stream", Guid.NewGuid(),
                retentionUntilUtc: createdAt.AddDays(30));
            paymentUploadSession.AppendChunk(paymentReviewBytes.Length);
            paymentUploadSession.FinalizeUpload(paymentReviewSha256);
            var paymentEvidence = PaymentEvidence.CreateFromSession(
                paymentUploadSession, paymentReviewOrder.Id, "application/octet-stream", createdAt);
            var fixedLicense = CommercialLicense.Issue(fixedOrder, createdAt, createdAt.AddDays(15), createdAt);
            var expiringAddon = CommercialEntitlement.Create(
                lifetimeLicense.Id, CommercialEntitlement.KindSupport, createdAt.AddDays(10), lifetimeOrder.Id);
            var addonPlan = PlanCatalogItem.Create($"SA-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
                "Synthetic browser support renewal", PlanCatalogItem.TargetSupport,
                PlanCatalogItem.TermFixed, "P1M", 25m, "USD");
            var addonSnapshot = PricingSnapshot.Create(shopId, addonPlan, 1, createdAt.AddMilliseconds(2));
            var addonRenewalOrder = Order.CreateFromSnapshot(addonSnapshot, customerId, createdAt.AddMilliseconds(2),
                "SUPPORT_RENEWAL", 1, lifetimeLicense.Id, expiringAddon.Id);
            var addonUploadSession = UploadSession.Create(
                UploadSession.PurposePaymentProof, shopId, user.Id, addonRenewalOrder.Id,
                addonPaymentBytes.Length, addonPaymentSha256, addonPaymentStoragePath,
                createdAt.AddHours(1), createdAt, "application/octet-stream", attachmentRetentionPolicyId,
                retentionUntilUtc: createdAt.AddDays(30));
            addonUploadSession.AppendChunk(addonPaymentBytes.Length);
            addonUploadSession.FinalizeUpload(addonPaymentSha256);
            var addonPaymentEvidence = PaymentEvidence.CreateFromSession(
                addonUploadSession, addonRenewalOrder.Id, "application/octet-stream", createdAt);
            addonPaymentEvidence.SetReviewDecision(PaymentEvidence.ReviewApproved);
            addonRenewalOrder.ApprovePayment(addonPaymentEvidence.Id, user.Id, createdAt);
            var addonRenewalExpiryUtc = new DateTimeOffset(createdAt.Year, createdAt.Month, createdAt.Day,
                createdAt.Hour, createdAt.Minute, 0, TimeSpan.Zero).AddDays(20);
            db.AdminUsers.Add(user);
            db.AdminUsers.Add(lifecycleApprover);
            db.AdminUsers.Add(approvalRequester);
            db.AdminSessions.AddRange(targetSession, expiredSession);
            db.AdminRoles.Add(role);
            db.AdminRoles.Add(lifecycleApproverRole);
            db.AdminUserRoles.Add(assignment);
            db.AdminUserRoles.Add(lifecycleApproverAssignment);
            db.Customers.Add(customer);
            db.Customers.Add(sensitiveCustomer);
            db.CentralAuditRecords.Add(sensitiveAuditRecord);
            db.SensitiveApprovalRequests.Add(pendingApproval);
            db.Shops.Add(shop);
            db.Shops.Add(foreignShop);
            db.PlanCatalogItems.AddRange(plan, fixedTermPlan, addonPlan);
            db.PricingSnapshots.AddRange(lifetimeSnapshot, fixedSnapshot, paymentReviewSnapshot, addonSnapshot);
            db.Orders.AddRange(lifetimeOrder, fixedOrder, paymentReviewOrder, addonRenewalOrder);
            db.UploadSessions.AddRange(paymentUploadSession, addonUploadSession);
            db.PaymentEvidences.AddRange(paymentEvidence, addonPaymentEvidence);
            db.CommercialLicenses.AddRange(lifetimeLicense, fixedLicense);
            db.ShopEnforcementPolicies.Add(lifecyclePolicy);
            db.CommercialEntitlements.Add(expiringAddon);
            db.Installations.AddRange(activationInstallation, browserUploadInstallation, transferTarget);
            db.HardwareReviews.AddRange(hardwareReview, approvalHardwareReview);
            db.PrimaryTransfers.AddRange(primaryTransfer, approvalPrimaryTransfer);
            db.ActivationTokens.Add(activationToken);
            db.ActivationRedemptions.Add(pendingActivation);
            for (var index = 0; index < initialQueueIds.Length; index++)
            {
                db.SupportTickets.Add(SupportTicket.Create(
                    initialQueueIds[index], shopId, Guid.NewGuid(),
                    $"{queueMarker} {index + 1:D2}", "Synthetic queue row for browser paging acceptance.",
                    new List<Guid>(), 1, createdAt.AddMilliseconds(index + 1)));
            }
            await db.SaveChangesAsync();

            var browserFixture = new
            {
                baseUrl = "",
                customerId,
                shopId,
                foreignShopId,
                planId,
                shopCode,
                paymentEvidenceId = paymentEvidence.Id,
                paymentReviewOrderId = paymentReviewOrder.Id,
                paymentReviewOrderVersion = paymentReviewOrder.RowVersion,
                paymentReviewOrderTermKind = paymentReviewOrder.TermKind,
                attachmentObjectId,
                attachmentBytesBase64 = Convert.ToBase64String(attachmentBytes),
                attachmentSha256,
                browserUploadObjectId,
                browserUploadBytesBase64 = Convert.ToBase64String(browserUploadBytes),
                browserUploadSha256,
                retentionPolicyId = attachmentRetentionPolicyId,
                browserUploadInstallationId = browserUploadInstallation.Id,
                hardwareReviewId = hardwareReview.Id,
                primaryTransferId = primaryTransfer.Id,
                approvalHardwareReviewId = approvalHardwareReview.Id,
                approvalPrimaryTransferId = approvalPrimaryTransfer.Id,
                browserUploadLicenseId = lifetimeLicense.Id,
                browserUploadLicenseVersion = lifetimeLicense.RowVersion,
                browserUploadLicenseAuthorityRevision = lifetimeLicense.AuthorityRevision,
                lifecycleCapabilityPolicyId,
                expiredLifecycleApprovalId,
                adminUserId = user.Id,
                browserUploadKeyId,
                addonRenewalOrderId = addonRenewalOrder.Id,
                addonRenewalPaymentEvidenceId = addonPaymentEvidence.Id,
                addonRenewalEntitlementId = expiringAddon.Id,
                addonRenewalExpiryUtc,
                unlinkedTicketId,
                queueIdsPageOne = initialQueueIds.Take(20).Select(id => id.ToString("D")).ToArray(),
                queueIdsPageTwo = initialQueueIds.Skip(20).Select(id => id.ToString("D")).ToArray(),
                queueMarker, directoryCustomerName, directoryShopName,
                sensitiveCustomerName, sensitiveCustomerEmail,
                sensitiveCustomerId = sensitiveCustomer.Id, sensitiveAuditRecordId = sensitiveAuditRecord.Id,
                pendingApprovalId = pendingApproval.Id, pendingApprovalOperationType = pendingApproval.OperationType,
                activationRequestId, activationReasonCode,
                targetSessionId,
                expiredSessionId,
                expiredSessionMutationMarker,
                ticketSubjectPrefix,
                replyText
            };

            await using var proxy = await TestServerLoopbackProxy.StartAsync(factory);
            var fixturePath = Path.Combine(evidenceDirectory, "browser-fixture.json");
            var workerProbeTriggerPath = Path.Combine(evidenceDirectory, "worker-probe-trigger.json");
            var workerProbeResultPath = Path.Combine(evidenceDirectory, "worker-probe-result.json");
            await WriteJsonAsync(fixturePath, new
            {
                runId,
                baseUrl = proxy.BaseUrl,
                workerProbeTriggerPath,
                workerProbeResultPath,
                browserFixture.customerId,
                browserFixture.shopId,
                browserFixture.foreignShopId,
                browserFixture.planId,
                browserFixture.shopCode,
                browserFixture.paymentEvidenceId,
                browserFixture.paymentReviewOrderId,
                browserFixture.paymentReviewOrderVersion,
                browserFixture.paymentReviewOrderTermKind,
                browserFixture.attachmentObjectId,
                browserFixture.attachmentBytesBase64,
                browserFixture.attachmentSha256,
                browserFixture.browserUploadObjectId,
                browserFixture.browserUploadBytesBase64,
                browserFixture.browserUploadSha256,
                browserFixture.retentionPolicyId,
                browserFixture.browserUploadInstallationId,
                browserFixture.hardwareReviewId,
                browserFixture.primaryTransferId,
                browserFixture.approvalHardwareReviewId,
                browserFixture.approvalPrimaryTransferId,
                browserFixture.browserUploadLicenseId,
                browserFixture.browserUploadLicenseVersion,
                browserFixture.browserUploadLicenseAuthorityRevision,
                browserFixture.lifecycleCapabilityPolicyId,
                browserFixture.expiredLifecycleApprovalId,
                browserFixture.adminUserId,
                browserFixture.browserUploadKeyId,
                browserFixture.addonRenewalOrderId,
                browserFixture.addonRenewalPaymentEvidenceId,
                browserFixture.addonRenewalEntitlementId,
                browserFixture.addonRenewalExpiryUtc,
                browserFixture.unlinkedTicketId,
                browserFixture.queueIdsPageOne,
                browserFixture.queueIdsPageTwo,
                browserFixture.queueMarker,
                browserFixture.directoryCustomerName,
                browserFixture.directoryShopName,
                browserFixture.sensitiveCustomerName,
                browserFixture.sensitiveCustomerEmail,
                browserFixture.sensitiveCustomerId,
                browserFixture.sensitiveAuditRecordId,
                browserFixture.pendingApprovalId,
                browserFixture.pendingApprovalOperationType,
                browserFixture.activationRequestId,
                browserFixture.activationReasonCode,
                browserFixture.targetSessionId,
                browserFixture.expiredSessionId,
                browserFixture.expiredSessionMutationMarker,
                browserFixture.ticketSubjectPrefix,
                browserFixture.replyText
            });
            var pythonScriptPath = Path.Combine(evidenceDirectory, "portal-browser-e2e.py");
            await File.WriteAllTextAsync(pythonScriptPath, BrowserScript, new UTF8Encoding(false));

            Assert.True(bool.TryParse(Environment.GetEnvironmentVariable("CENTRAL_OUTBOX_ENABLED"), out var workerEnabled) && workerEnabled,
                "The authenticated browser worker journey requires the hosted outbox worker to be enabled.");
            var workerMaxAttempts = int.Parse(Environment.GetEnvironmentVariable("CENTRAL_OUTBOX_MAX_ATTEMPTS") ?? "5");
            using var workerProbeCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            var workerProbeInjection = Task.Run(async () =>
            {
                while (!File.Exists(workerProbeTriggerPath))
                {
                    await Task.Delay(50, workerProbeCancellation.Token);
                }

                var poisonId = Guid.NewGuid();
                await using (var seedScope = factory.Services.CreateAsyncScope())
                {
                    var seedDb = seedScope.ServiceProvider.GetRequiredService<ICentralDbContext>();
                    seedDb.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        poisonId,
                        "activation.hardware_review_requested",
                        "BrowserWorkerProbe",
                        "not-a-guid",
                        "{\"synthetic\":true}",
                        DateTimeOffset.UtcNow));
                    await seedDb.SaveChangesAsync(workerProbeCancellation.Token);
                }

                var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await using var verifyScope = factory.Services.CreateAsyncScope();
                    var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ICentralDbContext>();
                    var message = await verifyDb.CentralOutboxMessages.AsNoTracking()
                        .SingleAsync(item => item.Id == poisonId, workerProbeCancellation.Token);
                    if (message.Status == OutboxStatus.DEAD_LETTER)
                    {
                        Assert.Equal(workerMaxAttempts, message.RetryCount);
                        Assert.False(await verifyDb.CentralConsumerEffects.AsNoTracking()
                            .AnyAsync(effect => effect.MessageId == poisonId, workerProbeCancellation.Token));
                        Assert.False(await verifyDb.AdminNotifications.AsNoTracking()
                            .AnyAsync(notification => notification.Id == poisonId, workerProbeCancellation.Token));
                        await WriteJsonAsync(workerProbeResultPath, new
                        {
                            messageId = poisonId,
                            status = message.Status.ToString(),
                        retryCount = message.RetryCount,
                            maxAttempts = workerMaxAttempts,
                            consumerEffectCount = 0,
                            notificationCount = 0,
                        eventType = message.EventType,
                        aggregateType = message.AggregateType,
                        aggregateId = message.AggregateId
                        });
                        return;
                    }

                    Assert.NotEqual(OutboxStatus.DISPATCHED, message.Status);
                    await Task.Delay(100, workerProbeCancellation.Token);
                }

                throw new TimeoutException("The browser worker probe did not reach its configured dead-letter threshold.");
            }, workerProbeCancellation.Token);

            var totp = new TotpService().GenerateCode(mfaSecret, DateTimeOffset.UtcNow);
            (int ExitCode, string Output) browserRun;
            try
            {
                browserRun = await RunPythonAsync(python, pythonScriptPath, fixturePath, evidenceDirectory,
                    new Dictionary<string, string>
                    {
                        ["CENTRAL_BROWSER_TEST_USERNAME"] = username,
                        ["CENTRAL_BROWSER_TEST_PASSWORD"] = syntheticPassword,
                        ["CENTRAL_BROWSER_TEST_TOTP"] = totp,
                        ["CENTRAL_BROWSER_TEST_MFA_SECRET"] = mfaSecret,
                        ["CENTRAL_BROWSER_TEST_INSTALLATION_PKCS8"] = browserUploadPrivateKeyPkcs8Base64,
                        ["CENTRAL_BROWSER_LIFECYCLE_APPROVER_USERNAME"] = lifecycleApproverUsername,
                        ["CENTRAL_BROWSER_LIFECYCLE_APPROVER_PASSWORD"] = lifecycleApproverPassword,
                        ["CENTRAL_BROWSER_LIFECYCLE_APPROVER_MFA_SECRET"] = lifecycleApproverMfaSecret
                    });
                await workerProbeInjection;
            }
            catch
            {
                workerProbeCancellation.Cancel();
                try { await workerProbeInjection; } catch (OperationCanceledException) { }
                throw;
            }
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "browser-run.log"), browserRun.Output, new UTF8Encoding(false));
            await WriteJsonAsync(Path.Combine(evidenceDirectory, "proxy-events.json"), proxy.Events);
            Assert.Equal(0, browserRun.ExitCode);

            var browserResultPath = Path.Combine(evidenceDirectory, "browser-result.json");
            Assert.True(File.Exists(browserResultPath), "Browser harness did not produce browser-result.json.");
            using var browserResult = JsonDocument.Parse(await File.ReadAllTextAsync(browserResultPath));
            Assert.Equal("PASS", browserResult.RootElement.GetProperty("result").GetString());
            Assert.Equal(username, browserResult.RootElement.GetProperty("displayedUsername").GetString());
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "recovery-worker-status");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "recovery-worker-degraded-after-dead-letter");
            var workerObservation = browserResult.RootElement.GetProperty("workerObservation").GetString();
            Assert.Contains("Outbox worker: Healthy", workerObservation, StringComparison.Ordinal);
            Assert.Contains("Last successful cycle:", workerObservation, StringComparison.Ordinal);
            var degradedWorkerObservation = browserResult.RootElement.GetProperty("degradedWorkerObservation").GetString();
            Assert.Contains("Outbox worker: Degraded", degradedWorkerObservation, StringComparison.Ordinal);
            Assert.Equal("degraded", browserResult.RootElement.GetProperty("workerDegradedApiStatus").GetString());
            Assert.Contains("no-store", browserResult.RootElement.GetProperty("workerDegradedCacheControl").GetString(), StringComparison.OrdinalIgnoreCase);
            using var workerProbe = JsonDocument.Parse(await File.ReadAllTextAsync(workerProbeResultPath));
            var workerProbeMessageId = Guid.Parse(workerProbe.RootElement.GetProperty("messageId").GetString()!);
            Assert.Equal("DEAD_LETTER", workerProbe.RootElement.GetProperty("status").GetString());
            Assert.Equal(workerMaxAttempts, workerProbe.RootElement.GetProperty("retryCount").GetInt32());
            Assert.Equal(workerMaxAttempts, browserResult.RootElement.GetProperty("workerPoisonRetryCount").GetInt32());
            Assert.Equal(workerMaxAttempts, browserResult.RootElement.GetProperty("workerPoisonMaxAttempts").GetInt32());
            Assert.Equal(0, browserResult.RootElement.GetProperty("workerPoisonConsumerEffectCount").GetInt32());
            Assert.Equal(0, browserResult.RootElement.GetProperty("workerPoisonNotificationCount").GetInt32());
            await using (var workerProbeScope = factory.Services.CreateAsyncScope())
            {
                var workerProbeDb = workerProbeScope.ServiceProvider.GetRequiredService<ICentralDbContext>();
                var persistedProbe = await workerProbeDb.CentralOutboxMessages.AsNoTracking()
                    .SingleAsync(item => item.Id == workerProbeMessageId);
                Assert.Equal(OutboxStatus.DEAD_LETTER, persistedProbe.Status);
                Assert.Equal(workerMaxAttempts, persistedProbe.RetryCount);
                Assert.False(await workerProbeDb.CentralConsumerEffects.AsNoTracking()
                    .AnyAsync(effect => effect.MessageId == workerProbeMessageId));
                Assert.False(await workerProbeDb.AdminNotifications.AsNoTracking()
                    .AnyAsync(notification => notification.Id == workerProbeMessageId));
                await WriteJsonAsync(Path.Combine(evidenceDirectory, "worker-probe-database-readback.json"), new
                {
                    messageId = persistedProbe.Id,
                    persistedProbe.EventType,
                    status = persistedProbe.Status.ToString(),
                    persistedProbe.RetryCount,
                    persistedProbe.LastError,
                    consumerEffectCount = await workerProbeDb.CentralConsumerEffects.AsNoTracking()
                        .CountAsync(effect => effect.MessageId == workerProbeMessageId),
                    notificationCount = await workerProbeDb.AdminNotifications.AsNoTracking()
                        .CountAsync(notification => notification.Id == workerProbeMessageId),
                    degradedWorkerObservation
                });
            }
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "login-mfa-session-logout");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "cookie-bound-step-up-session-derivation");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "auth-trace-secret-redaction");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "authoritative-nonzero-dashboard-metrics");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "directory-customer-shop-create-search");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "support-attachment-download");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "ciphertext-upload-resume-integrity-retention");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "pending-approval-queue-visible");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "independent-approval-decision-persisted");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "activation-review-request-information-persisted");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "audited-pii-reveal-injection-safe");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "sensitive-data-cleared-on-close-and-navigation");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "hardware-transfer-evidence-cleared-on-close");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "hardware-information-and-transfer-cancellation-persisted");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "hardware-command-replay-no-duplicate-effect");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "hardware-stale-revision-denied");
            Assert.Equal(200, browserResult.RootElement.GetProperty("hardwareCommandReplay").GetProperty("status").GetInt32());
            Assert.Equal("SUCCEEDED", browserResult.RootElement.GetProperty("hardwareCommandReplay").GetProperty("state").GetString());
            Assert.Equal(409, browserResult.RootElement.GetProperty("staleHardwareCommand").GetProperty("status").GetInt32());
            Assert.Equal("CONCURRENCY_CONFLICT", browserResult.RootElement.GetProperty("staleHardwareCommand").GetProperty("code").GetString());
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "closed-hardware-preview-cannot-submit");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "authenticated-browser-session-revocation");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "payment-review-persisted");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "license-issue-requires-separate-manual-action");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "license-issue-persisted");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "manual-addon-renewal-and-lifetime-conservation");
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "expired-license-approval-repreview-no-mutation");
            Assert.True(browserResult.RootElement.GetProperty("expiredLicenseApprovalRepreviewNoMutation").GetBoolean());
            Assert.Contains(browserResult.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetString() == "license-revocation-stepup-separate-approval-mutation");
            Assert.True(browserResult.RootElement.GetProperty("licenseRevocationConfirmed").GetBoolean());
            Assert.Equal(lifetimeLicense.Id.ToString("D"), browserResult.RootElement.GetProperty("licenseRevocationId").GetString());
            var licenseRevocationApprovalId = Guid.Parse(browserResult.RootElement.GetProperty("licenseRevocationApprovalId").GetString()!);
            Assert.Equal(lifecycleApproverUsername, browserResult.RootElement.GetProperty("licenseRevocationApprover").GetString());
            Assert.True(browserResult.RootElement.GetProperty("licenseIssueAvailableBeforeManualSubmit").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("manualLicenseIssueConfirmed").GetBoolean());
            Assert.Equal(paymentReviewOrder.Id.ToString("D"), browserResult.RootElement.GetProperty("manualLicenseIssueOrderId").GetString());
            Assert.True(browserResult.RootElement.GetProperty("addonRenewalConfirmed").GetBoolean());
            Assert.Equal(addonRenewalOrder.Id.ToString("D"), browserResult.RootElement.GetProperty("addonRenewalOrderId").GetString());
            Assert.Equal(addonRenewalExpiryUtc, browserResult.RootElement.GetProperty("addonRenewalExpiryUtc").GetDateTimeOffset());
            var manuallyIssuedLicenseId = Guid.Parse(browserResult.RootElement.GetProperty("manualLicenseIssueId").GetString()!);
            Assert.True(browserResult.RootElement.GetProperty("piiMarkupRenderedAsText").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("supportMarkupRenderedAsText").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("sessionCustodyBrowserSafe").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("crossOriginMutationDenied").GetBoolean());
            Assert.Equal("null", browserResult.RootElement.GetProperty("crossOriginRequestOrigin").GetString());
            Assert.True(browserResult.RootElement.GetProperty("crossOriginCookieExcluded").GetBoolean());
            Assert.Contains(browserResult.RootElement.GetProperty("crossOriginMutationStatus").GetInt32(), new[] { 401, 403 });
            Assert.True(browserResult.RootElement.GetProperty("expiredSessionDenied").GetBoolean());
            Assert.Equal(StatusCodes.Status401Unauthorized,
                browserResult.RootElement.GetProperty("expiredSessionReadStatus").GetInt32());
            Assert.Equal(StatusCodes.Status401Unauthorized,
                browserResult.RootElement.GetProperty("expiredSessionMutationStatus").GetInt32());
            Assert.True(browserResult.RootElement.GetProperty("piiClearedOnClose").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("piiClearedOnNavigation").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("sessionRevocationConfirmed").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("approvalDecisionConfirmed").GetBoolean());
            Assert.Equal(targetSessionId.ToString("D"), browserResult.RootElement.GetProperty("sessionRevocationTargetId").GetString());
            Assert.True(browserResult.RootElement.GetProperty("crossShopCreateDenied").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("unlinkedDownloadDenied").GetBoolean());
            Assert.Equal(attachmentBytes.Length, browserResult.RootElement.GetProperty("downloadedCiphertextLength").GetInt32());
            Assert.Equal(attachmentSha256, browserResult.RootElement.GetProperty("downloadedCiphertextSha256").GetString());
            Assert.True(browserResult.RootElement.GetProperty("ciphertextUploadResumed").GetBoolean());
            Assert.True(browserResult.RootElement.GetProperty("ciphertextUploadConflictingRetryDenied").GetBoolean());
            Assert.Equal(attachmentSha256, browserResult.RootElement.GetProperty("uploadedCiphertextSha256").GetString());
            Assert.Equal(attachmentObjectId.ToString("D"), browserResult.RootElement.GetProperty("uploadedCiphertextObjectId").GetString());
            var createdTicketId = Guid.Parse(browserResult.RootElement.GetProperty("ticketId").GetString()!);
            var subject = browserResult.RootElement.GetProperty("subject").GetString()!;
            var crossOriginMutationMarker = browserResult.RootElement.GetProperty("crossOriginMutationMarker").GetString()!;
            Assert.False(await db.Customers.AsNoTracking().AnyAsync(customer => customer.LegalName == crossOriginMutationMarker),
                "A cross-origin browser form request created a customer without an authenticated same-origin session.");
            Assert.False(await db.Customers.AsNoTracking().AnyAsync(customer => customer.LegalName == expiredSessionMutationMarker),
                "An expired persisted browser session created a customer despite the protected-route denial.");
            var createdOrderId = Guid.Parse(browserResult.RootElement.GetProperty("orderId").GetString()!);
            var createdDirectoryCustomerId = Guid.Parse(browserResult.RootElement.GetProperty("createdDirectoryCustomerId").GetString()!);
            var createdDirectoryShopId = Guid.Parse(browserResult.RootElement.GetProperty("createdDirectoryShopId").GetString()!);
            var dashboardMetrics = browserResult.RootElement.GetProperty("dashboardMetrics");
            Assert.Equal(2, dashboardMetrics.GetProperty("api").GetProperty("activeShopsCount").GetInt32());
            Assert.Equal(1, dashboardMetrics.GetProperty("api").GetProperty("lifetimeBaseCount").GetInt32());
            Assert.Equal(1, dashboardMetrics.GetProperty("api").GetProperty("fixedTermBaseCount").GetInt32());
            Assert.Equal(1, dashboardMetrics.GetProperty("api").GetProperty("expiringAddonsCount").GetInt32());
            Assert.Equal(1, dashboardMetrics.GetProperty("api").GetProperty("expiringFixedTermBasesCount").GetInt32());
            Assert.Equal(0, dashboardMetrics.GetProperty("api").GetProperty("pendingApprovalsCount").GetInt32());
            Assert.Equal(2, dashboardMetrics.GetProperty("rendered").GetProperty("activeShopsCount").GetInt32());
            Assert.Equal(1, dashboardMetrics.GetProperty("rendered").GetProperty("lifetimeBaseCount").GetInt32());
            Assert.Equal(1, dashboardMetrics.GetProperty("rendered").GetProperty("fixedTermBaseCount").GetInt32());
            Assert.Equal(1, dashboardMetrics.GetProperty("rendered").GetProperty("expiringAddonsCount").GetInt32());
            Assert.Equal(1, dashboardMetrics.GetProperty("rendered").GetProperty("expiringFixedTermBasesCount").GetInt32());
            Assert.Equal(0, dashboardMetrics.GetProperty("rendered").GetProperty("pendingApprovalsCount").GetInt32());
            Assert.Equal(1, browserResult.RootElement.GetProperty("initialPendingApprovalsCount").GetInt32());
            Assert.Equal(1, await db.CommercialLicenses.AsNoTracking()
                .CountAsync(license => license.TermKind == CommercialTerm.Lifetime && license.Status == CommercialLicense.StatusActive));
            Assert.Equal(1, await db.CommercialLicenses.AsNoTracking()
                .CountAsync(license => license.TermKind == CommercialTerm.FixedTerm && license.Status == CommercialLicense.StatusActive));
            Assert.Equal(1, await db.CommercialLicenses.AsNoTracking()
                .CountAsync(license => license.TermKind == CommercialTerm.FixedTerm &&
                    license.Status == CommercialLicense.StatusActive && license.ValidUntilUtc != null &&
                    license.ValidUntilUtc > DateTimeOffset.UtcNow &&
                    license.ValidUntilUtc <= DateTimeOffset.UtcNow.AddDays(30)));
            Assert.Equal(1, await db.CommercialEntitlements.AsNoTracking()
                .CountAsync(entitlement => entitlement.Status == CommercialEntitlement.StatusActive &&
                    entitlement.ValidUntilUtc != null && entitlement.ValidUntilUtc > DateTimeOffset.UtcNow &&
                    entitlement.ValidUntilUtc <= DateTimeOffset.UtcNow.AddDays(30)));

            var persistedActivation = await db.ActivationRedemptions.AsNoTracking().SingleAsync(item => item.Id == activationRequestId);
            Assert.Equal(ActivationRedemption.StatusRequestInformation, persistedActivation.Status);
            Assert.Equal(activationReasonCode, persistedActivation.DecisionReasonCode);
            Assert.Equal(user.Id, persistedActivation.ReviewedByAdminId);
            Assert.Equal(2, persistedActivation.RowVersion);
            Assert.True(await db.CentralOperationRecords.AsNoTracking().AnyAsync(record =>
                record.OperationType == "activation.review" && record.ResultReference == $"activation_requests/{activationRequestId:D}" &&
                record.ShopId == shopId && record.State == OperationState.SUCCEEDED));
            Assert.True(await db.CentralOutboxMessages.AsNoTracking().AnyAsync(message =>
                message.AggregateId == activationRequestId.ToString("D") && message.EventType == "activation.review_decided"));
            Assert.True(await db.CentralAuditRecords.AsNoTracking().AnyAsync(record =>
                record.Action == "ACTIVATION_REVIEW_DECIDED" && record.ResourceId == activationRequestId.ToString("D") &&
                record.ActorId == user.Id.ToString("D") && record.IsSuccess));

            var persistedHardware = await db.HardwareReviews.AsNoTracking().SingleAsync(x => x.Id == hardwareReview.Id);
            Assert.Equal("REQUEST_INFORMATION", persistedHardware.Status);
            Assert.Equal("REQUEST_INFORMATION", persistedHardware.ContractDecision);
            Assert.Equal(attachmentObjectId, persistedHardware.VerificationEvidenceId);
            Assert.Equal(user.Id, persistedHardware.ReviewedByAdminId);
            Assert.Equal(2, persistedHardware.RowVersion);
            var persistedTransfer = await db.PrimaryTransfers.AsNoTracking().SingleAsync(x => x.Id == primaryTransfer.Id);
            Assert.Equal(PrimaryTransfer.StatusCancelled, persistedTransfer.Status);
            Assert.Equal("CANCEL", persistedTransfer.ContractDecision);
            Assert.Equal("STANDARD_TRANSFER", persistedTransfer.RecoveryType);
            Assert.Equal(attachmentObjectId, persistedTransfer.VerificationEvidenceId);
            Assert.Equal(2, persistedTransfer.RowVersion);
            var approvedHardware = await db.HardwareReviews.AsNoTracking().SingleAsync(x => x.Id == approvalHardwareReview.Id);
            Assert.Equal("APPROVE_REBIND", approvedHardware.ContractDecision);
            Assert.Equal(HardwareReview.StatusApproved, approvedHardware.Status);
            Assert.Equal(lifecycleApprover.Id, approvedHardware.ReviewedByAdminId);
            Assert.Equal(attachmentObjectId, approvedHardware.VerificationEvidenceId);
            var approvedTransfer = await db.PrimaryTransfers.AsNoTracking().SingleAsync(x => x.Id == approvalPrimaryTransfer.Id);
            Assert.Equal("APPROVE", approvedTransfer.ContractDecision);
            Assert.Equal(PrimaryTransfer.StatusCompleted, approvedTransfer.Status);
            Assert.Equal(lifecycleApprover.Id, approvedTransfer.ApprovedByAdminId);
            Assert.Equal(attachmentObjectId, approvedTransfer.VerificationEvidenceId);
            Assert.Equal(3, approvedTransfer.RowVersion);
            Assert.Equal(1, await db.CentralOperationRecords.AsNoTracking().CountAsync(x => x.TargetResource == $"transfers/{approvalPrimaryTransfer.Id}/complete" && x.State == OperationState.SUCCEEDED));
            Assert.Equal(1, await db.CentralAuditRecords.AsNoTracking().CountAsync(x => x.Action == "PRIMARY_TRANSFER_COMPLETED" && x.ResourceId == approvalPrimaryTransfer.Id.ToString("D") && x.ActorId == lifecycleApprover.Id.ToString("D") && x.IsSuccess));
            Assert.Equal(1, await db.CentralOutboxMessages.AsNoTracking().CountAsync(x => x.AggregateId == approvalPrimaryTransfer.Id.ToString("D") && x.EventType == "activation.primary_transfer_completed"));
            foreach (var target in new[] { approvalHardwareReview.Id, approvalPrimaryTransfer.Id })
            {
                var approvedRequest = await db.SensitiveApprovalRequests.AsNoTracking().SingleAsync(x => x.TargetEntityId == target);
                Assert.Equal(user.Id, approvedRequest.RequesterAdminId);
                Assert.Equal(lifecycleApprover.Id, approvedRequest.ApproverAdminId);
                Assert.Equal(SensitiveApprovalRequest.StatusConsumed, approvedRequest.Status);
                Assert.Equal(1, await db.CentralOperationRecords.AsNoTracking().CountAsync(x =>
                    x.ResultReference == (target == approvalHardwareReview.Id ? $"hardware_reviews/{target:D}" : $"transfers/{target:D}") &&
                    (target == approvalHardwareReview.Id || x.TargetResource != $"transfers/{target:D}/complete") &&
                    x.State == OperationState.SUCCEEDED));
            }
            foreach (var (resourceId, operationType, resultPrefix, auditAction, eventType) in new[]
            {
                (hardwareReview.Id, "hardware.review", "hardware_reviews", "HARDWARE_REVIEW_DECIDED", "activation.hardware_review_decided"),
                (primaryTransfer.Id, "primary.transfer", "transfers", "PRIMARY_TRANSFER_DECIDED", "activation.primary_transfer_decided")
            })
            {
                Assert.Equal(1, await db.CentralOperationRecords.AsNoTracking().CountAsync(x =>
                    x.OperationType == operationType && x.ResultReference == $"{resultPrefix}/{resourceId:D}" && x.State == OperationState.SUCCEEDED));
                Assert.Equal(1, await db.CentralAuditRecords.AsNoTracking().CountAsync(x =>
                    x.Action == auditAction && x.ResourceId == resourceId.ToString("D") && x.ActorId == user.Id.ToString("D") && x.IsSuccess));
                Assert.Equal(1, await db.CentralOutboxMessages.AsNoTracking().CountAsync(x =>
                    x.AggregateId == resourceId.ToString("D") && x.EventType == eventType));
            }

            var persistedDirectoryCustomer = await db.Customers.AsNoTracking()
                .SingleAsync(record => record.Id == createdDirectoryCustomerId);
            Assert.Equal(directoryCustomerName, persistedDirectoryCustomer.LegalName);
            var persistedDirectoryShop = await db.Shops.AsNoTracking()
                .SingleAsync(record => record.Id == createdDirectoryShopId);
            Assert.Equal(createdDirectoryCustomerId, persistedDirectoryShop.CustomerId);
            Assert.Equal(directoryShopName, persistedDirectoryShop.ShopName);
            Assert.Equal(1, persistedDirectoryShop.PrimaryCapacity);

            var persistedOrder = await db.Orders.AsNoTracking().SingleAsync(order => order.Id == createdOrderId);
            var createdPricingSnapshotId = persistedOrder.SnapshotId;
            Assert.Equal(shopId, persistedOrder.ShopId);
            Assert.Equal(customerId, persistedOrder.CustomerId);
            Assert.Equal(planId, persistedOrder.PlanId);
            Assert.Equal(createdPricingSnapshotId, persistedOrder.SnapshotId);
            Assert.Equal("NEW_LICENSE", persistedOrder.OrderType);
            Assert.Equal(125m, persistedOrder.TotalAmount);
            Assert.Equal(Order.StatusPendingPayment, persistedOrder.Status);
            var persistedPaymentEvidence = await db.PaymentEvidences.AsNoTracking()
                .SingleAsync(evidence => evidence.Id == paymentEvidence.Id);
            Assert.Equal(paymentReviewOrder.Id, persistedPaymentEvidence.OrderId);
            Assert.Equal(shopId, persistedPaymentEvidence.ShopId);
            Assert.Equal(paymentReviewSha256, persistedPaymentEvidence.Sha256Digest);
            Assert.Equal(paymentReviewBytes.LongLength, persistedPaymentEvidence.ByteLength);
            Assert.Equal(PaymentEvidence.ReviewApproved, persistedPaymentEvidence.ReviewStatus);
            var persistedPaymentUpload = await db.UploadSessions.AsNoTracking()
                .SingleAsync(upload => upload.Id == persistedPaymentEvidence.UploadSessionId);
            Assert.Equal(UploadSession.StatusFinalized, persistedPaymentUpload.Status);
            Assert.Equal(paymentReviewBytes.LongLength, persistedPaymentUpload.UploadedBytes);
            Assert.Equal(paymentReviewSha256, persistedPaymentUpload.ActualSha256);
            var storedPaymentBytes = await File.ReadAllBytesAsync(paymentEvidenceFile);
            Assert.Equal(paymentReviewBytes, storedPaymentBytes);
            Assert.Equal(paymentReviewSha256, Convert.ToHexStringLower(SHA256.HashData(storedPaymentBytes)));
            var persistedPaymentOrder = await db.Orders.AsNoTracking().SingleAsync(order => order.Id == paymentReviewOrder.Id);
            Assert.Equal(Order.StatusPaidApproved, persistedPaymentOrder.Status);
            var persistedManuallyIssuedLicense = await db.CommercialLicenses.AsNoTracking()
                .SingleAsync(license => license.Id == manuallyIssuedLicenseId);
            Assert.Equal(persistedPaymentOrder.Id, persistedManuallyIssuedLicense.InitialOrderId);
            Assert.Equal(shopId, persistedManuallyIssuedLicense.ShopId);
            Assert.Equal(CommercialLicense.StatusActive, persistedManuallyIssuedLicense.Status);
            Assert.Equal(CommercialTerm.Lifetime, persistedManuallyIssuedLicense.TermKind);
            Assert.Null(persistedManuallyIssuedLicense.ValidUntilUtc);
            var persistedAddonRenewalOrder = await db.Orders.AsNoTracking().SingleAsync(order => order.Id == addonRenewalOrder.Id);
            Assert.Equal("SUPPORT_RENEWAL", persistedAddonRenewalOrder.OrderType);
            Assert.Equal(PlanCatalogItem.TargetSupport, persistedAddonRenewalOrder.PurchaseTarget);
            Assert.Equal(Order.StatusPaidApproved, persistedAddonRenewalOrder.Status);
            Assert.Equal(lifetimeLicense.Id, persistedAddonRenewalOrder.BaseLicenseId);
            Assert.Equal(expiringAddon.Id, persistedAddonRenewalOrder.ExistingEntitlementId);
            Assert.Equal(user.Id, persistedAddonRenewalOrder.ApprovedByAdminId);
            Assert.Equal(addonPaymentEvidence.Id, persistedAddonRenewalOrder.ApprovedEvidenceId);
            var persistedAddonEvidence = await db.PaymentEvidences.AsNoTracking().SingleAsync(evidence => evidence.OrderId == addonRenewalOrder.Id);
            Assert.Equal(PaymentEvidence.ReviewApproved, persistedAddonEvidence.ReviewStatus);
            Assert.Equal(addonPaymentSha256, persistedAddonEvidence.Sha256Digest);
            Assert.Equal(addonPaymentBytes.LongLength, persistedAddonEvidence.ByteLength);
            Assert.Equal(addonPaymentBytes, await File.ReadAllBytesAsync(addonPaymentFile));
            var renewedSupport = await db.CommercialEntitlements.AsNoTracking().SingleAsync(entitlement => entitlement.Id == expiringAddon.Id);
            Assert.Equal(CommercialEntitlement.KindSupport, renewedSupport.Kind);
            Assert.Equal(CommercialEntitlement.StatusActive, renewedSupport.Status);
            Assert.Equal(addonRenewalOrder.Id, renewedSupport.LastOrderId);
            Assert.Equal(addonRenewalExpiryUtc, renewedSupport.ValidUntilUtc);
            var preservedLifetimeBase = await db.CommercialLicenses.AsNoTracking().SingleAsync(license => license.Id == lifetimeLicense.Id);
            Assert.Equal(CommercialTerm.Lifetime, preservedLifetimeBase.TermKind);
            Assert.Equal(CommercialLicense.StatusRevoked, preservedLifetimeBase.Status);
            Assert.Null(preservedLifetimeBase.ValidUntilUtc);
            Assert.True(preservedLifetimeBase.RowVersion > lifetimeLicense.RowVersion);
            Assert.True(preservedLifetimeBase.AuthorityRevision > lifetimeLicense.AuthorityRevision);
            var persistedLifecycleApproval = await db.SensitiveApprovalRequests.AsNoTracking()
                .SingleAsync(approval => approval.Id == licenseRevocationApprovalId);
            Assert.Equal(SensitiveApprovalRequest.StatusConsumed, persistedLifecycleApproval.Status);
            Assert.Equal(user.Id, persistedLifecycleApproval.RequesterAdminId);
            Assert.Equal(lifecycleApprover.Id, persistedLifecycleApproval.ApproverAdminId);
            Assert.Equal(lifetimeLicense.Id, persistedLifecycleApproval.TargetEntityId);
            Assert.Equal("LICENSE_REVOKE", persistedLifecycleApproval.OperationType);
            Assert.Equal("license.revoke", persistedLifecycleApproval.ActionScope);
            Assert.Equal("Synthetic browser proof: approved license revocation workflow.", persistedLifecycleApproval.RequestReason);
            var persistedLifecycleOperation = await db.CentralOperationRecords.AsNoTracking()
                .SingleAsync(record => record.OperationType == "license.revoke" &&
                    record.TargetResource == $"license/{lifetimeLicense.Id:D}/license.revoke");
            Assert.Equal(OperationState.SUCCEEDED, persistedLifecycleOperation.State);
            Assert.Equal(licenseRevocationApprovalId, persistedLifecycleOperation.SensitiveApprovalId);
            Assert.Equal($"licenses/{lifetimeLicense.Id:D}", persistedLifecycleOperation.ResultReference);
            Assert.True(await db.CentralAuditRecords.AsNoTracking().AnyAsync(record =>
                record.Action == "COMMERCIAL_LICENSE_REVOKED" && record.ResourceId == lifetimeLicense.Id.ToString("D") &&
                record.ActorId == user.Id.ToString("D") && record.IsSuccess));
            Assert.True(await db.CentralOutboxMessages.AsNoTracking().AnyAsync(message =>
                message.AggregateId == lifetimeLicense.Id.ToString("D") && message.EventType == "license.revoked"));
            Assert.True(await db.CentralOperationRecords.AsNoTracking().AnyAsync(record =>
                record.OperationType == "addon.renew" && record.ResultReference == $"entitlements/{expiringAddon.Id:D}" &&
                record.State == OperationState.SUCCEEDED));
            Assert.True(await db.CentralOutboxMessages.AsNoTracking().AnyAsync(message =>
                message.AggregateId == expiringAddon.Id.ToString("D") && message.EventType == "commercial.addon_renewed"));
            Assert.True(await db.CentralAuditRecords.AsNoTracking().AnyAsync(record =>
                record.Action == "COMMERCIAL_ADDON_RENEWED" && record.ResourceId == expiringAddon.Id.ToString("D") &&
                record.ActorId == user.Id.ToString("D") && record.IsSuccess));
            Assert.Equal(1, await db.CommercialLicenses.AsNoTracking()
                .CountAsync(license => license.InitialOrderId == persistedPaymentOrder.Id));
            var persistedPaymentReview = await db.PaymentReviewRecords.AsNoTracking()
                .SingleAsync(review => review.EvidenceId == paymentEvidence.Id);
            Assert.Equal(paymentReviewOrder.Id, persistedPaymentReview.OrderId);
            Assert.Equal(user.Id, persistedPaymentReview.ReviewerAdminId);
            Assert.Equal(PaymentReviewRecord.DecisionApprove, persistedPaymentReview.Decision);
            Assert.Equal("SFR11_BROWSER_PAYMENT_CONFIRMED", persistedPaymentReview.ReasonCode);
            Assert.Equal("Synthetic authenticated browser payment review.", persistedPaymentReview.Notes);
            Assert.True(await db.CentralOperationRecords.AsNoTracking().AnyAsync(record =>
                record.OperationType == "payment.review" && record.ResultReference == $"payment_reviews/{persistedPaymentReview.Id:D}" &&
                record.State == OperationState.SUCCEEDED));
            Assert.True(await db.CentralOutboxMessages.AsNoTracking().AnyAsync(message =>
                message.AggregateId == persistedPaymentReview.Id.ToString("D") && message.EventType == "commercial.payment_reviewed"));
            Assert.True(await db.CentralAuditRecords.AsNoTracking().AnyAsync(record =>
                record.Action == "COMMERCIAL_PAYMENT_REVIEWED" && record.ResourceId == persistedPaymentReview.Id.ToString("D") &&
                record.ActorId == user.Id.ToString("D") && record.IsSuccess));
            Assert.True(await db.CentralOperationRecords.AsNoTracking().AnyAsync(record =>
                record.OperationType == "license.issue" && record.ResultReference == $"licenses/{manuallyIssuedLicenseId:D}" &&
                record.State == OperationState.SUCCEEDED));
            Assert.True(await db.CentralOutboxMessages.AsNoTracking().AnyAsync(message =>
                message.AggregateId == manuallyIssuedLicenseId.ToString("D") && message.EventType == "commercial.license_issued"));
            Assert.True(await db.CentralAuditRecords.AsNoTracking().AnyAsync(record =>
                record.Action == "COMMERCIAL_LICENSE_ISSUED" && record.ResourceId == manuallyIssuedLicenseId.ToString("D") &&
                record.ActorId == user.Id.ToString("D") && record.Reason == "[REDACTED_FREE_TEXT]" &&
                record.IsSuccess));
            var persistedSnapshot = await db.PricingSnapshots.AsNoTracking()
                .SingleAsync(snapshot => snapshot.Id == createdPricingSnapshotId);
            Assert.Equal(shopId, persistedSnapshot.ShopId);
            Assert.Equal(planId, persistedSnapshot.CatalogItemId);
            Assert.Equal(PlanCatalogItem.TargetBaseLicense, persistedSnapshot.PurchaseTarget);
            Assert.Equal(PlanCatalogItem.TermLifetime, persistedSnapshot.TermKind);
            Assert.Equal("USD", persistedSnapshot.Currency);

            var afterTicket = await db.SupportTickets.AsNoTracking().SingleAsync(ticket => ticket.Id == createdTicketId);
            Assert.Equal(shopId, afterTicket.ShopId);
            Assert.Equal(subject, afterTicket.Subject);
            Assert.Equal(3, afterTicket.RowVersion);
            Assert.Contains(replyText, afterTicket.RepliesJson, StringComparison.Ordinal);
            Assert.Contains(user.Id.ToString("D"), afterTicket.RepliesJson, StringComparison.OrdinalIgnoreCase);
            var persistedSupportHistory = JsonSerializer.Deserialize<List<SupportReplyDto>>(afterTicket.RepliesJson);
            Assert.NotNull(persistedSupportHistory);
            Assert.Collection(persistedSupportHistory,
                replyEvent =>
                {
                    Assert.Equal(SupportReplyDto.KindMessage, replyEvent.Kind);
                    Assert.Equal(replyText, replyEvent.Text);
                    Assert.Equal(user.Id.ToString("D"), replyEvent.Author);
                },
                statusEvent =>
                {
                    Assert.Equal(SupportReplyDto.KindStatusChanged, statusEvent.Kind);
                    Assert.Equal("OPEN -> IN_PROGRESS", statusEvent.Text);
                    Assert.Equal(user.Id.ToString("D"), statusEvent.Author);
                });
            var operationRecords = await db.CentralOperationRecords.AsNoTracking()
                .Where(record => record.ShopId == shopId &&
                    (record.OperationType == "support.create" || record.OperationType == "support.reply"))
                .OrderBy(record => record.CreatedAtUtc)
                .Select(record => new
                {
                    record.OperationType,
                    record.State,
                    record.ResultReference,
                    record.ShopId,
                    record.CreatedAtUtc
                }).ToArrayAsync();
            Assert.Contains(operationRecords, record => record.OperationType == "support.create" &&
                record.ResultReference == $"support/tickets/{createdTicketId:D}");
            Assert.Contains(operationRecords, record => record.OperationType == "support.reply" &&
                record.ResultReference == createdTicketId.ToString("D"));
            var commercialOperationRecords = await db.CentralOperationRecords.AsNoTracking()
                .Where(record => record.ShopId == shopId &&
                    (record.OperationType == "price.snapshot" || record.OperationType == "order.create"))
                .OrderBy(record => record.CreatedAtUtc)
                .ToArrayAsync();
            Assert.Contains(commercialOperationRecords, record => record.OperationType == "price.snapshot" &&
                record.ResultReference == $"pricing-snapshots/{createdPricingSnapshotId:D}" &&
                record.State == OperationState.SUCCEEDED);
            Assert.Contains(commercialOperationRecords, record => record.OperationType == "order.create" &&
                record.ResultReference == $"orders/{createdOrderId:D}" && record.State == OperationState.SUCCEEDED);
            Assert.True(await db.CentralOutboxMessages.AsNoTracking().AnyAsync(message =>
                message.AggregateId == createdOrderId.ToString("D") && message.EventType == "commercial.order_created"));
            Assert.True(await db.CentralAuditRecords.AsNoTracking().AnyAsync(record =>
                record.Action == "COMMERCIAL_ORDER_CREATED" && record.ResourceId == createdOrderId.ToString("D")));
            var persistedAttachment = await db.EncryptedObjects.AsNoTracking().SingleAsync(record => record.Id == attachmentObjectId);
            Assert.Equal(shopId, persistedAttachment.ShopId);
            Assert.Equal(browserUploadInstallation.Id, persistedAttachment.InstallationId);
            Assert.Equal(EncryptedObjectRecord.PurposeSupportAttachment, persistedAttachment.Purpose);
            Assert.Equal(attachmentSha256, persistedAttachment.CiphertextSha256);
            Assert.Equal(attachmentBytes.LongLength, persistedAttachment.ContentLength);
            Assert.Equal(attachmentRetentionPolicyId, persistedAttachment.RetentionPolicyId);
            Assert.NotNull(persistedAttachment.RetentionUntilUtc);
            Assert.True(persistedAttachment.RetentionUntilUtc > DateTimeOffset.UtcNow);
            var uploadedSessionId = Guid.Parse(browserResult.RootElement.GetProperty("uploadedCiphertextUploadId").GetString()!);
            var persistedUploadSession = await db.UploadSessions.AsNoTracking().SingleAsync(session => session.Id == uploadedSessionId);
            Assert.Equal(browserUploadInstallation.Id, persistedUploadSession.UploaderPrincipalId);
            Assert.Equal(shopId, persistedUploadSession.ShopId);
            Assert.Equal(browserUploadObjectId, persistedUploadSession.TargetEntityId);
            Assert.Equal(UploadSession.PurposeSupportAttachment, persistedUploadSession.Purpose);
            Assert.Equal(UploadSession.StatusFinalized, persistedUploadSession.Status);
            Assert.Equal(browserUploadBytes.LongLength, persistedUploadSession.UploadedBytes);
            Assert.Equal(browserUploadSha256, persistedUploadSession.ActualSha256);
            var objectStorage = testDatabase.Services.GetRequiredService<IObjectStorageProvider>();
            await using (var storedCiphertext = await objectStorage.GetCiphertextAsync(persistedAttachment.StoragePath))
            using (var storedBytes = new MemoryStream())
            {
                await storedCiphertext.CopyToAsync(storedBytes);
                Assert.Equal(browserUploadBytes, storedBytes.ToArray());
            }
            using (var attachmentIds = JsonDocument.Parse(afterTicket.AttachmentObjectIdsJson))
            {
                Assert.Contains(attachmentObjectId.ToString("D"), attachmentIds.RootElement.EnumerateArray()
                    .Select(id => id.GetString()!), StringComparer.OrdinalIgnoreCase);
            }
            Assert.False(await db.SupportTickets.AsNoTracking().AnyAsync(ticket => ticket.Subject.Contains("Synthetic cross-shop attachment")));
            var persistedApproval = await db.SensitiveApprovalRequests.AsNoTracking()
                .SingleAsync(item => item.Id == pendingApproval.Id);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, persistedApproval.Status);
            Assert.Equal(user.Id, persistedApproval.ApproverAdminId);
            Assert.Equal(2, persistedApproval.RowVersion);
            Assert.True(await db.CentralOperationRecords.AsNoTracking().AnyAsync(record =>
                record.OperationType == "sensitive.approve" && record.ResultReference == $"sensitive-approvals/{pendingApproval.Id:D}" &&
                record.State == OperationState.SUCCEEDED));
            Assert.True(await db.CentralOutboxMessages.AsNoTracking().AnyAsync(message =>
                message.AggregateId == pendingApproval.Id.ToString("D") && message.EventType == "approval.decided"));
            Assert.True(await db.CentralAuditRecords.AsNoTracking().AnyAsync(record =>
                record.Action == "SENSITIVE_APPROVAL_DECIDED" && record.ResourceId == pendingApproval.Id.ToString("D") &&
                record.ActorId == user.Id.ToString("D") && record.IsSuccess));
            var piiAccessAudits = await db.CentralAuditRecords.AsNoTracking().Where(record =>
                record.Action == "AUDIT_PII_UNMASKED" && record.ResourceId == sensitiveAuditRecord.Id.ToString("D"))
                .ToArrayAsync();
            Assert.Equal(2, piiAccessAudits.Length);
            Assert.All(piiAccessAudits, record => Assert.Equal(user.Id.ToString("D"), record.ActorId));
            Assert.Contains(piiAccessAudits, record => record.Reason == "Synthetic browser reason: audited contact review");
            Assert.Contains(piiAccessAudits, record => record.Reason == "Synthetic browser reason: navigation-clear review");

            var consumedChallenge = await db.LoginChallenges.AsNoTracking()
                .SingleAsync(challenge => challenge.AdminUserId == user.Id);
            Assert.True(consumedChallenge.IsConsumed);
            Assert.NotNull(consumedChallenge.ConsumedAtUtc);
            var persistedLoginUser = await db.AdminUsers.AsNoTracking().SingleAsync(candidate => candidate.Id == user.Id);
            Assert.NotNull(persistedLoginUser.LastLoginAtUtc);
            var loginSession = await db.AdminSessions.AsNoTracking().SingleAsync(candidate =>
                candidate.AdminUserId == user.Id && candidate.Id != targetSessionId && candidate.Id != expiredSessionId);
            Assert.True(loginSession.MfaSatisfied);
            Assert.True(loginSession.IsRevoked);
            Assert.NotNull(loginSession.RevokedAtUtc);
            var browserRevokedSession = await db.AdminSessions.AsNoTracking().SingleAsync(session => session.Id == targetSessionId);
            Assert.True(browserRevokedSession.IsRevoked);
            Assert.NotNull(browserRevokedSession.RevokedAtUtc);
            var sessionRevocationAuditCount = await db.CentralAuditRecords.AsNoTracking().CountAsync(record =>
                record.Action == "ADMIN_SESSION_REVOKED" && record.ResourceId == targetSessionId.ToString("D") && record.IsSuccess);
            Assert.Equal(1, sessionRevocationAuditCount);
            var persistedExpiredSession = await db.AdminSessions.AsNoTracking().SingleAsync(session => session.Id == expiredSessionId);
            Assert.False(persistedExpiredSession.IsRevoked);
            Assert.True(persistedExpiredSession.ExpiresAtUtc < DateTimeOffset.UtcNow);
            Assert.Equal(RequiredCapabilities.Order(StringComparer.Ordinal), loginSession.GetCapabilities().Order(StringComparer.Ordinal));
            var loginEvidence = new
            {
                adminUserId = user.Id,
                username,
                mfaEnabled = user.MfaEnabled,
                lastLoginAtUtc = persistedLoginUser.LastLoginAtUtc,
                challengeConsumed = consumedChallenge.IsConsumed,
                challengeConsumedAtUtc = consumedChallenge.ConsumedAtUtc,
                mfaSatisfied = loginSession.MfaSatisfied,
                sessionRevoked = loginSession.IsRevoked,
                sessionRevokedAtUtc = loginSession.RevokedAtUtc,
                targetSessionId,
                browserRevokedSession = browserRevokedSession.IsRevoked,
                browserRevokedSessionAtUtc = browserRevokedSession.RevokedAtUtc,
                browserSessionRevocationAuditCount = sessionRevocationAuditCount,
                capabilities = loginSession.GetCapabilities().Order(StringComparer.Ordinal).ToArray()
            };

            var persistedTicketCount = await db.SupportTickets.CountAsync();
            Assert.Equal(initialQueueIds.Length + 1, persistedTicketCount);
            await WriteJsonAsync(Path.Combine(evidenceDirectory, "db-after.json"), new
            {
                runId,
                capturedAtUtc = DateTimeOffset.UtcNow,
                supportTicketCount = persistedTicketCount,
                hardwareDecision = new { persistedHardware.Id, persistedHardware.Status, persistedHardware.ContractDecision,
                    persistedHardware.VerificationEvidenceId, persistedHardware.ReviewedByAdminId, persistedHardware.RowVersion },
                transferDecision = new { persistedTransfer.Id, persistedTransfer.Status, persistedTransfer.ContractDecision,
                    persistedTransfer.RecoveryType, persistedTransfer.VerificationEvidenceId, persistedTransfer.RowVersion },
                approvedHardwareDecision = new { approvedHardware.Id, approvedHardware.Status, approvedHardware.ContractDecision,
                    approvedHardware.ReviewedByAdminId, approvedHardware.VerificationEvidenceId, approvedHardware.RowVersion },
                approvedTransferDecision = new { approvedTransfer.Id, approvedTransfer.Status, approvedTransfer.ContractDecision,
                    approvedTransfer.ApprovedByAdminId, approvedTransfer.VerificationEvidenceId, approvedTransfer.RowVersion },
                commercialOrderCount = await db.Orders.CountAsync(),
                pricingSnapshotCount = await db.PricingSnapshots.CountAsync(),
                dashboardMetricsFromDatabase = new
                {
                    lifetimeBaseCount = await db.CommercialLicenses.CountAsync(license =>
                        license.TermKind == CommercialTerm.Lifetime && license.Status == CommercialLicense.StatusActive),
                    fixedTermBaseCount = await db.CommercialLicenses.CountAsync(license =>
                        license.TermKind == CommercialTerm.FixedTerm && license.Status == CommercialLicense.StatusActive),
                    expiringAddonsCount = await db.CommercialEntitlements.CountAsync(entitlement =>
                        entitlement.Status == CommercialEntitlement.StatusActive && entitlement.ValidUntilUtc != null &&
                        entitlement.ValidUntilUtc > DateTimeOffset.UtcNow && entitlement.ValidUntilUtc <= DateTimeOffset.UtcNow.AddDays(30)),
                    expiringFixedTermBasesCount = await db.CommercialLicenses.CountAsync(license =>
                        license.TermKind == CommercialTerm.FixedTerm && license.Status == CommercialLicense.StatusActive &&
                        license.ValidUntilUtc != null && license.ValidUntilUtc > DateTimeOffset.UtcNow &&
                        license.ValidUntilUtc <= DateTimeOffset.UtcNow.AddDays(30))
                },
                commercialOrder = new
                {
                    persistedOrder.Id,
                    persistedOrder.ShopId,
                    persistedOrder.CustomerId,
                    persistedOrder.PlanId,
                    persistedOrder.SnapshotId,
                    persistedOrder.OrderType,
                    persistedOrder.TotalAmount,
                    persistedSnapshot.AuthorityVersion
                },
                authentication = loginEvidence,
                attachment = new
                {
                    persistedAttachment.Id,
                    persistedAttachment.ShopId,
                    persistedAttachment.Purpose,
                    persistedAttachment.ContentLength,
                    persistedAttachment.CiphertextSha256,
                    ticketLinked = afterTicket.AttachmentObjectIdsJson.Contains(attachmentObjectId.ToString("D"), StringComparison.OrdinalIgnoreCase),
                    crossShopCreateDenied = browserResult.RootElement.GetProperty("crossShopCreateDenied").GetBoolean(),
                    unlinkedDownloadDenied = browserResult.RootElement.GetProperty("unlinkedDownloadDenied").GetBoolean(),
                    downloadedCiphertextSha256 = browserResult.RootElement.GetProperty("downloadedCiphertextSha256").GetString()
                },
                seededQueueTicketIds = initialQueueIds,
                createdTicket = new
                {
                    id = afterTicket.Id,
                    afterTicket.ShopId,
                    afterTicket.Subject,
                    afterTicket.Status,
                    afterTicket.RowVersion,
                    afterTicket.UpdatedAtUtc,
                    afterTicket.RepliesJson
                },
                operationRecords,
                commercialOperationRecords = commercialOperationRecords.Select(record => new
                {
                    record.OperationType,
                    record.State,
                    record.ResultReference,
                    record.ShopId,
                    record.CreatedAtUtc
                })
            });

            await WriteJsonAsync(manifestPath, new
            {
                schema = "CENTRAL_PORTAL_BROWSER_E2E_V1",
                runId,
                status = "PASS",
                startedAtUtc = createdAt,
                completedAtUtc = DateTimeOffset.UtcNow,
                evidenceDirectory,
                browser = "Playwright Python / Chromium",
                transport = "loopback HttpListener -> WebApplicationFactory TestServer handler",
                database = manifest.database,
                seeded = manifest.seeded,
                createdTicketId,
                assertions = new[]
                {
                    "server-validated cookie session and exact capabilities",
                    "queue pages 1 and 2 match separately seeded persisted IDs",
                    "authenticated portal creates customer and shop, searches the persisted shop, and opens its Customer 360 record",
                    "nonzero lifetime, fixed-term, expiring add-on and expiring base counts agree across database, API and rendered dashboard",
                    "browser-loaded server catalog and persisted a server-priced snapshot and order",
                    "commercial order is scoped to the persisted shop/customer/plan with operation ledger, audit and outbox evidence",
                    "browser-created support ticket is persisted for that shop",
                    "staff reply is persisted with authenticated admin identity",
                    "ticket and reply remain visible after a full browser reload",
                    "support.create/support.reply operation outcomes persisted",
                    "security.manage admin sees live worker observation through notifications.view authorization",
                    "authenticated recovery browser shows degraded worker after the persisted retry threshold dead-letters a recognized event with an invalid aggregate target",
                    "dead-letter has no consumer receipt or admin notification and the health response is no-store",
                    "recovery UI distinguishes worker health from backup/restore attestation",
                    "expired persisted browser session is denied protected portal read and customer mutation without a customer row",
                    "real password login, MFA challenge consumption, secure session cookie, identity display and logout revocation",
                    "browser console/page errors and Central API network results captured"
                },
                evidenceFiles = manifest.evidenceFiles
            });
        }
        catch (Exception exception)
        {
            await WriteJsonAsync(manifestPath, new
            {
                schema = "CENTRAL_PORTAL_BROWSER_E2E_V1",
                runId,
                status = "FAIL",
                startedAtUtc = createdAt,
                failedAtUtc = DateTimeOffset.UtcNow,
                evidenceDirectory,
                failure = exception.ToString(),
                database = manifest.database,
                seeded = manifest.seeded
            });
            throw new InvalidOperationException($"SFR-11 browser acceptance failed; inspect evidence at {evidenceDirectory}.", exception);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString) => new BrowserPortalFactory(connectionString);

    private static void ValidateOwnedDatabaseConfiguration(string connectionString)
    {
        var required = new[]
        {
            "CENTRAL_PG_HOST", "CENTRAL_PG_PORT", "CENTRAL_PG_DATABASE", "CENTRAL_PG_ROLE",
            "CENTRAL_PG_MIGRATION_ROLE", "CENTRAL_PG_PID", "CENTRAL_PG_BINARY",
            "CENTRAL_PG_OWNERSHIP_MARKER", "CENTRAL_PG_DATA_DIR"
        };
        foreach (var name in required)
        {
            _ = Required(name);
        }
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        Assert.Equal("127.0.0.1", Required("CENTRAL_PG_HOST"));
        Assert.Equal(int.Parse(Required("CENTRAL_PG_PORT")), builder.Port);
        Assert.Equal(Required("CENTRAL_PG_DATABASE"), builder.Database);
        Assert.Equal(Required("CENTRAL_PG_ROLE"), builder.Username);
        Assert.NotEqual(5432, builder.Port);
        Assert.NotEqual(7150, builder.Port);
        Assert.False(string.Equals("postgres", builder.Database, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Required("CENTRAL_PG_OWNERSHIP_MARKER")));
    }

    private static void ValidateRuntimeConfiguration()
    {
        var required = new[]
        {
            "CENTRAL_SIGNER_KEY_STORE_ROOT", "CENTRAL_OBJECT_STORAGE_ROOT",
            "CENTRAL_OUTBOX_ENABLED", "CENTRAL_OUTBOX_BATCH_SIZE", "CENTRAL_OUTBOX_MAX_ATTEMPTS",
            "CENTRAL_OUTBOX_POLL_MS", "CENTRAL_OUTBOX_LEASE_SECONDS", "CENTRAL_OUTBOX_RETRY_BASE_MS",
            "CENTRAL_OUTBOX_RETRY_MAX_MS", "CENTRAL_STORAGE_RECONCILIATION_ENABLED",
            "CENTRAL_STORAGE_RECONCILIATION_BATCH_SIZE", "CENTRAL_STORAGE_RECONCILIATION_POLL_MS",
            "CENTRAL_STORAGE_PENDING_GRACE_SECONDS", "CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS",
            "CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS"
        };
        foreach (var name in required)
        {
            _ = Required(name);
        }
        Assert.True(Directory.Exists(Required("CENTRAL_SIGNER_KEY_STORE_ROOT")));
        Assert.True(Directory.Exists(Required("CENTRAL_OBJECT_STORAGE_ROOT")));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"SFR-11 requires explicit owner-provisioned {name}; no fallback is permitted.");

    private static string RequiredPath(string name)
    {
        var value = Path.GetFullPath(Required(name));
        Directory.CreateDirectory(value);
        return value;
    }

    private static async Task WriteJsonAsync(string path, object value) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    private static async Task<(int ExitCode, string Output)> RunPythonAsync(string python, string script, string fixture, string evidence,
        IReadOnlyDictionary<string, string> childEnvironment)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.ArgumentList.Add(fixture);
        process.StartInfo.ArgumentList.Add(evidence);
        foreach (var (name, value) in childEnvironment)
        {
            process.StartInfo.Environment[name] = value;
        }
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private sealed class BrowserPortalFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:CentralDatabase", connectionString);
        }
    }

    private sealed class TestServerLoopbackProxy : IAsyncDisposable
    {
        private readonly WebApplication _proxy;
        private readonly HttpClient _testServerClient;
        private readonly X509Certificate2 _certificate;
        private readonly List<object> _events;

        private TestServerLoopbackProxy(WebApplication proxy, HttpClient client, X509Certificate2 certificate, string baseUrl, List<object> events)
        {
            _proxy = proxy;
            _testServerClient = client;
            _certificate = certificate;
            BaseUrl = baseUrl;
            _events = events;
        }

        public string BaseUrl { get; }
        public IReadOnlyList<object> Events => _events.ToArray();

        public static async Task<TestServerLoopbackProxy> StartAsync(WebApplicationFactory<Program> factory)
        {
            var certificate = CreateLoopbackCertificate();
            var client = new HttpClient(factory.Server.CreateHandler());
            var port = ReserveLoopbackPort();
            var events = new List<object>();
            var eventsGate = new object();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
            var proxy = builder.Build();
            proxy.Run(async context =>
            {
                var incoming = context.Request;
                var targetUri = new Uri($"https://127.0.0.1:{port}{incoming.Path}{incoming.QueryString}");
                using var forwarded = new HttpRequestMessage(new HttpMethod(incoming.Method), targetUri);
                if (incoming.ContentLength is > 0)
                {
                    using var body = new MemoryStream();
                    await incoming.Body.CopyToAsync(body, context.RequestAborted);
                    forwarded.Content = new ByteArrayContent(body.ToArray());
                }
                foreach (var header in incoming.Headers)
                {
                    if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    {
                        forwarded.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }
                }
                try
                {
                    using var response = await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    context.Response.StatusCode = (int)response.StatusCode;
                    foreach (var header in response.Headers)
                    {
                        CopyResponseHeader(context.Response, header.Key, header.Value);
                    }
                    foreach (var header in response.Content.Headers)
                    {
                        CopyResponseHeader(context.Response, header.Key, header.Value);
                    }
                    var bytes = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
                    context.Response.ContentLength = bytes.Length;
                    if (bytes.Length > 0)
                    {
                        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                    }
                    string? safeErrorCode = null;
                    string? errorRequestId = null;
                    if ((int)response.StatusCode >= 500 &&
                        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var errorBody = JsonDocument.Parse(bytes);
                            safeErrorCode = errorBody.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
                            errorRequestId = errorBody.RootElement.TryGetProperty("requestId", out var requestId) ? requestId.GetString() : null;
                        }
                        catch (JsonException)
                        {
                            safeErrorCode = "UNPARSEABLE_SERVER_ERROR";
                        }
                    }
                    lock (eventsGate)
                    {
                        events.Add(new { incoming.Method, Url = targetUri.ToString(), response.StatusCode, response.Content.Headers.ContentType?.MediaType, safeErrorCode, errorRequestId });
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lock (eventsGate)
                    {
                        events.Add(new { incoming.Method, Url = targetUri.ToString(), error = exception.GetType().Name });
                    }
                    context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                    await context.Response.WriteAsync("TestServer proxy failed.", context.RequestAborted);
                }
            });
            await proxy.StartAsync();
            var addresses = proxy.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.SingleOrDefault(value => value.StartsWith("https://127.0.0.1:", StringComparison.OrdinalIgnoreCase));
            var baseUrl = address is null ? $"https://127.0.0.1:{port}/" : address.TrimEnd('/') + "/";
            return new TestServerLoopbackProxy(proxy, client, certificate, baseUrl, events);
        }

        private static int ReserveLoopbackPort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        private static X509Certificate2 CreateLoopbackCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Central SFR11 loopback test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
        }

        private static void CopyResponseHeader(HttpResponse response, string name, IEnumerable<string> values)
        {
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            foreach (var value in values)
            {
                response.Headers.Append(name, value);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _proxy.StopAsync();
            await _proxy.DisposeAsync();
            _testServerClient.Dispose();
            _certificate.Dispose();
        }
    }

    private const string BrowserScript = """
import asyncio, base64, hashlib, hmac, json, os, pathlib, struct, sys, time, traceback, uuid, zipfile, tempfile, re, urllib.parse
from datetime import datetime, timedelta
from collections import Counter
from playwright.async_api import async_playwright

fixture_path = pathlib.Path(sys.argv[1])
evidence = pathlib.Path(sys.argv[2])
fixture = json.loads(fixture_path.read_text(encoding='utf-8'))
auth_username = os.environ['CENTRAL_BROWSER_TEST_USERNAME']
auth_password = os.environ['CENTRAL_BROWSER_TEST_PASSWORD']
auth_totp = os.environ['CENTRAL_BROWSER_TEST_TOTP']
auth_mfa_secret = os.environ['CENTRAL_BROWSER_TEST_MFA_SECRET']
approver_username = os.environ['CENTRAL_BROWSER_LIFECYCLE_APPROVER_USERNAME']
approver_password = os.environ['CENTRAL_BROWSER_LIFECYCLE_APPROVER_PASSWORD']
approver_mfa_secret = os.environ['CENTRAL_BROWSER_LIFECYCLE_APPROVER_MFA_SECRET']
secrets_to_redact = [auth_username, auth_password, auth_totp, fixture['expiredSessionId'],
    auth_mfa_secret, approver_username, approver_password, approver_mfa_secret,
    os.environ.get('CENTRAL_BROWSER_TEST_INSTALLATION_PKCS8', ''), 'intentionally-invalid-password']

def current_totp(secret):
    key = base64.b32decode(secret.upper() + '=' * ((8 - len(secret) % 8) % 8))
    counter = int(time.time() // 30)
    digest = hmac.new(key, struct.pack('>Q', counter), hashlib.sha1).digest()
    offset = digest[-1] & 0x0f
    value = int.from_bytes(digest[offset:offset + 4], 'big') & 0x7fffffff
    return f'{value % 1000000:06d}'
events = {"consoleErrors": [], "pageErrors": [], "requests": [], "responses": []}
result_path = evidence / "browser-result.json"
screenshots = evidence / "screenshots"
screenshots.mkdir(parents=True, exist_ok=True)
async def capture_screenshot(page, path, **kwargs):
    target = pathlib.Path(path)
    for attempt in range(2):
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            await page.screenshot(path=str(target), **kwargs)
            return
        except FileNotFoundError:
            if attempt == 1:
                raise

def redact_trace():
    trace_path = evidence / 'portal-trace.zip'
    if not trace_path.exists():
        return
    temporary = trace_path.with_suffix('.redacted.zip')
    with zipfile.ZipFile(trace_path, 'r') as source, zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED) as target:
        for item in source.infolist():
            data = source.read(item.filename)
            for secret in secrets_to_redact:
                if secret:
                    data = data.replace(secret.encode('utf-8'), b'[REDACTED]')
            target.writestr(item, data)
    temporary.replace(trace_path)
    with zipfile.ZipFile(trace_path, 'r') as sanitized:
        for item in sanitized.infolist():
            data = sanitized.read(item.filename)
            for secret in secrets_to_redact:
                assert not secret.encode('utf-8') in data, 'Sensitive authentication value remained in browser trace.'

async def main():
    browser = None
    context = None
    page = None
    ticket_id = None
    cross_origin_mutation_marker = f"SFR15 cross-origin mutation {uuid.uuid4()}"
    subject = f"{fixture['ticketSubjectPrefix']} {uuid.uuid4()}"
    reply_text = fixture['replyText']
    try:
        async with async_playwright() as p:
            launch_options = {"headless": True}
            chromium_executable = os.environ.get("CENTRAL_PLAYWRIGHT_CHROMIUM_EXECUTABLE")
            if chromium_executable:
                assert pathlib.Path(chromium_executable).is_file(), f"Configured Chromium executable not found: {chromium_executable}"
                launch_options["executable_path"] = chromium_executable
            browser = await p.chromium.launch(**launch_options)
            expired_context = await browser.new_context(viewport={"width": 1440, "height": 1000}, ignore_https_errors=True)
            await expired_context.add_cookies([{
                "name": "central_session", "value": fixture['expiredSessionId'],
                "url": fixture['baseUrl'], "httpOnly": True, "secure": True, "sameSite": "Strict"
            }])
            expired_page = await expired_context.new_page()
            await expired_page.goto(fixture['baseUrl'], wait_until='domcontentloaded')
            await expired_page.locator('#auth-wrapper').wait_for(state='visible')
            assert await expired_page.locator('#app-shell').is_hidden(), 'An expired persisted session displayed protected portal content.'
            expired_session_read = await expired_page.evaluate(r'''async () => {
                const response = await fetch('/api/admin/v1/portal/session', { credentials: 'include' });
                return { status: response.status };
            }''')
            expired_session_mutation = await expired_page.evaluate(r'''async (marker) => {
                const response = await fetch('/api/admin/v1/customers', {
                    method: 'POST', credentials: 'include', headers: { 'content-type': 'application/json' },
                    body: JSON.stringify({ commandMeta: {
                        clientOperationId: crypto.randomUUID(), operationType: 'directory.customer.create',
                        fingerprintVersion: 1, payloadSha256: '0'.repeat(64), expectedVersion: 0,
                        policyVersion: 0, reason: 'Synthetic expired-session denial'
                    }, legalName: marker, contactEmail: 'expired-session@example.invalid', contactPhone: '' })
                });
                return { status: response.status };
            }''', fixture['expiredSessionMutationMarker'])
            assert expired_session_read['status'] == 401, f"Expired session read was not denied: {expired_session_read['status']}."
            assert expired_session_mutation['status'] == 401, f"Expired session mutation was not denied: {expired_session_mutation['status']}."
            await capture_screenshot(expired_page, path=str(screenshots / '24-expired-session-denied.png'), full_page=True)
            await expired_context.close()
            context = await browser.new_context(viewport={"width": 1440, "height": 1000}, ignore_https_errors=True)
            await context.add_init_script("localStorage.setItem('central_session_id', 'synthetic-stale-session-value'); window.__seededLegacySession = localStorage.getItem('central_session_id')")
            page = await context.new_page()
            page.on('console', lambda message: events['consoleErrors'].append(message.text) if message.type == 'error' else None)
            page.on('pageerror', lambda error: events['pageErrors'].append(str(error)))
            page.on('request', lambda request: events['requests'].append({"method": request.method, "url": request.url}) if '/api/admin/v1/' in request.url or '/api/commercial/v1/' in request.url or '/api/licensing/v1/uploads/' in request.url else None)
            page.on('response', lambda response: events['responses'].append({"status": response.status, "url": response.url}) if '/api/admin/v1/' in response.url or '/api/commercial/v1/' in response.url or '/api/licensing/v1/uploads/' in response.url else None)

            await page.goto(fixture['baseUrl'], wait_until='domcontentloaded')
            favicon_response = await page.evaluate(r'''async () => {
                const response = await fetch('/favicon.ico', { cache: 'no-store' });
                return {
                    status: response.status,
                    contentType: response.headers.get('content-type'),
                    body: await response.text()
                };
            }''')
            assert favicon_response['status'] == 200, f"/favicon.ico did not return HTTP 200: {favicon_response['status']}."
            assert favicon_response['contentType'] == 'image/svg+xml', f"/favicon.ico returned unexpected content type: {favicon_response['contentType']}."
            assert '<svg' in favicon_response['body'] and 'viewBox="0 0 64 64"' in favicon_response['body'] and '#55d6a6' in favicon_response['body'], 'The /favicon.ico alias did not return the branded SVG asset.'
            await context.tracing.start(screenshots=True, snapshots=True, sources=True)
            await page.locator('#auth-wrapper').wait_for(state='visible')
            await page.wait_for_function("document.querySelector('#auth-status')?.textContent?.trim() === 'Sign in to continue.'")
            await page.locator('#login-username').fill(auth_username)
            await page.locator('#login-password').fill('intentionally-invalid-password')
            await page.locator('#login-form button[type="submit"]').click()
            await page.locator('#login-error').wait_for(state='visible')
            assert await page.locator('#mfa-step').is_hidden(), 'Invalid password unexpectedly advanced to MFA.'
            await page.locator('#login-username').fill(auth_username)
            await page.locator('#login-password').fill(auth_password)
            await page.locator('#login-form button[type="submit"]').click()
            await page.locator('#mfa-step').wait_for(state='visible')
            await page.locator('#mfa-totp').fill(auth_totp)
            await page.locator('#mfa-form button[type="submit"]').click()
            await page.locator('#app-shell').wait_for(state='visible')
            assert await page.locator('#auth-wrapper').is_hidden(), 'Server rejected the completed password/MFA login.'
            assert (await page.locator('#user-display-name').inner_text()).strip() == auth_username, 'Portal identity does not match the authenticated server user.'
            assert (await page.locator('#user-avatar-text').inner_text()).strip() == auth_username[:2].upper(), 'Portal identity avatar does not match the authenticated server user.'
            session_cookie = next((cookie for cookie in await context.cookies() if cookie['name'] == 'central_session'), None)
            assert session_cookie is not None, 'MFA did not create the Central browser session cookie.'
            assert session_cookie['httpOnly'] and session_cookie['secure'] and session_cookie['sameSite'] == 'Strict', 'Session cookie security attributes were not enforced.'
            await page.evaluate(r'''() => {
                document.querySelector('#queue-inspect-modal').classList.add('active');
                document.querySelector('#hardware-review-controls').hidden = false;
                document.querySelector('#transfer-review-controls').hidden = false;
                for (const [id, value] of Object.entries({
                    'hardware-review-evidence': '11111111-1111-4111-8111-111111111111',
                    'hardware-review-approval': '22222222-2222-4222-8222-222222222222',
                    'hardware-review-reason': 'synthetic hardware decision reason',
                    'transfer-review-evidence': '33333333-3333-4333-8333-333333333333',
                    'transfer-review-approval': '44444444-4444-4444-8444-444444444444',
                    'transfer-review-reason': 'synthetic transfer decision reason'
                })) document.getElementById(id).value = value;
                document.getElementById('hardware-review-status').textContent = 'Synthetic decision status';
                document.getElementById('transfer-review-status').textContent = 'Synthetic transfer status';
            }''')
            await capture_screenshot(page, path=str(screenshots / '29-hardware-transfer-data-before-close.png'), full_page=True)
            await page.locator('#btn-close-queue-modal').click()
            cleared_review_data = await page.evaluate(r'''() => ({
                modalClosed: !document.querySelector('#queue-inspect-modal').classList.contains('active'),
                hardwareHidden: document.querySelector('#hardware-review-controls').hidden,
                transferHidden: document.querySelector('#transfer-review-controls').hidden,
                fields: ['hardware-review-evidence', 'hardware-review-approval', 'hardware-review-reason',
                    'transfer-review-evidence', 'transfer-review-approval', 'transfer-review-reason']
                    .map(id => document.getElementById(id).value),
                statuses: ['hardware-review-status', 'transfer-review-status']
                    .map(id => document.getElementById(id).textContent.trim())
            })''')
            assert cleared_review_data['modalClosed'] and cleared_review_data['hardwareHidden'] and cleared_review_data['transferHidden'], \
                f'Hardware or transfer decision controls remained visible after close: {cleared_review_data}'
            assert not any(cleared_review_data['fields']) and not any(cleared_review_data['statuses']), \
                f'Hardware or transfer decision data remained in the DOM after close: {cleared_review_data}'
            await capture_screenshot(page, path=str(screenshots / '30-hardware-transfer-data-cleared-after-close.png'), full_page=True)
            step_up = await page.evaluate(r'''async (proof) => {
                const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode('synthetic step-up target'));
                const targetPayloadSha256 = Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, '0')).join('');
                const response = await fetch('/api/admin/v1/auth/step-up', {
                    method: 'POST', credentials: 'include', headers: { 'content-type': 'application/json' },
                    body: JSON.stringify({ sessionId: '00000000-0000-0000-0000-000000000000',
                        actionScope: 'license.revoke', targetPayloadSha256, proof, challengeId: crypto.randomUUID() })
                });
                return { status: response.status, body: response.ok ? await response.json() : await response.text(), targetPayloadSha256 };
            }''', auth_totp)
            assert step_up['status'] == 200, f"Cookie-bound step-up was not accepted: {step_up['status']} {step_up['body']}"
            assert step_up['body']['actionScope'] == 'license.revoke' and step_up['body']['targetPayloadSha256'] == step_up['targetPayloadSha256'], 'Step-up grant did not preserve its exact action and payload binding.'
            assert step_up['body']['sessionId'] and step_up['body']['sessionId'] != '00000000-0000-0000-0000-000000000000', 'Step-up grant was not bound to the authenticated cookie session.'
            session_custody = await page.evaluate(r'''() => {
                const authenticationKeys = Object.keys(localStorage).filter(key => /session|token|auth|bearer/i.test(key));
                const scriptCanReadCookie = document.cookie.split(';').some(value => value.trim().startsWith('central_session='));
                return { seededLegacyValue: window.__seededLegacySession, authenticationKeys, scriptCanReadCookie };
            }''')
            assert session_custody == { 'seededLegacyValue': 'synthetic-stale-session-value', 'authenticationKeys': [], 'scriptCanReadCookie': False }, \
                f'Browser authentication state escaped the HttpOnly cookie boundary: {session_custody}'
            secrets_to_redact.append(session_cookie['value'])
            attack_page = await context.new_page()
            attack_page.on('request', lambda request: events['requests'].append({"method": request.method, "url": request.url}) if '/api/admin/v1/' in request.url or '/api/commercial/v1/' in request.url else None)
            attack_page.on('response', lambda response: events['responses'].append({"status": response.status, "url": response.url}) if '/api/admin/v1/' in response.url or '/api/commercial/v1/' in response.url else None)
            attack_html = f'''<!doctype html><form method="post" enctype="application/x-www-form-urlencoded" action="{fixture['baseUrl']}api/admin/v1/customers"><input name="legalName" value="{cross_origin_mutation_marker}"></form><script>document.forms[0].submit()</script>'''
            attack_url = 'data:text/html;charset=utf-8,' + urllib.parse.quote(attack_html, safe='')
            async with attack_page.expect_response(lambda response: response.request.method == 'POST' and '/api/admin/v1/customers' in response.url) as cross_origin_response_info:
                await attack_page.goto(attack_url, wait_until='commit')
            cross_origin_response = await cross_origin_response_info.value
            cross_origin_headers = await cross_origin_response.request.all_headers()
            cross_origin_request_origin = cross_origin_headers.get('origin', '')
            cross_origin_cookie_excluded = 'central_session=' not in cross_origin_headers.get('cookie', '')
            assert cross_origin_request_origin == 'null', f'Expected an opaque-origin form request, got Origin={cross_origin_request_origin!r}.'
            assert cross_origin_cookie_excluded, 'SameSite session cookie was attached to the opaque-origin mutation attempt.'
            assert cross_origin_response.status in (401, 403), f'Unauthenticated cross-origin mutation was not denied: {cross_origin_response.status}.'
            await attack_page.close()
            capabilities = await page.locator('#session-capabilities').inner_text()
            for capability in ['dashboard.view', 'queues.view', 'support.view', 'support.create', 'support.reply', 'auth.stepup',
                'licensing.read', 'price.snapshot', 'order.create']:
                assert capability in capabilities, f'Expected server session capability {capability} was not rendered.'
            for capability in ['security.manage', 'notifications.view']:
                assert capability in capabilities, f'Expected recovery worker capability {capability} was not rendered.'
            for capability in ['audit.view', 'audit.read', 'customer.view_sensitive']:
                assert capability in capabilities, f'Expected audited contact capability {capability} was not rendered.'
            assert 'sensitive.approve' in capabilities, 'The browser approver did not receive the server-assigned sensitive.approve capability.'

            await page.wait_for_function("document.querySelector('#metrics-status')?.textContent?.includes('not a health attestation')")
            initial_dashboard_api = await page.evaluate(r'''async () => {
                const response = await fetch('/api/admin/v1/dashboard/metrics', { credentials: 'include' });
                return { status: response.status, body: await response.json() };
            }''')
            assert initial_dashboard_api['status'] == 200 and initial_dashboard_api['body']['pendingApprovalsCount'] == 1, \
                'The persisted pending approval was not reflected in the initial dashboard API metric.'
            assert int((await page.locator('#metric-pending-approvals').inner_text()).replace(',', '').strip()) == 1, \
                'The initial rendered dashboard did not show the persisted pending approval.'

            for capability in ['sessions.view', 'sessions.revoke']:
                assert capability in capabilities, f'Expected session-management capability {capability} was not rendered.'

            await page.locator('[data-view="sessions"]').click()
            target_revoke = page.locator(f'[data-session-revoke="{fixture["targetSessionId"]}"]')
            await target_revoke.wait_for(state='visible')
            await target_revoke.click()
            revoke_dialog = page.locator('#sessions-revoke-dialog')
            await revoke_dialog.wait_for(state='visible')
            assert fixture['targetSessionId'] in await page.locator('#sessions-revoke-target').inner_text(), \
                'The confirmation dialog did not identify the selected persisted session.'
            await page.locator('#sessions-revoke-cancel').click()
            await revoke_dialog.wait_for(state='hidden')
            assert not any('/api/admin/v1/portal/sessions/revoke' in response['url'] for response in events['responses']), \
                'Cancelling session revocation unexpectedly submitted a server mutation.'
            await page.locator(f'[data-session-revoke="{fixture["targetSessionId"]}"]').click()
            await revoke_dialog.wait_for(state='visible')
            await page.locator('#sessions-revoke-confirm').click()
            await revoke_dialog.wait_for(state='hidden')
            revoked_rows = page.locator('#sessions-table-tbody tr').filter(has_text='Revoked')
            await revoked_rows.first.wait_for()
            assert await page.locator(f'[data-session-revoke="{fixture["targetSessionId"]}"]').count() == 0, \
                'Revoked session still offered an active revoke action.'
            assert any('/api/admin/v1/portal/sessions/revoke' in response['url'] and response['status'] == 200
                for response in events['responses']), 'Central did not confirm the browser-triggered session revocation.'
            await capture_screenshot(page, path=str(screenshots / '19-admin-session-revocation.png'), full_page=True)

            await page.locator('[data-view="approvals"]').click()
            approval_row = page.locator('#approvals-table-tbody tr').filter(has_text=fixture['pendingApprovalId'])
            await approval_row.wait_for()
            approval_text = await approval_row.inner_text()
            assert fixture['pendingApprovalOperationType'] in approval_text and 'PENDING' in approval_text, \
                f'Pending approval was not visible with its persisted status: {approval_text}'
            await capture_screenshot(page, path=str(screenshots / '15-pending-approval-queue.png'), full_page=True)
            await approval_row.get_by_role('button', name='Review approval').click()
            approval_modal = page.locator('#queue-inspect-modal')
            await approval_modal.wait_for(state='visible')
            assert await page.locator('#sensitive-approval-controls').is_visible(), \
                'The authenticated independent approver did not receive the persisted approval decision form.'
            await page.locator('#sensitive-approval-decision').select_option('APPROVE')
            await page.locator('#sensitive-approval-reason').fill('Synthetic browser independent approval review')
            await page.locator('#sensitive-approval-submit').click()
            await page.wait_for_function("document.querySelector('#approvals-table-tbody')?.textContent?.includes('No Dual approvals items')")
            approval_status = await page.locator('#approvals-queue-status').inner_text()
            assert 'The approved action has not been executed.' in approval_status, \
                f'The portal did not report the persisted decision and remaining execution step: {approval_status}'
            assert any('/api/admin/v1/sensitive-approvals/' + fixture['pendingApprovalId'] + '/decide' in response['url']
                and response['status'] == 200 for response in events['responses']), \
                'Central did not confirm the browser-triggered independent approval decision.'
            await capture_screenshot(page, path=str(screenshots / '21-sensitive-approval-persisted.png'), full_page=True)

            await page.locator('[data-view="licensing"]').click()
            await page.wait_for_function("document.querySelector('#licensing-queue-status')?.textContent?.includes('1 records')")
            licensing_headers = await page.locator('#licensing-activations-tbody').evaluate("tbody => Array.from(tbody.closest('table').querySelectorAll('thead th')).map(cell => cell.textContent.trim())")
            assert licensing_headers == ['Case Type', 'Case ID', 'Summary', 'Customer', 'Shop', 'Current State', 'Priority', 'Opened At (UTC)', 'Age', 'Assigned Admin', 'Risk / Attention Marker', 'Action'], \
                f'Licensing queue common-field headers mismatch: {licensing_headers}'
            licensing_first_row = await page.locator('#licensing-activations-tbody tr').first.locator('td').all_inner_texts()
            assert len(licensing_first_row) == 12 and all(value.strip() for value in licensing_first_row), \
                f'Licensing queue common fields are incomplete: {licensing_first_row}'
            assert licensing_first_row[6] == 'Not set' and licensing_first_row[9] == 'Unassigned' and licensing_first_row[10] == 'No marker', \
                f'Licensing queue lacks explicit unconfigured-field values: {licensing_first_row}'
            activation_rows = page.locator('#licensing-activations-tbody tr').filter(has_text='PENDING')
            await activation_rows.first.wait_for(state='visible')
            await activation_rows.first.get_by_role('button', name='Review activation').click()
            review_modal = page.locator('#queue-inspect-modal')
            await review_modal.wait_for(state='visible')
            assert 'active' in (await review_modal.get_attribute('class') or ''), 'Activation review details did not open.'
            assert fixture['activationRequestId'] in await page.locator('#queue-item-entity').inner_text(), \
                'The activation review dialog did not bind to the persisted request.'
            assert await page.locator('#activation-review-controls').is_visible(), \
                'The authenticated activation reviewer did not receive the supported decision form.'
            await page.locator('#activation-review-decision').select_option('REQUEST_INFORMATION')
            await page.locator('#activation-review-reason').fill(fixture['activationReasonCode'])
            await page.locator('#activation-review-submit').click()
            await page.wait_for_function("document.querySelector('#licensing-activations-tbody')?.textContent?.includes('No Activation reviews items')")
            assert any('/api/admin/v1/activations/' + fixture['activationRequestId'] + '/review' in response['url']
                and response['status'] == 200 for response in events['responses']), \
                'Central did not confirm the browser-triggered activation decision.'
            activation_rows_after_decision = page.locator('#licensing-activations-tbody tr').filter(has_text='PENDING')
            assert await activation_rows_after_decision.count() == 0, \
                'The decided activation remained in the pending review queue.'
            await capture_screenshot(page, path=str(screenshots / '20-activation-review-persisted.png'), full_page=True)

            await page.locator('[data-view="audit"]').click()
            audit_row = page.locator('#audit-tbody tr').filter(has_text=fixture['sensitiveAuditRecordId'])
            await audit_row.wait_for()
            await audit_row.get_by_role('button', name='Request access').click()
            await page.locator('#pii-unmask-modal').wait_for(state='visible')
            await page.locator('#pii-unmask-reason').fill('Synthetic browser reason: audited contact review')
            await page.locator('#btn-submit-pii-unmask').click()
            await page.locator('#pii-unmask-result').get_by_text(fixture['sensitiveCustomerEmail'], exact=True).wait_for()
            pii_result_text = await page.locator('#pii-unmask-result').inner_text()
            assert fixture['sensitiveCustomerName'] in pii_result_text, \
                'The contact name was not returned as literal text by the audited reveal.'
            assert fixture['sensitiveCustomerEmail'] in pii_result_text, 'The synthetic contact email was not revealed.'
            assert await page.locator('#pii-unmask-result img').count() == 0, 'Contact content created an executable image element.'
            assert await page.evaluate('window.__piiInjected === true') is False, 'Untrusted contact text executed an injected handler.'
            await capture_screenshot(page, path=str(screenshots / '16-audited-pii-reveal.png'), full_page=True)
            await page.locator('#btn-close-pii-modal').click()
            await page.locator('#pii-unmask-modal').wait_for(state='hidden')
            cleared_result = await page.locator('#pii-unmask-result').evaluate("element => element.childElementCount === 0 && element.textContent === ''")
            page_text_after_close = await page.locator('body').inner_text()
            pii_cleared_on_close = cleared_result and fixture['sensitiveCustomerEmail'] not in page_text_after_close
            assert pii_cleared_on_close, 'Closing the reveal dialog retained sensitive content in the result or rendered page.'
            await capture_screenshot(page, path=str(screenshots / '17-pii-reveal-cleared.png'), full_page=True)
            await audit_row.get_by_role('button', name='Request access').click()
            await page.locator('#pii-unmask-reason').fill('Synthetic browser reason: navigation-clear review')
            await page.locator('#btn-submit-pii-unmask').click()
            await page.locator('#pii-unmask-result').get_by_text(fixture['sensitiveCustomerEmail'], exact=True).wait_for()
            # Dispatch the portal navigation event while the dialog's modal overlay intentionally blocks pointer clicks.
            await page.locator('[data-view="dashboard"]').evaluate("element => element.click()")
            await page.locator('#pii-unmask-modal').wait_for(state='hidden')
            navigation_result_cleared = await page.locator('#pii-unmask-result').evaluate("element => element.childElementCount === 0 && element.textContent === ''")
            navigation_body_text = await page.locator('body').inner_text()
            pii_cleared_on_navigation = navigation_result_cleared and fixture['sensitiveCustomerEmail'] not in navigation_body_text
            assert pii_cleared_on_navigation, 'Navigating away retained sensitive PII in the result or rendered page.'
            await capture_screenshot(page, path=str(screenshots / '18-pii-reveal-cleared-on-navigation.png'), full_page=True)
            await page.wait_for_function("document.querySelector('#metrics-status')?.textContent?.includes('not a health attestation')")
            dashboard_api = await page.evaluate(r'''async () => {
                const response = await fetch('/api/admin/v1/dashboard/metrics', { credentials: 'include' });
                return { status: response.status, body: await response.json() };
            }''')
            assert dashboard_api['status'] == 200, f'Dashboard metrics API returned {dashboard_api["status"]}.'
            metric_fields = {
                'activeShopsCount': '#metric-active-shops',
                'lifetimeBaseCount': '#metric-lifetime-bases',
                'fixedTermBaseCount': '#metric-fixed-term-bases',
                'expiringAddonsCount': '#metric-expiring-addons',
                'expiringFixedTermBasesCount': '#metric-expiring-fixed-bases',
                'pendingApprovalsCount': '#metric-pending-approvals'
            }
            rendered_metrics = {}
            for field, selector in metric_fields.items():
                rendered = int((await page.locator(selector).inner_text()).replace(',', '').strip())
                assert rendered == dashboard_api['body'][field], f'Portal metric {field} diverged from the Central API.'
                rendered_metrics[field] = rendered
            expected_metrics = {'activeShopsCount': 2, 'lifetimeBaseCount': 1, 'fixedTermBaseCount': 1,
                'expiringAddonsCount': 1, 'expiringFixedTermBasesCount': 1}
            expected_metrics['pendingApprovalsCount'] = 0
            for field, expected in expected_metrics.items():
                assert dashboard_api['body'][field] == expected, f'Expected synthetic fixture metric {field}={expected}; got {dashboard_api["body"][field]}.'
            await capture_screenshot(page, path=str(screenshots / '01-authenticated-dashboard.png'), full_page=True)
            await capture_screenshot(page, path=str(screenshots / '09-login-mfa-session.png'), full_page=True)

            await page.locator('[data-queue="SUPPORT_INBOX"]').click()
            await page.wait_for_function("document.querySelector('#dashboard-queue-status')?.textContent?.includes('records in this snapshot; page 1.')")
            page_one = await page.locator('#dashboard-queue-tbody tr').evaluate_all("rows => rows.map(row => row.cells[1]?.textContent?.trim()).filter(Boolean)")
            assert page_one == fixture['queueIdsPageOne'], f'Queue page 1 mismatch: {page_one}'
            await capture_screenshot(page, path=str(screenshots / '02-queue-page-1.png'), full_page=True)
            await page.locator('#dashboard-queue-next').click()
            await page.wait_for_function("document.querySelector('#dashboard-queue-status')?.textContent?.includes('records in this snapshot; page 2.')")
            page_two = await page.locator('#dashboard-queue-tbody tr').evaluate_all("rows => rows.map(row => row.cells[1]?.textContent?.trim()).filter(Boolean)")
            assert page_two == fixture['queueIdsPageTwo'], f'Queue page 2 mismatch: {page_two}'
            await capture_screenshot(page, path=str(screenshots / '03-queue-page-2.png'), full_page=True)

            await page.locator('[data-view="customer360"]').click()
            await page.locator('#customer-display-name').fill(fixture['directoryCustomerName'])
            await page.locator('#customer-create-reason').fill('Synthetic browser contract fixture for directory workflow.')
            await page.locator('#customer-create-form button[type="submit"]').click()
            await page.wait_for_function("document.querySelector('#customer-create-status')?.textContent?.startsWith('Customer created. ID:')")
            customer_create_status = await page.locator('#customer-create-status').inner_text()
            created_customer = re.search(r'Customer created\. ID: ([0-9a-f-]{36})', customer_create_status, re.I)
            assert created_customer, f'Customer creation did not return the persisted identity: {customer_create_status}'
            created_customer_id = created_customer.group(1)
            await capture_screenshot(page, path=str(screenshots / '12-customer-created.png'), full_page=True)

            await page.locator('#shop-customer-id').wait_for()
            assert (await page.locator('#shop-customer-id').input_value()) == created_customer_id, 'Shop form was not bound to the customer returned by Central.'
            await page.locator('#shop-display-name').fill(fixture['directoryShopName'])
            await page.locator('#shop-create-reason').fill('Synthetic browser contract fixture for directory workflow.')
            await page.locator('#shop-create-form button[type="submit"]').click()
            await page.wait_for_function("document.querySelector('#shop-create-status')?.textContent?.startsWith('Shop registered. ID:')")
            shop_create_status = await page.locator('#shop-create-status').inner_text()
            created_shop = re.search(r'Shop registered\. ID: ([0-9a-f-]{36})', shop_create_status, re.I)
            assert created_shop, f'Shop registration did not return the persisted identity: {shop_create_status}'
            created_shop_id = created_shop.group(1)
            await capture_screenshot(page, path=str(screenshots / '13-shop-registered.png'), full_page=True)

            await page.locator('#directory-filter').fill(fixture['directoryShopName'])
            await page.locator('#directory-search-form button[type="submit"]').click()
            await page.locator('#directory-results').get_by_text(fixture['directoryShopName'], exact=True).wait_for()
            directory_results = await page.locator('#directory-results').inner_text()
            assert fixture['directoryCustomerName'] not in directory_results, 'Masked directory search exposed the unmasked customer name.'
            await capture_screenshot(page, path=str(screenshots / '14-masked-directory-search.png'), full_page=True)
            await page.locator('#directory-results').get_by_role('button', name='Open Customer 360').click()
            await page.locator('#customer-360-details').get_by_text(fixture['directoryShopName'], exact=True).wait_for()
            customer360_details = await page.locator('#customer-360-details').inner_text()
            assert fixture['directoryCustomerName'] not in customer360_details, 'Customer 360 exposed the unmasked customer name.'

            await capture_screenshot(page, path=str(screenshots / '04-customer-360.png'), full_page=True)

            await page.locator('#customer-search-input').fill(fixture['shopId'])
            await page.locator('#btn-customer-search').click()
            await page.locator('#customer-360-details').get_by_text(fixture['browserUploadLicenseId'], exact=True).wait_for()
            lifecycle_key = f"central_license_lifecycle:{fixture['adminUserId']}:{fixture['browserUploadLicenseId']}"
            await page.evaluate("(args) => { const [key, fixture] = args; sessionStorage.setItem(key, JSON.stringify({ licenseId: fixture.browserUploadLicenseId, shopId: fixture.shopId, capabilityPolicyId: fixture.lifecycleCapabilityPolicyId, resourceVersion: fixture.browserUploadLicenseVersion, authorityRevision: fixture.browserUploadLicenseAuthorityRevision, policyVersion: 1, effectiveAtUtc: new Date().toISOString(), payloadSha256: 'a'.repeat(64), requestGrantId: crypto.randomUUID(), requestGrantExpiresAtUtc: new Date(Date.now() - 60000).toISOString(), approvalRequestId: null, approvalExpiresAtUtc: null })); }", [lifecycle_key, fixture])
            await page.locator(f"[data-license-id='{fixture['browserUploadLicenseId']}']").get_by_role('button', name='Review revocation impact').click()
            await page.wait_for_function("document.querySelector('#license-lifecycle-preview-status')?.textContent?.includes('validated the current persisted target')")
            assert 'previous step-up grant expired' in (await page.locator('#license-lifecycle-request-status').inner_text()).lower(), 'Expired step-up grant did not request fresh MFA.'
            expired_grant_state = await page.evaluate("(key) => JSON.parse(sessionStorage.getItem(key))", lifecycle_key)
            assert expired_grant_state['requestGrantId'] is None and expired_grant_state['requestGrantExpiresAtUtc'] is None, 'Expired step-up grant was retained instead of recovered.'
            await page.locator('#license-lifecycle-close').click()
            await page.evaluate("(args) => { const [key, fixture] = args; sessionStorage.setItem(key, JSON.stringify({ licenseId: fixture.browserUploadLicenseId, shopId: fixture.shopId, capabilityPolicyId: fixture.lifecycleCapabilityPolicyId, resourceVersion: fixture.browserUploadLicenseVersion, authorityRevision: fixture.browserUploadLicenseAuthorityRevision, policyVersion: 1, effectiveAtUtc: new Date().toISOString(), payloadSha256: 'a'.repeat(64), requestGrantId: null, requestGrantExpiresAtUtc: null, approvalRequestId: fixture.expiredLifecycleApprovalId, approvalExpiresAtUtc: new Date(Date.now() - 60000).toISOString() })); }", [lifecycle_key, fixture])
            lifecycle_responses_before = len(events['responses'])
            await page.locator(f"[data-license-id='{fixture['browserUploadLicenseId']}']").get_by_role('button', name='Review revocation impact').click()
            await page.wait_for_function("document.querySelector('#license-lifecycle-preview-status')?.textContent?.includes('validated the current persisted target')")
            assert await page.locator('#license-lifecycle-request-section').is_visible(), 'Expired approval did not offer a new independent approval request after fresh preview.'
            assert not await page.locator('#license-lifecycle-final-section').is_visible(), 'Expired approval remained executable.'
            assert 'previous approval expired' in (await page.locator('#license-lifecycle-request-status').inner_text()).lower(), 'Expired approval recovery state was not explained.'
            lifecycle_responses = events['responses'][lifecycle_responses_before:]
            assert any('/api/admin/v1/licenses/' in response['url'] and response['status'] == 200 for response in lifecycle_responses), 'Lifecycle flow did not read persisted license detail.'
            assert any('/api/admin/v1/action-preview' in response['url'] and response['status'] == 200 for response in lifecycle_responses), 'Lifecycle flow did not obtain the server consequence preview.'
            assert not any(response['url'].endswith('/revoke') for response in lifecycle_responses), 'Preview or expired-approval recovery submitted a license mutation.'
            await capture_screenshot(page, path=str(screenshots / '25-license-revocation-expired-approval-repreview.png'), full_page=True)
            await page.locator('#license-lifecycle-close').click()

            await page.locator(f"[data-license-id='{fixture['browserUploadLicenseId']}']").get_by_role('button', name='Review revocation impact').click()
            await page.wait_for_function("document.querySelector('#license-lifecycle-preview-status')?.textContent?.includes('validated the current persisted target')")
            lifecycle_request_reason = 'Synthetic browser proof: approved license revocation workflow.'
            await page.locator('#license-lifecycle-reason').fill(lifecycle_request_reason)
            lifecycle_request_totp = current_totp(auth_mfa_secret)
            secrets_to_redact.append(lifecycle_request_totp)
            await page.locator('#license-lifecycle-request-totp').fill(lifecycle_request_totp)
            await page.locator('#license-lifecycle-request-approval').click()
            await page.locator('#license-lifecycle-final-section').wait_for(state='visible')
            assert 'Pending independent approval' in await page.locator('#license-lifecycle-approval-state').inner_text(), 'Approval request did not remain pending before a separate approver acted.'
            lifecycle_approval_id = (await page.locator('#license-lifecycle-approval-id').input_value()).strip()
            assert re.fullmatch(r'[0-9a-fA-F-]{36}', lifecycle_approval_id), f'Central did not persist a lifecycle approval identity: {lifecycle_approval_id}'
            await capture_screenshot(page, path=str(screenshots / '26-license-revocation-pending-approval.png'), full_page=True)
            await page.locator('#license-lifecycle-close').click()

            approver_context = await browser.new_context(viewport={"width": 1440, "height": 1000}, ignore_https_errors=True)
            approver_page = await approver_context.new_page()
            approver_page.on('response', lambda response: events['responses'].append({"status": response.status, "url": response.url}) if '/api/admin/v1/' in response.url else None)
            await approver_page.goto(fixture['baseUrl'], wait_until='domcontentloaded')
            await approver_page.locator('#auth-wrapper').wait_for(state='visible')
            await approver_page.wait_for_function("document.querySelector('#auth-status')?.textContent?.trim() === 'Sign in to continue.'")
            await approver_page.locator('#login-username').fill(approver_username)
            await approver_page.locator('#login-password').fill(approver_password)
            await approver_page.locator('#login-form button[type="submit"]').click()
            await approver_page.locator('#mfa-step').wait_for(state='visible')
            lifecycle_approver_totp = current_totp(approver_mfa_secret)
            secrets_to_redact.append(lifecycle_approver_totp)
            await approver_page.locator('#mfa-totp').fill(lifecycle_approver_totp)
            await approver_page.locator('#mfa-form button[type="submit"]').click()
            await approver_page.locator('#app-shell').wait_for(state='visible')
            assert approver_username in (await approver_page.locator('#user-display-name').inner_text()), 'Approval was not performed in the distinct administrator session.'
            await approver_page.locator('[data-view="approvals"]').click()
            lifecycle_approval_row = approver_page.locator('#approvals-table-tbody tr').filter(has_text=lifecycle_approval_id)
            await lifecycle_approval_row.wait_for()
            assert 'LICENSE_REVOKE' in await lifecycle_approval_row.inner_text(), 'Separate approver queue did not show the persisted license-revoke request.'
            await lifecycle_approval_row.get_by_role('button', name='Review approval').click()
            await approver_page.locator('#sensitive-approval-controls').wait_for(state='visible')
            await approver_page.locator('#sensitive-approval-details').get_by_text(lifecycle_request_reason, exact=False).wait_for()
            await approver_page.locator('#sensitive-approval-decision').select_option('APPROVE')
            await approver_page.locator('#sensitive-approval-reason').fill('Synthetic distinct-admin approval for the exact persisted license command.')
            await approver_page.locator('#sensitive-approval-submit').click()
            await approver_page.wait_for_function("document.querySelector('#approvals-table-tbody')?.textContent?.includes('No Dual approvals items')")
            assert any(f'/api/admin/v1/sensitive-approvals/{lifecycle_approval_id}/decide' in response['url'] and response['status'] == 200 for response in events['responses']), 'Separate administrator approval was not persisted through the canonical route.'
            await approver_page.screenshot(path=str(screenshots / '27-license-revocation-separate-approval.png'), full_page=True)
            await approver_context.close()

            await page.locator('[data-view="recovery"]').click()
            await page.locator('#recovery-refresh').click()
            await page.wait_for_function("document.querySelector('#recovery-worker-status')?.textContent?.includes('Outbox worker:')")
            worker_observation = await page.locator('#recovery-worker-status').inner_text()
            assert 'Observed ' in worker_observation, f'No server observation timestamp was rendered: {worker_observation}'
            assert 'Outbox worker: Healthy' in worker_observation, f'Worker did not start with a fresh healthy cycle: {worker_observation}'
            worker_probe_trigger = pathlib.Path(fixture['workerProbeTriggerPath'])
            worker_probe_result_path = pathlib.Path(fixture['workerProbeResultPath'])
            worker_probe_trigger.write_text(json.dumps({'runId': fixture['runId']}), encoding='utf-8')
            probe_deadline = time.monotonic() + 60
            while not worker_probe_result_path.exists() and time.monotonic() < probe_deadline:
                await asyncio.sleep(0.05)
            assert worker_probe_result_path.exists(), 'The server-side worker probe did not reach its persisted dead-letter readback.'
            worker_probe_result = json.loads(worker_probe_result_path.read_text(encoding='utf-8'))
            worker_poison_message_id = worker_probe_result['messageId']
            assert worker_probe_result['status'] == 'DEAD_LETTER', f"Worker probe did not reach dead-letter: {worker_probe_result}"
            assert worker_probe_result['retryCount'] == worker_probe_result['maxAttempts'], f"Worker probe retry bound differed: {worker_probe_result}"
            assert worker_probe_result['consumerEffectCount'] == 0 and worker_probe_result['notificationCount'] == 0, \
                f"Unsupported worker probe unexpectedly created a business effect: {worker_probe_result}"
            async with page.expect_response(lambda response: '/api/admin/v1/notifications/status' in response.url) as degraded_worker_response:
                await page.locator('#recovery-refresh').click()
            degraded_worker_http = await degraded_worker_response.value
            assert degraded_worker_http.status == 200, f'Worker health endpoint returned {degraded_worker_http.status} after dead-letter.'
            degraded_worker_body = await degraded_worker_http.json()
            degraded_worker_headers = await degraded_worker_http.all_headers()
            assert degraded_worker_body['status'] == 'degraded', f"Persisted dead-letter was not reflected by worker health: {degraded_worker_body}"
            assert degraded_worker_body.get('lastSucceededAtUtc'), f"Degraded observation omitted the last successful cycle: {degraded_worker_body}"
            assert 'no-store' in degraded_worker_headers.get('cache-control', '').lower(), \
                f"Worker health response was cacheable: {degraded_worker_headers.get('cache-control')}"
            await page.wait_for_function("document.querySelector('#recovery-worker-status')?.textContent?.includes('Outbox worker: Degraded')")
            degraded_worker_observation = await page.locator('#recovery-worker-status').inner_text()
            assert 'Observed ' in degraded_worker_observation and 'Last successful cycle:' in degraded_worker_observation, \
                f'Portal omitted timestamped worker degradation detail: {degraded_worker_observation}'
            recovery_scope = await page.locator('#recovery-status').inner_text()
            assert 'does not prove a backup, restore, host identity, or database target' in recovery_scope
            await capture_screenshot(page, path=str(screenshots / '08-recovery-worker-status.png'), full_page=True)
            await capture_screenshot(page, path=str(screenshots / '41-worker-degraded-after-dead-letter.png'), full_page=True)

            await page.locator('[data-view="commercial"]').click()
            await page.locator(f'#commercial-plan-id option[value="{fixture["planId"]}"]').wait_for(state='attached')
            await page.locator('#commercial-shop-id').fill(str(fixture['shopId']))
            await page.locator('#commercial-plan-id').select_option(str(fixture['planId']))
            await page.locator('#commercial-order-reason').fill('Synthetic browser contract fixture; no payment or license issuance authorized.')
            await page.locator('#commercial-order-submit').click()
            await page.get_by_text(__import__('re').compile(r'Order ([0-9a-f-]{36}) was persisted by Central\.', __import__('re').I)).wait_for()
            commercial_message = await page.locator('#commercial-order-status').inner_text()
            order_match = __import__('re').search(r'Order ([0-9a-f-]{36}) was persisted by Central\.', commercial_message, __import__('re').I)
            assert order_match, f'No persisted order identity returned: {commercial_message}'
            order_id = order_match.group(1)
            assert 'Payment evidence must be uploaded and reviewed before license issuance.' in commercial_message, \
                f'Commercial portal misrepresented an order as paid or issued: {commercial_message}'
            await capture_screenshot(page, path=str(screenshots / '11-commercial-order.png'), full_page=True)

            await page.locator('[data-view="dashboard"]').click()
            await page.locator('[data-queue="PAYMENT_REVIEW"]').click()
            await page.wait_for_function("document.querySelector('#dashboard-queue-status')?.textContent?.includes('Payment reviews —')")
            payment_row = page.locator('#dashboard-queue-tbody tr').filter(has_text=fixture['paymentEvidenceId'])
            await payment_row.get_by_role('button', name='Review payment').click()
            await page.locator('#queue-inspect-modal.active').wait_for()
            await page.locator('#payment-review-controls:not([hidden])').wait_for()
            await page.locator('#payment-review-decision').select_option('APPROVE')
            await page.locator('#payment-review-reason').fill('SFR11_BROWSER_PAYMENT_CONFIRMED')
            await page.locator('#payment-review-note').fill('Synthetic authenticated browser payment review.')
            async with page.expect_response(
                lambda response: f"/api/admin/v1/payments/{fixture['paymentEvidenceId']}/review" in response.url) as payment_review_response:
                await page.locator('#payment-review-submit').click()
            payment_response = await payment_review_response.value
            assert payment_response.status == 200, f'Payment review route returned {payment_response.status}.'
            await page.wait_for_function("document.querySelector('#dashboard-queue-status')?.textContent?.includes('Central recorded approve for payment proof')")
            await capture_screenshot(page, path=str(screenshots / '22-payment-review-persisted.png'), full_page=True)

            await page.locator('[data-view="customer360"]').click()
            await page.locator('#customer-search-input').fill(str(fixture['shopId']))
            await page.locator('#btn-customer-search').click()
            addon_order_id = fixture['addonRenewalOrderId']
            addon_review_button = page.locator(f'button[data-addon-renew-order-id="{addon_order_id}"]')
            await addon_review_button.wait_for(state='visible')
            await addon_review_button.click()
            addon_form = page.locator(f'form[aria-label="Manual add-on renewal for order {addon_order_id}"]')
            await addon_form.wait_for(state='visible')
            renewal_expiry = datetime.fromisoformat(fixture['addonRenewalExpiryUtc'].replace('Z', '+00:00'))
            await addon_form.locator('[data-addon-renew-expiry]').fill(renewal_expiry.astimezone().strftime('%Y-%m-%dT%H:%M'))
            await addon_form.locator('[data-addon-renew-reason]').fill('Synthetic authenticated manual support renewal; payment fixture was separately approved.')
            addon_entitlement_id = fixture['addonRenewalEntitlementId']
            async with page.expect_response(lambda response:
                f"/api/admin/v1/licenses/{fixture['browserUploadLicenseId']}/addons/{addon_entitlement_id}/renew" in response.url) as addon_renewal_response:
                await addon_form.locator('[data-addon-renew-submit]').click()
            addon_renewal_http = await addon_renewal_response.value
            assert addon_renewal_http.status == 200, f'Manual add-on renewal returned {addon_renewal_http.status}.'
            addon_renewal_body = await addon_renewal_http.json()
            assert addon_renewal_body['id'] == addon_entitlement_id and addon_renewal_body['kind'] == 'SUPPORT', \
                f'Add-on renewal changed a different entitlement: {addon_renewal_body}'
            assert addon_renewal_body['status'] == 'ACTIVE', f'Support add-on was not active after renewal: {addon_renewal_body}'
            assert datetime.fromisoformat(addon_renewal_body['validUntilUtc'].replace('Z', '+00:00')) == renewal_expiry, \
                f'Support add-on expiry did not match the operator-confirmed date: {addon_renewal_body}'
            await page.wait_for_function("entitlementId => document.querySelector('#customer-360-details')?.textContent?.includes(entitlementId)", arg=addon_entitlement_id)
            await capture_screenshot(page, path=str(screenshots / '12-manual-addon-renewal.png'), full_page=True)


            issue_order_id = str(fixture['paymentReviewOrderId'])
            issue_button = page.locator(f'[data-license-issue-order-id="{issue_order_id}"]')
            await issue_button.wait_for(state='visible')
            license_issue_available_before_manual_submit = True
            await issue_button.click()
            issue_form = page.locator(f'[aria-label="Manual license issue for order {issue_order_id}"]')
            await issue_form.wait_for(state='visible')
            await issue_form.locator('[data-license-valid-from]').fill(datetime.now().astimezone().strftime('%Y-%m-%dT%H:%M'))
            if fixture['paymentReviewOrderTermKind'] == 'FIXED_TERM':
                await issue_form.locator('[data-license-valid-until]').fill((datetime.now().astimezone() + timedelta(days=365)).strftime('%Y-%m-%dT%H:%M'))
            await issue_form.locator('[data-license-issue-reason]').fill('Synthetic authenticated manual license issuance after payment review.')
            async with page.expect_response(
                lambda response: response.url.endswith('/api/admin/v1/licenses/issue')) as license_issue_response:
                await issue_form.locator('[data-license-issue-submit]').click()
            license_response = await license_issue_response.value
            assert license_response.status == 200, f'Manual license issue route returned {license_response.status}.'
            license_body = await license_response.json()
            assert license_body.get('initialOrderId', '').lower() == issue_order_id.lower(), f'License was not bound to the approved order: {license_body}'
            manually_issued_license_id = license_body.get('id')
            assert re.fullmatch(r'[0-9a-f-]{36}', manually_issued_license_id or '', re.I), f'Central returned no license id: {license_body}'
            await page.wait_for_function(
                "document.querySelector('#customer-360-details')?.textContent?.includes('Issued:')")
            await capture_screenshot(page, path=str(screenshots / '23-manual-license-issue-persisted.png'), full_page=True)


            upload_result = await page.evaluate(r'''async ({ fixture, privateKeyPkcs8Base64 }) => {
                const sortJson = value => Array.isArray(value) ? value.map(sortJson) : value && typeof value === 'object'
                    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, sortJson(value[key])])) : value;
                const canonical = value => JSON.stringify(sortJson(value));
                const encoder = new TextEncoder();
                const hex = bytes => Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
                const digest = async bytes => hex(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)));
                const decodeBase64 = value => Uint8Array.from(atob(value), character => character.charCodeAt(0));
                const privateKey = await crypto.subtle.importKey('pkcs8', decodeBase64(privateKeyPkcs8Base64),
                    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, false, ['sign']);
                const ciphertext = decodeBase64(fixture.browserUploadBytesBase64);
                const wholeSha256 = await digest(ciphertext);
                if (wholeSha256 !== fixture.browserUploadSha256) throw new Error('Browser ciphertext fixture digest mismatch.');
                const payload = {
                    shopId: fixture.shopId, installationId: fixture.browserUploadInstallationId,
                    objectId: fixture.browserUploadObjectId, purpose: 'SUPPORT_ATTACHMENT', contentLength: ciphertext.length,
                    contentType: 'application/octet-stream', ciphertextSha256: wholeSha256,
                    retentionPolicyId: fixture.retentionPolicyId
                };
                const envelope = (operationType, body, expectedVersion) => ({
                    meta: { clientOperationId: crypto.randomUUID(), operationType, fingerprintVersion: 1,
                        payloadSha256: '', expectedVersion, policyVersion: 0,
                        reason: 'Synthetic authenticated browser upload fixture' }, payload: body
                });
                const init = envelope('evidence.upload.init', payload, 0);
                init.meta.payloadSha256 = await digest(encoder.encode(canonical(payload)));
                const initResponse = await apiFetch('/api/admin/v1/evidence/uploads', { method: 'POST', body: JSON.stringify(init) });
                if (!initResponse.ok) throw new Error(`Upload init returned ${initResponse.status}: ${await initResponse.text()}`);
                const upload = await initResponse.json();
                if (upload.authorizedObjectId !== fixture.browserUploadObjectId || upload.acceptedOffset !== 0)
                    throw new Error('Upload init returned an unexpected object identity or offset.');
                const firstLength = Math.floor(ciphertext.length / 2);
                const firstChunk = ciphertext.slice(0, firstLength);
                const secondChunk = ciphertext.slice(firstLength);
                const signChunk = async (chunk, from, to, nonce) => {
                    const chunkSha256 = await digest(chunk);
                    const timestamp = Math.floor(Date.now() / 1000);
                    const proof = {
                        profile: 'CENTRAL_INSTALLATION_BINARY_REQUEST_V1', purpose: 'installation.request',
                        keyId: fixture.browserUploadKeyId, installationId: fixture.browserUploadInstallationId,
                        shopId: fixture.shopId, licenseId: fixture.browserUploadLicenseId, timestamp, nonce,
                        httpMethod: 'PUT', httpPath: upload.chunkRoute, bodyDigest: chunkSha256,
                        binaryRange: { from, to, totalLength: ciphertext.length, contentLength: chunk.length, chunkSha256 }
                    };
                    const signature = new Uint8Array(await crypto.subtle.sign('RSASSA-PKCS1-v1_5', privateKey,
                        encoder.encode(canonical(proof))));
                    const signatureBase64 = btoa(Array.from(signature, value => String.fromCharCode(value)).join(''));
                    return { chunkSha256, timestamp, signatureBase64 };
                };
                const append = async (chunk, from, to) => {
                    const nonce = crypto.randomUUID();
                    const signed = await signChunk(chunk, from, to, nonce);
                    return fetch(upload.chunkRoute, { method: 'PUT', credentials: 'same-origin', cache: 'no-store', body: chunk,
                        headers: { 'Content-Type': 'application/octet-stream',
                            'Content-Range': `bytes ${from}-${to}/${ciphertext.length}`, 'chunk-sha256': signed.chunkSha256,
                            'X-Installation-Id': fixture.browserUploadInstallationId,
                            'X-Installation-Key-Id': fixture.browserUploadKeyId,
                            'X-Installation-Timestamp': String(signed.timestamp), 'X-Installation-Nonce': nonce,
                            'X-Installation-Signature': signed.signatureBase64 }
                    });
                };
                const firstResponse = await append(firstChunk, 0, firstChunk.length - 1);
                const firstResult = await firstResponse.json();
                if (firstResponse.status !== 200 || firstResult.acceptedOffset !== firstChunk.length)
                    throw new Error(`Initial chunk was not persisted: ${firstResponse.status} ${JSON.stringify(firstResult)}`);
                const retryResponse = await append(firstChunk, 0, firstChunk.length - 1);
                const retryResult = await retryResponse.json();
                if (retryResponse.status !== 200 || retryResult.acceptedOffset !== firstChunk.length)
                    throw new Error(`Exact chunk retry failed: ${retryResponse.status} ${JSON.stringify(retryResult)}`);
                const alteredFirstChunk = firstChunk.slice();
                alteredFirstChunk[alteredFirstChunk.length - 1] ^= 1;
                const conflictResponse = await append(alteredFirstChunk, 0, alteredFirstChunk.length - 1);
                if (conflictResponse.status !== 409) throw new Error(`Altered retry returned ${conflictResponse.status}, expected 409.`);
                const resumeResponse = await append(secondChunk, firstChunk.length, ciphertext.length - 1);
                const resumeResult = await resumeResponse.json();
                if (resumeResponse.status !== 200 || resumeResult.acceptedOffset !== ciphertext.length)
                    throw new Error(`Upload did not resume from accepted offset: ${resumeResponse.status} ${JSON.stringify(resumeResult)}`);
                const finalizePayload = { uploadId: upload.uploadId, objectId: fixture.browserUploadObjectId,
                    expectedLength: ciphertext.length, expectedSha256: wholeSha256 };
                const finalize = envelope('evidence.upload.finalize', finalizePayload, 3);
                finalize.meta.payloadSha256 = await digest(encoder.encode(canonical(finalizePayload)));
                const finalizeResponse = await apiFetch(upload.finalizeRoute, { method: 'POST', body: JSON.stringify(finalize) });
                const finalizeResult = await finalizeResponse.json();
                if (!finalizeResponse.ok || finalizeResult.state !== 'SUCCEEDED')
                    throw new Error(`Upload finalize failed: ${finalizeResponse.status} ${JSON.stringify(finalizeResult)}`);
                return { uploadId: upload.uploadId, objectId: upload.authorizedObjectId,
                    acceptedOffset: resumeResult.acceptedOffset, exactRetryAccepted: retryResponse.status === 200,
                    conflictingRetryRejected: conflictResponse.status === 409, finalizeState: finalizeResult.state,
                    ciphertextSha256: wholeSha256, contentLength: ciphertext.length };
            }''', {
                'fixture': fixture,
                'privateKeyPkcs8Base64': os.environ['CENTRAL_BROWSER_TEST_INSTALLATION_PKCS8']
            })
            assert upload_result['objectId'] == fixture['attachmentObjectId'], 'Browser upload object identity differed from the support attachment.'
            assert upload_result['acceptedOffset'] == upload_result['contentLength'], 'Browser upload did not accept the full ciphertext.'
            assert upload_result['exactRetryAccepted'] and upload_result['conflictingRetryRejected'], 'Browser upload retry controls failed.'
            assert upload_result['finalizeState'] == 'SUCCEEDED', f'Browser ciphertext upload did not finalize: {upload_result}'
            await capture_screenshot(page, path=str(screenshots / '08-ciphertext-upload-resume.png'), full_page=True)

            async def accept_case_preview(dialog):
                assert 'Central preview' in dialog.message, f'Unexpected case decision confirmation: {dialog.message}'
                await dialog.accept()

            await page.locator('[data-view="licensing"]').click()
            await page.locator('#licensing-hardware-queue').click()
            hardware_row = page.locator('#licensing-activations-tbody tr').filter(has_text=fixture['hardwareReviewId'])
            await hardware_row.get_by_role('button', name='Review hardware', exact=True).click()
            await page.locator('#hardware-review-controls').wait_for(state='visible')
            await page.locator('#hardware-review-evidence').fill(fixture['attachmentObjectId'])
            await page.locator('#hardware-review-decision').select_option('REQUEST_INFORMATION')
            await page.locator('#hardware-review-reason').fill('Synthetic browser hardware verification needs more information')
            preview_started = asyncio.Event()
            release_preview = asyncio.Event()
            preview_finished = asyncio.Event()
            unexpected_case_dialogs = []
            async def hold_hardware_preview(route):
                body = route.request.post_data_json
                if body.get('targetEntityId') != fixture['hardwareReviewId']:
                    await route.continue_()
                    return
                preview_response = await route.fetch()
                preview_started.set()
                await release_preview.wait()
                await route.fulfill(response=preview_response)
                preview_finished.set()
            async def dismiss_stale_case_dialog(dialog):
                unexpected_case_dialogs.append(dialog.message)
                await dialog.dismiss()
            await page.route('**/api/admin/v1/action-preview', hold_hardware_preview)
            page.on('dialog', dismiss_stale_case_dialog)
            await page.locator('#hardware-review-submit').click()
            await asyncio.wait_for(preview_started.wait(), timeout=15)
            await page.locator('#btn-close-queue-modal').click()
            await hardware_row.get_by_role('button', name='Review hardware', exact=True).click()
            await page.locator('#hardware-review-evidence').fill(fixture['attachmentObjectId'])
            await page.locator('#hardware-review-decision').select_option('REQUEST_INFORMATION')
            await page.locator('#hardware-review-reason').fill('Synthetic current selection hardware review')
            release_preview.set()
            await asyncio.wait_for(preview_finished.wait(), timeout=15)
            await page.evaluate("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))")
            await page.unroute('**/api/admin/v1/action-preview', hold_hardware_preview)
            page.remove_listener('dialog', dismiss_stale_case_dialog)
            assert unexpected_case_dialogs == [], f'Closed preview still prompted for a prior selection: {unexpected_case_dialogs}'
            assert not any(response['url'].endswith('/hardware-reviews/' + fixture['hardwareReviewId'] + '/decide') for response in events['responses']), 'A cancelled preview submitted a hardware decision.'
            assert await page.locator('#hardware-review-status').inner_text() == '', 'Old preview changed the newly opened selection status.'
            assert await page.locator('#hardware-review-reason').input_value() == 'Synthetic current selection hardware review', 'Old preview changed new selection data.'
            await capture_screenshot(page, path=str(screenshots / '33-closed-preview-cancelled.png'), full_page=True)
            captured_hardware_command = {}
            hardware_command_path = '/api/admin/v1/hardware-reviews/' + fixture['hardwareReviewId'] + '/decide'
            async def capture_hardware_command(route):
                captured_hardware_command.update({
                    'url': route.request.url,
                    'method': route.request.method,
                    'body': route.request.post_data
                })
                await route.continue_()
            await page.route('**' + hardware_command_path, capture_hardware_command)
            page.once('dialog', accept_case_preview)
            await page.locator('#hardware-review-submit').click()
            await page.wait_for_function("document.querySelector('#hardware-review-status')?.textContent?.startsWith('Decision saved at Central revision ')")
            await page.unroute('**' + hardware_command_path, capture_hardware_command)
            assert any(response['url'].endswith('/hardware-reviews/' + fixture['hardwareReviewId'] + '/decide') and response['status'] == 200 for response in events['responses']), 'Browser hardware decision was not confirmed by Central.'
            assert captured_hardware_command.get('body'), 'The UI hardware decision request body was not captured for exact replay.'
            hardware_command_replay = await page.evaluate('''async request => {
                const response = await fetch(request.url, { method: request.method,
                    headers: { 'Accept': 'application/json', 'Content-Type': 'application/json' },
                    credentials: 'same-origin', cache: 'no-store', body: request.body });
                const body = await response.json();
                return { status: response.status, state: body.state, outcomeId: body.outcomeId, code: body.code };
            }''', captured_hardware_command)
            assert hardware_command_replay['status'] == 200 and hardware_command_replay['state'] == 'SUCCEEDED', \
                f'Exact hardware decision replay did not return its persisted result: {hardware_command_replay}'
            stale_hardware_command = await page.evaluate('''async request => {
                const command = JSON.parse(request.body);
                command.meta.clientOperationId = crypto.randomUUID();
                const response = await fetch(request.url, { method: request.method,
                    headers: { 'Accept': 'application/json', 'Content-Type': 'application/json' },
                    credentials: 'same-origin', cache: 'no-store', body: JSON.stringify(command) });
                const body = await response.json();
                return { status: response.status, code: body.code };
            }''', captured_hardware_command)
            assert stale_hardware_command['status'] == 409 and stale_hardware_command['code'] == 'CONCURRENCY_CONFLICT', \
                f'A stale hardware revision was not rejected without a mutation: {stale_hardware_command}'
            await capture_screenshot(page, path=str(screenshots / '31-hardware-information-persisted.png'), full_page=True)
            await page.locator('#btn-close-queue-modal').click()
            await page.locator('#licensing-transfer-queue').click()
            transfer_row = page.locator('#licensing-activations-tbody tr').filter(has_text=fixture['primaryTransferId'])
            await transfer_row.get_by_role('button', name='Review transfer', exact=True).click()
            await page.locator('#transfer-review-controls').wait_for(state='visible')
            await page.locator('#transfer-review-evidence').fill(fixture['attachmentObjectId'])
            await page.locator('#transfer-review-recovery').select_option('STANDARD_TRANSFER')
            await page.locator('#transfer-review-decision').select_option('CANCEL')
            await page.locator('#transfer-review-reason').fill('Synthetic browser operator cancels the pending transfer')
            page.once('dialog', accept_case_preview)
            await page.locator('#transfer-review-submit').click()
            await page.wait_for_function("document.querySelector('#transfer-review-status')?.textContent?.startsWith('Decision saved at Central revision ')")
            assert any(response['url'].endswith('/transfers/' + fixture['primaryTransferId'] + '/decide') and response['status'] == 200 for response in events['responses']), 'Browser transfer cancellation was not confirmed by Central.'
            await capture_screenshot(page, path=str(screenshots / '32-transfer-cancellation-persisted.png'), full_page=True)
            await page.locator('#btn-close-queue-modal').click()

            case_approvals = []
            for prefix, target_key, queue_button, decision, operation_type in [
                ('hardware', 'approvalHardwareReviewId', '#licensing-hardware-queue', 'APPROVE_REBIND', 'HARDWARE_REVIEW'),
                ('transfer', 'approvalPrimaryTransferId', '#licensing-transfer-queue', 'APPROVE', 'PRIMARY_TRANSFER')
            ]:
                await page.locator(queue_button).click()
                case_row = page.locator('#licensing-activations-tbody tr').filter(has_text=fixture[target_key])
                await case_row.get_by_role('button', name='Review hardware' if prefix == 'hardware' else 'Review transfer', exact=True).click()
                await page.locator(f'#{prefix}-review-controls').wait_for(state='visible')
                await page.locator(f'#{prefix}-review-evidence').fill(fixture['attachmentObjectId'])
                await page.locator(f'#{prefix}-review-decision').select_option(decision)
                if prefix == 'transfer':
                    await page.locator('#transfer-review-recovery').select_option('STANDARD_TRANSFER')
                await page.locator(f'#{prefix}-review-reason').fill(f'Synthetic browser requests independent {prefix} approval')
                if prefix == 'hardware':
                    request_preview_started = asyncio.Event()
                    release_request_preview = asyncio.Event()
                    request_preview_finished = asyncio.Event()
                    async def hold_request_preview(route):
                        body = route.request.post_data_json
                        if body.get('targetEntityId') != fixture['approvalHardwareReviewId']:
                            await route.continue_()
                            return
                        preview_response = await route.fetch()
                        request_preview_started.set()
                        await release_request_preview.wait()
                        await route.fulfill(response=preview_response)
                        request_preview_finished.set()
                    await page.route('**/api/admin/v1/action-preview', hold_request_preview)
                    step_up_count_before = sum('/auth/step-up' in response['url'] for response in events['responses'])
                    await page.locator('#hardware-review-request-totp').fill('111111')
                    await page.locator('#hardware-review-request-approval').click()
                    await asyncio.wait_for(request_preview_started.wait(), timeout=15)
                    await page.locator('#hardware-review-request-totp').fill('222222')
                    release_request_preview.set()
                    await asyncio.wait_for(request_preview_finished.wait(), timeout=15)
                    await page.wait_for_timeout(100)
                    await page.unroute('**/api/admin/v1/action-preview', hold_request_preview)
                    assert await page.locator('#hardware-review-request-totp').input_value() == '222222', 'An older in-flight approval preview erased a newer request MFA code.'
                    assert not await page.locator('#hardware-review-request-approval').is_disabled(), 'Changing request MFA did not restore the approval request control.'
                    assert sum('/auth/step-up' in response['url'] for response in events['responses']) == step_up_count_before, 'A superseded approval preview submitted stale MFA.'
                request_proof = current_totp(auth_mfa_secret)
                secrets_to_redact.append(request_proof)
                await page.locator(f'#{prefix}-review-request-totp').fill(request_proof)
                page.once('dialog', accept_case_preview)
                await page.locator(f'#{prefix}-review-request-approval').click()
                await page.wait_for_function("prefix => document.querySelector('#'+prefix+'-review-status')?.textContent?.includes('Independent approval request saved')", arg=prefix)
                case_approval_id = await page.locator(f'#{prefix}-review-approval').input_value()
                assert re.fullmatch(r'[0-9a-fA-F-]{36}', case_approval_id), 'No persisted approval identity was displayed.'
                assert 'no hardware or transfer change has occurred' in await page.locator(f'#{prefix}-approval-state').inner_text()
                assert await page.locator(f'#{prefix}-review-request-totp').input_value() == '', 'Request MFA was retained.'
                case_approvals.append((prefix, target_key, queue_button, decision, operation_type, case_approval_id))
                await capture_screenshot(page, path=str(screenshots / f'34-{prefix}-approval-request-pending.png'), full_page=True)
                await page.locator('#btn-close-queue-modal').click()

            case_context = await browser.new_context(viewport={"width": 1440, "height": 1000}, ignore_https_errors=True)
            case_page = await case_context.new_page()
            case_page.on('pageerror', lambda error: events['pageErrors'].append(str(error)))
            case_page.on('response', lambda response: events['responses'].append({"status": response.status, "url": response.url}) if '/api/' in response.url else None)
            await case_page.goto(fixture['baseUrl'], wait_until='domcontentloaded')
            await case_page.wait_for_function("document.querySelector('#auth-status')?.textContent?.trim() === 'Sign in to continue.'")
            await case_page.locator('#login-username').fill(approver_username)
            await case_page.locator('#login-password').fill(approver_password)
            await case_page.locator('#login-form button[type="submit"]').click()
            await case_page.locator('#mfa-step').wait_for(state='visible')
            case_login_proof = current_totp(approver_mfa_secret)
            secrets_to_redact.append(case_login_proof)
            await case_page.locator('#mfa-totp').fill(case_login_proof)
            await case_page.locator('#mfa-form button[type="submit"]').click()
            await case_page.locator('#app-shell').wait_for(state='visible')
            assert approver_username in await case_page.locator('#user-display-name').inner_text()
            for prefix, target_key, queue_button, decision, operation_type, case_approval_id in case_approvals:
                await case_page.locator('[data-view="approvals"]').click()
                approval_row = case_page.locator('#approvals-table-tbody tr').filter(has_text=case_approval_id)
                await approval_row.wait_for()
                assert operation_type in await approval_row.inner_text()
                await approval_row.get_by_role('button', name='Review approval', exact=True).click()
                await case_page.locator('#sensitive-approval-decision').select_option('APPROVE')
                await case_page.locator('#sensitive-approval-reason').fill(f'Synthetic distinct administrator authorizes exact {prefix} payload')
                await case_page.locator('#sensitive-approval-submit').click()
                await approval_row.wait_for(state='detached')
                assert any('/sensitive-approvals/' + case_approval_id + '/decide' in response['url'] and response['status'] == 200 for response in events['responses'])
                await case_page.locator('[data-view="licensing"]').click()
                await case_page.locator(queue_button).click()
                target_row = case_page.locator('#licensing-activations-tbody tr').filter(has_text=fixture[target_key])
                await target_row.get_by_role('button', name='Review hardware' if prefix == 'hardware' else 'Review transfer', exact=True).click()
                await case_page.locator(f'#{prefix}-review-evidence').fill(fixture['attachmentObjectId'])
                await case_page.locator(f'#{prefix}-review-decision').select_option(decision)
                if prefix == 'transfer':
                    await case_page.locator('#transfer-review-recovery').select_option('STANDARD_TRANSFER')
                await case_page.locator(f'#{prefix}-review-reason').fill(f'Synthetic distinct administrator executes approved {prefix} decision')
                await case_page.locator(f'#{prefix}-review-approval').fill(case_approval_id)
                execution_proof = current_totp(approver_mfa_secret)
                secrets_to_redact.append(execution_proof)
                await case_page.locator(f'#{prefix}-review-execution-totp').fill(execution_proof)
                decision_response_start = len(events['responses'])
                case_page.once('dialog', accept_case_preview)
                await case_page.locator(f'#{prefix}-review-submit').click()
                await case_page.wait_for_function("prefix => { const status = document.querySelector('#'+prefix+'-review-status')?.textContent || ''; return status.startsWith('Decision saved at Central revision ') || status.includes('Too many requests. Please retry later.'); }", arg=prefix)
                decision_status = await case_page.locator(f'#{prefix}-review-status').inner_text()
                if 'Too many requests. Please retry later.' in decision_status:
                    limited_step_up = [response for response in events['responses'][decision_response_start:]
                        if response['url'].endswith('/api/admin/v1/auth/step-up') and response['status'] == 429]
                    assert limited_step_up, f'{prefix} decision showed a rate limit without a matching step-up 429.'
                    assert not any(response['url'].endswith(f'/api/admin/v1/{"hardware-reviews" if prefix == "hardware" else "transfers"}/{fixture[target_key]}/decide')
                        for response in events['responses'][decision_response_start:]), \
                        f'{prefix} decision was sent despite a rate-limited execution step-up.'
                    await case_page.wait_for_timeout((int(os.environ['CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS']) + 1) * 1000)
                    execution_proof = current_totp(approver_mfa_secret)
                    secrets_to_redact.append(execution_proof)
                    await case_page.locator(f'#{prefix}-review-execution-totp').fill(execution_proof)
                    decision_response_start = len(events['responses'])
                    case_page.once('dialog', accept_case_preview)
                    await case_page.locator(f'#{prefix}-review-submit').click()
                    await case_page.wait_for_function("prefix => document.querySelector('#'+prefix+'-review-status')?.textContent?.startsWith('Decision saved at Central revision ')", arg=prefix)
                    assert any(response['url'].endswith('/api/admin/v1/auth/step-up') and response['status'] == 200
                        for response in events['responses'][decision_response_start:]), \
                        f'{prefix} decision retry did not obtain a fresh step-up.'
                assert await case_page.locator(f'#{prefix}-review-execution-totp').input_value() == '', 'Execution MFA was retained.'
                await capture_screenshot(case_page, path=str(screenshots / f'35-{prefix}-independent-approval-executed.png'), full_page=True)
                await case_page.locator('#btn-close-queue-modal').click()
            await case_page.locator('[data-view="licensing"]').click()
            await case_page.locator('#licensing-transfer-queue').click()
            completed_transfer_row = case_page.locator('#licensing-activations-tbody tr').filter(has_text=fixture['approvalPrimaryTransferId'])
            await completed_transfer_row.get_by_role('button', name='Review transfer', exact=True).click()
            await case_page.locator('#transfer-completion-controls').wait_for(state='visible')
            assert await case_page.locator('#transfer-review-controls').is_hidden(), 'An already approved transfer remained exposed as a decision request.'
            await case_page.locator('#transfer-completion-reason').fill('Synthetic browser completes the separately approved, evidence-backed transfer')
            completion_proof = current_totp(approver_mfa_secret)
            if completion_proof == execution_proof:
                for _ in range(70):
                    await case_page.wait_for_timeout(500)
                    completion_proof = current_totp(approver_mfa_secret)
                    if completion_proof != execution_proof:
                        break
            assert completion_proof != execution_proof, 'Fresh completion MFA code was not available after the approval step-up.'
            secrets_to_redact.append(completion_proof)
            await case_page.locator('#transfer-completion-execution-totp').fill(completion_proof)
            async def accept_completion_preview(dialog):
                assert 'Central completion preview' in dialog.message, f'Unexpected completion confirmation: {dialog.message}'
                await dialog.accept()
            case_page.once('dialog', accept_completion_preview)
            completion_response_start = len(events['responses'])
            await case_page.locator('#transfer-completion-submit').click()
            try:
                await case_page.wait_for_function("() => { const status = document.querySelector('#transfer-completion-status')?.textContent || ''; return status.includes('Central completed the approved transfer') || status.includes('Too many requests. Please retry later.'); }", timeout=30000)
            except Exception as error:
                completion_status = await case_page.locator('#transfer-completion-status').inner_text()
                await capture_screenshot(case_page, path=str(screenshots / '36-primary-transfer-completion-failure.png'), full_page=True)
                raise AssertionError(f'Portal transfer completion failed: {completion_status}') from error
            completion_status = await case_page.locator('#transfer-completion-status').inner_text()
            if 'Too many requests. Please retry later.' in completion_status:
                limited_step_up = [response for response in events['responses'][completion_response_start:]
                    if response['url'].endswith('/api/admin/v1/auth/step-up') and response['status'] == 429]
                assert limited_step_up, 'Transfer completion showed a rate limit without a matching step-up 429.'
                assert not any(response['url'].endswith('/api/activation/v1/primary-transfers/' + fixture['approvalPrimaryTransferId'] + '/complete')
                    for response in events['responses'][completion_response_start:]), \
                    'Transfer completion was sent despite a rate-limited execution step-up.'
                await case_page.wait_for_timeout((int(os.environ['CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS']) + 1) * 1000)
                completion_proof = current_totp(approver_mfa_secret)
                assert completion_proof != execution_proof, 'Fresh completion MFA code was not available after the rate-limit window.'
                secrets_to_redact.append(completion_proof)
                await case_page.locator('#transfer-completion-execution-totp').fill(completion_proof)
                completion_response_start = len(events['responses'])
                case_page.once('dialog', accept_completion_preview)
                await case_page.locator('#transfer-completion-submit').click()
                await case_page.wait_for_function("document.querySelector('#transfer-completion-status')?.textContent?.includes('Central completed the approved transfer')", timeout=30000)
                assert any(response['url'].endswith('/api/admin/v1/auth/step-up') and response['status'] == 200
                    for response in events['responses'][completion_response_start:]), \
                    'Transfer completion retry did not obtain a fresh step-up.'
                assert any(response['url'].endswith('/api/activation/v1/primary-transfers/' + fixture['approvalPrimaryTransferId'] + '/complete') and response['status'] == 200
                    for response in events['responses'][completion_response_start:]), \
                    'Transfer completion retry did not reach Central after the fresh step-up.'
            assert await case_page.locator('#transfer-completion-execution-totp').input_value() == '', 'Completion MFA was retained.'
            assert any('/api/activation/v1/primary-transfers/' + fixture['approvalPrimaryTransferId'] + '/complete' in response['url'] and response['status'] == 200 for response in events['responses']), 'Portal did not complete the separately approved transfer through the Central route.'
            await capture_screenshot(case_page, path=str(screenshots / '36-primary-transfer-completed.png'), full_page=True)
            await case_context.close()

            await page.locator('[data-view="support"]').click()
            cross_shop_result = await page.evaluate(r'''async ({ foreignShopId, attachmentObjectId }) => {
                const payload = { shopId: foreignShopId, subject: `Synthetic cross-shop attachment ${crypto.randomUUID()}`,
                    summary: 'Cross-shop attachment must be rejected by persisted shop ownership.',
                    attachmentObjectIds: [attachmentObjectId], redactionProfileVersion: 1 };
                const operation = await supportCommandBody('cross-shop-negative', payload, 'support.create', 0, AppState.currentUser.id);
                const response = await apiFetch('/api/admin/v1/support/tickets', {
                    method: 'POST', headers: { 'X-Client-Operation-Id': operation.id }, body: operation.body
                });
                const body = await response.json();
                return { status: response.status, code: body.code };
            }''', { 'foreignShopId': fixture['foreignShopId'], 'attachmentObjectId': fixture['attachmentObjectId'] })
            assert cross_shop_result == { 'status': 400, 'code': 'SUPPORT_ATTACHMENT_ACCESS_DENIED' }, \
                f'Cross-shop attachment reference was not denied: {cross_shop_result}'
            await page.locator('#support-shop-id').fill(str(fixture['shopId']))
            await page.locator('#support-subject').fill(subject)
            await page.locator('#support-summary').fill(f'Synthetic browser workflow {subject}')
            await page.locator('#support-redaction-version').fill('1')
            await page.locator('#support-attachments').fill(str(fixture['attachmentObjectId']))
            await page.locator('#support-create-submit').click()
            await page.get_by_text(__import__('re').compile(r'Ticket ([0-9a-f-]{36}) was saved by the server\.', __import__('re').I)).wait_for()
            create_message = await page.locator('#support-create-status').inner_text()
            match = __import__('re').search(r'Ticket ([0-9a-f-]{36}) was saved by the server\.', create_message, __import__('re').I)
            assert match, f'No persisted ticket identity returned: {create_message}'
            ticket_id = match.group(1)
            await capture_screenshot(page, path=str(screenshots / '05-support-created.png'), full_page=True)
            row = page.locator('#support-tbody tr').filter(has_text=subject)
            await row.get_by_role('button', name='Open Ticket').click()
            support_markup_rendered_as_text = await page.evaluate(r'''({ subject }) => {
                const title = document.querySelector('#support-detail-title');
                const row = [...document.querySelectorAll('#support-tbody tr')].find(item => item.cells[2]?.textContent === subject);
                const actionButtons = row ? [...row.querySelectorAll('button')] : [];
                return title?.textContent === subject && title.childElementCount === 0 &&
                    row?.cells[2]?.textContent === subject &&
                    document.querySelectorAll('#support-tbody img, #support-tbody a[href]').length === 0 &&
                    actionButtons.length === 1 && actionButtons[0].textContent === 'Open Ticket' &&
                    window.__supportInjected !== true && window.__supportTargetInjected !== true;
            }''', { 'subject': subject })
            assert support_markup_rendered_as_text, 'Support ticket markup was not rendered as inert text.'
            await page.locator('#support-reply-text').fill(reply_text)
            await page.locator('#support-reply-form').get_by_role('button', name='Save Reply').click()
            await page.get_by_text('Reply saved in the portal. No external message was sent.').wait_for()
            await capture_screenshot(page, path=str(screenshots / '06-support-reply.png'), full_page=True)

            support_status = 'IN_PROGRESS'
            async with page.expect_response(
                lambda response: response.url.endswith(f'/api/admin/v1/support/tickets/{ticket_id}/commands') and
                    response.request.method == 'POST') as support_status_response:
                await page.locator('#support-new-status').select_option(support_status)
                await page.locator('#support-status-form').get_by_role('button', name='Save Status').click()
            status_response = await support_status_response.value
            assert status_response.status == 200, f'Support status command returned {status_response.status}.'
            status_result = await status_response.json()
            assert status_result.get('state') == 'SUCCEEDED', f'Support status outcome was not persisted: {status_result}'
            await page.get_by_text('Ticket status and history saved by the server.').wait_for()
            await page.locator('#support-ticket-metadata').get_by_text(support_status, exact=True).wait_for()
            await page.locator('#support-history').get_by_text('OPEN -> IN_PROGRESS', exact=True).wait_for()

            await page.reload(wait_until='domcontentloaded')
            await page.locator('[data-view="support"]').click()
            await page.locator('#support-tbody').get_by_text(subject, exact=True).wait_for()
            row = page.locator('#support-tbody tr').filter(has_text=subject)
            await row.get_by_role('button', name='Open Ticket').click()
            await page.locator('#support-history').get_by_text(reply_text, exact=True).wait_for()
            await page.locator('#support-ticket-metadata').get_by_text(support_status, exact=True).wait_for()
            await page.locator('#support-history').get_by_text('OPEN -> IN_PROGRESS', exact=True).wait_for()
            attachment_link = page.locator('#support-ticket-attachments').get_by_role('link', name='Download encrypted attachment')
            await attachment_link.wait_for()
            attachment_href = await attachment_link.get_attribute('href')
            expected_attachment_path = f"/api/admin/v1/support/tickets/{ticket_id}/attachments/{fixture['attachmentObjectId']}/download"
            assert attachment_href == expected_attachment_path, f'Portal attachment link did not preserve ticket/object identity: {attachment_href}'
            await capture_screenshot(page, path=str(screenshots / '07-reloaded-support-history.png'), full_page=True)
            async with page.expect_download() as download_info:
                await attachment_link.click()
            attachment_download = await download_info.value
            downloaded_bytes = pathlib.Path(await attachment_download.path()).read_bytes()
            assert downloaded_bytes == __import__('base64').b64decode(fixture['attachmentBytesBase64']), 'Downloaded attachment bytes differ from the encrypted fixture.'
            downloaded_sha256 = __import__('hashlib').sha256(downloaded_bytes).hexdigest()
            assert downloaded_sha256 == fixture['attachmentSha256'], 'Downloaded ciphertext digest mismatch.'
            attachment_response = await page.evaluate(r'''async (href) => {
                const response = await apiFetch(href);
                const bytes = new Uint8Array(await response.arrayBuffer());
                return { status: response.status, objectId: response.headers.get('x-object-id'), digest: response.headers.get('digest'),
                    bytesBase64: btoa(Array.from(bytes, value => String.fromCharCode(value)).join('')) };
            }''', attachment_href)
            assert attachment_response['status'] == 200, f'Authorized linked attachment API returned {attachment_response["status"]}.'
            assert attachment_response['objectId'] == fixture['attachmentObjectId'], 'Download response object identity does not match the persisted link.'
            assert attachment_response['digest'] == 'sha-256=' + __import__('base64').b64encode(bytes.fromhex(fixture['attachmentSha256'])).decode('ascii'), 'Download ciphertext digest did not match persisted metadata.'
            assert __import__('base64').b64decode(attachment_response['bytesBase64']) == downloaded_bytes, 'Anchor download and same-origin API bytes differed.'
            unlinked_result = await page.evaluate(r'''async ({ ticketId, objectId }) => {
                const response = await apiFetch(`/api/admin/v1/support/tickets/${ticketId}/attachments/${objectId}/download`);
                const body = await response.json();
                return { status: response.status, code: body.code };
            }''', { 'ticketId': fixture['unlinkedTicketId'], 'objectId': fixture['attachmentObjectId'] })
            assert unlinked_result == { 'status': 404, 'code': 'SUPPORT_ATTACHMENT_NOT_FOUND' }, \
                f'Unlinked ticket could access another ticket attachment: {unlinked_result}'

            await page.locator('[data-view="customer360"]').click()
            await page.locator('#customer-search-input').fill(fixture['shopId'])
            await page.locator('#btn-customer-search').click()
            await page.locator('#customer-360-details').get_by_text(fixture['browserUploadLicenseId'], exact=True).wait_for()
            await page.locator(f"[data-license-id='{fixture['browserUploadLicenseId']}']").get_by_role('button', name='Review revocation impact').click()
            await page.wait_for_function("document.querySelector('#license-lifecycle-preview-status')?.textContent?.includes('validated the current persisted target')")
            await page.locator('#license-lifecycle-final-reason').fill('Synthetic browser proof: approved license revocation workflow.')
            lifecycle_final_totp = current_totp(auth_mfa_secret)
            secrets_to_redact.append(lifecycle_final_totp)
            await page.locator('#license-lifecycle-final-totp').fill(lifecycle_final_totp)
            revocation_response_count_before_execute = len(events['responses'])
            await page.locator('#license-lifecycle-execute').click()
            await page.wait_for_function(r'''() => {
                const status = document.querySelector('#license-lifecycle-final-status')?.textContent || '';
                return status.includes('Central confirmed revocation') || status.includes('Too many requests. Please retry later.');
            }''')
            revocation_status = await page.locator('#license-lifecycle-final-status').inner_text()
            rate_limit_recovered = False
            if 'Too many requests. Please retry later.' in revocation_status:
                limited_step_up = [response for response in events['responses'][revocation_response_count_before_execute:]
                    if response['url'].endswith('/api/admin/v1/auth/step-up') and response['status'] == 429]
                assert limited_step_up, f'Portal reported a rate limit without a matching step-up 429 response: {revocation_status}'
                assert not any(response['url'].endswith(f'/api/admin/v1/licenses/{fixture["browserUploadLicenseId"]}/revoke')
                    for response in events['responses'][revocation_response_count_before_execute:]), \
                    'Rate-limited step-up must have no license-revocation request or effect.'
                await page.wait_for_timeout((int(os.environ['CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS']) + 1) * 1000)
                lifecycle_final_totp = current_totp(auth_mfa_secret)
                secrets_to_redact.append(lifecycle_final_totp)
                await page.locator('#license-lifecycle-final-totp').fill(lifecycle_final_totp)
                retry_response_count_before_execute = len(events['responses'])
                await page.locator('#license-lifecycle-execute').click()
                await page.wait_for_function("document.querySelector('#license-lifecycle-final-status')?.textContent?.includes('Central confirmed revocation')")
                assert any(response['url'].endswith('/api/admin/v1/auth/step-up') and response['status'] == 200
                    for response in events['responses'][retry_response_count_before_execute:]), \
                    'Fresh step-up retry after the configured rate window did not succeed.'
                rate_limit_recovered = True
            assert any(response['url'].endswith(f'/api/admin/v1/licenses/{fixture["browserUploadLicenseId"]}/revoke') and response['status'] == 200 for response in events['responses']), 'Approved license revocation did not reach the canonical server route.'
            await page.locator('#license-lifecycle-close').click()
            await page.locator('#customer-360-details').get_by_text('REVOKED', exact=True).wait_for()
            await capture_screenshot(page, path=str(screenshots / '28-license-revocation-confirmed.png'), full_page=True)

            await page.locator('#btn-logout').click()
            await page.locator('#auth-wrapper').wait_for(state='visible')
            assert await page.locator('#app-shell').is_hidden(), 'Logout left the protected portal visible.'
            await capture_screenshot(page, path=str(screenshots / '10-logout-revoked.png'), full_page=True)
            await page.reload(wait_until='domcontentloaded')
            await page.locator('#auth-wrapper').wait_for(state='visible')
            assert await page.locator('#app-shell').is_hidden(), 'Revoked session restored portal access after reload.'
            post_logout_session = await page.evaluate(r'''async () => {
                const response = await apiFetch('/api/admin/v1/portal/session');
                return { status: response.status };
            }''')
            assert post_logout_session == { 'status': 401 }, \
                f'Central did not reject the revoked session after logout: {post_logout_session}'

            assert not events['pageErrors'], f'Browser page errors: {events["pageErrors"]}'
            for marker in ['/api/admin/v1/queues/SUPPORT_INBOX', '/api/admin/v1/dashboard/customer360/',
                '/api/admin/v1/support/tickets', '/api/admin/v1/notifications/status',
                '/api/admin/v1/dashboard/metrics', '/api/admin/v1/customers', '/api/admin/v1/shops',
                '/api/admin/v1/search?', '/api/commercial/v1/catalog-items',
                '/api/admin/v1/pricing-snapshots', '/api/admin/v1/orders', '/api/admin/v1/sessions?',
                '/api/admin/v1/queues/ACTIVATION_REVIEW', '/api/admin/v1/queues/PAYMENT_REVIEW']:
                assert any(marker in response['url'] and 200 <= response['status'] < 300 for response in events['responses']), f'No successful Central API response for {marker}'
            assert any('/api/admin/v1/sensitive-approvals/' + fixture['pendingApprovalId'] + '/decide' in response['url'] and
                200 <= response['status'] < 300 for response in events['responses']), 'Independent approval did not reach the canonical decision route.'
            assert any(f"/api/admin/v1/payments/{fixture['paymentEvidenceId']}/review" in response['url'] and
                response['status'] == 200 for response in events['responses']), 'Payment review did not reach the canonical Central route.'
            assert any('/api/admin/v1/support/tickets' in response['url'] and response['status'] == 400 for response in events['responses']), 'Cross-shop attachment creation was not rejected by Central.'
            assert any('/attachments/' in response['url'] and '/download' in response['url'] and response['status'] == 200 for response in events['responses']), 'Linked ciphertext attachment download was not authorized.'
            assert any('/attachments/' in response['url'] and '/download' in response['url'] and response['status'] == 404 for response in events['responses']), 'Unlinked attachment download was not denied.'
            assert any(f"/api/admin/v1/licenses/{fixture['browserUploadLicenseId']}/addons/{fixture['addonRenewalEntitlementId']}/renew" in response['url'] and response['status'] == 200 for response in events['responses']), 'Manual add-on renewal did not reach the authorized Central route.'
            upload_chunk_events = [response for response in events['responses'] if '/api/licensing/v1/uploads/' in response['url']]
            assert [response['status'] for response in upload_chunk_events].count(409) == 1, 'Browser upload did not record exactly one denied conflicting chunk retry.'
            assert [response['status'] for response in upload_chunk_events].count(200) == 3, 'Browser upload did not persist initial, repeated, and resumed chunks.'
            assert any('/auth/login' in response['url'] and response['status'] == 401 for response in events['responses']), 'Invalid password was not denied by Central.'
            assert any('/auth/login' in response['url'] and 200 <= response['status'] < 300 for response in events['responses']), 'Valid login did not issue an MFA challenge.'
            assert any('/auth/mfa' in response['url'] and 200 <= response['status'] < 300 for response in events['responses']), 'MFA proof was not accepted.'
            assert any('/auth/logout' in response['url'] and 200 <= response['status'] < 300 for response in events['responses']), 'Logout was not acknowledged.'
            assert any('/portal/session' in response['url'] and response['status'] == 401 for response in events['responses']), 'Revoked session was accepted after logout.'
            expected_401s = [response for response in events['responses'] if response['status'] == 401 and
                ('/portal/session' in response['url'] or '/auth/login' in response['url'])]
            assert len(expected_401s) >= 3, f'Expected initial anonymous, invalid-password, and post-logout denials; observed {len(expected_401s)}.'
            console_statuses = [int(match.group(1)) for error in events['consoleErrors']
                if (match := re.search(r'status of (\d+)', error))]
            rate_limited = [(index, response) for index, response in enumerate(events['responses']) if response['status'] == 429]
            assert all(response['url'].endswith('/api/admin/v1/auth/step-up') for _, response in rate_limited), \
                f'Unexpected API route was rate limited during the browser journey: {rate_limited}'
            for index, _ in rate_limited:
                retry_success_index = next((later_index for later_index, response in enumerate(events['responses'][index + 1:], start=index + 1)
                    if response['url'].endswith('/api/admin/v1/auth/step-up') and response['status'] == 200), None)
                assert retry_success_index is not None, \
                    'A rate-limited step-up was not followed by an accepted retry.'
                rate_limit_recovered = True
                assert not any(response['url'].endswith(f'/api/admin/v1/licenses/{fixture["browserUploadLicenseId"]}/revoke')
                    for response in events['responses'][index + 1:retry_success_index]), \
                    'License revocation proceeded without a successful step-up after rate limiting.'
            expected_console_statuses = Counter([401] * console_statuses.count(401) + [400, 404, 409, 409])
            actual_console_statuses = Counter(console_statuses)
            console_rate_limit_count = actual_console_statuses.pop(429, 0)
            assert console_rate_limit_count <= len(rate_limited) and actual_console_statuses == expected_console_statuses, \
                f'Browser console failures did not match expected authorization and conflict denials or observed step-up rate limits: expected={expected_console_statuses}, actual={Counter(console_statuses)}, rateLimited={len(rate_limited)}, recovered={rate_limit_recovered}, errors={events["consoleErrors"]}'
            await context.tracing.stop(path=str(evidence / 'portal-trace.zip'))
            redact_trace()
            payload = {"result": "PASS", "ticketId": ticket_id, "subject": subject, "replyText": reply_text,
                "rateLimitRecovered": rate_limit_recovered,
                "supportMarkupRenderedAsText": support_markup_rendered_as_text,
                "sessionCustodyBrowserSafe": session_custody == { 'seededLegacyValue': 'synthetic-stale-session-value', 'authenticationKeys': [], 'scriptCanReadCookie': False },
                "crossOriginMutationDenied": cross_origin_response.status in (401, 403),
                "crossOriginRequestOrigin": cross_origin_request_origin,
                "crossOriginCookieExcluded": cross_origin_cookie_excluded,
                "crossOriginMutationStatus": cross_origin_response.status,
                "expiredSessionDenied": expired_session_read['status'] == 401 and expired_session_mutation['status'] == 401,
                "expiredSessionReadStatus": expired_session_read['status'],
                "expiredSessionMutationStatus": expired_session_mutation['status'],
                "crossOriginMutationMarker": cross_origin_mutation_marker,
                "pendingApprovalId": fixture['pendingApprovalId'], "piiAuditRecordId": fixture['sensitiveAuditRecordId'],
                "approvalDecision": "APPROVE", "approvalDecisionConfirmed": True,
                "hardwareCommandReplay": hardware_command_replay,
                "staleHardwareCommand": stale_hardware_command,
                "initialPendingApprovalsCount": initial_dashboard_api['body']['pendingApprovalsCount'],
                "activationRequestId": fixture['activationRequestId'], "activationDecision": "REQUEST_INFORMATION",
                "sessionRevocationTargetId": fixture['targetSessionId'], "sessionRevocationConfirmed": True,
                "piiMarkupRenderedAsText": fixture['sensitiveCustomerName'] in pii_result_text and await page.locator('#pii-unmask-result img').count() == 0,
                "piiClearedOnClose": pii_cleared_on_close, "piiClearedOnNavigation": pii_cleared_on_navigation,
                "orderId": order_id, "dashboardMetrics": {"api": dashboard_api['body'], "rendered": rendered_metrics},
                "paymentEvidenceId": fixture['paymentEvidenceId'], "paymentReviewOrderId": fixture['paymentReviewOrderId'],
                "paymentReviewDecision": "APPROVE", "paymentReviewPersisted": True,
                "licenseIssueAvailableBeforeManualSubmit": license_issue_available_before_manual_submit,
                "manualLicenseIssueConfirmed": True, "manualLicenseIssueOrderId": issue_order_id,
                "manualLicenseIssueId": manually_issued_license_id,
                "expiredLicenseApprovalRepreviewNoMutation": True,
                "licenseRevocationConfirmed": True, "licenseRevocationId": fixture['browserUploadLicenseId'],
                "licenseRevocationApprovalId": lifecycle_approval_id, "licenseRevocationApprover": approver_username,
                "addonRenewalConfirmed": True, "addonRenewalOrderId": fixture['addonRenewalOrderId'],
                "addonRenewalExpiryUtc": fixture['addonRenewalExpiryUtc'],
                "createdDirectoryCustomerId": created_customer_id, "createdDirectoryShopId": created_shop_id,
                "queuePageOne": page_one, "queuePageTwo": page_two, "capabilities": capabilities,
                "displayedUsername": auth_username,
                "workerObservation": worker_observation,
                "degradedWorkerObservation": degraded_worker_observation,
                "workerPoisonMessageId": worker_poison_message_id,
                "workerDegradedApiStatus": degraded_worker_body['status'],
                "workerDegradedCacheControl": degraded_worker_headers.get('cache-control', ''),
                "workerPoisonRetryCount": worker_probe_result['retryCount'],
                "workerPoisonMaxAttempts": worker_probe_result['maxAttempts'],
                "workerPoisonConsumerEffectCount": worker_probe_result['consumerEffectCount'],
                "workerPoisonNotificationCount": worker_probe_result['notificationCount'],
                "crossShopCreateDenied": cross_shop_result['status'] == 400 and cross_shop_result['code'] == 'SUPPORT_ATTACHMENT_ACCESS_DENIED',
                "unlinkedDownloadDenied": unlinked_result['status'] == 404 and unlinked_result['code'] == 'SUPPORT_ATTACHMENT_NOT_FOUND',
                "attachmentObjectId": fixture['attachmentObjectId'],
                "uploadedCiphertextObjectId": upload_result['objectId'],
                "uploadedCiphertextUploadId": upload_result['uploadId'],
                "uploadedCiphertextSha256": upload_result['ciphertextSha256'],
                "ciphertextUploadResumed": upload_result['acceptedOffset'] == upload_result['contentLength'] and upload_result['exactRetryAccepted'],
                "ciphertextUploadConflictingRetryDenied": upload_result['conflictingRetryRejected'],
                "supportStatus": support_status, "supportStatusPersistedAfterReload": True,
                "downloadedCiphertextLength": len(downloaded_bytes),
                "downloadedCiphertextSha256": downloaded_sha256,
                "checks": ["favicon-ico-branded-svg-alias", "login-mfa-session-expiry-denied", "login-mfa-session-logout", "cookie-bound-step-up-session-derivation", "session-identity", "http-only-session-custody", "cross-origin-mutation-denied", "auth-trace-secret-redaction", "authoritative-nonzero-dashboard-metrics", "session", "queue-paging", "queue-common-fields-dashboard", "queue-common-fields-licensing", "customer-360", "directory-customer-shop-create-search", "expired-license-approval-repreview-no-mutation", "license-revocation-stepup-separate-approval-mutation", "recovery-worker-status", "recovery-worker-degraded-after-dead-letter", "pending-approval-queue-visible", "independent-approval-decision-persisted", "activation-review-request-information-persisted", "audited-pii-reveal-injection-safe", "sensitive-data-cleared-on-close-and-navigation", "hardware-transfer-evidence-cleared-on-close", "hardware-transfer-independent-approval-browser-persisted", "hardware-transfer-execution-mfa-session-bound", "hardware-information-and-transfer-cancellation-persisted", "hardware-command-replay-no-duplicate-effect", "hardware-stale-revision-denied", "approved-primary-transfer-completed-from-portal", "closed-hardware-preview-cannot-submit", "request-mfa-new-input-survives-old-preview", "superseded-preview-sends-no-stale-mfa", "authenticated-browser-session-revocation", "commercial-catalog", "price-snapshot", "commercial-order-created", "payment-review-persisted", "license-issue-requires-separate-manual-action", "license-issue-persisted", "manual-addon-renewal-and-lifetime-conservation", "ciphertext-upload-resume-integrity-retention", "support-create", "support-attachment-download", "support-attachment-cross-shop-denial", "support-attachment-unlinked-download-denial", "support-reply", "support-status-persisted", "reload-persistence", "console-network"], "events": events}
            result_path.write_text(json.dumps(payload, indent=2), encoding='utf-8')
            (evidence / 'browser-events.json').write_text(json.dumps(events, indent=2), encoding='utf-8')
            print(json.dumps({"result": "PASS", "ticketId": ticket_id, "checks": payload['checks']}))
    except Exception as error:
        if page is not None:
            try: await capture_screenshot(page, path=str(screenshots / 'failure.png'), full_page=True)
            except Exception: pass
        if context is not None:
            try: await context.tracing.stop(path=str(evidence / 'portal-trace.zip'))
            except Exception: pass
        try: redact_trace()
        except Exception: pass
        payload = {"result": "FAIL", "message": str(error), "traceback": traceback.format_exc(), "events": events}
        result_path.write_text(json.dumps(payload, indent=2), encoding='utf-8')
        (evidence / 'browser-events.json').write_text(json.dumps(events, indent=2), encoding='utf-8')
        print(json.dumps(payload), file=sys.stderr)
        raise
    finally:
        if browser is not None:
            await browser.close()

asyncio.run(main())
""";
}
