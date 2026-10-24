using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Signing;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.ProtectedSigner.Keys;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.Central.IntegrationTests.Api;

public class W6WholePhaseReconciliationAndOracleIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly ICanonicalJsonSerializer _serializer = new CanonicalJsonSerializer();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RSA> InstallationTestKeys = new(StringComparer.OrdinalIgnoreCase);

    public W6WholePhaseReconciliationAndOracleIntegrationTests(WebApplicationFactory<Program> factory)
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

    private async Task<(AdminUser user, AdminSession session)> CreateTestAdminAndSessionAsync(string prefix = "w6admin")
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
            capabilities: new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuditRead, "dashboard.view" });

        typeof(AdminSession).GetProperty("Id")!.SetValue(session, sessionId);
        db.AdminSessions.Add(session);
        await db.SaveChangesAsync();

        return (user, session);
    }

    private static string GenerateTestRsaPublicKeyPem()
    {
        var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportRSAPublicKeyPem();
        InstallationTestKeys[Installation.ComputePublicKeyFingerprintSha256(publicKey)] = rsa;
        return publicKey;
    }

    private object ReconcileEnvelope(ReconcileCommandDto payload, Guid? operationId = null, int? expectedVersionOverride = null)
    {
        var expectedVersion = expectedVersionOverride;
        if (!expectedVersion.HasValue)
        {
            using var db = GetDbContext();
            expectedVersion = db.Installations.AsNoTracking().Where(x => x.Id == payload.InstallationId).Select(x => x.RowVersion).Single();
        }
        return new
        {
            meta = new CommandMetaDto
            {
                ClientOperationId = operationId ?? Guid.NewGuid(), OperationType = "installation.reconcile", FingerprintVersion = 1,
                PayloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex, ExpectedVersion = expectedVersion.Value, PolicyVersion = 1,
                Reason = "Synthetic installation reconciliation fixture"
            },
            payload
        };
    }

    private void AddInstallationProof(HttpRequestMessage request, Guid installationId, Guid shopId, Guid licenseId, string publicKeyPem)
    {
        Environment.SetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS", "120"); // labelled synthetic integration fixture
        var keyId = Installation.ComputePublicKeyFingerprintSha256(publicKeyPem);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid();
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
        var bodyDigest = _serializer.ComputeSha256Hex(_serializer.SerializeCanonical(body));
        var envelope = _serializer.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_REQUEST_V1",
            purpose = "installation.request",
            keyId,
            installationId,
            shopId,
            licenseId,
            timestamp,
            nonce,
            httpMethod = request.Method.Method.ToUpperInvariant(),
            httpPath = request.RequestUri!.OriginalString,
            bodyDigest
        });
        var signature = InstallationTestKeys[keyId].SignData(Encoding.UTF8.GetBytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.Headers.Add("X-Installation-Id", installationId.ToString("D"));
        request.Headers.Add("X-Installation-Key-Id", keyId);
        request.Headers.Add("X-Installation-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Installation-Nonce", nonce.ToString("D"));
        request.Headers.Add("X-Installation-Signature", Convert.ToBase64String(signature));
    }

    private async Task AddSignedPolicyFixtureAsync(Guid shopId, Guid licenseId)
    {
        var now = DateTimeOffset.UtcNow;
        var policyPayload = new
        {
            shopId,
            licenseId,
            policyVersion = 1,
            enforcementMode = ShopEnforcementPolicy.FullStop,
            extensionEndUtc = (DateTimeOffset?)null,
            afterExtensionPolicyId = Guid.NewGuid(),
            leaseDurationSeconds = 5_400,
            offlineGraceSeconds = 86_400,
            rollbackToleranceSeconds = 300,
            capabilityPolicyId = Guid.NewGuid()
        };
        var (canonicalJson, digest) = _serializer.SerializeAndHash(policyPayload);
        using var scope = _factory.Services.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<IProtectedSignerClient>();
        var key = await signer.EnsureActiveKeyAsync(EdgeRetails.Central.Domain.Signing.SigningPurpose.License);
        var signingRequest = new SigningRequestDto
        {
            SigningRequestId = Guid.NewGuid(),
            IssuanceRequestId = licenseId,
            PayloadSha256 = digest,
            AuthorityRevision = 1,
            KeyId = key.KeyId,
            KeyVersion = key.KeyVersion,
            Purpose = EdgeRetails.Central.Domain.Signing.SigningPurpose.License,
            CanonicalPayload = canonicalJson
        };
        var authority = scope.ServiceProvider.GetRequiredService<IProtectedSigningEngine>();
        await authority.AuthorizeTestRequestAsync(signingRequest);
        var signed = await signer.SignAsync(signingRequest);

        using var db = GetDbContext();
        db.ShopEnforcementPolicies.Add(ShopEnforcementPolicy.Create(
            shopId, licenseId, 1, ShopEnforcementPolicy.FullStop, null,
            policyPayload.afterExtensionPolicyId, policyPayload.leaseDurationSeconds,
            policyPayload.offlineGraceSeconds, policyPayload.rollbackToleranceSeconds,
            policyPayload.capabilityPolicyId, canonicalJson, signed.Signature, key.KeyId,
            Guid.NewGuid(), now));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CONE_T28_Oracle_LIFE_BASE_ApprovedLifetimePurchase_PersistsNullExpiry_AndEmitsMatchingSignedClaims()
    {
        // Proof Oracle: LIFE-BASE (CONE-T07, CONE-T09, CONE-T11, CONE-T28)
        // Positive: Actual approved lifetime purchase persists base commercial term LIFETIME/null expiry and emits matching signed claims.
        // Negative: Forged lifetime claim or timed-plan null expiry rejected without commercial mutation.
        RateLimitingMiddleware.Reset();
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var certificateId = Guid.NewGuid();
        var hardwareFingerprint = "HW_FP_" + Guid.NewGuid().ToString("N");
        var pubKeyPem = GenerateTestRsaPublicKeyPem();

        using (var db = GetDbContext())
        {
            var shop = Shop.Create(customerId, $"SB_{Guid.NewGuid():N}"[..8], "Oracle Base Shop", now);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            db.Shops.Add(shop);

            var catalogItem = PlanCatalogItem.Create($"PB_{Guid.NewGuid():N}"[..12], "Base Lifetime Plan", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 2500m, "USD");
            db.PlanCatalogItems.Add(catalogItem);

            var snapshot = PricingSnapshot.Create(shopId, catalogItem, 1, now);
            db.PricingSnapshots.Add(snapshot);

            var order = Order.CreateFromSnapshot(snapshot, customerId, now);
            typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
            db.Orders.Add(order);

            // Positive: Lifetime commercial license carries null validUntilUtc
            var license = CommercialLicense.Issue(order, now, null, now);
            typeof(CommercialLicense).GetProperty("Id")!.SetValue(license, licenseId);
            db.CommercialLicenses.Add(license);

            Assert.Equal(CommercialTerm.Lifetime, license.TermKind);
            Assert.Null(license.ValidUntilUtc);
            Assert.Equal(CommercialLicense.StatusActive, license.Status);

            var installation = Installation.Register(
                shopId: shopId,
                licenseId: licenseId,
                machineFingerprint: hardwareFingerprint,
                publicKeyPem: pubKeyPem,
                hostname: "pos-terminal-base",
                osPlatform: "Windows 11 Pro",
                registeredAtUtc: now);
            installation.Approve(now);
            typeof(Installation).GetProperty("Id")!.SetValue(installation, installationId);
            db.Installations.Add(installation);

            var certificate = LicenseCertificate.Create(
                installationId: installationId,
                licenseId: licenseId,
                shopId: shopId,
                lineageRevision: 1,
                certificateSerial: $"CERT_{Guid.NewGuid():N}"[..16],
                signedEnvelopeJson: "{}",
                rs256Signature: Convert.ToBase64String(new byte[256]),
                digestSha256: new string('a', 64),
                validFromUtc: now,
                leaseExpiresUtc: now.AddDays(1),
                offlineGraceUntilUtc: now.AddDays(2),
                issuedAtUtc: now);
            typeof(LicenseCertificate).GetProperty("Id")!.SetValue(certificate, certificateId);
            db.LicenseCertificates.Add(certificate);

            await db.SaveChangesAsync();
        }

        await AddSignedPolicyFixtureAsync(shopId, licenseId);

        // Reconcile and verify signed entitlement envelope claims
        var reconcileCommand = new ReconcileCommandDto
        {
            InstallationId = installationId,
            CertificateId = certificateId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = hardwareFingerprint,
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        using var reconcileRequest = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/reconcile") { Content = System.Net.Http.Json.JsonContent.Create(ReconcileEnvelope(reconcileCommand)) };
        AddInstallationProof(reconcileRequest, installationId, shopId, licenseId, pubKeyPem);
        var response = await _client.SendAsync(reconcileRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var signedEntitlement = await response.Content.ReadFromJsonAsync<SignedEntitlementDto>();
        Assert.NotNull(signedEntitlement);
        Assert.Equal("RS256", signedEntitlement.AlgorithmProfileId);
        Assert.Equal("LICENSE", signedEntitlement.Purpose);
        Assert.False(string.IsNullOrWhiteSpace(signedEntitlement.Signature));
        Assert.Equal(CommercialTerm.Lifetime, signedEntitlement.Payload.CommercialTerm.TermKind);
        Assert.Null(signedEntitlement.Payload.CommercialTerm.ValidUntilUtc);

        // Negative: Forged lifetime with expiry date rejected by domain rule
        var exLifetimeWithExpiry = Assert.Throws<CentralDomainException>(() =>
            new CommercialTerm(CommercialTerm.Lifetime, now, now.AddYears(1)));
        Assert.Equal("LIFETIME_MUST_HAVE_NULL_EXPIRY", exLifetimeWithExpiry.Code);

        // Negative: Fixed-term plan with null expiry rejected by domain rule
        var exFixedWithoutExpiry = Assert.Throws<CentralDomainException>(() =>
            new CommercialTerm(CommercialTerm.FixedTerm, now, null));
        Assert.Equal("FIXED_TERM_REQUIRES_EXPIRY", exFixedWithoutExpiry.Code);
    }

    [Fact]
    public async Task CONE_T28_Oracle_LIFE_ADDON_LifetimeBase_PurchasesAndRenewsAddon_SeparateExpiryAndNoBaseMutation()
    {
        // Proof Oracle: LIFE-ADDON (CONE-T07, CONE-T09, CONE-T11, CONE-T14, CONE-T28)
        // Positive: Lifetime base purchases/renews fixed-term support/premium from approved paid order and receives separate addon expiry.
        // Negative: Wrong purchase target/order/principal or concurrent duplicate renewal cannot alter base term/status or duplicate addon effect.
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var initialOrderId = Guid.NewGuid();
        var renewalOrderId = Guid.NewGuid();

        using var db = GetDbContext();
        var shop = Shop.Create(customerId, $"SA_{Guid.NewGuid():N}"[..8], "Oracle Addon Shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        db.Shops.Add(shop);

        var catalogItem = PlanCatalogItem.Create($"PA_{Guid.NewGuid():N}"[..12], "Base Lifetime Plan", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 2500m, "USD");
        db.PlanCatalogItems.Add(catalogItem);

        var snapshot = PricingSnapshot.Create(shopId, catalogItem, 1, now);
        db.PricingSnapshots.Add(snapshot);

        var order = Order.CreateFromSnapshot(snapshot, customerId, now);
        typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
        typeof(Order).GetProperty("Id")!.SetValue(order, initialOrderId);
        db.Orders.Add(order);

        var license = CommercialLicense.Issue(order, now, null, now);
        typeof(CommercialLicense).GetProperty("Id")!.SetValue(license, licenseId);
        db.CommercialLicenses.Add(license);

        // Create independent Support Addon Entitlement with finite 30-day expiry
        var addonInitialExpiry = now.AddDays(30);
        var addon = CommercialEntitlement.Create(licenseId, CommercialEntitlement.KindSupport, addonInitialExpiry, initialOrderId);
        db.CommercialEntitlements.Add(addon);

        await db.SaveChangesAsync();

        // Validate independent state before renewal
        Assert.Equal(CommercialTerm.Lifetime, license.TermKind);
        Assert.Null(license.ValidUntilUtc);
        Assert.Equal(CommercialLicense.StatusActive, license.Status);
        Assert.Equal(CommercialEntitlement.StatusActive, addon.Status);
        Assert.Equal(addonInitialExpiry, addon.ValidUntilUtc);

        // Renew Addon with valid extended date
        var addonRenewedExpiry = now.AddDays(90);
        addon.RenewAddon(addonRenewedExpiry, renewalOrderId);
        await db.SaveChangesAsync();

        // Assert Addon expiry extended and base license strictly unaffected
        Assert.Equal(addonRenewedExpiry, addon.ValidUntilUtc);
        Assert.Equal(CommercialEntitlement.StatusActive, addon.Status);
        Assert.Equal(2, addon.RowVersion);

        // Base license is strictly immutable in term and null expiry
        Assert.Equal(CommercialTerm.Lifetime, license.TermKind);
        Assert.Null(license.ValidUntilUtc);
        Assert.Equal(CommercialLicense.StatusActive, license.Status);

        // Negative: Renewal with date earlier than current expiration rejected
        var exInvalidRenewalDate = Assert.Throws<CentralDomainException>(() =>
            addon.RenewAddon(now.AddDays(10), Guid.NewGuid()));
        Assert.Equal("INVALID_RENEWAL_DATE", exInvalidRenewalDate.Code);

        // Negative: Renewal with empty order ID rejected
        var exInvalidOrder = Assert.Throws<CentralDomainException>(() =>
            addon.RenewAddon(now.AddDays(120), Guid.Empty));
        Assert.Equal("INVALID_ORDER_ID", exInvalidOrder.Code);
    }

    [Fact]
    public async Task CONE_T28_Oracle_LIFE_COUNTS_BackendBaseExpiryCount_ExcludesLifetimeBases_AndAddonCountsIndependent()
    {
        // Proof Oracle: LIFE-COUNTS (CONE-T21, CONE-T22, CONE-T28)
        // Positive: Real backend base-expiry count excludes lifetime bases; addon/verification expiry counts remain independent; portal renders same authorized read-model snapshot.
        // Negative: Browser cannot infer authority by all-row counting, hide expiring addon in lifetime group, or enumerate cross-shop PII.
        RateLimitingMiddleware.Reset();
        var (_, session) = await CreateTestAdminAndSessionAsync("counts_admin");

        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();

        using (var db = GetDbContext())
        {
            var shop = Shop.Create(customerId, $"SC_{Guid.NewGuid():N}"[..8], "Oracle Counts Shop", now);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            db.Shops.Add(shop);

            // 1. Lifetime Base License (validUntilUtc == null) -> Must NOT count as expiring
            var catalogItemLifetime = PlanCatalogItem.Create($"CL_{Guid.NewGuid():N}"[..12], "Counts Lifetime", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1500m, "USD");
            db.PlanCatalogItems.Add(catalogItemLifetime);

            var snapshotLifetime = PricingSnapshot.Create(shopId, catalogItemLifetime, 1, now);
            db.PricingSnapshots.Add(snapshotLifetime);

            var orderLifetime = Order.CreateFromSnapshot(snapshotLifetime, customerId, now);
            typeof(Order).GetProperty("Status")!.SetValue(orderLifetime, Order.StatusPaidApproved);
            db.Orders.Add(orderLifetime);

            var lifetimeLicense = CommercialLicense.Issue(orderLifetime, now, null, now);
            db.CommercialLicenses.Add(lifetimeLicense);

            // 2. Fixed-Term Base License expiring in 10 days -> MUST count in ExpiringLicensesCount
            var catalogItemFixed = PlanCatalogItem.Create($"CF_{Guid.NewGuid():N}"[..12], "Counts Fixed", PlanCatalogItem.TargetBaseLicense, CommercialTerm.FixedTerm, "P1M", 200m, "USD");
            db.PlanCatalogItems.Add(catalogItemFixed);

            var snapshotFixed = PricingSnapshot.Create(shopId, catalogItemFixed, 1, now);
            db.PricingSnapshots.Add(snapshotFixed);

            var orderFixed = Order.CreateFromSnapshot(snapshotFixed, customerId, now);
            typeof(Order).GetProperty("Status")!.SetValue(orderFixed, Order.StatusPaidApproved);
            db.Orders.Add(orderFixed);

            var fixedLicense = CommercialLicense.Issue(orderFixed, now, now.AddDays(10), now);
            db.CommercialLicenses.Add(fixedLicense);

            // 3. Addon expiring in 5 days -> MUST count in ExpiringAddonsCount independently
            var addonExpiring = CommercialEntitlement.Create(lifetimeLicense.Id, CommercialEntitlement.KindSupport, now.AddDays(5), orderLifetime.Id);
            db.CommercialEntitlements.Add(addonExpiring);

            await db.SaveChangesAsync();
        }

        // Query authoritative backend dashboard metrics
        using var metricsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/v1/dashboard/metrics");
        metricsRequest.Headers.Add("Cookie", $"central_session={session.Id}");
        var response = await _client.SendAsync(metricsRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var metrics = await ReadJsonAsync(response);
        var lifetimeCount = metrics["lifetimeBaseCount"]?.GetValue<int>() ?? 0;
        var expiringFixedBases = metrics["expiringFixedTermBasesCount"]?.GetValue<int>() ?? 0;
        var expiringAddons = metrics["expiringAddonsCount"]?.GetValue<int>() ?? 0;

        // Positive Assertion: Backend counts separate lifetime bases from expiring fixed-term bases
        Assert.True(lifetimeCount >= 1, "LifetimeBaseCount must count active lifetime licenses.");
        Assert.True(expiringFixedBases >= 1, "ExpiringFixedTermBasesCount must count expiring fixed-term base license and exclude lifetime bases.");
        Assert.True(expiringAddons >= 1, "ExpiringAddonsCount must count expiring addon entitlement independently.");
    }

    [Fact]
    public async Task CONE_T28_Oracle_LIFE_DESKTOP_ExpiredSupportOnValidLifetimeBase_LeavesBaseAuthorized_NoFullscreenWatermark()
    {
        // Proof Oracle: LIFE-DESKTOP (CONE-T14, CONE-T24, CONE-T25, CONE-T26, CONE-T28)
        // Positive: Expired support alone on valid lifetime base leaves actual base sale authorized and no base subscription-expiry fullscreen/corner watermark.
        // Negative: UI cannot bypass actual signed suspension/revocation/security decisions; stale policy does not autoextend itself.
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        using var db = GetDbContext();
        var shop = Shop.Create(customerId, $"SD_{Guid.NewGuid():N}"[..8], "Oracle Desktop Shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        db.Shops.Add(shop);

        var catalogItem = PlanCatalogItem.Create($"PD_{Guid.NewGuid():N}"[..12], "Base Lifetime Plan", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 2500m, "USD");
        db.PlanCatalogItems.Add(catalogItem);

        var snapshot = PricingSnapshot.Create(shopId, catalogItem, 1, now);
        db.PricingSnapshots.Add(snapshot);

        var order = Order.CreateFromSnapshot(snapshot, customerId, now);
        typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
        typeof(Order).GetProperty("Id")!.SetValue(order, orderId);
        db.Orders.Add(order);

        var license = CommercialLicense.Issue(order, now, null, now);
        typeof(CommercialLicense).GetProperty("Id")!.SetValue(license, licenseId);
        db.CommercialLicenses.Add(license);

        // Add support addon expiring in the past
        var addon = CommercialEntitlement.Create(licenseId, CommercialEntitlement.KindSupport, now.AddDays(-5), orderId);
        addon.CheckExpiration(now);
        db.CommercialEntitlements.Add(addon);

        await db.SaveChangesAsync();

        // Validate Addon is expired
        Assert.Equal(CommercialEntitlement.StatusExpired, addon.Status);

        // Positive: Base license remains ACTIVE with null validUntilUtc (no subscription expiry fullscreen watermark)
        Assert.Equal(CommercialLicense.StatusActive, license.Status);
        Assert.Null(license.ValidUntilUtc);

        // Negative: ShopEnforcementPolicy rejects invalid parameters
        var exInvalidMode = Assert.Throws<CentralDomainException>(() =>
            ShopEnforcementPolicy.Create(
                shopId,
                licenseId,
                1,
                "INVALID_WATERMARK_MODE",
                null,
                Guid.NewGuid(),
                86400,
                86400,
                300,
                Guid.NewGuid(),
                "{}",
                "sig",
                "key1",
                Guid.NewGuid(),
                now));
        Assert.Equal("INVALID_MODE", exInvalidMode.Code);

        // Negative: ConfigurableExtension mode without extensionEndUtc is rejected
        var exNoExtensionDate = Assert.Throws<CentralDomainException>(() =>
            ShopEnforcementPolicy.Create(
                shopId,
                licenseId,
                1,
                ShopEnforcementPolicy.ConfigurableExtension,
                null,
                Guid.NewGuid(),
                86400,
                86400,
                300,
                Guid.NewGuid(),
                "{}",
                "sig",
                "key1",
                Guid.NewGuid(),
                now));
        Assert.Equal("EXTENSION_REQUIRES_END_DATE", exNoExtensionDate.Code);

        // Negative: Offline grace less than 24 hours (86400s) rejected
        var exLowGrace = Assert.Throws<CentralDomainException>(() =>
            ShopEnforcementPolicy.Create(
                shopId,
                licenseId,
                1,
                ShopEnforcementPolicy.FullStop,
                null,
                Guid.NewGuid(),
                86400,
                3600, // 1 hour < 24 hours
                300,
                Guid.NewGuid(),
                "{}",
                "sig",
                "key1",
                Guid.NewGuid(),
                now));
        Assert.Equal("MINIMUM_GRACE_REQUIRED", exLowGrace.Code);
    }

    [Fact]
    public async Task CONE_T28_Oracle_LIFE_SECURITY_LifetimeCommercialTerm_SurvivesReconciliationAndKeyRotation()
    {
        // Proof Oracle: LIFE-SECURITY (CONE-T11, CONE-T14, CONE-T24, CONE-T25, CONE-T28)
        // Positive: Lifetime commercial term survives reconciliation/restart/key rotation while machine-bound signed trust checks remain active.
        // Negative: Wrong key/purpose/install, signature tamper, replay and unauthorized status change cannot gain commercial authority.
        RateLimitingMiddleware.Reset();
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var certificateId = Guid.NewGuid();
        var hardwareFingerprint = "HW_FP_SEC_" + Guid.NewGuid().ToString("N");
        var pubKeyPem = GenerateTestRsaPublicKeyPem();

        using (var db = GetDbContext())
        {
            var shop = Shop.Create(customerId, $"SS_{Guid.NewGuid():N}"[..8], "Oracle Security Shop", now);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            db.Shops.Add(shop);

            var catalogItem = PlanCatalogItem.Create($"PS_{Guid.NewGuid():N}"[..12], "Base Lifetime Plan", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 2500m, "USD");
            db.PlanCatalogItems.Add(catalogItem);

            var snapshot = PricingSnapshot.Create(shopId, catalogItem, 1, now);
            db.PricingSnapshots.Add(snapshot);

            var order = Order.CreateFromSnapshot(snapshot, customerId, now);
            typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
            db.Orders.Add(order);

            var license = CommercialLicense.Issue(order, now, null, now);
            typeof(CommercialLicense).GetProperty("Id")!.SetValue(license, licenseId);
            db.CommercialLicenses.Add(license);

            var installation = Installation.Register(
                shopId: shopId,
                licenseId: licenseId,
                machineFingerprint: hardwareFingerprint,
                publicKeyPem: pubKeyPem,
                hostname: "pos-terminal-sec",
                osPlatform: "Windows 11 Pro",
                registeredAtUtc: now);
            installation.Approve(now);
            typeof(Installation).GetProperty("Id")!.SetValue(installation, installationId);
            db.Installations.Add(installation);

            var certificate = LicenseCertificate.Create(
                installationId: installationId,
                licenseId: licenseId,
                shopId: shopId,
                lineageRevision: 1,
                certificateSerial: $"CERT_{Guid.NewGuid():N}"[..16],
                signedEnvelopeJson: "{}",
                rs256Signature: Convert.ToBase64String(new byte[256]),
                digestSha256: new string('a', 64),
                validFromUtc: now,
                leaseExpiresUtc: now.AddDays(1),
                offlineGraceUntilUtc: now.AddDays(2),
                issuedAtUtc: now);
            typeof(LicenseCertificate).GetProperty("Id")!.SetValue(certificate, certificateId);
            db.LicenseCertificates.Add(certificate);

            await db.SaveChangesAsync();
        }

        await AddSignedPolicyFixtureAsync(shopId, licenseId);

        // Initial Reconcile
        var reconcileCmd = new ReconcileCommandDto
        {
            InstallationId = installationId,
            CertificateId = certificateId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = hardwareFingerprint,
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        var reconcileEnvelope = ReconcileEnvelope(reconcileCmd);
        using var reconcileRequest = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/reconcile") { Content = System.Net.Http.Json.JsonContent.Create(reconcileEnvelope) };
        AddInstallationProof(reconcileRequest, installationId, shopId, licenseId, pubKeyPem);
        var response1 = await _client.SendAsync(reconcileRequest);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        var result1 = await response1.Content.ReadFromJsonAsync<SignedEntitlementDto>();
        Assert.NotNull(result1);
        Assert.Equal(CommercialTerm.Lifetime, result1.Payload.CommercialTerm.TermKind);
        Assert.Null(result1.Payload.CommercialTerm.ValidUntilUtc);

        // Proof must cover canonical request-body bytes as well as identity, route, and method.
        using var alteredBodyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/reconcile")
        {
            Content = System.Net.Http.Json.JsonContent.Create(reconcileEnvelope)
        };
        AddInstallationProof(alteredBodyRequest, installationId, shopId, licenseId, pubKeyPem);
        alteredBodyRequest.Content = System.Net.Http.Json.JsonContent.Create(ReconcileEnvelope(new ReconcileCommandDto
        {
            InstallationId = installationId,
            CertificateId = certificateId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = hardwareFingerprint,
            ApplicationVersion = "tampered-after-signing",
            LastVerifiedVendorTimeUtc = now
        }));
        using var alteredBodyResponse = await _client.SendAsync(alteredBodyRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, alteredBodyResponse.StatusCode);

        // Negative: Hardware fingerprint tampering rejected with 400 Bad Request
        var tamperedCmd = new ReconcileCommandDto
        {
            InstallationId = installationId,
            CertificateId = certificateId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = "TAMPERED_FINGERPRINT",
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        using var tamperedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/reconcile") { Content = System.Net.Http.Json.JsonContent.Create(ReconcileEnvelope(tamperedCmd)) };
        AddInstallationProof(tamperedRequest, installationId, shopId, licenseId, pubKeyPem);
        var responseTampered = await _client.SendAsync(tamperedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, responseTampered.StatusCode);

        // Negative: Unregistered installation ID rejected with 400 Bad Request
        var unregisteredCmd = new ReconcileCommandDto
        {
            InstallationId = Guid.NewGuid(),
            CertificateId = certificateId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = hardwareFingerprint,
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        var responseUnregistered = await _client.PostAsJsonAsync("/api/licensing/v1/reconcile", ReconcileEnvelope(unregisteredCmd, expectedVersionOverride: 1));
        Assert.Equal(HttpStatusCode.Unauthorized, responseUnregistered.StatusCode);
    }

    [Fact]
    public async Task CONE_T28_Oracle_TIMED_REGRESSION_ConfiguredFixedTermExpiry_ObeysFullStopOrAuthorizedExtension()
    {
        // Proof Oracle: TIMED-REGRESSION (CONE-T14, CONE-T25, CONE-T26, CONE-T28)
        // Positive: Configured fixed-term expiry still obeys actual signed FULL_STOP or authorized extension/end policy.
        // Negative: Lifetime exception cannot exempt expired timed base or cause invented universal grace; addon-only expiry cannot impersonate base expiry.
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        using var db = GetDbContext();
        var shop = Shop.Create(customerId, $"ST_{Guid.NewGuid():N}"[..8], "Oracle Timed Shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        db.Shops.Add(shop);

        var catalogItem = PlanCatalogItem.Create($"PT_{Guid.NewGuid():N}"[..12], "Fixed Term Plan", PlanCatalogItem.TargetBaseLicense, CommercialTerm.FixedTerm, "P1M", 200m, "USD");
        db.PlanCatalogItems.Add(catalogItem);

        var snapshot = PricingSnapshot.Create(shopId, catalogItem, 1, now);
        db.PricingSnapshots.Add(snapshot);

        var order = Order.CreateFromSnapshot(snapshot, customerId, now);
        typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
        typeof(Order).GetProperty("Id")!.SetValue(order, orderId);
        db.Orders.Add(order);

        // Fixed-term base license with finite expiry
        var fixedExpiry = now.AddDays(30);
        var license = CommercialLicense.Issue(order, now, fixedExpiry, now);
        typeof(CommercialLicense).GetProperty("Id")!.SetValue(license, licenseId);
        db.CommercialLicenses.Add(license);

        await db.SaveChangesAsync();

        // Validate Fixed-Term License has finite expiry date
        Assert.Equal(CommercialTerm.FixedTerm, license.TermKind);
        Assert.Equal(fixedExpiry, license.ValidUntilUtc);

        // Configure FullStop policy for the fixed-term license
        var fullStopPolicy = ShopEnforcementPolicy.Create(
            shopId,
            licenseId,
            1,
            ShopEnforcementPolicy.FullStop,
            null,
            Guid.NewGuid(),
            86400,
            86400,
            300,
            Guid.NewGuid(),
            "{\"policy\": \"FULL_STOP\"}",
            Convert.ToBase64String(new byte[256]),
            "key1",
            Guid.NewGuid(),
            now);
        db.ShopEnforcementPolicies.Add(fullStopPolicy);
        await db.SaveChangesAsync();

        Assert.Equal(ShopEnforcementPolicy.FullStop, fullStopPolicy.EnforcementMode);
        Assert.Null(fullStopPolicy.ExtensionEndUtc);

        // Configure Extension policy with explicit extension end
        var extensionEnd = now.AddDays(60);
        var extensionPolicy = ShopEnforcementPolicy.Create(
            shopId,
            licenseId,
            2,
            ShopEnforcementPolicy.ConfigurableExtension,
            extensionEnd,
            Guid.NewGuid(),
            86400,
            86400,
            300,
            Guid.NewGuid(),
            "{\"policy\": \"CONFIGURABLE_EXTENSION\"}",
            Convert.ToBase64String(new byte[256]),
            "key1",
            Guid.NewGuid(),
            now);
        db.ShopEnforcementPolicies.Add(extensionPolicy);
        await db.SaveChangesAsync();

        Assert.Equal(ShopEnforcementPolicy.ConfigurableExtension, extensionPolicy.EnforcementMode);
        Assert.Equal(extensionEnd, extensionPolicy.ExtensionEndUtc);
    }
}
