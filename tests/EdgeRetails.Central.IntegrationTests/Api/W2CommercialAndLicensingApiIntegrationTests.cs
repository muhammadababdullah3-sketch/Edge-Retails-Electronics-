using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Api.Controllers;
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

namespace EdgeRetails.Central.IntegrationTests.Api;

public class W2CommercialAndLicensingApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly ICanonicalJsonSerializer _serializer = new CanonicalJsonSerializer();
    private readonly ITotpService _totpService = new TotpService();
    private readonly IPasswordHasher _passwordHasher = new PasswordHasher();
    private readonly string? _previousBootstrapChallengeTtl;

    public W2CommercialAndLicensingApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _previousBootstrapChallengeTtl = Environment.GetEnvironmentVariable("CENTRAL_BOOTSTRAP_CHALLENGE_TTL_SECONDS");
        Environment.SetEnvironmentVariable("CENTRAL_BOOTSTRAP_CHALLENGE_TTL_SECONDS", "300"); // labelled synthetic integration fixture
        _factory = factory;
        _client = factory.CreateDefaultClient(new CanonicalFingerprintDelegatingHandler());
        _client.BaseAddress = new Uri("https://localhost");

        RateLimitingMiddleware.Reset();
    }

    private CentralDbContext GetDbContext()
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CentralDbContext>();
    }

    private async Task<Guid> AddPersistedInstallationFixtureAsync(Guid shopId, Guid licenseId, string publicKeyPem, string machineFingerprint, string hostname)
    {
        using var db = GetDbContext();
        var installation = Installation.Register(shopId, licenseId, machineFingerprint, publicKeyPem, hostname, "Windows 11", DateTimeOffset.UtcNow);
        db.Installations.Add(installation);
        await db.SaveChangesAsync();
        return installation.Id;
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(content) ?? throw new InvalidOperationException("Failed to parse JSON response: " + content);
    }

    private static CommandMetaDto CreateMeta(string op = "TEST_OP") => new()
    {
        ClientOperationId = Guid.NewGuid(),
        OperationType = op,
        PayloadSha256 = new string('0', 64),
        Reason = "Integration test command execution",
        FingerprintVersion = 1,
        ExpectedVersion = 1,
        PolicyVersion = 1
    };

    private void AddInstallationRequestProof(HttpRequestMessage request, Installation installation, RSA privateKey)
    {
        var keyId = installation.PublicKeyFingerprintSha256;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid();
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
        var bodyDigest = _serializer.ComputeSha256Hex(_serializer.SerializeCanonical(body));
        var envelope = _serializer.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_REQUEST_V1",
            purpose = "installation.request",
            keyId,
            installationId = installation.Id,
            shopId = installation.ShopId,
            licenseId = installation.LicenseId,
            timestamp,
            nonce,
            httpMethod = request.Method.Method.ToUpperInvariant(),
            httpPath = request.RequestUri!.OriginalString,
            bodyDigest
        });
        var signature = privateKey.SignData(Encoding.UTF8.GetBytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.Headers.Add("X-Installation-Id", installation.Id.ToString("D"));
        request.Headers.Add("X-Installation-Key-Id", keyId);
        request.Headers.Add("X-Installation-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Installation-Nonce", nonce.ToString("D"));
        request.Headers.Add("X-Installation-Signature", Convert.ToBase64String(signature));
    }

    private async Task<(Guid SessionId, Guid UserId, HttpClient Client)> CreateAdminClientAsync(string[] capabilities)
    {
        RateLimitingMiddleware.Reset();
        var username = $"w2_admin_{Guid.NewGuid():N}";
        const string password = "AdminPassword#123";
        var totpSecret = _totpService.GenerateSecret();
        var now = DateTimeOffset.UtcNow;

        Guid userId;
        using (var db = GetDbContext())
        {
            var user = AdminUser.Create(
                username,
                _passwordHasher.HashPassword(password),
                "salt",
                totpSecret,
                true,
                now);

            // These positive commercial journeys exercise the exact granular route
            // permissions issued by effective040; the session fixture must carry them.
            var role = AdminRole.Create($"W2_Role_{Guid.NewGuid():N}", "Role", capabilities.Concat(new[]
            {
                "customer.create", "shop.register", "shop.view", "customer.view", "price.snapshot",
                "order.create", "payment.review", "evidence.upload.init", "evidence.upload.finalize",
                "license.issue", "license.renew", "license.extend", "license.policy", "license.pause",
                "license.resume", "license.suspend", "license.reinstate", "license.revoke",
                "addon.renew", "activation.issue", "activation.review",
                "hardware.review", "primary.transfer", "sensitive.approve", "security.manage"
            }).Distinct(StringComparer.Ordinal));
            db.AdminRoles.Add(role);
            db.AdminUsers.Add(user);
            db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, now));
            await db.SaveChangesAsync();

            userId = user.Id;
        }

        var client = _factory.CreateDefaultClient(new CanonicalFingerprintDelegatingHandler());
        client.BaseAddress = new Uri("https://localhost");
        var loginResponse = await client.PostAsJsonAsync("/api/admin/v1/auth/login", new LoginRequestDto
        {
            Username = username,
            PasswordOrProviderAssertion = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var loginJson = await ReadJsonAsync(loginResponse);
        var challengeId = Guid.Parse(loginJson["challengeId"]!.GetValue<string>());

        var validTotp = _totpService.GenerateCode(totpSecret, DateTimeOffset.UtcNow);
        var mfaResponse = await client.PostAsJsonAsync("/api/admin/v1/auth/mfa", new MfaRequestDto
        {
            LoginChallengeId = challengeId,
            Proof = validTotp
        });
        Assert.Equal(HttpStatusCode.OK, mfaResponse.StatusCode);

        Assert.True(mfaResponse.Headers.TryGetValues("Set-Cookie", out var cookieHeaders));
        var sessionCookie = cookieHeaders
            .Select(header => header.Split(';', 2)[0])
            .Single(header => header.StartsWith("central_session=", StringComparison.OrdinalIgnoreCase));
        var sessionId = Guid.Parse(sessionCookie["central_session=".Length..]);

        client.DefaultRequestHeaders.Add("Cookie", sessionCookie);
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");

        return (sessionId, userId, client);
    }

    [Fact]
    public async Task CONE_T07_AdminOrderRoute_UsesCanonicalPayloadAndPersistsReplayableOrderIdentity()
    {
        var (_, userId, adminClient) = await CreateAdminClientAsync(new[] { "order.create" });
        Guid customerId;
        Guid shopId;
        Guid planId;
        Guid snapshotId;
        using (var db = GetDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            var customer = Customer.CreateFromContract("Canonical order test customer", null, now);
            var shop = Shop.RegisterFromContract(customer.Id, "Canonical order test shop", null, now);
            var item = PlanCatalogItem.Create($"CAN-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
                "Canonical order compatibility fixture", PlanCatalogItem.TargetBaseLicense,
                PlanCatalogItem.TermLifetime, null, 125m, "USD");
            var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.PlanCatalogItems.Add(item);
            db.PricingSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
            customerId = customer.Id;
            shopId = shop.Id;
            planId = item.Id;
            snapshotId = snapshot.Id;
        }

        var payload = new
        {
            shopId,
            orderType = "NEW_LICENSE",
            planId,
            pricingSnapshotId = snapshotId,
            requestedPrimaryCapacity = 1,
            termKind = PlanCatalogItem.TermLifetime,
            purchaseTarget = new { kind = PlanCatalogItem.TargetBaseLicense }
        };
        var operationId = Guid.NewGuid();
        var payloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex;
        var request = new
        {
            meta = new
            {
                clientOperationId = operationId,
                operationType = "order.create",
                fingerprintVersion = 1,
                payloadSha256,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Source-approved canonical order contract integration fixture"
            },
            payload
        };

        var response = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = await ReadJsonAsync(response);
        Assert.Equal("SUCCEEDED", outcome["state"]!.GetValue<string>());
        Assert.Equal(operationId, Guid.Parse(outcome["clientOperationId"]!.GetValue<string>()));
        var orderId = Guid.Parse(outcome["resultReference"]!.GetValue<string>()["orders/".Length..]);

        var replayResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", request);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await ReadJsonAsync(replayResponse);
        Assert.Equal(outcome["outcomeId"]!.GetValue<string>(), replay["outcomeId"]!.GetValue<string>());
        Assert.Equal(outcome["resultReference"]!.GetValue<string>(), replay["resultReference"]!.GetValue<string>());

        var staleOperationId = Guid.NewGuid();
        var staleVersionRequest = new
        {
            meta = new
            {
                clientOperationId = staleOperationId,
                operationType = "order.create",
                fingerprintVersion = 1,
                payloadSha256,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Stale pricing authority version must conflict"
            },
            payload
        };
        var staleVersionResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", staleVersionRequest);
        Assert.Equal(HttpStatusCode.Conflict, staleVersionResponse.StatusCode);

        var invalidPayload = new
        {
            shopId,
            orderType = "NEW_LICENSE",
            planId,
            pricingSnapshotId = snapshotId,
            requestedPrimaryCapacity = 1,
            termKind = PlanCatalogItem.TermLifetime,
            purchaseTarget = new { kind = PlanCatalogItem.TargetBaseLicense },
            unexpectedClientAuthority = true
        };
        var invalidRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "order.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(invalidPayload).Sha256Hex,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Unknown payload fields are rejected"
            },
            payload = invalidPayload
        };
        var invalidResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        using (var db = GetDbContext())
        {
            var order = await db.Orders.SingleAsync(x => x.Id == orderId);
            Assert.Equal(shopId, order.ShopId);
            Assert.Equal(customerId, order.CustomerId);
            Assert.Equal(snapshotId, order.SnapshotId);
            Assert.Equal(planId, order.PlanId);
            Assert.Equal("NEW_LICENSE", order.OrderType);
            Assert.Equal(1, order.RequestedPrimaryCapacity);
            Assert.Null(order.BaseLicenseId);
            Assert.Null(order.ExistingEntitlementId);

            var ledgerRecord = await db.CentralOperationRecords.SingleAsync(x => x.ClientOperationId == operationId);
            Assert.Equal($"admin:{userId:D}", ledgerRecord.ActorScope);
            Assert.Equal(shopId, ledgerRecord.ShopId);
            Assert.Equal(payloadSha256, ledgerRecord.PayloadSha256);
            Assert.Contains("pricingSnapshotId", ledgerRecord.CanonicalPayload, StringComparison.Ordinal);
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == staleOperationId));
            Assert.True(await db.CentralOutboxMessages.AnyAsync(x => x.Id == operationId));
            Assert.True(await db.CentralAuditRecords.AnyAsync(x => x.Action == "COMMERCIAL_ORDER_CREATED" && x.ResourceId == orderId.ToString("D")));
        }

        var changedPayload = new
        {
            shopId,
            orderType = "NEW_LICENSE",
            planId,
            pricingSnapshotId = Guid.NewGuid(),
            requestedPrimaryCapacity = 1,
            termKind = PlanCatalogItem.TermLifetime,
            purchaseTarget = new { kind = PlanCatalogItem.TargetBaseLicense }
        };
        var changedRequest = new
        {
            meta = new
            {
                clientOperationId = operationId,
                operationType = "order.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(changedPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Changed payload replay must conflict"
            },
            payload = changedPayload
        };
        var conflict = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", changedRequest);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task CONE_T07_AdminPricingSnapshotRoute_UsesCanonicalPayloadAndChecksPlanVersion()
    {
        var (_, userId, adminClient) = await CreateAdminClientAsync(new[] { "price.snapshot" });
        Guid shopId;
        Guid planId;
        using (var db = GetDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            var customer = Customer.CreateFromContract("Canonical price test customer", null, now);
            var shop = Shop.RegisterFromContract(customer.Id, "Canonical price test shop", null, now);
            var plan = PlanCatalogItem.Create($"PRC-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
                "Canonical price compatibility fixture", PlanCatalogItem.TargetBaseLicense,
                PlanCatalogItem.TermLifetime, null, 125m, "USD");
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.PlanCatalogItems.Add(plan);
            await db.SaveChangesAsync();
            shopId = shop.Id;
            planId = plan.Id;
        }

        var payload = new
        {
            shopId,
            planId,
            orderType = "NEW_LICENSE",
            billingPeriodId = (string?)null,
            currency = "USD",
            requestedPrimaryCapacity = 1,
            termKind = PlanCatalogItem.TermLifetime,
            purchaseTarget = new { kind = PlanCatalogItem.TargetBaseLicense }
        };
        var operationId = Guid.NewGuid();
        var request = new
        {
            meta = new
            {
                clientOperationId = operationId,
                operationType = "price.snapshot",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Source-approved canonical price snapshot integration fixture"
            },
            payload
        };

        var response = await adminClient.PostAsJsonAsync("/api/admin/v1/pricing-snapshots", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = await ReadJsonAsync(response);
        Assert.Equal("SUCCEEDED", outcome["state"]!.GetValue<string>());
        var snapshotId = Guid.Parse(outcome["resultReference"]!.GetValue<string>()["pricing-snapshots/".Length..]);
        var replayResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/pricing-snapshots", request);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await ReadJsonAsync(replayResponse);
        Assert.Equal(outcome["outcomeId"]!.GetValue<string>(), replay["outcomeId"]!.GetValue<string>());

        var staleOperationId = Guid.NewGuid();
        var staleRequest = new
        {
            meta = new
            {
                clientOperationId = staleOperationId,
                operationType = "price.snapshot",
                fingerprintVersion = 1,
                payloadSha256 = request.meta.payloadSha256,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Stale plan authority version must conflict"
            },
            payload
        };
        var staleResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/pricing-snapshots", staleRequest);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);

        using (var db = GetDbContext())
        {
            var snapshot = await db.PricingSnapshots.SingleAsync(x => x.Id == snapshotId);
            Assert.Equal(shopId, snapshot.ShopId);
            Assert.Equal(planId, snapshot.CatalogItemId);
            Assert.Equal(125m, snapshot.UnitPrice);
            Assert.Equal(1, snapshot.AuthorityVersion);
            var ledger = await db.CentralOperationRecords.SingleAsync(x => x.ClientOperationId == operationId);
            Assert.Equal($"admin:{userId:D}", ledger.ActorScope);
            Assert.True(await db.CentralOutboxMessages.AnyAsync(x => x.Id == operationId));
            Assert.True(await db.CentralAuditRecords.AnyAsync(x => x.Action == "COMMERCIAL_PRICING_SNAPSHOT_CREATED" && x.ResourceId == snapshotId.ToString("D")));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == staleOperationId));
        }
    }

    [Fact]
    public async Task CONE_T07_PricingSnapshot_ServerCalculatedAuthoritativeDigest()
    {
        var (_, _, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead, "customer.create", "shop.register", "price.snapshot" });

        // 1. Create Catalog Item
        var itemReq = new CreateCatalogItemRequest
        {
            Code = $"CAT-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Enterprise Base Plan",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermLifetime,
            BillingPeriodId = null,
            UnitPrice = 999.00m,
            Currency = "USD"
        };
        var itemRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", itemReq);
        Assert.Equal(HttpStatusCode.OK, itemRes.StatusCode);
        var itemJson = await ReadJsonAsync(itemRes);
        var itemId = Guid.Parse(itemJson["id"]!.GetValue<string>());

        // 2. Register Customer and Shop
        var custRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Acme Global POS",
            ContactEmail = $"acme_{Guid.NewGuid():N}@example.com",
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, custRes.StatusCode);
        var custJson = await ReadJsonAsync(custRes);
        var custId = Guid.Parse(custJson["id"]!.GetValue<string>());

        var shopRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = custId,
            ShopCode = $"SHOP-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Acme Flagship Store",
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, shopRes.StatusCode);
        var shopJson = await ReadJsonAsync(shopRes);
        var shopId = Guid.Parse(shopJson["id"]!.GetValue<string>());

        // 3. Request Server-Calculated Price Snapshot
        var snapRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, snapRes.StatusCode);
        var snapJson = await ReadJsonAsync(snapRes);
        var snapshotId = Guid.Parse(snapJson["id"]!.GetValue<string>());

        // 4. Verify authoritative server digest matches canonical rule
        string rawPayload = $"{shopId:N}:{itemId:N}:{PlanCatalogItem.TargetBaseLicense}:{PlanCatalogItem.TermLifetime}:NONE:999.00:USD:1";
        string expectedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload))).ToLowerInvariant();

        Assert.Equal(expectedDigest, snapJson["serverGeneratedDigest"]?.GetValue<string>());
        Assert.Equal(999.00m, snapJson["unitPrice"]?.GetValue<decimal>());
        Assert.Equal("USD", snapJson["currency"]?.GetValue<string>());

        // Verify persisted in PostgreSQL 18.6
        using var db = GetDbContext();
        var persisted = await db.PricingSnapshots.FindAsync(snapshotId);
        Assert.NotNull(persisted);
        Assert.Equal(expectedDigest, persisted.ServerGeneratedDigest);
    }

    [Fact]
    public async Task CONE_T08_ChunkedEvidenceUpload_CompleteSession_AndQuarantineDefenses()
    {
        var (_, policyAdminId, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

        // Setup customer, shop, catalog item, snapshot, and order
        var itemRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
        {
            Code = $"CAT-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Fixed Term License",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermFixed,
            BillingPeriodId = "ANNUAL",
            UnitPrice = 250.00m,
            Currency = "USD"
        });
        var itemId = Guid.Parse((await ReadJsonAsync(itemRes))["id"]!.GetValue<string>());

        var custRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Evidence Test Corp",
            ContactEmail = $"evidence_{Guid.NewGuid():N}@test.com",
            CommandMeta = CreateMeta()
        });
        var custId = Guid.Parse((await ReadJsonAsync(custRes))["id"]!.GetValue<string>());

        var shopRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = custId,
            ShopCode = $"SHOP-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Evidence Shop",
            CommandMeta = CreateMeta()
        });
        var shopId = Guid.Parse((await ReadJsonAsync(shopRes))["id"]!.GetValue<string>());

        var snapRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        });
        var snapId = Guid.Parse((await ReadJsonAsync(snapRes))["id"]!.GetValue<string>());

        var orderRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = custId,
            SnapshotId = snapId,
            CommandMeta = CreateMeta()
        });
        var orderId = Guid.Parse((await ReadJsonAsync(orderRes))["id"]!.GetValue<string>());

        // Journey 1: Successful Chunked Upload Session
        var chunk1 = Encoding.UTF8.GetBytes("Payment confirmation document part 1. ");
        var chunk2 = Encoding.UTF8.GetBytes("Payment confirmation document part 2.");
        var totalPayload = chunk1.Concat(chunk2).ToArray();
        var totalSha256 = Convert.ToHexString(SHA256.HashData(totalPayload)).ToLowerInvariant();

        var initRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/uploads/sessions", new InitUploadSessionCommand
        {
            Purpose = UploadSession.PurposePaymentProof,
            ShopId = shopId,
            TargetEntityId = orderId,
            ExpectedBytes = totalPayload.Length,
            ExpectedSha256 = totalSha256,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, initRes.StatusCode);
        var sessionId = Guid.Parse((await ReadJsonAsync(initRes))["id"]!.GetValue<string>());

        // Append Chunk 1
        var appendReq1 = new HttpRequestMessage(HttpMethod.Post, $"/api/commercial/v1/uploads/sessions/{sessionId}/chunks")
        {
            Content = new ByteArrayContent(chunk1)
        };
        var appendRes1 = await adminClient.SendAsync(appendReq1);
        Assert.Equal(HttpStatusCode.OK, appendRes1.StatusCode);

        // Append Chunk 2
        var appendReq2 = new HttpRequestMessage(HttpMethod.Post, $"/api/commercial/v1/uploads/sessions/{sessionId}/chunks")
        {
            Content = new ByteArrayContent(chunk2)
        };
        var appendRes2 = await adminClient.SendAsync(appendReq2);
        Assert.Equal(HttpStatusCode.OK, appendRes2.StatusCode);

        // Finalize Session
        var finRes = await adminClient.PostAsJsonAsync($"/api/commercial/v1/uploads/sessions/{sessionId}/finalize?orderId={orderId}", new FinalizeUploadSessionCommand
        {
            ComputedSha256 = totalSha256,
            MimeType = "application/pdf",
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, finRes.StatusCode);
        var evidenceJson = await ReadJsonAsync(finRes);
        Assert.Equal(orderId.ToString(), evidenceJson["orderId"]?.GetValue<string>());
        Assert.Equal(totalSha256, evidenceJson["sha256Digest"]?.GetValue<string>());

        var proofId = Guid.Parse(evidenceJson["id"]!.GetValue<string>());
        var reviewPayload = new
        {
            orderId,
            proofId,
            decision = "APPROVE",
            reasonCode = "PAYMENT_CONFIRMED",
            internalNote = "Synthetic integration fixture approval"
        };
        var reviewOperationId = Guid.NewGuid();
        var reviewRequest = new
        {
            meta = new
            {
                clientOperationId = reviewOperationId,
                operationType = "payment.review",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(reviewPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Synthetic payment review contract test"
            },
            payload = reviewPayload
        };
        var reviewResponse = await adminClient.PostAsJsonAsync($"/api/admin/v1/payments/{proofId}/review", reviewRequest);
        Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
        var reviewOutcome = await ReadJsonAsync(reviewResponse);
        Assert.Equal("SUCCEEDED", reviewOutcome["state"]!.GetValue<string>());
        Assert.Equal(reviewOperationId, Guid.Parse(reviewOutcome["clientOperationId"]!.GetValue<string>()));
        Assert.StartsWith("payment_reviews/", reviewOutcome["resultReference"]!.GetValue<string>(), StringComparison.Ordinal);

        var replayResponse = await adminClient.PostAsJsonAsync($"/api/admin/v1/payments/{proofId}/review", reviewRequest);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayOutcome = await ReadJsonAsync(replayResponse);
        Assert.Equal(reviewOutcome["outcomeId"]!.GetValue<string>(), replayOutcome["outcomeId"]!.GetValue<string>());
        using (var db = GetDbContext())
        {
            var persistedEvidence = await db.PaymentEvidences.FindAsync(proofId);
            var persistedOrder = await db.Orders.FindAsync(orderId);
            var persistedReview = await db.PaymentReviewRecords.SingleAsync(x => x.EvidenceId == proofId);
            Assert.Equal(PaymentEvidence.ReviewApproved, persistedEvidence!.ReviewStatus);
            Assert.Equal(Order.StatusPaidApproved, persistedOrder!.Status);
            Assert.Equal("PAYMENT_CONFIRMED", persistedReview.ReasonCode);
            Assert.Equal("Synthetic integration fixture approval", persistedReview.Notes);
            Assert.Equal(1, await db.CentralOperationRecords.CountAsync(x => x.ClientOperationId == reviewOperationId));
            Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.Id == reviewOperationId));
            Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.CorrelationId == reviewOperationId.ToString()));
        }

        var staleOperationId = Guid.NewGuid();
        var staleReview = new
        {
            meta = new
            {
                clientOperationId = staleOperationId,
                operationType = "payment.review",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(reviewPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Stale proof version must not create a second review"
            },
            payload = reviewPayload
        };
        var staleResponse = await adminClient.PostAsJsonAsync($"/api/admin/v1/payments/{proofId}/review", staleReview);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        using (var db = GetDbContext())
        {
            Assert.Equal(0, await db.CentralOperationRecords.CountAsync(x => x.ClientOperationId == staleOperationId));
            Assert.Equal(1, await db.PaymentReviewRecords.CountAsync(x => x.EvidenceId == proofId));
        }

        // Journey 2: Quarantine Defense - Reject session exceeding 100MB ceiling
        var over100MbRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/uploads/sessions", new InitUploadSessionCommand
        {
            Purpose = UploadSession.PurposePaymentProof,
            ShopId = shopId,
            TargetEntityId = orderId,
            ExpectedBytes = 104857601, // 100MB + 1 byte
            ExpectedSha256 = new string('a', 64),
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.BadRequest, over100MbRes.StatusCode);

        // Journey 3: Quarantine Defense - Uploading oversized chunks triggers OVERSIZE_UPLOAD
        var smallSessionRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/uploads/sessions", new InitUploadSessionCommand
        {
            Purpose = UploadSession.PurposePaymentProof,
            ShopId = shopId,
            TargetEntityId = orderId,
            ExpectedBytes = 10,
            ExpectedSha256 = new string('b', 64),
            CommandMeta = CreateMeta()
        });
        var smallSessionId = Guid.Parse((await ReadJsonAsync(smallSessionRes))["id"]!.GetValue<string>());

        var badChunk = new HttpRequestMessage(HttpMethod.Post, $"/api/commercial/v1/uploads/sessions/{smallSessionId}/chunks")
        {
            Content = new ByteArrayContent(new byte[50]) // Exceeds 10 bytes expected
        };
        var badChunkRes = await adminClient.SendAsync(badChunk);
        Assert.Equal(HttpStatusCode.BadRequest, badChunkRes.StatusCode);

        // Journey 4: Quarantine Defense - Hash mismatch triggers SHA256_MISMATCH
        var mismatchSessionRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/uploads/sessions", new InitUploadSessionCommand
        {
            Purpose = UploadSession.PurposePaymentProof,
            ShopId = shopId,
            TargetEntityId = orderId,
            ExpectedBytes = 5,
            ExpectedSha256 = new string('c', 64),
            CommandMeta = CreateMeta()
        });
        var mismatchSessionId = Guid.Parse((await ReadJsonAsync(mismatchSessionRes))["id"]!.GetValue<string>());

        await adminClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/commercial/v1/uploads/sessions/{mismatchSessionId}/chunks")
        {
            Content = new ByteArrayContent(new byte[5])
        });

        var mismatchFinRes = await adminClient.PostAsJsonAsync($"/api/commercial/v1/uploads/sessions/{mismatchSessionId}/finalize?orderId={orderId}", new FinalizeUploadSessionCommand
        {
            ComputedSha256 = new string('d', 64), // Discrepancy with declared expectedSha256
            MimeType = "application/pdf",
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.BadRequest, mismatchFinRes.StatusCode);
    }

    [Fact]
    public async Task CONE_T09_CommercialLicense_LifetimeBase_CannotBeRenewed()
    {
        var (_, _, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

        // Setup Lifetime Base License order
        var itemRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
        {
            Code = $"CAT-LIFE-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Lifetime Perpetual Plan",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermLifetime,
            UnitPrice = 1500.00m,
            Currency = "USD"
        });
        var itemId = Guid.Parse((await ReadJsonAsync(itemRes))["id"]!.GetValue<string>());

        var custRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Lifetime Corp",
            ContactEmail = $"lifetime_{Guid.NewGuid():N}@test.com",
            CommandMeta = CreateMeta()
        });
        var custId = Guid.Parse((await ReadJsonAsync(custRes))["id"]!.GetValue<string>());

        var shopRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = custId,
            ShopCode = $"SHOP-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Lifetime Shop",
            CommandMeta = CreateMeta()
        });
        var shopId = Guid.Parse((await ReadJsonAsync(shopRes))["id"]!.GetValue<string>());

        var snapRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        });
        var snapId = Guid.Parse((await ReadJsonAsync(snapRes))["id"]!.GetValue<string>());

        var orderRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = custId,
            SnapshotId = snapId,
            CommandMeta = CreateMeta()
        });
        var orderId = Guid.Parse((await ReadJsonAsync(orderRes))["id"]!.GetValue<string>());

        // Directly approve order payment for issuance test
        using (var db = GetDbContext())
        {
            var dbOrder = await db.Orders.FindAsync(orderId);
            dbOrder!.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // Issue Lifetime License
        var now = DateTimeOffset.UtcNow;
        var issueRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", new IssueLicenseCommand
        {
            ApprovedOrderId = orderId,
            ValidFromUtc = now,
            ValidUntilUtc = null, // Invariant: Lifetime must have null ValidUntilUtc
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, issueRes.StatusCode);
        var licenseJson = await ReadJsonAsync(issueRes);
        var licenseId = Guid.Parse(licenseJson["id"]!.GetValue<string>());
        Assert.Null(licenseJson["validUntilUtc"]);
        Assert.Equal(PlanCatalogItem.TermLifetime, licenseJson["termKind"]?.GetValue<string>());
        Assert.Equal(CommercialLicense.StatusActive, licenseJson["status"]?.GetValue<string>());

        // Invariant: Lifetime license cannot be renewed
        var renewRes = await adminClient.PostAsJsonAsync($"/api/commercial/v1/licenses/{licenseId}/renew", new RenewLicenseCommand
        {
            NewValidUntilUtc = now.AddYears(1),
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.BadRequest, renewRes.StatusCode);
        var error = await ReadJsonAsync(renewRes);
        Assert.Equal("LIFETIME_CANNOT_RENEW", error["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CONE_T09_CommercialLicense_FixedTerm_AllowsRenewal_AndAuthorityIncrements()
    {
        var (_, _, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

        // Setup Fixed-Term Base License order
        var itemRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
        {
            Code = $"CAT-TERM-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Annual Subscription Plan",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermFixed,
            BillingPeriodId = "ANNUAL",
            UnitPrice = 300.00m,
            Currency = "USD"
        });
        var itemId = Guid.Parse((await ReadJsonAsync(itemRes))["id"]!.GetValue<string>());

        var custRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Term Corp",
            ContactEmail = $"term_{Guid.NewGuid():N}@test.com",
            CommandMeta = CreateMeta()
        });
        var custId = Guid.Parse((await ReadJsonAsync(custRes))["id"]!.GetValue<string>());

        var shopRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = custId,
            ShopCode = $"SHOP-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Term Shop",
            CommandMeta = CreateMeta()
        });
        var shopId = Guid.Parse((await ReadJsonAsync(shopRes))["id"]!.GetValue<string>());

        var snapRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        });
        var snapId = Guid.Parse((await ReadJsonAsync(snapRes))["id"]!.GetValue<string>());

        var orderRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = custId,
            SnapshotId = snapId,
            CommandMeta = CreateMeta()
        });
        var orderId = Guid.Parse((await ReadJsonAsync(orderRes))["id"]!.GetValue<string>());

        using (var db = GetDbContext())
        {
            var dbOrder = await db.Orders.FindAsync(orderId);
            dbOrder!.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // Issue Fixed Term License
        var now = DateTimeOffset.UtcNow;
        var initialValidUntil = now.AddDays(365);
        var issueRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", new IssueLicenseCommand
        {
            ApprovedOrderId = orderId,
            ValidFromUtc = now,
            ValidUntilUtc = initialValidUntil,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, issueRes.StatusCode);
        var licenseJson = await ReadJsonAsync(issueRes);
        var licenseId = Guid.Parse(licenseJson["id"]!.GetValue<string>());
        Assert.Equal(1, licenseJson["authorityRevision"]?.GetValue<int>());
        Assert.NotNull(licenseJson["validUntilUtc"]);

        // Renew Fixed Term License
        var renewedValidUntil = initialValidUntil.AddDays(365);

        // Negative: Renewal without approved paid order fails closed (ORDER_REQUIRED)
        var unapprovedRenewRes = await adminClient.PostAsJsonAsync($"/api/commercial/v1/licenses/{licenseId}/renew", new RenewLicenseCommand
        {
            NewValidUntilUtc = renewedValidUntil,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.BadRequest, unapprovedRenewRes.StatusCode);
        var unapprovedRenewErr = await ReadJsonAsync(unapprovedRenewRes);
        Assert.Equal("ORDER_REQUIRED", unapprovedRenewErr["code"]?.GetValue<string>());

        // Create and approve a paid renewal order
        var renewalOrderId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = custId,
            SnapshotId = snapId,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        using (var db = GetDbContext())
        {
            var dbOrder = await db.Orders.FindAsync(renewalOrderId);
            dbOrder!.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // Renew Fixed Term License with approved paid order
        var renewRes = await adminClient.PostAsJsonAsync($"/api/commercial/v1/licenses/{licenseId}/renew", new RenewLicenseCommand
        {
            ApprovedOrderId = renewalOrderId,
            NewValidUntilUtc = renewedValidUntil,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, renewRes.StatusCode);
        var renewedLicenseJson = await ReadJsonAsync(renewRes);
        Assert.Equal(2, renewedLicenseJson["authorityRevision"]?.GetValue<int>());
    }

    [Fact]
    public async Task CONE_R01_ZeroTrustIdentity_RejectsForgedAdminHeaderWithoutValidSession()
    {
        // Forge X-Admin-User-Id header without valid session
        var rawClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        rawClient.DefaultRequestHeaders.Add("X-Admin-User-Id", Guid.NewGuid().ToString());

        var res = await rawClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Attacker Corp",
            ContactEmail = "attacker@test.com",
            CommandMeta = CreateMeta()
        });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task CONE_T10_Activation_TerminalIdentity_TokenHashPersistence()
    {
        var (_, _, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

        // Setup base shop and license
        var custId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Activation Identity Corp",
            ContactEmail = $"ident_{Guid.NewGuid():N}@test.com",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var shopId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = custId,
            ShopCode = $"SHOP-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Activation Shop",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var itemId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
        {
            Code = $"CAT-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Activation Base Plan",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermLifetime,
            UnitPrice = 500m,
            Currency = "USD"
        })))["id"]!.GetValue<string>());

        var snapId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var orderId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = custId,
            SnapshotId = snapId,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        using (var db = GetDbContext())
        {
            var dbOrder = await db.Orders.FindAsync(orderId);
            dbOrder!.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var licenseId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", new IssueLicenseCommand
        {
            ApprovedOrderId = orderId,
            ValidFromUtc = DateTimeOffset.UtcNow,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        // 1. Register Installation with RSA-4096 Public Key
        using var clientRsa = RSA.Create(4096);
        var pubPem = clientRsa.ExportSubjectPublicKeyInfoPem();
        var hardwareFingerprint = "HW-CPU-CORE-i9-DISK-NVME-MAC-001122334455";

        var installId = await AddPersistedInstallationFixtureAsync(shopId, licenseId, pubPem, hardwareFingerprint, "POS-TERMINAL-1");
        using (var db = GetDbContext())
        {
            var installation = await db.Installations.FindAsync(installId);
            Assert.NotNull(installation);
            Assert.Equal(Installation.StatusRegistered, installation.Status);
        }

        // 2. Issue Activation Token
        var tokenRes = await adminClient.PostAsJsonAsync("/api/activation/v1/tokens", new IssueActivationTokenCommand
        {
            ShopId = shopId,
            LicenseId = licenseId,
            FirstRedemptionTtlSeconds = 86400,
            CommandMeta = CreateMeta()
        });
        Assert.True(tokenRes.StatusCode == HttpStatusCode.OK,
            $"Activation-token issue failed with {(int)tokenRes.StatusCode}: {await tokenRes.Content.ReadAsStringAsync()}");
        var tokenJson = await ReadJsonAsync(tokenRes);
        var tokenId = Guid.Parse(tokenJson["tokenId"]!.GetValue<string>());
        var tokenSecret = tokenJson["tokenSecret"]!.GetValue<string>();
        Assert.NotEmpty(tokenSecret); // Secret returned once to caller

        // 3. Inspect PostgreSQL 18.6 DB: Prove TokenHashSha256 persisted, Raw Secret NEVER persisted
        using var checkDb = GetDbContext();
        var dbToken = await checkDb.ActivationTokens.FindAsync(tokenId);
        Assert.NotNull(dbToken);
        Assert.Equal(licenseId, dbToken.LicenseId);

        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenSecret))).ToLowerInvariant();
        Assert.Equal(expectedHash, dbToken.TokenHashSha256);
        Assert.NotEqual(tokenSecret, dbToken.TokenHashSha256);
    }

    [Fact]
    public async Task CONE_T11_Activation_ChallengeResponseRedemption_AndMonotonicCertificate()
    {
        var (_, _, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

        // Setup shop, lifetime license, installation
        var custId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Cert Test Corp",
            ContactEmail = $"cert_{Guid.NewGuid():N}@test.com",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var shopId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = custId,
            ShopCode = $"SHOP-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Cert Shop",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var itemId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
        {
            Code = $"CAT-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Cert Base Plan",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermLifetime,
            UnitPrice = 500m,
            Currency = "USD"
        })))["id"]!.GetValue<string>());

        var snapId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var orderId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = custId,
            SnapshotId = snapId,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        using (var db = GetDbContext())
        {
            var dbOrder = await db.Orders.FindAsync(orderId);
            dbOrder!.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var licenseId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", new IssueLicenseCommand
        {
            ApprovedOrderId = orderId,
            ValidFromUtc = DateTimeOffset.UtcNow,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        using var clientRsa = RSA.Create(2048);
        var pubPem = clientRsa.ExportSubjectPublicKeyInfoPem();

        var installId = Guid.NewGuid();

        using var activationIssueResponse = await adminClient.PostAsJsonAsync("/api/activation/v1/tokens", new IssueActivationTokenCommand
        {
            ShopId = shopId,
            LicenseId = licenseId,
            FirstRedemptionTtlSeconds = 86400,
            CommandMeta = CreateMeta()
        });
        Assert.True(activationIssueResponse.StatusCode == HttpStatusCode.OK,
            $"Activation-token issue failed with {(int)activationIssueResponse.StatusCode}: {await activationIssueResponse.Content.ReadAsStringAsync()}");
        var tokenJson = await ReadJsonAsync(activationIssueResponse);
        var tokenId = Guid.Parse(tokenJson["tokenId"]!.GetValue<string>());
        var tokenSecret = tokenJson["tokenSecret"]!.GetValue<string>();

        // Bootstrap uses only the contract-defined challenge, proof-of-possession, and activation routes.
        var activationTokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tokenSecret.Trim())));
        var requestNonce = Guid.NewGuid();
        var challengeRequest = new BootstrapChallengeRequestDto
        {
            InstallationId = installId,
            InstallationPublicKey = pubPem,
            ActivationTokenHash = activationTokenHash,
            RequestNonce = requestNonce
        };

        // Competing bootstrap requests may both pass the preflight replay lookup.
        // The durable unique nonce must still yield one issued challenge and one
        // contract-level replay denial rather than an unhandled database exception.
        var concurrentNonce = Guid.NewGuid();
        var concurrentChallengeRequest = new BootstrapChallengeRequestDto
        {
            InstallationId = Guid.NewGuid(),
            InstallationPublicKey = pubPem,
            ActivationTokenHash = activationTokenHash,
            RequestNonce = concurrentNonce
        };
        var concurrentChallengeResponses = await Task.WhenAll(
            _client.PostAsJsonAsync("/api/licensing/v1/bootstrap/challenge", concurrentChallengeRequest),
            _client.PostAsJsonAsync("/api/licensing/v1/bootstrap/challenge", concurrentChallengeRequest));
        Assert.Single(concurrentChallengeResponses, x => x.StatusCode == HttpStatusCode.OK);
        var concurrentReplay = Assert.Single(concurrentChallengeResponses, x => x.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal("BOOTSTRAP_REQUEST_REPLAYED", (await ReadJsonAsync(concurrentReplay))["code"]?.GetValue<string>());
        using (var db = GetDbContext())
        {
            Assert.Equal(1, await db.BootstrapChallenges.CountAsync(x => x.RequestNonce == concurrentNonce));
        }
        foreach (var response in concurrentChallengeResponses)
        {
            response.Dispose();
        }

        var challengeRes = await _client.PostAsJsonAsync("/api/licensing/v1/bootstrap/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.OK, challengeRes.StatusCode);
        var challengeJson = await ReadJsonAsync(challengeRes);
        var challengeId = Guid.Parse(challengeJson["challengeId"]!.GetValue<string>());
        var challengeNonce = challengeJson["challengeNonce"]!.GetValue<string>();
        Assert.NotEmpty(challengeNonce);

        var replayedChallenge = await _client.PostAsJsonAsync("/api/licensing/v1/bootstrap/challenge", challengeRequest);
        Assert.Equal(HttpStatusCode.BadRequest, replayedChallenge.StatusCode);
        Assert.Equal("BOOTSTRAP_REQUEST_REPLAYED", (await ReadJsonAsync(replayedChallenge))["code"]?.GetValue<string>());

        var registrationPayload = new InstallRegistrationPayloadDto
        {
            ShopId = shopId,
            LicenseId = licenseId,
            InstallationId = installId,
            InstallationPublicKey = pubPem,
            InstallationPublicKeyFingerprint = Installation.ComputePublicKeyFingerprintSha256(pubPem),
            BootstrapChallengeId = challengeId,
            DeviceIdentityVersion = 1,
            DeviceIdentityHash = new string('a', 64),
            HardwareFingerprintVersion = 1,
            HardwareFingerprintHash = new string('b', 64),
            SignalQuality = "STRONG",
            ApplicationVersion = "0.0.0-dev-fixture"
        };
        var unsignedRegistrationPayload = new
        {
            registrationPayload.ShopId,
            registrationPayload.LicenseId,
            registrationPayload.InstallationId,
            registrationPayload.InstallationPublicKey,
            registrationPayload.InstallationPublicKeyFingerprint,
            registrationPayload.BootstrapChallengeId,
            registrationPayload.DeviceIdentityVersion,
            registrationPayload.DeviceIdentityHash,
            registrationPayload.HardwareFingerprintVersion,
            registrationPayload.HardwareFingerprintHash,
            registrationPayload.SignalQuality,
            registrationPayload.ApplicationVersion
        };
        var registrationDigest = _serializer.SerializeAndHash(unsignedRegistrationPayload).Sha256Hex;
        var registrationEnvelope = _serializer.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_BOOTSTRAP_V1",
            purpose = "installation.register",
            httpMethod = "POST",
            httpPath = "/api/licensing/v1/primary/register",
            challengeId,
            challengeNonce,
            requestNonce,
            activationTokenHash,
            payloadDigest = registrationDigest
        });
        registrationPayload.ProofOfPossession = Convert.ToHexString(clientRsa.SignData(Encoding.UTF8.GetBytes(registrationEnvelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).ToLowerInvariant();
        var registrationMeta = new CommandMetaDto
        {
            ClientOperationId = Guid.NewGuid(),
            OperationType = "installation.register",
            PayloadSha256 = _serializer.SerializeAndHash(registrationPayload).Sha256Hex,
            Reason = "Synthetic development bootstrap registration"
        };

        // A different submitted public key cannot replace the key pinned by this challenge.
        var trustedPublicKey = registrationPayload.InstallationPublicKey;
        var trustedFingerprint = registrationPayload.InstallationPublicKeyFingerprint;
        using (var substitutedRsa = RSA.Create(2048))
        {
            registrationPayload.InstallationPublicKey = substitutedRsa.ExportSubjectPublicKeyInfoPem();
            registrationPayload.InstallationPublicKeyFingerprint = Installation.ComputePublicKeyFingerprintSha256(registrationPayload.InstallationPublicKey);
            var substitutedMeta = new CommandMetaDto
            {
                ClientOperationId = Guid.NewGuid(),
                OperationType = "installation.register",
                PayloadSha256 = _serializer.SerializeAndHash(registrationPayload).Sha256Hex,
                Reason = "Synthetic key-substitution negative"
            };
            var substitutedResponse = await _client.PostAsJsonAsync("/api/licensing/v1/primary/register", new InstallRegistrationRequestDto
            {
                Meta = substitutedMeta,
                Payload = registrationPayload
            });
            Assert.Equal(HttpStatusCode.BadRequest, substitutedResponse.StatusCode);
            Assert.Equal("INSTALLATION_KEY_MISMATCH", (await ReadJsonAsync(substitutedResponse))["code"]?.GetValue<string>());
            using var db = GetDbContext();
            Assert.False(await db.Installations.AnyAsync(x => x.Id == installId));
        }
        registrationPayload.InstallationPublicKey = trustedPublicKey;
        registrationPayload.InstallationPublicKeyFingerprint = trustedFingerprint;

        var registrationRes = await _client.PostAsJsonAsync("/api/licensing/v1/primary/register", new InstallRegistrationRequestDto
        {
            Meta = registrationMeta,
            Payload = registrationPayload
        });
        Assert.Equal(HttpStatusCode.OK, registrationRes.StatusCode);
        var registrationOutcome = await ReadJsonAsync(registrationRes);
        Assert.Equal("SUCCEEDED", registrationOutcome["state"]?.GetValue<string>());

        Installation registeredInstallation;
        using (var db = GetDbContext())
        {
            registeredInstallation = await db.Installations.FindAsync(installId)
                ?? throw new InvalidOperationException("Registered installation fixture was not persisted.");
            Assert.Equal(Installation.StatusRegistered, registeredInstallation.Status);
        }
        var unauthorizedReconcile = await _client.PostAsJsonAsync("/api/licensing/v1/reconcile", new ReconcileCommandDto
        {
            InstallationId = installId,
            CertificateId = Guid.NewGuid(),
            AuthorityRevision = 0,
            HardwareFingerprintHash = registrationPayload.HardwareFingerprintHash,
            ApplicationVersion = "0.0.0-dev-fixture",
            LastVerifiedVendorTimeUtc = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReconcile.StatusCode);

        var redeemPayload = new BootstrapActivationRedeemPayloadDto
        {
            ActivationToken = tokenSecret,
            InstallationId = installId,
            HardwareFingerprintHash = registrationPayload.HardwareFingerprintHash,
            InstallationPublicKeyFingerprint = registrationPayload.InstallationPublicKeyFingerprint,
            ValidationNonce = Guid.NewGuid(),
            BootstrapChallengeId = challengeId
        };
        var unsignedRedeemPayload = new
        {
            redeemPayload.ActivationToken,
            redeemPayload.InstallationId,
            redeemPayload.HardwareFingerprintHash,
            redeemPayload.InstallationPublicKeyFingerprint,
            redeemPayload.ValidationNonce,
            redeemPayload.BootstrapChallengeId
        };
        var redeemDigest = _serializer.SerializeAndHash(unsignedRedeemPayload).Sha256Hex;
        var redeemEnvelope = _serializer.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_BOOTSTRAP_V1",
            purpose = "activation.redeem",
            httpMethod = "POST",
            httpPath = "/api/licensing/v1/activation/redeem",
            challengeId,
            challengeNonce,
            requestNonce,
            validationNonce = redeemPayload.ValidationNonce,
            activationTokenHash,
            payloadDigest = redeemDigest
        });
        redeemPayload.ProofOfPossession = Convert.ToHexString(clientRsa.SignData(Encoding.UTF8.GetBytes(redeemEnvelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).ToLowerInvariant();
        var redeemMeta = new CommandMetaDto
        {
            ClientOperationId = Guid.NewGuid(),
            OperationType = "activation.redeem",
            PayloadSha256 = _serializer.SerializeAndHash(redeemPayload).Sha256Hex,
            Reason = "Synthetic development bootstrap redemption"
        };
        var redeemRes = await _client.PostAsJsonAsync("/api/licensing/v1/activation/redeem", new BootstrapActivationRedeemRequestDto
        {
            Meta = redeemMeta,
            Payload = redeemPayload
        });
        Assert.Equal(HttpStatusCode.OK, redeemRes.StatusCode);
        var redemptionOutcome = await ReadJsonAsync(redeemRes);
        Assert.Equal("SUCCEEDED", redemptionOutcome["state"]?.GetValue<string>());
        var redemptionId = Guid.Parse(redemptionOutcome["resultReference"]!.GetValue<string>().Split('/').Last());
        using (var db = GetDbContext())
        {
            Assert.Equal(ActivationRedemption.StatusPending, (await db.ActivationRedemptions.FindAsync(redemptionId))!.Status);
        }

        using (var unsignedStatusRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/activation/{redemptionId:D}/status"))
        using (var unsignedStatusResponse = await _client.SendAsync(unsignedStatusRequest))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unsignedStatusResponse.StatusCode);
        }

        using (var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/activation/{redemptionId:D}/status"))
        {
            AddInstallationRequestProof(statusRequest, registeredInstallation, clientRsa);
            using var statusResponse = await _client.SendAsync(statusRequest);
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            var activationStatus = await ReadJsonAsync(statusResponse);
            Assert.Equal(redeemMeta.ClientOperationId.ToString("D"), activationStatus["clientOperationId"]?.GetValue<string>());
            Assert.Equal("activation.redeem", activationStatus["operationType"]?.GetValue<string>());
            Assert.Equal(redeemMeta.PayloadSha256, activationStatus["payloadSha256"]?.GetValue<string>());
            Assert.Equal(redemptionOutcome["outcomeId"]?.GetValue<string>(), activationStatus["outcomeId"]?.GetValue<string>());
            Assert.Equal($"redemptions/{redemptionId:D}", activationStatus["resultReference"]?.GetValue<string>());
            Assert.Equal("PENDING", activationStatus["state"]?.GetValue<string>());
            Assert.Equal(redemptionOutcome["resourceVersion"]?.GetValue<int>(), activationStatus["resourceVersion"]?.GetValue<int>());
            Assert.Equal(redemptionOutcome["authorityRevision"]?.GetValue<int>(), activationStatus["authorityRevision"]?.GetValue<int>());
        }

        // 4. Admin Reviews and Approves Redemption
        var reviewRes = await adminClient.PostAsJsonAsync($"/api/activation/v1/redemptions/{redemptionId}/review", new ReviewActivationCommand
        {
            Decision = "APPROVE",
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, reviewRes.StatusCode);

        using (var approvedStatusRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/activation/{redemptionId:D}/status"))
        {
            AddInstallationRequestProof(approvedStatusRequest, registeredInstallation, clientRsa);
            using var approvedStatusResponse = await _client.SendAsync(approvedStatusRequest);
            Assert.Equal(HttpStatusCode.OK, approvedStatusResponse.StatusCode);
            Assert.Equal("SUCCEEDED", (await ReadJsonAsync(approvedStatusResponse))["state"]?.GetValue<string>());
        }

        // 5. Issue License Certificate via ProtectedSigningEngine
        var certRes1 = await adminClient.PostAsJsonAsync("/api/activation/v1/certificates/issue", new IssueCertificateRequest
        {
            InstallationId = installId
        });
        Assert.Equal(HttpStatusCode.OK, certRes1.StatusCode);
        var cert1Json = await ReadJsonAsync(certRes1);
        var cert1Id = Guid.Parse(cert1Json["id"]!.GetValue<string>());
        Assert.Equal(LicenseCertificate.StatusActive, cert1Json["status"]?.GetValue<string>());
        var sig1 = (cert1Json["rs256Signature"] ?? cert1Json["rS256Signature"] ?? cert1Json["RS256Signature"])?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(sig1));

        using (var db = GetDbContext())
        {
            var dbCert = await db.LicenseCertificates.FindAsync(cert1Id);
            Assert.NotNull(dbCert);
            Assert.NotEmpty(dbCert.RS256Signature);
        }

        // Verify Monotonic Time Bounds: ValidFromUtc <= LeaseExpiresUtc <= OfflineGraceUntilUtc
        var validFromUtc = cert1Json["validFromUtc"]!.GetValue<DateTimeOffset>();
        var leaseExpiresUtc = cert1Json["leaseExpiresUtc"]!.GetValue<DateTimeOffset>();
        var offlineGraceUntilUtc = cert1Json["offlineGraceUntilUtc"]!.GetValue<DateTimeOffset>();
        Assert.True(validFromUtc <= leaseExpiresUtc, "validFromUtc <= leaseExpiresUtc");
        Assert.True(leaseExpiresUtc <= offlineGraceUntilUtc, "leaseExpiresUtc <= offlineGraceUntilUtc");

        // 6. Issue a second certificate for the same installation -> Proves strictly 1 active certificate
        var certRes2 = await adminClient.PostAsJsonAsync("/api/activation/v1/certificates/issue", new IssueCertificateRequest
        {
            InstallationId = installId
        });
        Assert.Equal(HttpStatusCode.OK, certRes2.StatusCode);
        var cert2Json = await ReadJsonAsync(certRes2);
        Assert.Equal(LicenseCertificate.StatusActive, cert2Json["status"]?.GetValue<string>());

        using (var db = GetDbContext())
        {
            var oldCert = await db.LicenseCertificates.FindAsync(cert1Id);
            Assert.Equal(LicenseCertificate.StatusSuperseded, oldCert!.Status);
        }
    }

    [Fact]
    public async Task CONE_T12_HardwareDiscrepancy_AndPrimaryTransfer_DualControl()
    {
        var (_, admin1Id, adminClient1) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });
        var (_, admin2Id, adminClient2) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

        // Setup shop and license
        var custId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Transfer Corp",
            ContactEmail = $"transfer_{Guid.NewGuid():N}@test.com",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var shopId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = custId,
            ShopCode = $"SHOP-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Transfer Shop",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var itemId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
        {
            Code = $"CAT-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Transfer Base Plan",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermLifetime,
            UnitPrice = 500m,
            Currency = "USD"
        })))["id"]!.GetValue<string>());

        var snapId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var orderId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = custId,
            SnapshotId = snapId,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        using (var db = GetDbContext())
        {
            var dbOrder = await db.Orders.FindAsync(orderId);
            dbOrder!.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var licenseId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/licenses/issue", new IssueLicenseCommand
        {
            ApprovedOrderId = orderId,
            ValidFromUtc = DateTimeOffset.UtcNow,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        // Register Primary Source Installation
        using var rsaSource = RSA.Create(2048);
        var sourceInstallId = await AddPersistedInstallationFixtureAsync(
            shopId, licenseId, rsaSource.ExportSubjectPublicKeyInfoPem(), "HW-SOURCE-OLD-FP", "OLD-POS");

        // Journey 1: Hardware Discrepancy Reporting & Decision
        var reportRes = await adminClient1.PostAsJsonAsync("/api/activation/v1/hardware-reviews", new ReportHardwareDiscrepancyRequest
        {
            ShopId = shopId,
            InstallationId = sourceInstallId,
            ReportedFingerprint = "HW-SOURCE-NEW-FP",
            StoredFingerprint = "HW-SOURCE-OLD-FP",
            Reason = "Upgraded motherboard"
        });
        Assert.Equal(HttpStatusCode.OK, reportRes.StatusCode);
        var reviewJson = await ReadJsonAsync(reportRes);
        var reviewId = Guid.Parse(reviewJson["id"]!.GetValue<string>());

        var decideRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/hardware-reviews/{reviewId}/decide", new DecideHardwareReviewCommand
        {
            Approve = true,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, decideRes.StatusCode);

        using (var db = GetDbContext())
        {
            var r = await db.HardwareReviews.FindAsync(reviewId);
            Assert.Equal(HardwareReview.StatusApproved, r!.Status);
        }

        // Journey 2: Primary Transfer Dual-Control Governance
        // Register Target Installation
        using var rsaTarget = RSA.Create(2048);
        var targetInstallId = await AddPersistedInstallationFixtureAsync(
            shopId, licenseId, rsaTarget.ExportSubjectPublicKeyInfoPem(), "HW-TARGET-NEW-PRIMARY", "NEW-POS");

        // Admin 1 initiates primary transfer
        var initTransferRes = await adminClient1.PostAsJsonAsync("/api/activation/v1/primary-transfers", new InitiatePrimaryTransferCommand
        {
            ShopId = shopId,
            SourceInstallationId = sourceInstallId,
            TargetInstallationId = targetInstallId,
            TransferReason = "Hardware replacement transfer",
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, initTransferRes.StatusCode);
        var transferJson = await ReadJsonAsync(initTransferRes);
        var transferId = Guid.Parse(transferJson["id"]!.GetValue<string>());

        // Invariant: Self-approval is strictly prohibited (Admin 1 cannot approve own transfer)
        var selfApproveRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/approve", new ApprovePrimaryTransferCommand
        {
            SensitiveApprovalId = Guid.NewGuid(),
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.BadRequest, selfApproveRes.StatusCode);
        var selfError = await ReadJsonAsync(selfApproveRes);
        Assert.Equal("SELF_APPROVAL_PROHIBITED", selfError["code"]?.GetValue<string>());

        // Invariant: Nonexistent approval fails closed
        var nonexistentApproveRes = await adminClient2.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/approve", new ApprovePrimaryTransferCommand
        {
            SensitiveApprovalId = Guid.NewGuid(),
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.BadRequest, nonexistentApproveRes.StatusCode);
        var nonexistentError = await ReadJsonAsync(nonexistentApproveRes);
        Assert.Equal("APPROVAL_NOT_FOUND", nonexistentError["code"]?.GetValue<string>());

        // Request sensitive approval for primary transfer (Admin 1 requests, Admin 2 approves)
        var reqTransferApprovalRes = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
        {
            OperationType = "PRIMARY_TRANSFER",
            TargetEntityId = transferId,
            TargetPayloadSha256 = new string('t', 64),
            ActionScope = "primary.transfer",
            StepUpSessionId = Guid.NewGuid(),
            ValidityMinutes = 60
        });
        Assert.Equal(HttpStatusCode.OK, reqTransferApprovalRes.StatusCode);
        var transferApprovalJson = await ReadJsonAsync(reqTransferApprovalRes);
        var transferApprovalId = Guid.Parse(transferApprovalJson["id"]!.GetValue<string>());

        var decideTransferApprovalRes = await adminClient2.PostAsJsonAsync($"/api/lifecycle/v1/sensitive-approvals/{transferApprovalId}/decide", new DecideSensitiveApprovalCommand
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = new string('t', 64),
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, decideTransferApprovalRes.StatusCode);

        // Independent Admin 2 approves transfer with valid sensitive approval
        var admin2ApproveRes = await adminClient2.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/approve", new ApprovePrimaryTransferCommand
        {
            SensitiveApprovalId = transferApprovalId,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, admin2ApproveRes.StatusCode);

        // Complete Transfer
        var completeRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/complete", new { });
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);

        // Verify Invariants: Source is REPLACED, Target is in DB (Single primary capacity enforced)
        using (var db = GetDbContext())
        {
            var src = await db.Installations.FindAsync(sourceInstallId);
            var tgt = await db.Installations.FindAsync(targetInstallId);
            Assert.Equal(Installation.StatusReplaced, src!.Status);
            Assert.NotNull(tgt);
        }
    }

    [Fact]
    public async Task CONE_T13_SensitiveLifecycle_DualControl_AndAllowlistTransitions()
    {
        var (admin1SessionId, admin1Id, adminClient1) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });
        var (_, admin2Id, adminClient2) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

        // Setup base shop and license
        var custId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Lifecycle Corp",
            ContactEmail = $"life_{Guid.NewGuid():N}@test.com",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var shopId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = custId,
            ShopCode = $"SHOP-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Lifecycle Shop",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var itemId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
        {
            Code = $"CAT-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Lifecycle Base Plan",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermLifetime,
            UnitPrice = 500m,
            Currency = "USD"
        })))["id"]!.GetValue<string>());

        var snapId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var orderId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = custId,
            SnapshotId = snapId,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        using (var db = GetDbContext())
        {
            var dbOrder = await db.Orders.FindAsync(orderId);
            dbOrder!.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var licenseId = Guid.Parse((await ReadJsonAsync(await adminClient1.PostAsJsonAsync("/api/commercial/v1/licenses/issue", new IssueLicenseCommand
        {
            ApprovedOrderId = orderId,
            ValidFromUtc = DateTimeOffset.UtcNow,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        // Journey 1: Sensitive Approval Dual-Control
        var targetPayloadSha256 = new string('e', 64);
        var reqApprovalRes = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
        {
            OperationType = "LICENSE_REVOKE",
            TargetEntityId = licenseId,
            TargetPayloadSha256 = targetPayloadSha256,
            ActionScope = "license.revoke",
            StepUpSessionId = Guid.NewGuid(),
            ValidityMinutes = 60
        });
        Assert.Equal(HttpStatusCode.OK, reqApprovalRes.StatusCode);
        var approvalJson = await ReadJsonAsync(reqApprovalRes);
        var approvalId = Guid.Parse(approvalJson["id"]!.GetValue<string>());

        // Self-approval rejection
        var selfApproveRes = await adminClient1.PostAsJsonAsync($"/api/lifecycle/v1/sensitive-approvals/{approvalId}/decide", new DecideSensitiveApprovalCommand
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = targetPayloadSha256,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.BadRequest, selfApproveRes.StatusCode);
        var selfErr = await ReadJsonAsync(selfApproveRes);
        Assert.Equal("SELF_APPROVAL_PROHIBITED", selfErr["code"]?.GetValue<string>());

        // Independent approval by Admin 2
        var indApproveRes = await adminClient2.PostAsJsonAsync($"/api/lifecycle/v1/sensitive-approvals/{approvalId}/decide", new DecideSensitiveApprovalCommand
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = targetPayloadSha256,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, indApproveRes.StatusCode);

        async Task<Guid> RequestAndApproveAsync(string opType)
        {
            var reqRes = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
            {
                OperationType = opType,
                TargetEntityId = licenseId,
                TargetPayloadSha256 = new string('0', 64),
                ActionScope = opType.ToLowerInvariant(),
                StepUpSessionId = Guid.NewGuid(),
                ValidityMinutes = 60
            });
            var json = await ReadJsonAsync(reqRes);
            var id = Guid.Parse(json["id"]!.GetValue<string>());
            var decRes = await adminClient2.PostAsJsonAsync($"/api/lifecycle/v1/sensitive-approvals/{id}/decide", new DecideSensitiveApprovalCommand
            {
                Decision = "APPROVE",
                TargetPayloadSha256 = new string('0', 64),
                CommandMeta = CreateMeta()
            });
            Assert.Equal(HttpStatusCode.OK, decRes.StatusCode);
            return id;
        }

        // Journey 2: Strict Allowlist State Machine Transitions
        // Negative: Unapproved Pause fails closed (APPROVAL_REQUIRED)
        var unapprovedPauseRes = await adminClient1.PostAsJsonAsync($"/api/lifecycle/v1/licenses/{licenseId}/pause", new LicenseLifecycleCommand { CommandMeta = CreateMeta() });
        Assert.Equal(HttpStatusCode.Forbidden, unapprovedPauseRes.StatusCode);
        var unapprovedErr = await ReadJsonAsync(unapprovedPauseRes);
        Assert.Equal("APPROVAL_REQUIRED", unapprovedErr["code"]?.GetValue<string>());

        // 1. Pause
        var pauseApprId = await RequestAndApproveAsync("LICENSE_PAUSE");
        var pauseRes = await adminClient1.PostAsJsonAsync($"/api/lifecycle/v1/licenses/{licenseId}/pause", new LicenseLifecycleCommand
        {
            SensitiveApprovalId = pauseApprId,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, pauseRes.StatusCode);
        using (var db = GetDbContext())
        {
            var l = await db.CommercialLicenses.FindAsync(licenseId);
            Assert.Equal(CommercialLicense.StatusPaused, l!.Status);
        }

        // 2. Resume
        var resumeApprId = await RequestAndApproveAsync("LICENSE_RESUME");
        var resumeRes = await adminClient1.PostAsJsonAsync($"/api/lifecycle/v1/licenses/{licenseId}/resume", new LicenseLifecycleCommand
        {
            SensitiveApprovalId = resumeApprId,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, resumeRes.StatusCode);
        using (var db = GetDbContext())
        {
            var l = await db.CommercialLicenses.FindAsync(licenseId);
            Assert.Equal(CommercialLicense.StatusActive, l!.Status);
        }

        // 3. Suspend
        var suspendApprId = await RequestAndApproveAsync("LICENSE_SUSPEND");
        var suspendRes = await adminClient1.PostAsJsonAsync($"/api/lifecycle/v1/licenses/{licenseId}/suspend", new LicenseLifecycleCommand
        {
            SensitiveApprovalId = suspendApprId,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, suspendRes.StatusCode);
        using (var db = GetDbContext())
        {
            var l = await db.CommercialLicenses.FindAsync(licenseId);
            Assert.Equal(CommercialLicense.StatusSuspended, l!.Status);
        }

        // 4. Reinstate
        var reinstateApprId = await RequestAndApproveAsync("LICENSE_REINSTATE");
        var reinstateRes = await adminClient1.PostAsJsonAsync($"/api/lifecycle/v1/licenses/{licenseId}/reinstate", new LicenseLifecycleCommand
        {
            SensitiveApprovalId = reinstateApprId,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, reinstateRes.StatusCode);
        using (var db = GetDbContext())
        {
            var l = await db.CommercialLicenses.FindAsync(licenseId);
            Assert.Equal(CommercialLicense.StatusActive, l!.Status);
        }

        // Canonical Central route: payload-bound independent approval, persisted policy scope,
        // idempotent outcome, stale-version rejection, and an actual pause/resume transition.
        var capabilityPolicyId = Guid.NewGuid();
        using (var db = GetDbContext())
        {
            db.ShopEnforcementPolicies.Add(ShopEnforcementPolicy.Create(
                shopId, licenseId, 1, ShopEnforcementPolicy.FullStop, null, Guid.NewGuid(),
                86400, 86400, 300, capabilityPolicyId, "{}", "synthetic-development-signature",
                "synthetic-development-key", admin1Id, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var canonicalEffectiveAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var canonicalPayload = new { licenseId, effectiveAtUtc = canonicalEffectiveAt, capabilityPolicyId };
        var canonicalPayloadHash = _serializer.SerializeAndHash(canonicalPayload).Sha256Hex;
        var canonicalApprovalResponse = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
        {
            OperationType = "LICENSE_PAUSE",
            TargetEntityId = licenseId,
            TargetPayloadSha256 = canonicalPayloadHash,
            ActionScope = "license.pause",
            StepUpSessionId = admin1SessionId,
            ValidityMinutes = 60
        });
        Assert.Equal(HttpStatusCode.OK, canonicalApprovalResponse.StatusCode);
        var canonicalApprovalId = Guid.Parse((await ReadJsonAsync(canonicalApprovalResponse))["id"]!.GetValue<string>());
        var canonicalApprovalDecision = await adminClient2.PostAsJsonAsync($"/api/lifecycle/v1/sensitive-approvals/{canonicalApprovalId}/decide", new DecideSensitiveApprovalCommand
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = canonicalPayloadHash,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, canonicalApprovalDecision.StatusCode);

        int expectedLicenseVersion;
        using (var db = GetDbContext())
        {
            expectedLicenseVersion = (await db.CommercialLicenses.FindAsync(licenseId))!.RowVersion;
        }
        var canonicalOperationId = Guid.NewGuid();
        var canonicalRequest = new
        {
            meta = new
            {
                clientOperationId = canonicalOperationId,
                operationType = "license.pause",
                fingerprintVersion = 1,
                payloadSha256 = canonicalPayloadHash,
                expectedVersion = expectedLicenseVersion,
                policyVersion = 1,
                reason = "Synthetic canonical lifecycle integration fixture",
                sensitiveApprovalId = canonicalApprovalId
            },
            payload = canonicalPayload
        };
        var tamperedEffectivePayload = new { licenseId, effectiveAtUtc = canonicalEffectiveAt.AddSeconds(-1), capabilityPolicyId };
        var tamperedRequest = new
        {
            meta = new
            {
                clientOperationId = canonicalOperationId,
                operationType = "license.pause",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(tamperedEffectivePayload).Sha256Hex,
                expectedVersion = expectedLicenseVersion,
                policyVersion = 1,
                reason = "Approved payload digest must bind the actual lifecycle command",
                sensitiveApprovalId = canonicalApprovalId
            },
            payload = tamperedEffectivePayload
        };
        var tamperedResponse = await adminClient1.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/pause", tamperedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, tamperedResponse.StatusCode);
        Assert.Equal("APPROVAL_PAYLOAD_MISMATCH", (await ReadJsonAsync(tamperedResponse))["code"]?.GetValue<string>());
        using (var db = GetDbContext())
        {
            Assert.Equal(CommercialLicense.StatusActive, (await db.CommercialLicenses.FindAsync(licenseId))!.Status);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, (await db.SensitiveApprovalRequests.FindAsync(canonicalApprovalId))!.Status);
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == canonicalOperationId));
        }

        var canonicalResponse = await adminClient1.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/pause", canonicalRequest);
        Assert.Equal(HttpStatusCode.OK, canonicalResponse.StatusCode);
        var canonicalOutcome = await ReadJsonAsync(canonicalResponse);
        Assert.Equal("SUCCEEDED", canonicalOutcome["state"]!.GetValue<string>());
        var canonicalReplayResponse = await adminClient1.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/pause", canonicalRequest);
        Assert.Equal(HttpStatusCode.OK, canonicalReplayResponse.StatusCode);
        var canonicalReplay = await ReadJsonAsync(canonicalReplayResponse);
        Assert.Equal(canonicalOutcome["outcomeId"]!.GetValue<string>(), canonicalReplay["outcomeId"]!.GetValue<string>());
        using (var db = GetDbContext())
        {
            Assert.Equal(CommercialLicense.StatusPaused, (await db.CommercialLicenses.FindAsync(licenseId))!.Status);
            Assert.Equal(SensitiveApprovalRequest.StatusConsumed, (await db.SensitiveApprovalRequests.FindAsync(canonicalApprovalId))!.Status);
            Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.CorrelationId == canonicalOperationId.ToString()));
            Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.Id == canonicalOperationId));
        }
        var staleCanonicalRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "license.pause",
                fingerprintVersion = 1,
                payloadSha256 = canonicalPayloadHash,
                expectedVersion = expectedLicenseVersion,
                policyVersion = 1,
                reason = "Stale lifecycle revision must not apply twice"
            },
            payload = canonicalPayload
        };
        var staleCanonicalResponse = await adminClient1.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/pause", staleCanonicalRequest);
        Assert.Equal(HttpStatusCode.Conflict, staleCanonicalResponse.StatusCode);

        var resumeEffectiveAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var resumePayload = new { licenseId, effectiveAtUtc = resumeEffectiveAt, capabilityPolicyId };
        var resumePayloadHash = _serializer.SerializeAndHash(resumePayload).Sha256Hex;
        var resumeApprovalResponse = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
        {
            OperationType = "LICENSE_RESUME",
            TargetEntityId = licenseId,
            TargetPayloadSha256 = resumePayloadHash,
            ActionScope = "license.resume",
            StepUpSessionId = admin1SessionId,
            ValidityMinutes = 60
        });
        var resumeApprovalId = Guid.Parse((await ReadJsonAsync(resumeApprovalResponse))["id"]!.GetValue<string>());
        var resumeDecisionResponse = await adminClient2.PostAsJsonAsync($"/api/lifecycle/v1/sensitive-approvals/{resumeApprovalId}/decide", new DecideSensitiveApprovalCommand
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = resumePayloadHash,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, resumeDecisionResponse.StatusCode);
        int resumedLicenseVersion;
        using (var db = GetDbContext())
        {
            resumedLicenseVersion = (await db.CommercialLicenses.FindAsync(licenseId))!.RowVersion;
        }
        var resumeRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "license.resume",
                fingerprintVersion = 1,
                payloadSha256 = resumePayloadHash,
                expectedVersion = resumedLicenseVersion,
                policyVersion = 1,
                reason = "Resume using current persisted policy and approval",
                sensitiveApprovalId = resumeApprovalId
            },
            payload = resumePayload
        };
        var resumeResponse = await adminClient1.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/resume", resumeRequest);
        Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);
        using (var db = GetDbContext())
        {
            Assert.Equal(CommercialLicense.StatusActive, (await db.CommercialLicenses.FindAsync(licenseId))!.Status);
        }

        async Task<HttpResponseMessage> ExecuteApprovedCanonicalLifecycleAsync(string operationType, string action)
        {
            var effectiveAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            var payload = new { licenseId, effectiveAtUtc = effectiveAt, capabilityPolicyId };
            var digest = _serializer.SerializeAndHash(payload).Sha256Hex;
            var approvalType = operationType.Replace('.', '_').ToUpperInvariant();
            var approvalResponse = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
            {
                OperationType = approvalType,
                TargetEntityId = licenseId,
                TargetPayloadSha256 = digest,
                ActionScope = operationType,
                StepUpSessionId = admin1SessionId,
                ValidityMinutes = 60
            });
            Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
            var approvalId = Guid.Parse((await ReadJsonAsync(approvalResponse))["id"]!.GetValue<string>());
            var decisionResponse = await adminClient2.PostAsJsonAsync($"/api/lifecycle/v1/sensitive-approvals/{approvalId}/decide", new DecideSensitiveApprovalCommand
            {
                Decision = "APPROVE",
                TargetPayloadSha256 = digest,
                CommandMeta = CreateMeta()
            });
            Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
            int expectedVersion;
            using (var db = GetDbContext())
            {
                expectedVersion = (await db.CommercialLicenses.FindAsync(licenseId))!.RowVersion;
            }
            var request = new
            {
                meta = new
                {
                    clientOperationId = Guid.NewGuid(),
                    operationType,
                    fingerprintVersion = 1,
                    payloadSha256 = digest,
                    expectedVersion,
                    policyVersion = 1,
                    reason = "Synthetic canonical lifecycle transition fixture",
                    sensitiveApprovalId = approvalId
                },
                payload
            };
            return await adminClient1.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/{action}", request);
        }

        var canonicalSuspend = await ExecuteApprovedCanonicalLifecycleAsync("license.suspend", "suspend");
        Assert.Equal(HttpStatusCode.OK, canonicalSuspend.StatusCode);
        using (var db = GetDbContext())
        {
            Assert.Equal(CommercialLicense.StatusSuspended, (await db.CommercialLicenses.FindAsync(licenseId))!.Status);
        }
        var canonicalReinstate = await ExecuteApprovedCanonicalLifecycleAsync("license.reinstate", "reinstate");
        Assert.Equal(HttpStatusCode.OK, canonicalReinstate.StatusCode);
        using (var db = GetDbContext())
        {
            Assert.Equal(CommercialLicense.StatusActive, (await db.CommercialLicenses.FindAsync(licenseId))!.Status);
        }

        // 5. Revoke (Terminal State)
        var revokeRes = await ExecuteApprovedCanonicalLifecycleAsync("license.revoke", "revoke");
        Assert.Equal(HttpStatusCode.OK, revokeRes.StatusCode);
        using (var db = GetDbContext())
        {
            var l = await db.CommercialLicenses.FindAsync(licenseId);
            Assert.Equal(CommercialLicense.StatusRevoked, l!.Status);
        }

        // 6. Proves terminal state cannot transition back to active even with approval
        var resumeRevokedApprId = await RequestAndApproveAsync("LICENSE_RESUME");
        var invalidResumeRes = await adminClient1.PostAsJsonAsync($"/api/lifecycle/v1/licenses/{licenseId}/resume", new LicenseLifecycleCommand
        {
            SensitiveApprovalId = resumeRevokedApprId,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidResumeRes.StatusCode);
        var invalidErr = await ReadJsonAsync(invalidResumeRes);
        Assert.Equal("INVALID_STATE_TRANSITION", invalidErr["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task CONE_T14_SignedEnforcementPolicy_Guarantees24HourMinimumOfflineGrace()
    {
        var (_, policyAdminId, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

        // Setup shop and license
        var custId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Policy Corp",
            ContactEmail = $"policy_{Guid.NewGuid():N}@test.com",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var shopId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = custId,
            ShopCode = $"SHOP-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Policy Shop",
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var itemId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
        {
            Code = $"CAT-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Policy Base Plan",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermLifetime,
            UnitPrice = 500m,
            Currency = "USD"
        })))["id"]!.GetValue<string>());

        var snapId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        var orderId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = custId,
            SnapshotId = snapId,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        using (var db = GetDbContext())
        {
            var dbOrder = await db.Orders.FindAsync(orderId);
            dbOrder!.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var licenseId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", new IssueLicenseCommand
        {
            ApprovedOrderId = orderId,
            ValidFromUtc = DateTimeOffset.UtcNow,
            CommandMeta = CreateMeta()
        })))["id"]!.GetValue<string>());

        // Journey 1: Rejects offline grace period less than 24 hours (86400 seconds)
        var invalidGraceRes = await adminClient.PostAsJsonAsync("/api/lifecycle/v1/policies", new ConfigurePolicyCommand
        {
            Policy = new PolicyDto
            {
                ShopId = shopId,
                LicenseId = licenseId,
                PolicyVersion = 1,
                EnforcementMode = ShopEnforcementPolicy.FullStop,
                LeaseDurationSeconds = 86400,
                OfflineGraceSeconds = 3600, // Invalid: 1 hour is less than required 24 hours (86400s)
                RollbackToleranceSeconds = 300,
                CapabilityPolicyId = Guid.NewGuid()
            },
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidGraceRes.StatusCode);
        var err = await ReadJsonAsync(invalidGraceRes);
        Assert.Equal("MINIMUM_GRACE_REQUIRED", err["code"]?.GetValue<string>());

        // Journey 2: Accepts valid offline grace period >= 86400s and produces RS256 signature
        var validPolicyRes = await adminClient.PostAsJsonAsync("/api/lifecycle/v1/policies", new ConfigurePolicyCommand
        {
            Policy = new PolicyDto
            {
                ShopId = shopId,
                LicenseId = licenseId,
                PolicyVersion = 1,
                EnforcementMode = ShopEnforcementPolicy.FullStop,
                LeaseDurationSeconds = 86400,
                OfflineGraceSeconds = 86400, // Exactly 24 hours
                RollbackToleranceSeconds = 300,
                CapabilityPolicyId = Guid.NewGuid()
            },
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, validPolicyRes.StatusCode);
        var policyJson = await ReadJsonAsync(validPolicyRes);
        var policyId = Guid.Parse(policyJson["id"]!.GetValue<string>());
        var sigPolicy = (policyJson["rs256Signature"] ?? policyJson["rS256Signature"] ?? policyJson["RS256Signature"])?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(sigPolicy));
        Assert.Equal(86400, policyJson["offlineGraceSeconds"]?.GetValue<int>());
        Assert.Equal(ShopEnforcementPolicy.FullStop, policyJson["enforcementMode"]?.GetValue<string>());

        using (var db = GetDbContext())
        {
            var dbPolicy = await db.ShopEnforcementPolicies.FindAsync(policyId);
            Assert.NotNull(dbPolicy);
            Assert.NotEmpty(dbPolicy.RS256Signature);
        }

        var canonicalPolicyPayload = new
        {
            shopId,
            licenseId,
            policyVersion = 2,
            enforcementMode = ShopEnforcementPolicy.FullStop,
            afterExtensionPolicyId = Guid.NewGuid(),
            leaseDurationSeconds = 86400,
            offlineGraceSeconds = 86400,
            rollbackToleranceSeconds = 300,
            capabilityPolicyId = Guid.NewGuid(),
            approvedByAdminId = policyAdminId,
            approvedAtUtc = DateTimeOffset.UtcNow
        };
        var canonicalPolicyRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "license.policy",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(canonicalPolicyPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 2,
                reason = "Synthetic canonical enforcement policy integration fixture"
            },
            payload = canonicalPolicyPayload
        };
        var forgedApproverPayload = new
        {
            canonicalPolicyPayload.shopId,
            canonicalPolicyPayload.licenseId,
            canonicalPolicyPayload.policyVersion,
            canonicalPolicyPayload.enforcementMode,
            canonicalPolicyPayload.afterExtensionPolicyId,
            canonicalPolicyPayload.leaseDurationSeconds,
            canonicalPolicyPayload.offlineGraceSeconds,
            canonicalPolicyPayload.rollbackToleranceSeconds,
            canonicalPolicyPayload.capabilityPolicyId,
            approvedByAdminId = Guid.NewGuid(),
            canonicalPolicyPayload.approvedAtUtc
        };
        var forgedApproverRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "license.policy",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(forgedApproverPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 2,
                reason = "Forged policy approver must be denied"
            },
            payload = forgedApproverPayload
        };
        var forgedApproverResponse = await adminClient.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/policy", forgedApproverRequest);
        Assert.Equal(HttpStatusCode.BadRequest, forgedApproverResponse.StatusCode);
        var alteredPolicyRequest = new
        {
            canonicalPolicyRequest.meta,
            payload = new
            {
                canonicalPolicyPayload.shopId,
                canonicalPolicyPayload.licenseId,
                canonicalPolicyPayload.policyVersion,
                canonicalPolicyPayload.enforcementMode,
                canonicalPolicyPayload.afterExtensionPolicyId,
                canonicalPolicyPayload.leaseDurationSeconds,
                offlineGraceSeconds = 172800,
                canonicalPolicyPayload.rollbackToleranceSeconds,
                canonicalPolicyPayload.capabilityPolicyId,
                canonicalPolicyPayload.approvedByAdminId,
                canonicalPolicyPayload.approvedAtUtc
            }
        };
        var alteredPolicyResponse = await adminClient.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/policy", alteredPolicyRequest);
        Assert.Equal(HttpStatusCode.BadRequest, alteredPolicyResponse.StatusCode);
        using (var db = GetDbContext())
        {
            Assert.Equal(1, await db.ShopEnforcementPolicies.CountAsync(x => x.LicenseId == licenseId));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == forgedApproverRequest.meta.clientOperationId));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == canonicalPolicyRequest.meta.clientOperationId));
        }
        var canonicalPolicyResponse = await adminClient.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/policy", canonicalPolicyRequest);
        Assert.Equal(HttpStatusCode.OK, canonicalPolicyResponse.StatusCode);
        var canonicalPolicyOutcome = await ReadJsonAsync(canonicalPolicyResponse);
        Assert.Equal("SUCCEEDED", canonicalPolicyOutcome["state"]!.GetValue<string>());
        var canonicalPolicyReplayResponse = await adminClient.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/policy", canonicalPolicyRequest);
        Assert.Equal(HttpStatusCode.OK, canonicalPolicyReplayResponse.StatusCode);
        var canonicalPolicyReplay = await ReadJsonAsync(canonicalPolicyReplayResponse);
        Assert.Equal(canonicalPolicyOutcome["outcomeId"]!.GetValue<string>(), canonicalPolicyReplay["outcomeId"]!.GetValue<string>());

        var stalePolicyRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "license.policy",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(canonicalPolicyPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 2,
                reason = "Stale policy version must not change the signed policy"
            },
            payload = canonicalPolicyPayload
        };
        var stalePolicyResponse = await adminClient.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/policy", stalePolicyRequest);
        Assert.Equal(HttpStatusCode.Conflict, stalePolicyResponse.StatusCode);
        using (var db = GetDbContext())
        {
            var persisted = await db.ShopEnforcementPolicies.SingleAsync(x => x.LicenseId == licenseId && x.PolicyVersion == 2);
            Assert.NotEmpty(persisted.RS256Signature);
            Assert.Equal(policyAdminId, persisted.ApprovedByAdminId);
            Assert.Single(await db.ShopEnforcementPolicies.Where(x => x.LicenseId == licenseId && x.PolicyVersion == 2).ToListAsync());
            Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.CorrelationId == canonicalPolicyRequest.meta.clientOperationId.ToString()));
            Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.Id == canonicalPolicyRequest.meta.clientOperationId));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == stalePolicyRequest.meta.clientOperationId));
        }
    }

    [Fact]
    public async Task CONE_T22_LicenseView_RequiresExactCapabilityAndReturnsPiiFreePersistedPage()
    {
        var now = DateTimeOffset.UtcNow;
        var customer = Customer.Create("License View Fixture", $"license-view-{Guid.NewGuid():N}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"LV-{Guid.NewGuid():N}"[..12], "License View Shop", now);
        var plan = PlanCatalogItem.Create($"LV-{Guid.NewGuid():N}"[..12], "Synthetic Lifetime Base", PlanCatalogItem.TargetBaseLicense,
            PlanCatalogItem.TermLifetime, null, 0m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, plan, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, customer.Id, now);
        order.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), now);
        var license = CommercialLicense.Issue(order, now, null, now);
        using (var db = GetDbContext())
        {
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.PlanCatalogItems.Add(plan);
            db.PricingSnapshots.Add(snapshot);
            db.Orders.Add(order);
            db.CommercialLicenses.Add(license);
            await db.SaveChangesAsync();
        }

        var (_, _, deniedClient) = await CreateAdminClientAsync(Array.Empty<string>());
        var denied = await deniedClient.GetAsync($"/api/admin/v1/licenses/{license.Id:D}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var (_, _, authorizedClient) = await CreateAdminClientAsync(new[] { "license.view" });
        var response = await authorizedClient.GetAsync($"/api/admin/v1/licenses/{license.Id:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        var page = JsonNode.Parse(responseBody)!;
        Assert.Equal(1, page["authorizedCount"]!.GetValue<int>());
        Assert.Equal(license.RowVersion, page["asOfVersion"]!.GetValue<int>());
        Assert.Null(page["nextCursor"]);
        var item = Assert.IsType<JsonObject>(page["items"]![0]);
        Assert.Equal(license.Id.ToString("D"), item["id"]!.GetValue<string>());
        Assert.Equal(shop.Id.ToString("D"), item["shopId"]!.GetValue<string>());
        Assert.Equal(license.Status, item["status"]!.GetValue<string>());
        Assert.Equal(license.AuthorityRevision, item["authorityRevision"]!.GetValue<int>());
        Assert.Null(item["contactEmail"]);
        Assert.DoesNotContain("example.invalid", responseBody, StringComparison.Ordinal);

        var missing = await authorizedClient.GetAsync($"/api/admin/v1/licenses/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        Environment.SetEnvironmentVariable("CENTRAL_BOOTSTRAP_CHALLENGE_TTL_SECONDS", _previousBootstrapChallengeTtl);
        GC.SuppressFinalize(this);
    }
}

public class CanonicalFingerprintDelegatingHandler : DelegatingHandler
{
    private static readonly ICanonicalJsonSerializer Serializer = new CanonicalJsonSerializer();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var rawJson = await request.Content.ReadAsStringAsync(cancellationToken);
            if (rawJson.Contains("\"commandMeta\"", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var node = JsonNode.Parse(rawJson);
                    if (node is JsonObject obj && obj.TryGetPropertyValue("commandMeta", out var metaNode) && metaNode is JsonObject metaObj)
                    {
                        var currentHash = metaObj["payloadSha256"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(currentHash) || currentHash == new string('0', 64))
                        {
                            var canonical = Serializer.SerializeCanonical(rawJson);
                            var sha256 = Serializer.ComputeSha256Hex(canonical);
                            metaObj["payloadSha256"] = sha256;
                            request.Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
                        }
                    }
                }
                catch
                {
                    // Proceed unchanged if not json
                }
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
