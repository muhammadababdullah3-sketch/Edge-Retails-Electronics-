using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Api;

public sealed class CanonicalActivationReviewContractTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly ICanonicalJsonSerializer _serializer = new CanonicalJsonSerializer();
    private readonly ITotpService _totp = new TotpService();
    private readonly IPasswordHasher _hasher = new PasswordHasher();

    private CentralDbContext OpenDb()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CentralDbContext>();
    }

    [Fact]
    public async Task CanonicalActivationReview_UsesPersistedScopeAndReplaysAtomicOutcome()
    {
        RateLimitingMiddleware.Reset();
        var (requestId, shopId) = await CreatePendingRequestAsync();
        var (_, adminId, allowed) = await CreateAdminAsync("activation.review");
        var (_, _, denied) = await CreateAdminAsync("license.view");
        var payload = new CanonicalActivationDecisionDto(requestId, "APPROVE", "ELIGIBLE", 1);
        var meta = CreateMeta(payload, expectedVersion: 1);

        var forbidden = await denied.PostAsJsonAsync($"/api/admin/v1/activations/{requestId:D}/review", new { meta, payload });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var db = OpenDb())
        {
            Assert.False(await db.CentralOutboxMessages.AnyAsync(x => x.Id == meta.ClientOperationId));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == meta.ClientOperationId));
            Assert.Equal(ActivationRedemption.StatusPending, (await db.ActivationRedemptions.SingleAsync(x => x.Id == requestId)).Status);
        }

        var response = await allowed.PostAsJsonAsync($"/api/admin/v1/activations/{requestId:D}/review", new { meta, payload });
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected success, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var first = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("SUCCEEDED", first["state"]?.GetValue<string>());
        Assert.Equal(shopId.ToString("D"), await ReadOperationShopAsync(meta.ClientOperationId));

        using (var db = OpenDb())
        {
            var redemption = await db.ActivationRedemptions.SingleAsync(x => x.Id == requestId);
            Assert.Equal(ActivationRedemption.StatusApproved, redemption.Status);
            Assert.Equal("ELIGIBLE", redemption.DecisionReasonCode);
            Assert.Equal(adminId, redemption.ReviewedByAdminId);
            Assert.NotNull(redemption.DecidedAtUtc);
            Assert.Equal(2, redemption.RowVersion);
            Assert.Equal(ActivationToken.StatusConsumed, (await db.ActivationTokens.SingleAsync(x => x.Id == redemption.TokenId)).Status);
            Assert.Equal(Installation.StatusActive, (await db.Installations.SingleAsync(x => x.Id == redemption.InstallationId)).Status);
            var activationEvent = Assert.Single(await db.CentralOutboxMessages
                .Where(x => x.Id == meta.ClientOperationId)
                .ToListAsync());
            Assert.Equal("activation.review_decided", activationEvent.EventType);
            Assert.Equal(nameof(ActivationRedemption), activationEvent.AggregateType);
            Assert.Equal(requestId.ToString("D"), activationEvent.AggregateId);
            Assert.Single(await db.CentralAuditRecords.Where(x => x.CorrelationId == meta.ClientOperationId.ToString()).ToListAsync());
            Assert.Single(await db.CentralOperationRecords.Where(x => x.ClientOperationId == meta.ClientOperationId).ToListAsync());
        }

        var replay = await allowed.PostAsJsonAsync($"/api/admin/v1/activations/{requestId:D}/review", new { meta, payload });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayJson = JsonNode.Parse(await replay.Content.ReadAsStringAsync())!;
        Assert.Equal(first["outcomeId"]?.GetValue<string>(), replayJson["outcomeId"]?.GetValue<string>());
        using (var db = OpenDb())
        {
            Assert.Single(await db.CentralOutboxMessages.Where(x => x.Id == meta.ClientOperationId).ToListAsync());
            Assert.Single(await db.CentralAuditRecords.Where(x => x.CorrelationId == meta.ClientOperationId.ToString()).ToListAsync());
        }

        var changedPayload = payload with { ReasonCode = "ALTERED" };
        var altered = await allowed.PostAsJsonAsync($"/api/admin/v1/activations/{requestId:D}/review", new { meta, payload = changedPayload });
        Assert.Equal(HttpStatusCode.BadRequest, altered.StatusCode);
    }

    [Fact]
    public async Task CanonicalActivationReview_EnforcesRequestIdentityVersionAndDistinctNonApprovalStates()
    {
        RateLimitingMiddleware.Reset();
        var (_, _, admin) = await CreateAdminAsync("activation.review");
        var (staleId, _) = await CreatePendingRequestAsync();
        var stalePayload = new CanonicalActivationDecisionDto(staleId, "REJECT", "NOT_ELIGIBLE", 1);
        var staleMeta = CreateMeta(stalePayload, expectedVersion: 2);
        var stale = await admin.PostAsJsonAsync($"/api/admin/v1/activations/{staleId:D}/review", new { meta = staleMeta, payload = stalePayload });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var db = OpenDb())
        {
            Assert.Equal(ActivationRedemption.StatusPending, (await db.ActivationRedemptions.SingleAsync(x => x.Id == staleId)).Status);
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == staleMeta.ClientOperationId));
        }

        var (infoId, _) = await CreatePendingRequestAsync();
        var info = new CanonicalActivationDecisionDto(infoId, "REQUEST_INFORMATION", "NEED_MORE_INFO", 1);
        var infoMeta = CreateMeta(info, 1);
        var infoResponse = await admin.PostAsJsonAsync($"/api/admin/v1/activations/{infoId:D}/review", new { meta = infoMeta, payload = info });
        Assert.True(infoResponse.StatusCode == HttpStatusCode.OK, $"Expected success, got {(int)infoResponse.StatusCode}: {await infoResponse.Content.ReadAsStringAsync()}");
        using (var db = OpenDb())
        {
            var row = await db.ActivationRedemptions.SingleAsync(x => x.Id == infoId);
            Assert.Equal(ActivationRedemption.StatusRequestInformation, row.Status);
            Assert.NotNull(row.DecisionReasonCode);
            Assert.Equal(ActivationToken.StatusPendingApproval, (await db.ActivationTokens.SingleAsync(x => x.Id == row.TokenId)).Status);
        }

        var (hardwareId, _) = await CreatePendingRequestAsync();
        var hardware = new CanonicalActivationDecisionDto(hardwareId, "OPEN_HARDWARE_REVIEW", "CHECK_DEVICE", 1);
        var hardwareMeta = CreateMeta(hardware, 1);
        var hardwareResponse = await admin.PostAsJsonAsync($"/api/admin/v1/activations/{hardwareId:D}/review", new { meta = hardwareMeta, payload = hardware });
        Assert.True(hardwareResponse.StatusCode == HttpStatusCode.BadRequest, $"Expected evidence denial, got {(int)hardwareResponse.StatusCode}: {await hardwareResponse.Content.ReadAsStringAsync()}");
        var hardwareDenial = JsonNode.Parse(await hardwareResponse.Content.ReadAsStringAsync());
        Assert.Equal("HARDWARE_REVIEW_EVIDENCE_REQUIRED", hardwareDenial?["code"]?.GetValue<string>());
        using (var db = OpenDb())
        {
            Assert.Equal(ActivationRedemption.StatusPending, (await db.ActivationRedemptions.SingleAsync(x => x.Id == hardwareId)).Status);
            Assert.False(await db.HardwareReviews.AnyAsync(x => x.InstallationId == db.ActivationRedemptions.Where(r => r.Id == hardwareId).Select(r => r.InstallationId).Single()));
        }
    }

    [Fact]
    public async Task CanonicalActivationReview_RejectsAlteredDigestAndRoutePayloadIdentity()
    {
        RateLimitingMiddleware.Reset();
        var (requestId, _) = await CreatePendingRequestAsync();
        var (_, _, admin) = await CreateAdminAsync("activation.review");
        var payload = new CanonicalActivationDecisionDto(requestId, "REJECT", "INVALID", 1);
        var validMeta = CreateMeta(payload, 1);
        var meta = new CanonicalDirectoryMeta
        {
            ClientOperationId = validMeta.ClientOperationId,
            OperationType = validMeta.OperationType,
            FingerprintVersion = validMeta.FingerprintVersion,
            PayloadSha256 = new string('0', 64),
            ExpectedVersion = validMeta.ExpectedVersion,
            PolicyVersion = validMeta.PolicyVersion,
            Reason = validMeta.Reason
        };
        var alteredDigest = await admin.PostAsJsonAsync($"/api/admin/v1/activations/{requestId:D}/review", new { meta, payload });
        Assert.Equal(HttpStatusCode.BadRequest, alteredDigest.StatusCode);

        var otherId = Guid.NewGuid();
        var mismatchPayload = payload with { ActivationRequestId = otherId };
        var mismatchMeta = CreateMeta(mismatchPayload, 1);
        var mismatch = await admin.PostAsJsonAsync($"/api/admin/v1/activations/{requestId:D}/review", new { meta = mismatchMeta, payload = mismatchPayload });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
    }

    [Fact]
    public async Task CanonicalActivationReviewQueue_ReturnsPersistedTargetAndVersionsWithoutSnapshotSecrets()
    {
        RateLimitingMiddleware.Reset();
        var (requestId, shopId) = await CreatePendingRequestAsync();
        var (_, _, queueReader) = await CreateAdminAsync("activation.review");
        var denied = await queueReader.GetAsync("/api/admin/v1/queues/ACTIVATION_REVIEW?pageSize=1");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var (_, _, authorized) = await CreateAdminAsync("activation.review", "queues.view");
        using var response = await authorized.GetAsync($"/api/admin/v1/queues/ACTIVATION_REVIEW?pageSize=1&filter={Uri.EscapeDataString(requestId.ToString("D"))}");
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected a successful activation queue, got {(int)response.StatusCode}: {responseBody}");
        var json = JsonNode.Parse(responseBody)!;
        var item = Assert.Single(json["items"]!.AsArray());
        Assert.Equal(requestId.ToString("D"), item!["entityId"]?.GetValue<string>());
        Assert.Equal(shopId.ToString("D"), item["shopId"]?.GetValue<string>());
        Assert.Equal("ACTIVATION_REVIEW", item["queueType"]?.GetValue<string>());
        Assert.Equal(1, item["resourceVersion"]?.GetValue<int>());
        Assert.Equal(1, item["validationSnapshotVersion"]?.GetValue<int>());
        var responseText = json.ToJsonString();
        Assert.DoesNotContain("validationSnapshotJson", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientSignatureHex", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("challengeNonce", responseText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(Guid RequestId, Guid ShopId)> CreatePendingRequestAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var customer = Customer.Create($"Activation {Guid.NewGuid():N}", $"activation-{Guid.NewGuid():N}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"AR-{Guid.NewGuid():N}"[..12], "Synthetic activation shop", now);
        var plan = PlanCatalogItem.Create($"AR-{Guid.NewGuid():N}"[..12], "Synthetic lifetime plan", PlanCatalogItem.TargetBaseLicense,
            PlanCatalogItem.TermLifetime, null, 0m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, plan, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, customer.Id, now);
        order.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), now);
        var license = CommercialLicense.Issue(order, now, null, now);
        var installation = Installation.Register(shop.Id, license.Id, $"machine-{Guid.NewGuid():N}", "synthetic-public-key", "fixture", "Windows", now);
        installation.MoveToPendingApproval();
        var token = ActivationToken.Issue(license.Id, shop.Id, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(), now.AddDays(1), Guid.NewGuid(), now);
        token.MarkRedeemed(now);
        token.MoveToPendingApproval();
        var validationJson = _serializer.SerializeCanonical(new
        {
            version = 1,
            tokenId = token.Id,
            tokenStatus = token.Status,
            tokenRowVersion = token.RowVersion,
            tokenExpiresAtUtc = new DateTimeOffset(token.FirstRedemptionExpiresAtUtc.ToUniversalTime().Ticks - (token.FirstRedemptionExpiresAtUtc.ToUniversalTime().Ticks % 10), TimeSpan.Zero),
            installationId = installation.Id,
            installationStatus = installation.Status,
            installationRowVersion = installation.RowVersion,
            installationShopId = installation.ShopId,
            installationLicenseId = installation.LicenseId,
            installationPublicKeyFingerprint = installation.PublicKeyFingerprintSha256,
            hardwareFingerprintVersion = installation.HardwareFingerprintVersion,
            reportedHardwareFingerprintHash = installation.HardwareFingerprintHash,
            hardwareFingerprintHash = installation.HardwareFingerprintHash,
            licenseId = license.Id,
            licenseShopId = license.ShopId,
            licenseStatus = license.Status,
            licenseAuthorityRevision = license.AuthorityRevision,
            licenseRowVersion = license.RowVersion
        });
        var redemption = ActivationRedemption.Create(token.Id, installation.Id, "synthetic-nonce", "proof", now, validationJson, 1);
        using (var db = OpenDb())
        {
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.PlanCatalogItems.Add(plan);
            db.PricingSnapshots.Add(snapshot);
            db.Orders.Add(order);
            db.CommercialLicenses.Add(license);
            db.Installations.Add(installation);
            db.ActivationTokens.Add(token);
            db.ActivationRedemptions.Add(redemption);
            await db.SaveChangesAsync();
        }
        return (redemption.Id, shop.Id);
    }

    private async Task<(Guid SessionId, Guid AdminId, HttpClient Client)> CreateAdminAsync(params string[] capabilities)
    {
        var username = $"activation_review_{Guid.NewGuid():N}";
        const string password = "DevelopmentPassword#123";
        var totpSecret = _totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        Guid adminId;
        using (var db = OpenDb())
        {
            var admin = AdminUser.Create(username, _hasher.HashPassword(password), "salt", totpSecret, true, now);
            var role = AdminRole.Create($"Activation review {Guid.NewGuid():N}", "Synthetic activation review role", capabilities);
            db.AdminUsers.Add(admin);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(admin.Id, role.Id, now));
            await db.SaveChangesAsync();
            adminId = admin.Id;
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/admin/v1/auth/login", new LoginRequestDto { Username = username, PasswordOrProviderAssertion = password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginJson = JsonNode.Parse(await login.Content.ReadAsStringAsync())!;
        var challengeId = Guid.Parse(loginJson["challengeId"]!.GetValue<string>());
        var mfa = await client.PostAsJsonAsync("/api/admin/v1/auth/mfa", new MfaRequestDto { LoginChallengeId = challengeId, Proof = _totp.GenerateCode(totpSecret, DateTimeOffset.UtcNow) });
        Assert.Equal(HttpStatusCode.OK, mfa.StatusCode);
        var sessionCookie = mfa.Headers.GetValues("Set-Cookie").Select(x => x.Split(';', 2)[0]).Single(x => x.StartsWith("central_session=", StringComparison.OrdinalIgnoreCase));
        var sessionId = Guid.Parse(sessionCookie["central_session=".Length..]);
        client.DefaultRequestHeaders.Add("Cookie", sessionCookie);
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return (sessionId, adminId, client);
    }

    private CanonicalDirectoryMeta CreateMeta(CanonicalActivationDecisionDto payload, int expectedVersion) => new()
    {
        ClientOperationId = Guid.NewGuid(),
        OperationType = "activation.review",
        FingerprintVersion = 1,
        PayloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex,
        ExpectedVersion = expectedVersion,
        PolicyVersion = 1,
        Reason = "Synthetic activation review test"
    };

    private async Task<string?> ReadOperationShopAsync(Guid operationId)
    {
        using var db = OpenDb();
        return (await db.CentralOperationRecords.SingleAsync(x => x.ClientOperationId == operationId)).ShopId?.ToString("D");
    }

    public void Dispose() { }
}
