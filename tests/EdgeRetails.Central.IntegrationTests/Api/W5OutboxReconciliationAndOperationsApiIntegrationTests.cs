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
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EdgeRetails.Central.IntegrationTests.Api;

public class W5OutboxReconciliationAndOperationsApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly ICanonicalJsonSerializer _serializer = new CanonicalJsonSerializer();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RSA> InstallationTestKeys = new(StringComparer.OrdinalIgnoreCase);

    public W5OutboxReconciliationAndOperationsApiIntegrationTests(WebApplicationFactory<Program> factory)
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

    private async Task<(AdminUser user, AdminSession session)> CreateTestAdminAndSessionAsync(string prefix = "w5admin")
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
            capabilities: new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuditRead, "operation.admin.lookup" });

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

    private void AddInstallationProof(HttpRequestMessage request, Guid installationId, Guid shopId, Guid licenseId, string publicKeyPem)
    {
        Environment.SetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS", "120"); // labelled synthetic integration fixture
        var keyId = Installation.ComputePublicKeyFingerprintSha256(publicKeyPem);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid();
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
        var bodyDigest = _serializer.ComputeSha256Hex(_serializer.SerializeCanonical(body));
        var path = request.RequestUri!.OriginalString;
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
            httpPath = path,
            bodyDigest
        });
        var signature = InstallationTestKeys[keyId].SignData(Encoding.UTF8.GetBytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.Headers.Add("X-Installation-Id", installationId.ToString("D"));
        request.Headers.Add("X-Installation-Key-Id", keyId);
        request.Headers.Add("X-Installation-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Installation-Nonce", nonce.ToString("D"));
        request.Headers.Add("X-Installation-Signature", Convert.ToBase64String(signature));
    }

    [Fact]
    public async Task CONE_T24_Outbox_DeadLetter_Isolation_And_Postgres_Deduplication()
    {
        using var db = GetDbContext();
        var sink = new DurableOutboxConsumerSink(db);
        var dispatcher = new OutboxDispatcher(db, sink);

        var messageId1 = Guid.NewGuid();
        var outboxMessage1 = CentralOutboxMessage.Create(
            messageId1,
            "shop.license_reconciled",
            "Installation",
            Guid.NewGuid().ToString(),
            """{"test":"w5_outbox_dedup"}""",
            DateTimeOffset.UtcNow);

        db.CentralOutboxMessages.Add(outboxMessage1);
        await db.SaveChangesAsync();

        // 1. Initial dispatch delivers message and records effect in central.outbox_consumer_effects
        var dispatchedCount = await dispatcher.DispatchPendingMessagesAsync(batchSize: 500, maxRetries: 3);
        Assert.True(dispatchedCount >= 1);

        db.ChangeTracker.Clear();
        var refreshedMsg1 = await db.CentralOutboxMessages.FindAsync(messageId1);
        Assert.NotNull(refreshedMsg1);
        Assert.Equal(OutboxStatus.DISPATCHED, refreshedMsg1.Status);
        Assert.True(sink.HasProcessed(messageId1));
        Assert.Equal(1, sink.GetDeliveryCount(messageId1));

        // 2. Deliver again using DurableOutboxConsumerSink directly: ON CONFLICT ("MessageId") DO NOTHING
        await sink.DeliverAsync(
            messageId1,
            "shop.license_reconciled",
            "Installation",
            outboxMessage1.AggregateId,
            outboxMessage1.PayloadJson);

        Assert.Equal(1, sink.GetDeliveryCount(messageId1)); // Strictly deduplicated in PostgreSQL

        // 3. Test Dead-Letter isolation with failing sink
        var failingMessageId = Guid.NewGuid();
        var failingSink = new MockFailingConsumerSink(failingMessageId);
        var failingDispatcher = new OutboxDispatcher(db, failingSink);

        var failingMessage = CentralOutboxMessage.Create(
            failingMessageId,
            "order.failed_delivery",
            "Order",
            Guid.NewGuid().ToString(),
            """{"test":"failing_order"}""",
            DateTimeOffset.UtcNow);

        db.CentralOutboxMessages.Add(failingMessage);
        await db.SaveChangesAsync();

        // Attempt 1: RetryCount becomes 1, status FAILED
        await failingDispatcher.DispatchPendingMessagesAsync(batchSize: 500, maxRetries: 2);
        db.ChangeTracker.Clear();
        var msgAttempt1 = await db.CentralOutboxMessages.FindAsync(failingMessageId);
        Assert.NotNull(msgAttempt1);
        Assert.Equal(OutboxStatus.FAILED, msgAttempt1.Status);
        Assert.Equal(1, msgAttempt1.RetryCount);

        // Attempt 2: Reaches maxRetries=2, status transitions to DEAD_LETTER
        await failingDispatcher.DispatchPendingMessagesAsync(batchSize: 500, maxRetries: 2);
        db.ChangeTracker.Clear();
        var msgAttempt2 = await db.CentralOutboxMessages.FindAsync(failingMessageId);
        Assert.NotNull(msgAttempt2);
        Assert.Equal(OutboxStatus.DEAD_LETTER, msgAttempt2.Status);
        Assert.Equal(2, msgAttempt2.RetryCount);
        Assert.Contains("Simulated consumer failure", msgAttempt2.LastError);

        // Attempt 3: Normal dispatcher isolates DEAD_LETTER and does NOT attempt re-delivery
        var processedIsolated = await dispatcher.DispatchPendingMessagesAsync(batchSize: 500, maxRetries: 2);
        db.ChangeTracker.Clear();
        var msgAttempt3 = await db.CentralOutboxMessages.FindAsync(failingMessageId);
        Assert.NotNull(msgAttempt3);
        Assert.Equal(OutboxStatus.DEAD_LETTER, msgAttempt3.Status);
        Assert.Equal(2, msgAttempt3.RetryCount);
    }

    [Fact]
    public async Task CONE_T25_Cryptographic_Entitlement_Reconciliation_EndToEnd()
    {
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
            var shop = Shop.Create(customerId, $"S5_{Guid.NewGuid():N}"[..8], "Wave 5 Reconcile Shop", now);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            db.Shops.Add(shop);

            var catalogItem = PlanCatalogItem.Create($"PLAN_{Guid.NewGuid():N}"[..12], "Wave 5 Plan", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 2000m, "USD");
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
                hostname: "pos-terminal-01",
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

        // Execute Reconciliation Request
        var reconcileCommand = new ReconcileCommandDto
        {
            InstallationId = installationId,
            CertificateId = certificateId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = hardwareFingerprint,
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        using var reconcileRequest = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/reconcile") { Content = System.Net.Http.Json.JsonContent.Create(reconcileCommand) };
        AddInstallationProof(reconcileRequest, installationId, shopId, licenseId, pubKeyPem);
        var response = await _client.SendAsync(reconcileRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var signedEntitlement = await response.Content.ReadFromJsonAsync<SignedEntitlementDto>();
        Assert.NotNull(signedEntitlement);
        Assert.Equal("RS256", signedEntitlement.AlgorithmProfileId);
        Assert.Equal("CANONICAL_JSON_V1", signedEntitlement.CanonicalizationProfileId);
        Assert.Equal("LICENSE", signedEntitlement.Purpose);
        Assert.NotNull(signedEntitlement.Payload);
        Assert.Equal(installationId, signedEntitlement.Payload.InstallationId);
        Assert.Equal(shopId, signedEntitlement.Payload.ShopId);
        Assert.NotEmpty(signedEntitlement.Signature);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/reconcile")
        {
            Content = System.Net.Http.Json.JsonContent.Create(reconcileCommand)
        };
        foreach (var header in reconcileRequest.Headers.Where(x => x.Key.StartsWith("X-Installation-", StringComparison.Ordinal)))
        {
            replayRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        var replayResponse = await _client.SendAsync(replayRequest);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, replayResponse.StatusCode);

        // Fetch public trust set and verify signature
        using var trustRequest = new HttpRequestMessage(HttpMethod.Get, "/api/licensing/v1/trust-set");
        AddInstallationProof(trustRequest, installationId, shopId, licenseId, pubKeyPem);
        var trustResponse = await _client.SendAsync(trustRequest);
        Assert.Equal(HttpStatusCode.OK, trustResponse.StatusCode);

        var trustSet = await trustResponse.Content.ReadFromJsonAsync<TrustSetDto>();
        Assert.NotNull(trustSet);
        Assert.NotEmpty(trustSet.Keys);

        var matchingKey = trustSet.Keys.FirstOrDefault(k => k.KeyId == signedEntitlement.Payload.SigningKeyId);
        Assert.NotNull(matchingKey);

        var (canonicalJson, expectedSha256) = _serializer.SerializeAndHash(signedEntitlement.Payload);
        Assert.Equal(expectedSha256, signedEntitlement.PayloadSha256);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(matchingKey.PublicKey);
        var isValid = rsa.VerifyData(
            Encoding.UTF8.GetBytes(canonicalJson),
            Convert.FromBase64String(signedEntitlement.Signature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        Assert.True(isValid, "Cryptographic entitlement signature must verify against RS256 trust-set.");

        // Verify outbox event and audit record were written
        using (var db = GetDbContext())
        {
            var outboxMsg = await db.CentralOutboxMessages
                .FirstOrDefaultAsync(m => m.EventType == "licensing.installation_reconciled" && m.AggregateId == installationId.ToString());
            Assert.NotNull(outboxMsg);

            var auditRecord = await db.CentralAuditRecords
                .FirstOrDefaultAsync(a => a.Action == "INSTALLATION_RECONCILED" && a.ResourceId == installationId.ToString());
            Assert.NotNull(auditRecord);
        }
    }

    [Fact]
    public async Task CONE_T25_Reconciliation_ApprovedHardwareReview_AllowsDifferentFingerprint()
    {
        RateLimitingMiddleware.Reset();
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var certificateId = Guid.NewGuid();
        var originalFingerprint = "HW_ORIGINAL_" + Guid.NewGuid().ToString("N");
        var newApprovedFingerprint = "HW_NEW_APPROVED_" + Guid.NewGuid().ToString("N");
        var pubKeyPem = GenerateTestRsaPublicKeyPem();

        using (var db = GetDbContext())
        {
            var shop = Shop.Create(customerId, $"S5_{Guid.NewGuid():N}"[..8], "Wave 5 Hardware Review Shop", now);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            db.Shops.Add(shop);

            var catalogItem = PlanCatalogItem.Create($"PLAN_{Guid.NewGuid():N}"[..12], "Plan", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1500m, "USD");
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
                machineFingerprint: originalFingerprint,
                publicKeyPem: pubKeyPem,
                hostname: "pos-terminal-02",
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
                digestSha256: new string('b', 64),
                validFromUtc: now,
                leaseExpiresUtc: now.AddDays(1),
                offlineGraceUntilUtc: now.AddDays(2),
                issuedAtUtc: now);
            typeof(LicenseCertificate).GetProperty("Id")!.SetValue(certificate, certificateId);
            db.LicenseCertificates.Add(certificate);

            // Add approved hardware review for new fingerprint
            var review = HardwareReview.Create(
                shopId: shopId,
                installationId: installationId,
                reportedFingerprint: newApprovedFingerprint,
                storedFingerprint: originalFingerprint,
                discrepancyReason: "Motherboard replacement under warranty",
                createdAtUtc: now);
            review.Decide(true, Guid.NewGuid(), now);
            db.HardwareReviews.Add(review);

            await db.SaveChangesAsync();
        }

        var reconcileCommand = new ReconcileCommandDto
        {
            InstallationId = installationId,
            CertificateId = certificateId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = newApprovedFingerprint, // Differing, but approved!
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        using var reconcileRequest = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/reconcile") { Content = System.Net.Http.Json.JsonContent.Create(reconcileCommand) };
        AddInstallationProof(reconcileRequest, installationId, shopId, licenseId, pubKeyPem);
        var response = await _client.SendAsync(reconcileRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var signedEntitlement = await response.Content.ReadFromJsonAsync<SignedEntitlementDto>();
        Assert.NotNull(signedEntitlement);
        Assert.Equal(newApprovedFingerprint, signedEntitlement.Payload.HardwareFingerprintHash);
    }

    [Fact]
    public async Task CONE_T25_Reconciliation_MismatchedFingerprint_ThrowsHardwareMismatch()
    {
        RateLimitingMiddleware.Reset();
        var now = DateTimeOffset.UtcNow;
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var certificateId = Guid.NewGuid();
        var registeredFingerprint = "HW_LEGIT_" + Guid.NewGuid().ToString("N");
        var tamperedFingerprint = "HW_TAMPERED_" + Guid.NewGuid().ToString("N");
        var pubKeyPem = GenerateTestRsaPublicKeyPem();

        using (var db = GetDbContext())
        {
            var shop = Shop.Create(customerId, $"S5_{Guid.NewGuid():N}"[..8], "Tamper Test Shop", now);
            typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
            db.Shops.Add(shop);

            var catalogItem = PlanCatalogItem.Create($"PLAN_{Guid.NewGuid():N}"[..12], "Plan", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1000m, "USD");
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
                machineFingerprint: registeredFingerprint,
                publicKeyPem: pubKeyPem,
                hostname: "pos-terminal-tamper",
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
                digestSha256: new string('c', 64),
                validFromUtc: now,
                leaseExpiresUtc: now.AddDays(1),
                offlineGraceUntilUtc: now.AddDays(2),
                issuedAtUtc: now);
            typeof(LicenseCertificate).GetProperty("Id")!.SetValue(certificate, certificateId);
            db.LicenseCertificates.Add(certificate);

            await db.SaveChangesAsync();
        }

        var reconcileCommand = new ReconcileCommandDto
        {
            InstallationId = installationId,
            CertificateId = certificateId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = tamperedFingerprint,
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        using var reconcileRequest = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/reconcile") { Content = System.Net.Http.Json.JsonContent.Create(reconcileCommand) };
        AddInstallationProof(reconcileRequest, installationId, shopId, licenseId, pubKeyPem);
        var response = await _client.SendAsync(reconcileRequest);
        Assert.False(response.IsSuccessStatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal("HARDWARE_FINGERPRINT_MISMATCH", json["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CONE_T26_Operations_Outcome_Replay_And_Scope_Isolation_Admin_And_Licensing()
    {
        RateLimitingMiddleware.Reset();
        var (adminUser, adminSession) = await CreateTestAdminAndSessionAsync("w5_op_admin");

        var clientOpId = Guid.NewGuid();
        var command = new AdminActionCommandDto
        {
            AdminUserId = adminUser.Id,
            Action = "ENABLE"
        };
        var (_, sha256) = _serializer.SerializeAndHash(command);

        var meta = new CommandMetaDto
        {
            ClientOperationId = clientOpId,
            OperationType = "admin.action",
            PayloadSha256 = sha256,
            Reason = "Wave 5 outcome check",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        var scope = _factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IOperationLedgerService>();

        var firstOutcome = await ledger.ExecuteOrReplayAsync(
            actorScope: $"admin:{adminSession.Id}",
            shopId: null,
            meta: meta,
            payload: command,
            executeAsync: _ => Task.FromResult((true, (string?)"users/target_user_01", 2, 1)));

        Assert.Equal("SUCCEEDED", firstOutcome.State);
        Assert.Equal(clientOpId, firstOutcome.ClientOperationId);

        // 1. Query Outcome via Admin Endpoint GET /api/admin/v1/operations/{id}
        var adminReq = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/v1/operations/{clientOpId}");
        adminReq.Headers.Add("Cookie", $"central_session={adminSession.Id}");

        var adminRes = await _client.SendAsync(adminReq);
        Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);

        var adminOutcome = await adminRes.Content.ReadFromJsonAsync<OutcomeDto>();
        Assert.NotNull(adminOutcome);
        Assert.Equal(clientOpId, adminOutcome.ClientOperationId);
        Assert.Equal("SUCCEEDED", adminOutcome.State);
        Assert.Equal("users/target_user_01", adminOutcome.ResultReference);
        Assert.Equal(2, adminOutcome.ResourceVersion);
        Assert.Equal(1, adminOutcome.AuthorityRevision);

        // 2. Query without admin session returns 401 Unauthorized
        var unauthReq = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/v1/operations/{clientOpId}");
        var unauthRes = await _client.SendAsync(unauthReq);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        // 3. Query non-existent operation returns 404 Not Found
        var notFoundReq = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/v1/operations/{Guid.NewGuid()}");
        notFoundReq.Headers.Add("Cookie", $"central_session={adminSession.Id}");
        var notFoundRes = await _client.SendAsync(notFoundReq);
        Assert.Equal(HttpStatusCode.NotFound, notFoundRes.StatusCode);

        // 4. Test Licensing Operation Outcome Replay & Scope Isolation
        var licOpId = Guid.NewGuid();
        var shopA = Guid.NewGuid();
        var shopB = Guid.NewGuid();

        var licMeta = new CommandMetaDto
        {
            ClientOperationId = licOpId,
            OperationType = "licensing.reconcile",
            PayloadSha256 = sha256,
            Reason = "Licensing op check",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        await ledger.ExecuteOrReplayAsync(
            actorScope: $"installation:{Guid.NewGuid()}",
            shopId: shopA,
            meta: licMeta,
            payload: command,
            executeAsync: _ => Task.FromResult((true, (string?)"licenses/lic_01", 3, 2)));

        // Durable ledger scope is bound to Shop A; the client header alone cannot authorize API access.
        var scopedLedgerOutcome = await ledger.GetLicensingOutcomeAsync(licOpId, shopA.ToString());
        Assert.NotNull(scopedLedgerOutcome);
        Assert.Equal(licOpId, scopedLedgerOutcome.ClientOperationId);
        Assert.Null(await ledger.GetLicensingOutcomeAsync(licOpId, shopB.ToString()));

        var licReqA = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/operations/{licOpId}");
        licReqA.Headers.Add("X-Shop-Scope", shopA.ToString());
        var licResA = await _client.SendAsync(licReqA);
        Assert.Equal(HttpStatusCode.Unauthorized, licResA.StatusCode);
        var bodyA = await ReadJsonAsync(licResA);
        Assert.Equal("INSTALLATION_AUTHORITY_NOT_ACTIVE", bodyA["code"]?.GetValue<string>());

        // A different shop identifier still cannot manufacture installation authority.
        var licReqB = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/operations/{licOpId}");
        licReqB.Headers.Add("X-Shop-Scope", shopB.ToString());
        var licResB = await _client.SendAsync(licReqB);
        Assert.Equal(HttpStatusCode.Unauthorized, licResB.StatusCode);
        var bodyB = await ReadJsonAsync(licResB);
        Assert.Equal("INSTALLATION_AUTHORITY_NOT_ACTIVE", bodyB["code"]?.GetValue<string>());

        // Unauthenticated licensing query rejected with 401 Unauthorized
        var licReqNoScope = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/operations/{licOpId}");
        var licResNoScope = await _client.SendAsync(licReqNoScope);
        Assert.Equal(HttpStatusCode.Unauthorized, licResNoScope.StatusCode);
    }

    [Fact]
    public async Task CONE_T27_DisasterRecovery_DatabaseTargetGuard_FailClosed_And_Rehearsal()
    {
        var guard = new DatabaseTargetGuard();

        // 1. Fail-closed: Rejects non-loopback IP
        var exNonLoopback = Assert.Throws<DatabaseTargetGuardException>(() =>
            guard.ValidateConnectionString("Host=10.0.0.1;Port=55454;Database=central054_test;Username=central054_app;Password=pass"));
        Assert.Equal("NON_LOOPBACK_TARGET", exNonLoopback.ViolationCode);

        // 2. Fail-closed: Rejects port 5432
        var ex5432 = Assert.Throws<DatabaseTargetGuardException>(() =>
            guard.ValidateConnectionString("Host=127.0.0.1;Port=5432;Database=central054_test;Username=central054_app;Password=pass"));
        Assert.Equal("FORBIDDEN_PORT_TARGET", ex5432.ViolationCode);

        // 3. Fail-closed: Rejects port 7150
        var ex7150 = Assert.Throws<DatabaseTargetGuardException>(() =>
            guard.ValidateConnectionString("Host=127.0.0.1;Port=7150;Database=central054_test;Username=central054_app;Password=pass"));
        Assert.Equal("FORBIDDEN_PORT_TARGET", ex7150.ViolationCode);

        // 4. Fail-closed: Rejects forbidden database names
        foreach (var forbiddenDb in new[] { "edgeretails", "shop_pos", "postgres", "template1" })
        {
            var exDb = Assert.Throws<DatabaseTargetGuardException>(() =>
                guard.ValidateConnectionString($"Host=127.0.0.1;Port=55454;Database={forbiddenDb};Username=central054_app;Password=pass"));
            Assert.Equal("FORBIDDEN_DATABASE_TARGET", exDb.ViolationCode);
        }

        // 5. Positive Attestation: Valid disposable target accepted by target guard
        var validConn = Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Owned disposable test connection string required.");
        guard.ValidateConnectionString(validConn);

        // 6. Live Server Identity Probe Verification
        var migrationConn = Environment.GetEnvironmentVariable("CENTRAL_MIGRATION_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Owned migration connection string required.");

        await using var conn = new NpgsqlConnection(migrationConn);
        await conn.OpenAsync();

        var migrationTarget = new NpgsqlConnectionStringBuilder(migrationConn);
        var expectedDatabase = migrationTarget.Database ?? throw new InvalidOperationException("Owned database name required.");
        var expectedRole = migrationTarget.Username ?? throw new InvalidOperationException("Owned migration role required.");
        await guard.VerifyServerIdentityAsync(conn, expectedDatabase, expectedRole);
    }

    private sealed class MockFailingConsumerSink : IOutboxConsumerSink
    {
        private readonly Guid _targetFailingMessageId;

        public MockFailingConsumerSink(Guid targetFailingMessageId)
        {
            _targetFailingMessageId = targetFailingMessageId;
        }

        public Task DeliverAsync(Guid messageId, string eventType, string aggregateType, string aggregateId, string payloadJson, CancellationToken cancellationToken = default)
        {
            if (messageId == _targetFailingMessageId)
            {
                throw new InvalidOperationException("Simulated consumer failure for dead-letter testing");
            }
            return Task.CompletedTask;
        }

        public bool HasProcessed(Guid messageId) => false;
        public int GetDeliveryCount(Guid messageId) => 0;
        public IReadOnlyCollection<Guid> DeliveredMessageIds => Array.Empty<Guid>();
    }
}
