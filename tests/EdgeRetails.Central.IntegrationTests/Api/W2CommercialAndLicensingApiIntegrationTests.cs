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
    private readonly Dictionary<Guid, string> _totpSecretsBySession = new();
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
                "hardware.review", "primary.transfer", "sensitive.approve", "security.manage", "auth.stepup"
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
        _totpSecretsBySession[sessionId] = totpSecret;

        return (sessionId, userId, client);
    }

    private async Task<Guid> CreateStepUpGrantAsync(Guid sessionId, HttpClient client, string actionScope, string digest)
    {
        // Isolate each action-bound grant from the five-per-minute throttle; the throttle's
        // own boundary is exercised by its dedicated auth/rate-limit tests.
        RateLimitingMiddleware.Reset();
        Assert.True(_totpSecretsBySession.TryGetValue(sessionId, out var secret), "The authenticated development fixture must own the session's TOTP secret.");
        var response = await client.PostAsJsonAsync("/api/admin/v1/auth/step-up", new StepUpRequestDto
        {
            SessionId = sessionId,
            ActionScope = actionScope,
            TargetPayloadSha256 = digest,
            Proof = _totpService.GenerateCode(secret!, DateTimeOffset.UtcNow),
            ChallengeId = Guid.NewGuid()
        });
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,
            $"Step-up request returned {(int)response.StatusCode}: {responseContent}");
        return Guid.Parse((JsonNode.Parse(responseContent)?["stepUpGrantId"]?.GetValue<string>())
            ?? throw new InvalidOperationException("Step-up response omitted its grant identifier."));
    }

    [Fact]
    public async Task CONE_T07_AdminOrderRoute_UsesCanonicalPayloadAndPersistsReplayableOrderIdentity()
    {
        var (_, userId, adminClient) = await CreateAdminClientAsync(new[] { "order.create" });
        Guid customerId;
        Guid shopId;
        Guid foreignShopId;
        Guid planId;
        Guid snapshotId;
        using (var db = GetDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            var customer = Customer.CreateFromContract("Canonical order test customer", null, now);
            var shop = Shop.RegisterFromContract(customer.Id, "Canonical order test shop", null, now);
            var foreignShop = Shop.RegisterFromContract(customer.Id, "Canonical order foreign shop fixture", null, now);
            var item = PlanCatalogItem.Create($"CAN-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
                "Canonical order compatibility fixture", PlanCatalogItem.TargetBaseLicense,
                PlanCatalogItem.TermLifetime, null, 125m, "USD");
            var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
            db.Customers.Add(customer);
            db.Shops.AddRange(shop, foreignShop);
            db.PlanCatalogItems.Add(item);
            db.PricingSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
            customerId = customer.Id;
            shopId = shop.Id;
            foreignShopId = foreignShop.Id;
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
            totalAmount = 0.01m,
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
                reason = "Client-supplied order totals and unknown authority fields are rejected"
            },
            payload = invalidPayload
        };
        var invalidResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        var overCapacityPayload = new
        {
            shopId,
            orderType = "NEW_LICENSE",
            planId,
            pricingSnapshotId = snapshotId,
            requestedPrimaryCapacity = 2,
            termKind = PlanCatalogItem.TermLifetime,
            purchaseTarget = new { kind = PlanCatalogItem.TargetBaseLicense }
        };
        var overCapacityOperationId = Guid.NewGuid();
        var overCapacityRequest = new
        {
            meta = new
            {
                clientOperationId = overCapacityOperationId,
                operationType = "order.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(overCapacityPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Central V1 commercial authority permits one operating Primary"
            },
            payload = overCapacityPayload
        };
        var overCapacityResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", overCapacityRequest);
        Assert.Equal(HttpStatusCode.BadRequest, overCapacityResponse.StatusCode);

        var foreignShopOperationId = Guid.NewGuid();
        var foreignShopPayload = new
        {
            shopId = foreignShopId,
            orderType = "NEW_LICENSE",
            planId,
            pricingSnapshotId = snapshotId,
            requestedPrimaryCapacity = 1,
            termKind = PlanCatalogItem.TermLifetime,
            purchaseTarget = new { kind = PlanCatalogItem.TargetBaseLicense }
        };
        var foreignShopRequest = new
        {
            meta = new
            {
                clientOperationId = foreignShopOperationId,
                operationType = "order.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(foreignShopPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "A pricing snapshot for another shop cannot establish purchase authority"
            },
            payload = foreignShopPayload
        };
        var foreignShopResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", foreignShopRequest);
        Assert.Equal(HttpStatusCode.BadRequest, foreignShopResponse.StatusCode);

        var alteredTermOperationId = Guid.NewGuid();
        var alteredTermPayload = new
        {
            shopId,
            orderType = "NEW_LICENSE",
            planId,
            pricingSnapshotId = snapshotId,
            requestedPrimaryCapacity = 1,
            termKind = PlanCatalogItem.TermFixed,
            purchaseTarget = new { kind = PlanCatalogItem.TargetBaseLicense }
        };
        var alteredTermRequest = new
        {
            meta = new
            {
                clientOperationId = alteredTermOperationId,
                operationType = "order.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(alteredTermPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Client term cannot override the server pricing snapshot"
            },
            payload = alteredTermPayload
        };
        var alteredTermResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", alteredTermRequest);
        Assert.Equal(HttpStatusCode.BadRequest, alteredTermResponse.StatusCode);

        var alteredTargetOperationId = Guid.NewGuid();
        var alteredTargetPayload = new
        {
            shopId,
            orderType = "NEW_LICENSE",
            planId,
            pricingSnapshotId = snapshotId,
            requestedPrimaryCapacity = 1,
            termKind = PlanCatalogItem.TermLifetime,
            purchaseTarget = new { kind = PlanCatalogItem.TargetSupport }
        };
        var alteredTargetRequest = new
        {
            meta = new
            {
                clientOperationId = alteredTargetOperationId,
                operationType = "order.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(alteredTargetPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Client purchase target cannot override the server pricing snapshot"
            },
            payload = alteredTargetPayload
        };
        var alteredTargetResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/orders", alteredTargetRequest);
        Assert.Equal(HttpStatusCode.BadRequest, alteredTargetResponse.StatusCode);

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
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == foreignShopOperationId));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == alteredTermOperationId));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == alteredTargetOperationId));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == overCapacityOperationId));
            Assert.False(await db.Orders.AnyAsync(x => x.ShopId == foreignShopId));
            Assert.Equal(1, await db.Orders.CountAsync(x => x.SnapshotId == snapshotId));
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
    public async Task CONE_T08_ConcurrentCompetingPaymentReviews_CommitOneOutcomeAndOneEffect()
    {
        var (_, _, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

        var itemResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
        {
            Code = $"RACE-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Synthetic review race fixture",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermFixed,
            BillingPeriodId = "ANNUAL",
            UnitPrice = 125m,
            Currency = "USD"
        });
        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        var itemId = Guid.Parse((await ReadJsonAsync(itemResponse))["id"]!.GetValue<string>());

        var customerResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Synthetic payment race customer",
            ContactEmail = $"review-race-{Guid.NewGuid():N}@example.invalid",
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, customerResponse.StatusCode);
        var customerId = Guid.Parse((await ReadJsonAsync(customerResponse))["id"]!.GetValue<string>());

        var shopResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = customerId,
            ShopCode = $"RACE-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            ShopName = "Synthetic payment race shop",
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, shopResponse.StatusCode);
        var shopId = Guid.Parse((await ReadJsonAsync(shopResponse))["id"]!.GetValue<string>());

        var snapshotResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
        {
            ShopId = shopId,
            CatalogItemId = itemId,
            AuthorityVersion = 1,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
        var snapshotId = Guid.Parse((await ReadJsonAsync(snapshotResponse))["id"]!.GetValue<string>());

        var orderResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
        {
            CustomerId = customerId,
            SnapshotId = snapshotId,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, orderResponse.StatusCode);
        var orderId = Guid.Parse((await ReadJsonAsync(orderResponse))["id"]!.GetValue<string>());

        var bytes = Encoding.UTF8.GetBytes("synthetic review race evidence");
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var uploadResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/uploads/sessions", new InitUploadSessionCommand
        {
            Purpose = UploadSession.PurposePaymentProof,
            ShopId = shopId,
            TargetEntityId = orderId,
            ExpectedBytes = bytes.Length,
            ExpectedSha256 = digest,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        var uploadSessionId = Guid.Parse((await ReadJsonAsync(uploadResponse))["id"]!.GetValue<string>());
        using (var chunkRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/commercial/v1/uploads/sessions/{uploadSessionId}/chunks")
        {
            Content = new ByteArrayContent(bytes)
        })
        {
            var chunkResponse = await adminClient.SendAsync(chunkRequest);
            Assert.Equal(HttpStatusCode.OK, chunkResponse.StatusCode);
        }

        var finalizeResponse = await adminClient.PostAsJsonAsync($"/api/commercial/v1/uploads/sessions/{uploadSessionId}/finalize?orderId={orderId}", new FinalizeUploadSessionCommand
        {
            ComputedSha256 = digest,
            MimeType = "application/pdf",
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, finalizeResponse.StatusCode);
        var evidenceId = Guid.Parse((await ReadJsonAsync(finalizeResponse))["id"]!.GetValue<string>());

        var approveOperationId = Guid.NewGuid();
        var rejectOperationId = Guid.NewGuid();
        var approvePayload = new { orderId, proofId = evidenceId, decision = "APPROVE", reasonCode = "RACE_APPROVE", internalNote = "Synthetic competing approval fixture" };
        var rejectPayload = new { orderId, proofId = evidenceId, decision = "REJECT", reasonCode = "RACE_REJECT", internalNote = "Synthetic competing rejection fixture" };
        object Request(Guid operationId, object payload) => new
        {
            meta = new
            {
                clientOperationId = operationId,
                operationType = "payment.review",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Competing human payment review integration fixture"
            },
            payload
        };

        var responseTasks = new[]
        {
            adminClient.PostAsJsonAsync($"/api/admin/v1/payments/{evidenceId}/review", Request(approveOperationId, approvePayload)),
            adminClient.PostAsJsonAsync($"/api/admin/v1/payments/{evidenceId}/review", Request(rejectOperationId, rejectPayload))
        };
        var responses = await Task.WhenAll(responseTasks);
        var winnerIndex = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.True(winnerIndex >= 0, "One concurrent review must commit.");
        var loserIndex = 1 - winnerIndex;
        var losingResponseBody = await responses[loserIndex].Content.ReadAsStringAsync();
        Assert.True(responses[loserIndex].StatusCode == HttpStatusCode.Conflict,
            $"The competing payment review must report a conflict; got {(int)responses[loserIndex].StatusCode}: {losingResponseBody}");
        var winnerOperationId = winnerIndex == 0 ? approveOperationId : rejectOperationId;
        var winnerDecision = winnerIndex == 0 ? "APPROVE" : "REJECT";
        var winnerOutcome = await ReadJsonAsync(responses[winnerIndex]);
        Assert.Equal("SUCCEEDED", winnerOutcome["state"]!.GetValue<string>());

        using var db = GetDbContext();
        var evidence = await db.PaymentEvidences.SingleAsync(x => x.Id == evidenceId);
        var order = await db.Orders.SingleAsync(x => x.Id == orderId);
        Assert.Equal(winnerDecision == "APPROVE" ? PaymentEvidence.ReviewApproved : PaymentEvidence.ReviewRejected, evidence.ReviewStatus);
        Assert.Equal(winnerDecision == "APPROVE" ? Order.StatusPaidApproved : Order.StatusRejected, order.Status);
        Assert.Equal(1, await db.PaymentReviewRecords.CountAsync(x => x.EvidenceId == evidenceId));
        Assert.Equal(1, await db.CentralOperationRecords.CountAsync(x => x.ClientOperationId == winnerOperationId));
        Assert.Equal(0, await db.CentralOperationRecords.CountAsync(x => x.ClientOperationId == (winnerIndex == 0 ? rejectOperationId : approveOperationId)));
        Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.Id == winnerOperationId));
        Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.CorrelationId == winnerOperationId.ToString()));
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
            CommandMeta = CreateMeta("license.issue")
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
    public async Task CONE_T09_PaidSupportAddonRenewalAndExpiry_LeavesLifetimeBaseUnchanged()
    {
        var (_, _, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });
        var customerId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Synthetic Support Addon Customer",
            ContactEmail = $"support_addon_{Guid.NewGuid():N}@test.invalid",
            CommandMeta = CreateMeta()
        }))) ["id"]!.GetValue<string>());
        var shopId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = customerId,
            ShopCode = $"SA-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Synthetic Support Addon Shop",
            CommandMeta = CreateMeta()
        }))) ["id"]!.GetValue<string>());

        async Task<Guid> CreatePaidOrderAsync(CreateCatalogItemRequest itemRequest, Guid? scopedCustomerId = null, Guid? scopedShopId = null)
        {
            var itemResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", itemRequest);
            Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
            var itemId = Guid.Parse((await ReadJsonAsync(itemResponse))["id"]!.GetValue<string>());
            var snapshotResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
            {
                ShopId = scopedShopId ?? shopId,
                CatalogItemId = itemId,
                AuthorityVersion = 1,
                CommandMeta = CreateMeta()
            });
            Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
            var snapshotId = Guid.Parse((await ReadJsonAsync(snapshotResponse))["id"]!.GetValue<string>());
            var orderResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
            {
                CustomerId = scopedCustomerId ?? customerId,
                SnapshotId = snapshotId,
                CommandMeta = CreateMeta()
            });
            Assert.Equal(HttpStatusCode.OK, orderResponse.StatusCode);
            var orderId = Guid.Parse((await ReadJsonAsync(orderResponse))["id"]!.GetValue<string>());
            using var db = GetDbContext();
            var order = await db.Orders.FindAsync(orderId);
            Assert.NotNull(order);
            order.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow); // Synthetic fixture for the separate manual payment-review step.
            await db.SaveChangesAsync();
            return orderId;
        }

        var foreignCustomerId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Synthetic Foreign Addon Customer",
            ContactEmail = $"foreign_addon_{Guid.NewGuid():N}@test.invalid",
            CommandMeta = CreateMeta()
        }))) ["id"]!.GetValue<string>());
        var foreignShopId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = foreignCustomerId,
            ShopCode = $"SF-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Synthetic Foreign Addon Shop",
            CommandMeta = CreateMeta()
        }))) ["id"]!.GetValue<string>());

        var baseOrderId = await CreatePaidOrderAsync(new CreateCatalogItemRequest
        {
            Code = $"LB-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Synthetic Lifetime Base",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermLifetime,
            UnitPrice = 500m,
            Currency = "USD"
        });
        var issuedAt = DateTimeOffset.UtcNow;
        var licenseResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", new IssueLicenseCommand
        {
            ApprovedOrderId = baseOrderId,
            ValidFromUtc = issuedAt,
            CommandMeta = CreateMeta("license.issue")
        });
        Assert.Equal(HttpStatusCode.OK, licenseResponse.StatusCode);
        var licenseJson = await ReadJsonAsync(licenseResponse);
        var licenseId = Guid.Parse(licenseJson["id"]!.GetValue<string>());
        var initialAuthorityRevision = licenseJson["authorityRevision"]!.GetValue<int>();
        Assert.Equal(CommercialLicense.StatusActive, licenseJson["status"]?.GetValue<string>());
        Assert.Null(licenseJson["validUntilUtc"]);

        var addonOrderId = await CreatePaidOrderAsync(new CreateCatalogItemRequest
        {
            Code = $"SUP-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Synthetic Fixed Term Support",
            PurchaseTarget = PlanCatalogItem.TargetSupport,
            TermKind = PlanCatalogItem.TermFixed,
            BillingPeriodId = "ANNUAL",
            UnitPrice = 100m,
            Currency = "USD"
        });
        var expiredAddonCommand = new RenewAddonCommand
        {
            ApprovedOrderId = addonOrderId,
            NewValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            CommandMeta = CreateMeta("addon.renew")
        };
        var expiredAddonResponse = await adminClient.PostAsJsonAsync(
            $"/api/commercial/v1/licenses/{licenseId:D}/addons/renew", expiredAddonCommand);
        Assert.Equal(HttpStatusCode.BadRequest, expiredAddonResponse.StatusCode);
        Assert.Equal("INVALID_RENEWAL_DATE", (await ReadJsonAsync(expiredAddonResponse))["code"]?.GetValue<string>());

        var foreignAddonOrderId = await CreatePaidOrderAsync(new CreateCatalogItemRequest
        {
            Code = $"FSP-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Synthetic Foreign Shop Support",
            PurchaseTarget = PlanCatalogItem.TargetSupport,
            TermKind = PlanCatalogItem.TermFixed,
            BillingPeriodId = "ANNUAL",
            UnitPrice = 100m,
            Currency = "USD"
        }, foreignCustomerId, foreignShopId);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(15);
        var renewalCommand = new RenewAddonCommand
        {
            ApprovedOrderId = addonOrderId,
            NewValidUntilUtc = expiresAt,
            CommandMeta = CreateMeta("addon.renew")
        };
        var initialRenewalResponses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            adminClient.PostAsJsonAsync($"/api/commercial/v1/licenses/{licenseId:D}/addons/renew", renewalCommand)));
        foreach (var initialRenewalResponse in initialRenewalResponses)
        {
            Assert.Equal(HttpStatusCode.OK, initialRenewalResponse.StatusCode);
        }
        var entitlementJson = await ReadJsonAsync(initialRenewalResponses[0]);
        var entitlementId = Guid.Parse(entitlementJson["id"]!.GetValue<string>());
        var duplicateInitialJson = await ReadJsonAsync(initialRenewalResponses[1]);
        Assert.Equal(entitlementId, Guid.Parse(duplicateInitialJson["id"]!.GetValue<string>()));
        Assert.Equal(CommercialEntitlement.KindSupport, entitlementJson["kind"]?.GetValue<string>());
        Assert.Equal(CommercialEntitlement.StatusActive, entitlementJson["status"]?.GetValue<string>());
        var persistedExpiry = entitlementJson["validUntilUtc"]!.GetValue<DateTimeOffset>();
        Assert.InRange(Math.Abs((persistedExpiry - expiresAt).Ticks), 0L, 10L); // PostgreSQL stores timestamptz at microsecond precision.

        var secondBaseOrderId = await CreatePaidOrderAsync(new CreateCatalogItemRequest
        {
            Code = $"LB2-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Synthetic Second Lifetime Base",
            PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
            TermKind = PlanCatalogItem.TermLifetime,
            UnitPrice = 500m,
            Currency = "USD"
        });
        var secondLicenseResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", new IssueLicenseCommand
        {
            ApprovedOrderId = secondBaseOrderId,
            ValidFromUtc = DateTimeOffset.UtcNow,
            CommandMeta = CreateMeta("license.issue")
        });
        Assert.Equal(HttpStatusCode.OK, secondLicenseResponse.StatusCode);
        var secondLicenseId = Guid.Parse((await ReadJsonAsync(secondLicenseResponse))["id"]!.GetValue<string>());

        Guid mismatchedTargetOrderId;
        using (var db = GetDbContext())
        {
            var sourceOrder = await db.Orders.FindAsync(addonOrderId);
            var sourceSnapshot = await db.PricingSnapshots.FindAsync(sourceOrder!.SnapshotId);
            var mismatchedTargetOrder = Order.CreateFromSnapshot(sourceSnapshot!, customerId, DateTimeOffset.UtcNow,
                "SUPPORT_RENEWAL", 1, secondLicenseId, entitlementId);
            mismatchedTargetOrder.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            db.Orders.Add(mismatchedTargetOrder);
            await db.SaveChangesAsync();
            mismatchedTargetOrderId = mismatchedTargetOrder.Id;
        }
        var mismatchedTargetCommand = new RenewAddonCommand
        {
            ApprovedOrderId = mismatchedTargetOrderId,
            NewValidUntilUtc = expiresAt.AddDays(30),
            CommandMeta = CreateMeta("addon.renew")
        };
        mismatchedTargetCommand.CommandMeta.ExpectedVersion = entitlementJson["rowVersion"]!.GetValue<int>();
        var mismatchedTargetResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/v1/licenses/{licenseId:D}/addons/{entitlementId:D}/renew",
            mismatchedTargetCommand);
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedTargetResponse.StatusCode);
        Assert.Equal("TARGET_MISMATCH", (await ReadJsonAsync(mismatchedTargetResponse))["code"]?.GetValue<string>());

        var consumedOrderReuseCommand = new RenewAddonCommand
        {
            ApprovedOrderId = addonOrderId,
            NewValidUntilUtc = expiresAt.AddDays(30),
            CommandMeta = CreateMeta("addon.renew")
        };
        consumedOrderReuseCommand.CommandMeta.ExpectedVersion = entitlementJson["rowVersion"]!.GetValue<int>();
        var consumedOrderReuseResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/v1/licenses/{licenseId:D}/addons/{entitlementId:D}/renew",
            consumedOrderReuseCommand);
        Assert.Equal(HttpStatusCode.BadRequest, consumedOrderReuseResponse.StatusCode);
        Assert.Equal("ORDER_ALREADY_FULFILLED", (await ReadJsonAsync(consumedOrderReuseResponse))["code"]?.GetValue<string>());

        // The targeted route must bind both the license and entitlement and reject a stale version.
        var wrongTargetResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/v1/licenses/{licenseId:D}/addons/{Guid.NewGuid():D}/renew",
            renewalCommand);
        Assert.Equal(HttpStatusCode.NotFound, wrongTargetResponse.StatusCode);

        var foreignOrderCommand = new RenewAddonCommand
        {
            ApprovedOrderId = foreignAddonOrderId,
            NewValidUntilUtc = expiresAt.AddDays(30),
            CommandMeta = CreateMeta("addon.renew")
        };
        foreignOrderCommand.CommandMeta.ExpectedVersion = entitlementJson["rowVersion"]!.GetValue<int>();
        var foreignOrderResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/v1/licenses/{licenseId:D}/addons/{entitlementId:D}/renew",
            foreignOrderCommand);
        Assert.Equal(HttpStatusCode.BadRequest, foreignOrderResponse.StatusCode);
        Assert.Equal("TARGET_MISMATCH", (await ReadJsonAsync(foreignOrderResponse))["code"]?.GetValue<string>());

        var staleRenewalCommand = new RenewAddonCommand
        {
            ApprovedOrderId = addonOrderId,
            NewValidUntilUtc = expiresAt.AddDays(30),
            CommandMeta = CreateMeta("addon.renew")
        };
        staleRenewalCommand.CommandMeta.ExpectedVersion = entitlementJson["rowVersion"]!.GetValue<int>() - 1;
        var staleRenewalResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/v1/licenses/{licenseId:D}/addons/{entitlementId:D}/renew",
            staleRenewalCommand);
        Assert.Equal(HttpStatusCode.Conflict, staleRenewalResponse.StatusCode);

        var concurrentReplayResponses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            adminClient.PostAsJsonAsync(
                $"/api/admin/v1/licenses/{licenseId:D}/addons/{entitlementId:D}/renew",
                renewalCommand)));
        foreach (var replayResponse in concurrentReplayResponses)
        {
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            var replayJson = await ReadJsonAsync(replayResponse);
            Assert.Equal(entitlementId, Guid.Parse(replayJson["id"]!.GetValue<string>()));
        }

        using (var db = GetDbContext())
        {
            var license = await db.CommercialLicenses.FindAsync(licenseId);
            var entitlement = await db.CommercialEntitlements.FindAsync(entitlementId);
            Assert.NotNull(license);
            Assert.NotNull(entitlement);
            Assert.Equal(CommercialTerm.Lifetime, license.TermKind);
            Assert.Null(license.ValidUntilUtc);
            Assert.Equal(CommercialLicense.StatusActive, license.Status);
            Assert.Equal(initialAuthorityRevision, license.AuthorityRevision);
            Assert.Equal(entitlementJson["rowVersion"]!.GetValue<int>(), entitlement.RowVersion);
            Assert.True(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == renewalCommand.CommandMeta.ClientOperationId));
            Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.Id == renewalCommand.CommandMeta.ClientOperationId));
            Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.Action == "COMMERCIAL_ADDON_RENEWED" && x.ResourceId == entitlementId.ToString()));
        }

        var remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining + TimeSpan.FromMilliseconds(100));
        }
        using (var db = GetDbContext())
        {
            var entitlement = await db.CommercialEntitlements.FindAsync(entitlementId);
            var license = await db.CommercialLicenses.FindAsync(licenseId);
            Assert.NotNull(entitlement);
            Assert.NotNull(license);
            entitlement.CheckExpiration(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            Assert.Equal(CommercialEntitlement.StatusExpired, entitlement.Status);
            Assert.Equal(CommercialTerm.Lifetime, license.TermKind);
            Assert.Null(license.ValidUntilUtc);
            Assert.Equal(CommercialLicense.StatusActive, license.Status);
            Assert.Equal(initialAuthorityRevision, license.AuthorityRevision);
        }

        var delayedReplayResponse = await adminClient.PostAsJsonAsync(
            $"/api/commercial/v1/licenses/{licenseId:D}/addons/renew", renewalCommand);
        Assert.Equal(HttpStatusCode.OK, delayedReplayResponse.StatusCode);
        var delayedReplay = await ReadJsonAsync(delayedReplayResponse);
        Assert.Equal(entitlementId, Guid.Parse(delayedReplay["id"]!.GetValue<string>()));
        Assert.Equal(CommercialEntitlement.StatusExpired, delayedReplay["status"]?.GetValue<string>());
    }

    [Fact]
    public async Task CONE_T09_OrdersRemainBoundToSelectedImmutableCatalogPricingRevision()
    {
        var (_, _, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });
        var customerId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/customers", new CreateCustomerCommand
        {
            LegalName = "Synthetic Catalog Revision Customer",
            ContactEmail = $"catalog_revision_{Guid.NewGuid():N}@test.invalid",
            CommandMeta = CreateMeta()
        }))) ["id"]!.GetValue<string>());
        var shopId = Guid.Parse((await ReadJsonAsync(await adminClient.PostAsJsonAsync("/api/commercial/v1/shops", new RegisterShopCommand
        {
            CustomerId = customerId,
            ShopCode = $"CR-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            ShopName = "Synthetic Catalog Revision Shop",
            CommandMeta = CreateMeta()
        }))) ["id"]!.GetValue<string>());

        async Task<(Guid CatalogItemId, Guid SnapshotId)> CreateCatalogRevisionAsync(string revision, decimal unitPrice, int authorityVersion)
        {
            var itemResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/catalog-items", new CreateCatalogItemRequest
            {
                Code = $"CR-{revision}-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
                Name = $"Synthetic Lifetime Plan {revision}",
                PurchaseTarget = PlanCatalogItem.TargetBaseLicense,
                TermKind = PlanCatalogItem.TermLifetime,
                UnitPrice = unitPrice,
                Currency = "USD"
            });
            Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
            var catalogItemId = Guid.Parse((await ReadJsonAsync(itemResponse))["id"]!.GetValue<string>());
            var snapshotResponse = await adminClient.PostAsJsonAsync("/api/commercial/v1/pricing-snapshots", new CreatePriceSnapshotCommand
            {
                ShopId = shopId,
                CatalogItemId = catalogItemId,
                AuthorityVersion = authorityVersion,
                CommandMeta = CreateMeta()
            });
            Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
            var snapshotId = Guid.Parse((await ReadJsonAsync(snapshotResponse))["id"]!.GetValue<string>());
            return (catalogItemId, snapshotId);
        }

        var revisionOne = await CreateCatalogRevisionAsync("V1", 100m, 1);
        var revisionTwo = await CreateCatalogRevisionAsync("V2", 275m, 2);

        async Task<Guid> CreateOrderFromSnapshotAsync(Guid snapshotId)
        {
            var response = await adminClient.PostAsJsonAsync("/api/commercial/v1/orders", new CreateOrderCommand
            {
                CustomerId = customerId,
                SnapshotId = snapshotId,
                CommandMeta = CreateMeta()
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return Guid.Parse((await ReadJsonAsync(response))["id"]!.GetValue<string>());
        }

        // Deliberately create the V1 order after V2 exists: the server must honor the referenced immutable snapshot.
        var oldRevisionOrderId = await CreateOrderFromSnapshotAsync(revisionOne.SnapshotId);
        var newRevisionOrderId = await CreateOrderFromSnapshotAsync(revisionTwo.SnapshotId);
        using var db = GetDbContext();
        var snapshotOne = await db.PricingSnapshots.SingleAsync(x => x.Id == revisionOne.SnapshotId);
        var snapshotTwo = await db.PricingSnapshots.SingleAsync(x => x.Id == revisionTwo.SnapshotId);
        var oldRevisionOrder = await db.Orders.SingleAsync(x => x.Id == oldRevisionOrderId);
        var newRevisionOrder = await db.Orders.SingleAsync(x => x.Id == newRevisionOrderId);

        Assert.Equal(revisionOne.CatalogItemId, snapshotOne.CatalogItemId);
        Assert.Equal(1, snapshotOne.AuthorityVersion);
        Assert.Equal(100m, snapshotOne.UnitPrice);
        Assert.Equal(PlanCatalogItem.TermLifetime, snapshotOne.TermKind);
        Assert.Equal(revisionTwo.CatalogItemId, snapshotTwo.CatalogItemId);
        Assert.Equal(2, snapshotTwo.AuthorityVersion);
        Assert.Equal(275m, snapshotTwo.UnitPrice);
        Assert.NotEqual(snapshotOne.ServerGeneratedDigest, snapshotTwo.ServerGeneratedDigest);

        Assert.Equal(snapshotOne.Id, oldRevisionOrder.SnapshotId);
        Assert.Equal(snapshotOne.CatalogItemId, oldRevisionOrder.PlanId);
        Assert.Equal(100m, oldRevisionOrder.TotalAmount);
        Assert.Equal(Order.StatusPendingPayment, oldRevisionOrder.Status);
        Assert.Equal(snapshotTwo.Id, newRevisionOrder.SnapshotId);
        Assert.Equal(snapshotTwo.CatalogItemId, newRevisionOrder.PlanId);
        Assert.Equal(275m, newRevisionOrder.TotalAmount);
        Assert.Equal(Order.StatusPendingPayment, newRevisionOrder.Status);
        Assert.NotEqual(oldRevisionOrder.Id, newRevisionOrder.Id);
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

        var issueCommand = new IssueLicenseCommand
        {
            ApprovedOrderId = orderId,
            ValidFromUtc = DateTimeOffset.UtcNow,
            ValidUntilUtc = DateTimeOffset.UtcNow.AddDays(365),
            CommandMeta = CreateMeta("license.issue")
        };

        // Manual issuance must reject an order until its payment has been reviewed and approved.
        var unpaidIssueRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", issueCommand);
        Assert.Equal(HttpStatusCode.BadRequest, unpaidIssueRes.StatusCode);
        var unpaidIssueError = await ReadJsonAsync(unpaidIssueRes);
        Assert.Equal("ORDER_NOT_APPROVED", unpaidIssueError["code"]?.GetValue<string>());
        using (var db = GetDbContext())
        {
            Assert.False(await db.CommercialLicenses.AnyAsync(x => x.InitialOrderId == orderId));
            Assert.False(await db.CentralOutboxMessages.AnyAsync(x => x.Id == issueCommand.CommandMeta.ClientOperationId));
        }

        using (var db = GetDbContext())
        {
            var dbOrder = await db.Orders.FindAsync(orderId);
            dbOrder!.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // Issue Fixed Term License
        issueCommand.ValidFromUtc = DateTimeOffset.UtcNow;
        issueCommand.ValidUntilUtc = issueCommand.ValidFromUtc.AddDays(365);
        issueCommand.CommandMeta = CreateMeta("license.issue");
        var issueRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", issueCommand);
        Assert.Equal(HttpStatusCode.OK, issueRes.StatusCode);
        var licenseJson = await ReadJsonAsync(issueRes);
        var licenseId = Guid.Parse(licenseJson["id"]!.GetValue<string>());
        Assert.Equal(1, licenseJson["authorityRevision"]?.GetValue<int>());
        Assert.NotNull(licenseJson["validUntilUtc"]);

        // Exact retries replay the original result; a new operation cannot fulfill the order twice.
        var replayRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", issueCommand);
        Assert.Equal(HttpStatusCode.OK, replayRes.StatusCode);
        Assert.Equal(licenseId, Guid.Parse((await ReadJsonAsync(replayRes))["id"]!.GetValue<string>()));

        var duplicateIssueCommand = new IssueLicenseCommand
        {
            ApprovedOrderId = orderId,
            ValidFromUtc = issueCommand.ValidFromUtc,
            ValidUntilUtc = issueCommand.ValidUntilUtc,
            CommandMeta = CreateMeta("license.issue")
        };
        var duplicateIssueRes = await adminClient.PostAsJsonAsync("/api/commercial/v1/licenses/issue", duplicateIssueCommand);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateIssueRes.StatusCode);
        var duplicateIssueError = await ReadJsonAsync(duplicateIssueRes);
        Assert.Equal("ORDER_ALREADY_FULFILLED", duplicateIssueError["code"]?.GetValue<string>());
        using (var db = GetDbContext())
        {
            Assert.Equal(1, await db.CommercialLicenses.CountAsync(x => x.InitialOrderId == orderId));
            Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x =>
                x.EventType == "commercial.license_issued" && x.AggregateId == licenseId.ToString()));
            Assert.False(await db.CentralOutboxMessages.AnyAsync(x => x.Id == duplicateIssueCommand.CommandMeta.ClientOperationId));
        }

        // Renew Fixed Term License
        var renewedValidUntil = issueCommand.ValidUntilUtc!.Value.AddDays(365);

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
            CommandMeta = CreateMeta("license.issue")
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
        var (_, adminId, adminClient) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });

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
            CommandMeta = CreateMeta("license.issue")
        })))["id"]!.GetValue<string>());

        var unpaidTokenOperationId = Guid.NewGuid();
        using (var eligibilityDb = GetDbContext())
        {
            var tokenCountBefore = await eligibilityDb.ActivationTokens.CountAsync(x => x.LicenseId == licenseId);
            await eligibilityDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE central.orders SET \"Status\" = {Order.StatusPendingPayment} WHERE \"Id\" = {orderId}");
            try
            {
                using var unpaidTokenResponse = await adminClient.PostAsJsonAsync("/api/activation/v1/tokens", new IssueActivationTokenCommand
                {
                    ShopId = shopId,
                    LicenseId = licenseId,
                    FirstRedemptionTtlSeconds = 60,
                    CommandMeta = new CommandMetaDto
                    {
                        ClientOperationId = unpaidTokenOperationId,
                        OperationType = "ACTIVATION_TOKEN_ISSUE",
                        PayloadSha256 = new string('0', 64),
                        Reason = "Synthetic unpaid-order activation denial"
                    }
                });
                Assert.Equal(HttpStatusCode.BadRequest, unpaidTokenResponse.StatusCode);
                Assert.Equal("COMMERCIAL_ELIGIBILITY_REQUIRED", (await ReadJsonAsync(unpaidTokenResponse))["code"]?.GetValue<string>());
                Assert.Equal(tokenCountBefore, await eligibilityDb.ActivationTokens.CountAsync(x => x.LicenseId == licenseId));
                Assert.False(await eligibilityDb.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == unpaidTokenOperationId));
                Assert.False(await eligibilityDb.CentralOutboxMessages.AnyAsync(x => x.Id == unpaidTokenOperationId));
            }
            finally
            {
                await eligibilityDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE central.orders SET \"Status\" = {Order.StatusPaidApproved} WHERE \"Id\" = {orderId}");
            }
        }

        using var clientRsa = RSA.Create(2048);
        var pubPem = clientRsa.ExportSubjectPublicKeyInfoPem();

        var installId = Guid.NewGuid();

        using (var expiringIssueResponse = await adminClient.PostAsJsonAsync("/api/activation/v1/tokens", new IssueActivationTokenCommand
        {
            ShopId = shopId,
            LicenseId = licenseId,
            FirstRedemptionTtlSeconds = 1,
            CommandMeta = CreateMeta()
        }))
        {
            Assert.Equal(HttpStatusCode.OK, expiringIssueResponse.StatusCode);
            var expiringToken = await ReadJsonAsync(expiringIssueResponse);
            var expiringTokenId = Guid.Parse(expiringToken["tokenId"]!.GetValue<string>());
            var expiringTokenSecret = expiringToken["tokenSecret"]!.GetValue<string>();
            var expiringTokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expiringTokenSecret.Trim())));
            await Task.Delay(TimeSpan.FromSeconds(2));
            var expiredNonce = Guid.NewGuid();
            using var expiredChallengeResponse = await _client.PostAsJsonAsync("/api/licensing/v1/bootstrap/challenge", new BootstrapChallengeRequestDto
            {
                ShopId = shopId,
                InstallationId = Guid.NewGuid(),
                InstallationPublicKey = pubPem,
                ActivationTokenHash = expiringTokenHash,
                RequestNonce = expiredNonce
            });
            Assert.Equal(HttpStatusCode.BadRequest, expiredChallengeResponse.StatusCode);
            Assert.Equal("BOOTSTRAP_NOT_ELIGIBLE", (await ReadJsonAsync(expiredChallengeResponse))["code"]?.GetValue<string>());
            using var expiredDb = GetDbContext();
            Assert.True((await expiredDb.ActivationTokens.FindAsync(expiringTokenId))!.FirstRedemptionExpiresAtUtc <= DateTimeOffset.UtcNow);
            Assert.False(await expiredDb.BootstrapChallenges.AnyAsync(x => x.RequestNonce == expiredNonce));
        }

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

        CommandMetaDto SignRegistrationPayload(InstallRegistrationPayloadDto payload, string reason)
        {
            var unsignedPayload = new
            {
                payload.ShopId,
                payload.LicenseId,
                payload.InstallationId,
                payload.InstallationPublicKey,
                payload.InstallationPublicKeyFingerprint,
                payload.BootstrapChallengeId,
                payload.DeviceIdentityVersion,
                payload.DeviceIdentityHash,
                payload.HardwareFingerprintVersion,
                payload.HardwareFingerprintHash,
                payload.SignalQuality,
                payload.ApplicationVersion
            };
            var payloadDigest = _serializer.SerializeAndHash(unsignedPayload).Sha256Hex;
            var envelope = _serializer.SerializeCanonical(new
            {
                profile = "CENTRAL_INSTALLATION_BOOTSTRAP_V1",
                purpose = "installation.register",
                httpMethod = "POST",
                httpPath = "/api/licensing/v1/primary/register",
                challengeId,
                challengeNonce,
                requestNonce,
                activationTokenHash,
                payloadDigest
            });
            payload.ProofOfPossession = Convert.ToHexString(clientRsa.SignData(Encoding.UTF8.GetBytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).ToLowerInvariant();
            var meta = CreateMeta("installation.register");
            meta.PayloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex;
            meta.Reason = reason;
            return meta;
        }

        var registrationMeta = SignRegistrationPayload(registrationPayload, "Synthetic development bootstrap registration");

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

        var wrongInstallationId = Guid.NewGuid();
        var wrongInstallationPayload = JsonSerializer.Deserialize<InstallRegistrationPayloadDto>(JsonSerializer.Serialize(registrationPayload))!;
        wrongInstallationPayload.InstallationId = wrongInstallationId;
        var wrongInstallationMeta = SignRegistrationPayload(wrongInstallationPayload, "Synthetic wrong-installation identity denial");
        using (var wrongInstallationResponse = await _client.PostAsJsonAsync("/api/licensing/v1/primary/register", new InstallRegistrationRequestDto
        {
            Meta = wrongInstallationMeta,
            Payload = wrongInstallationPayload
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongInstallationResponse.StatusCode);
            Assert.Equal("BOOTSTRAP_BINDING_MISMATCH", (await ReadJsonAsync(wrongInstallationResponse))["code"]?.GetValue<string>());
            using var db = GetDbContext();
            Assert.False(await db.Installations.AnyAsync(x => x.Id == wrongInstallationId));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == wrongInstallationMeta.ClientOperationId));
        }

        var foreignShopRegistrationPayload = JsonSerializer.Deserialize<InstallRegistrationPayloadDto>(JsonSerializer.Serialize(registrationPayload))!;
        foreignShopRegistrationPayload.ShopId = Guid.NewGuid();
        var foreignShopRegistrationMeta = SignRegistrationPayload(foreignShopRegistrationPayload, "Synthetic cross-shop bootstrap registration denial");
        using (var foreignShopRegistrationResponse = await _client.PostAsJsonAsync("/api/licensing/v1/primary/register", new InstallRegistrationRequestDto
        {
            Meta = foreignShopRegistrationMeta,
            Payload = foreignShopRegistrationPayload
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, foreignShopRegistrationResponse.StatusCode);
            Assert.Equal("BOOTSTRAP_BINDING_MISMATCH", (await ReadJsonAsync(foreignShopRegistrationResponse))["code"]?.GetValue<string>());
            using var db = GetDbContext();
            Assert.False(await db.Installations.AnyAsync(x => x.Id == installId));
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == foreignShopRegistrationMeta.ClientOperationId));
        }

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
            Assert.Equal(registrationPayload.DeviceIdentityVersion, registeredInstallation.DeviceIdentityVersion);
            Assert.Equal(registrationPayload.DeviceIdentityHash, registeredInstallation.DeviceIdentityHash);
            Assert.Equal(registrationPayload.HardwareFingerprintVersion, registeredInstallation.HardwareFingerprintVersion);
            Assert.Equal(registrationPayload.HardwareFingerprintHash, registeredInstallation.HardwareFingerprintHash);
            Assert.NotEqual(registeredInstallation.DeviceIdentityHash, registeredInstallation.HardwareFingerprintHash);
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

        var consumedTokenNonce = Guid.NewGuid();
        using (var consumedTokenChallenge = await _client.PostAsJsonAsync("/api/licensing/v1/bootstrap/challenge", new BootstrapChallengeRequestDto
        {
            ShopId = shopId,
            InstallationId = Guid.NewGuid(),
            InstallationPublicKey = pubPem,
            ActivationTokenHash = activationTokenHash,
            RequestNonce = consumedTokenNonce
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, consumedTokenChallenge.StatusCode);
            Assert.Equal("BOOTSTRAP_NOT_ELIGIBLE", (await ReadJsonAsync(consumedTokenChallenge))["code"]?.GetValue<string>());
            using var db = GetDbContext();
            Assert.False(await db.BootstrapChallenges.AnyAsync(x => x.RequestNonce == consumedTokenNonce));
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

        // Certificate issuance must fail closed until a persisted signed policy exists.
        using (var missingPolicyResponse = await adminClient.PostAsJsonAsync("/api/activation/v1/certificates/issue", new IssueCertificateRequest
        {
            InstallationId = installId
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingPolicyResponse.StatusCode);
            Assert.Equal("POLICY_CONFIGURATION_REQUIRED", (await ReadJsonAsync(missingPolicyResponse))["code"]?.GetValue<string>());
        }

        // Labelled synthetic configuration fixture: certificate dates must follow this signed policy.
        var policyPayload = new
        {
            shopId,
            licenseId,
            policyVersion = 1,
            enforcementMode = ShopEnforcementPolicy.FullStop,
            afterExtensionPolicyId = Guid.NewGuid(),
            leaseDurationSeconds = 5_400,
            offlineGraceSeconds = 86_400,
            rollbackToleranceSeconds = 300,
            capabilityPolicyId = Guid.NewGuid(),
            approvedByAdminId = adminId,
            approvedAtUtc = DateTimeOffset.UtcNow
        };
        var policyMeta = new CanonicalDirectoryMeta
        {
            ClientOperationId = Guid.NewGuid(),
            OperationType = "license.policy",
            FingerprintVersion = 1,
            PayloadSha256 = _serializer.SerializeAndHash(policyPayload).Sha256Hex,
            ExpectedVersion = 0,
            PolicyVersion = 1,
            Reason = "Synthetic explicit signed policy for certificate issuance integration test"
        };
        using (var policyResponse = await adminClient.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId:D}/policy", new { meta = policyMeta, payload = policyPayload }))
        {
            Assert.True(policyResponse.StatusCode == HttpStatusCode.OK, $"Expected signed policy configuration, got {(int)policyResponse.StatusCode}: {await policyResponse.Content.ReadAsStringAsync()}");
        }

        var activeAddonExpiry = DateTimeOffset.UtcNow.AddDays(30);
        var expiredAddonExpiry = DateTimeOffset.UtcNow.AddDays(-1);
        using (var db = GetDbContext())
        {
            db.CommercialEntitlements.Add(CommercialEntitlement.Create(licenseId, CommercialEntitlement.KindSupport, activeAddonExpiry, orderId));
            // A status that has not yet been swept must still not leak an expired entitlement into a new signed claim.
            db.CommercialEntitlements.Add(CommercialEntitlement.Create(licenseId, CommercialEntitlement.KindPremiumUpgrade, expiredAddonExpiry, orderId));
            await db.SaveChangesAsync();
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
        var signedClaimsText = cert1Json["signedEnvelopeJson"]!.GetValue<string>();
        using (var signedClaims = JsonDocument.Parse(signedClaimsText))
        {
            var claimRoot = signedClaims.RootElement;
            Assert.Equal(CommercialTerm.Lifetime, claimRoot.GetProperty("termKind").GetString());
            Assert.Equal(JsonValueKind.Null, claimRoot.GetProperty("baseValidUntilUtc").ValueKind);
            Assert.Equal(registeredInstallation.PublicKeyFingerprintSha256, claimRoot.GetProperty("installationPublicKeyFingerprint").GetString());
            var addonClaims = claimRoot.GetProperty("addonEntitlements").EnumerateArray().ToArray();
            var supportClaim = Assert.Single(addonClaims);
            Assert.Equal(CommercialEntitlement.KindSupport, supportClaim.GetProperty("addonKind").GetString());
            Assert.InRange(Math.Abs((activeAddonExpiry - supportClaim.GetProperty("validUntilUtc").GetDateTimeOffset()).Ticks), 0, 10);
        }

        string cert1SignedEnvelope;
        string cert1StoredSignature;
        string cert1Digest;
        string cert1Serial;
        DateTimeOffset cert1IssuedAt;
        DateTimeOffset cert1ValidFrom;
        DateTimeOffset cert1LeaseExpires;
        DateTimeOffset cert1OfflineGrace;
        int cert1StoredLineage;
        int cert1StoredRowVersion;
        using (var db = GetDbContext())
        {
            var dbCert = await db.LicenseCertificates.FindAsync(cert1Id);
            Assert.NotNull(dbCert);
            Assert.NotEmpty(dbCert.RS256Signature);
            var signingRequest = await db.CentralSigningRequests.SingleAsync(x => x.PayloadSha256 == dbCert.DigestSha256);
            Assert.Equal("SIGNED", signingRequest.Status);
            Assert.Equal(signingRequest.Signature, dbCert.RS256Signature);
            using var signerScope = _factory.Services.CreateScope();
            var signerClient = signerScope.ServiceProvider.GetRequiredService<IProtectedSignerClient>();
            var trustSet = await signerClient.GetTrustSetAsync();
            var trustedKey = Assert.Single(trustSet.Keys, x => x.KeyId == signingRequest.KeyId &&
                x.KeyVersion == signingRequest.KeyVersion && x.Purpose == SigningPurpose.License && x.Status == "ACTIVE");
            using var verifier = RSA.Create();
            verifier.ImportFromPem(trustedKey.PublicKey);
            Assert.True(verifier.VerifyData(
                Encoding.UTF8.GetBytes(dbCert.SignedEnvelopeJson),
                Convert.FromBase64String(dbCert.RS256Signature),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));
            cert1SignedEnvelope = dbCert.SignedEnvelopeJson;
            cert1StoredSignature = dbCert.RS256Signature;
            cert1Digest = dbCert.DigestSha256;
            cert1Serial = dbCert.CertificateSerial;
            cert1IssuedAt = dbCert.IssuedAtUtc;
            cert1ValidFrom = dbCert.ValidFromUtc;
            cert1LeaseExpires = dbCert.LeaseExpiresUtc;
            cert1OfflineGrace = dbCert.OfflineGraceUntilUtc;
            cert1StoredLineage = dbCert.LineageRevision;
            cert1StoredRowVersion = dbCert.RowVersion;
        }

        // Verify Monotonic Time Bounds: ValidFromUtc <= LeaseExpiresUtc <= OfflineGraceUntilUtc
        var validFromUtc = cert1Json["validFromUtc"]!.GetValue<DateTimeOffset>();
        var leaseExpiresUtc = cert1Json["leaseExpiresUtc"]!.GetValue<DateTimeOffset>();
        var offlineGraceUntilUtc = cert1Json["offlineGraceUntilUtc"]!.GetValue<DateTimeOffset>();
        Assert.True(validFromUtc <= leaseExpiresUtc, "validFromUtc <= leaseExpiresUtc");
        Assert.True(leaseExpiresUtc <= offlineGraceUntilUtc, "leaseExpiresUtc <= offlineGraceUntilUtc");
        Assert.InRange(Math.Abs((leaseExpiresUtc - validFromUtc).TotalSeconds - policyPayload.leaseDurationSeconds), 0, 1);
        Assert.InRange(Math.Abs((offlineGraceUntilUtc - leaseExpiresUtc).TotalSeconds - policyPayload.offlineGraceSeconds), 0, 1);

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
            Assert.Equal(cert1SignedEnvelope, oldCert.SignedEnvelopeJson);
            Assert.Equal(cert1StoredSignature, oldCert.RS256Signature);
            Assert.Equal(cert1Digest, oldCert.DigestSha256);
            Assert.Equal(cert1Serial, oldCert.CertificateSerial);
            Assert.Equal(cert1IssuedAt, oldCert.IssuedAtUtc);
            Assert.Equal(cert1ValidFrom, oldCert.ValidFromUtc);
            Assert.Equal(cert1LeaseExpires, oldCert.LeaseExpiresUtc);
            Assert.Equal(cert1OfflineGrace, oldCert.OfflineGraceUntilUtc);
            Assert.Equal(cert1StoredLineage, oldCert.LineageRevision);
            Assert.Equal(cert1StoredRowVersion + 1, oldCert.RowVersion);
        }
    }

    [Fact]
    public async Task CONE_T12_HardwareDiscrepancy_AndPrimaryTransfer_DualControl()
    {
        var (admin1SessionId, admin1Id, adminClient1) = await CreateAdminClientAsync(new[] { AdminCapabilities.LicensingManage, AdminCapabilities.LicensingRead });
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
            CommandMeta = CreateMeta("license.issue")
        })))["id"]!.GetValue<string>());

        // Labelled synthetic policy fixture makes certificate dates explicit before the verified transfer.
        var transferPolicyPayload = new
        {
            shopId,
            licenseId,
            policyVersion = 1,
            enforcementMode = ShopEnforcementPolicy.FullStop,
            afterExtensionPolicyId = Guid.NewGuid(),
            leaseDurationSeconds = 5_400,
            offlineGraceSeconds = 86_400,
            rollbackToleranceSeconds = 300,
            capabilityPolicyId = Guid.NewGuid(),
            approvedByAdminId = admin1Id,
            approvedAtUtc = DateTimeOffset.UtcNow
        };
        var transferPolicyMeta = CreateMeta("license.policy");
        transferPolicyMeta.PayloadSha256 = _serializer.SerializeAndHash(transferPolicyPayload).Sha256Hex;
        transferPolicyMeta.ExpectedVersion = 0;
        transferPolicyMeta.PolicyVersion = 1;
        transferPolicyMeta.Reason = "Synthetic explicit policy for T12 transfer certificate proof";
        using (var policyResponse = await adminClient1.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId:D}/policy", new
        {
            meta = transferPolicyMeta,
            payload = transferPolicyPayload
        }))
        {
            Assert.True(policyResponse.StatusCode == HttpStatusCode.OK,
                $"Expected explicit signed policy for T12, got {(int)policyResponse.StatusCode}: {await policyResponse.Content.ReadAsStringAsync()}");
        }

        // Register Primary Source Installation
        using var rsaSource = RSA.Create(2048);
        var sourceInstallId = await AddPersistedInstallationFixtureAsync(
            shopId, licenseId, rsaSource.ExportSubjectPublicKeyInfoPem(), "HW-SOURCE-OLD-FP", "OLD-POS");
        using (var db = GetDbContext())
        {
            var sourceInstallation = await db.Installations.FindAsync(sourceInstallId);
            sourceInstallation!.Approve(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var sourceCertificateResponse = await adminClient1.PostAsJsonAsync("/api/activation/v1/certificates/issue", new IssueCertificateRequest
        {
            InstallationId = sourceInstallId
        });
        Assert.Equal(HttpStatusCode.OK, sourceCertificateResponse.StatusCode);
        var sourceCertificateJson = await ReadJsonAsync(sourceCertificateResponse);
        var sourceCertificateId = Guid.Parse(sourceCertificateJson["id"]!.GetValue<string>());
        Assert.Equal(LicenseCertificate.StatusActive, sourceCertificateJson["status"]?.GetValue<string>());

        // Journey 1: Hardware Discrepancy Reporting & Decision
        var reportRes = await adminClient1.PostAsJsonAsync("/api/activation/v1/hardware-reviews", new
        {
            // The caller-supplied shop is deliberately forged. The service must use the persisted installation scope.
            ShopId = Guid.NewGuid(),
            InstallationId = sourceInstallId,
            ReportedFingerprint = "HW-SOURCE-NEW-FP",
            // A forged stored fingerprint must be ignored; it comes from the persisted installation.
            StoredFingerprint = "HW-CLIENT-FORGED-STORED-FP",
            Reason = "Upgraded motherboard"
        });
        Assert.Equal(HttpStatusCode.OK, reportRes.StatusCode);
        var reviewJson = await ReadJsonAsync(reportRes);
        var reviewId = Guid.Parse(reviewJson["id"]!.GetValue<string>());
        Assert.Equal(shopId, Guid.Parse(reviewJson["shopId"]!.GetValue<string>()));
        Assert.Equal("HW-SOURCE-OLD-FP", reviewJson["storedMachineFingerprint"]?.GetValue<string>());

        var missingOperationIdMeta = CreateMeta("hardware.review");
        missingOperationIdMeta.ClientOperationId = Guid.Empty;
        var missingOperationIdRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/hardware-reviews/{reviewId}/decide", new DecideHardwareReviewCommand
        {
            Approve = true,
            CommandMeta = missingOperationIdMeta
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingOperationIdRes.StatusCode);
        using (var db = GetDbContext())
        {
            var unchanged = await db.HardwareReviews.FindAsync(reviewId);
            Assert.Equal(HardwareReview.StatusPending, unchanged!.Status);
            Assert.Equal(1, unchanged.RowVersion);
            Assert.False(await db.CentralOutboxMessages.AnyAsync(x => x.EventType == "activation.hardware_review_decided" && x.AggregateId == reviewId.ToString()));
            Assert.False(await db.CentralAuditRecords.AnyAsync(x => x.Action == "HARDWARE_REVIEW_DECIDED" && x.ResourceId == reviewId.ToString()));
        }

        var hardwareDecision = new DecideHardwareReviewCommand
        {
            Approve = true,
            CommandMeta = CreateMeta("hardware.review")
        };
        hardwareDecision.CommandMeta.PayloadSha256 = _serializer.SerializeAndHash(hardwareDecision).Sha256Hex;
        var decideRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/hardware-reviews/{reviewId}/decide", hardwareDecision);
        Assert.Equal(HttpStatusCode.OK, decideRes.StatusCode);
        var replayRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/hardware-reviews/{reviewId}/decide", hardwareDecision);
        Assert.Equal(HttpStatusCode.OK, replayRes.StatusCode);

        var staleCommandMeta = CreateMeta("hardware.review");
        staleCommandMeta.ExpectedVersion = 1;
        var staleHardwareDecision = new DecideHardwareReviewCommand
        {
            Approve = false,
            CommandMeta = staleCommandMeta
        };
        staleCommandMeta.PayloadSha256 = _serializer.SerializeAndHash(staleHardwareDecision).Sha256Hex;
        var staleRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/hardware-reviews/{reviewId}/decide", staleHardwareDecision);
        Assert.Equal(HttpStatusCode.Conflict, staleRes.StatusCode);

        using (var db = GetDbContext())
        {
            var r = await db.HardwareReviews.FindAsync(reviewId);
            Assert.Equal(HardwareReview.StatusApproved, r!.Status);
            Assert.Equal(2, r.RowVersion);
            Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.EventType == "activation.hardware_review_decided" && x.AggregateId == reviewId.ToString()));
            Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.Action == "HARDWARE_REVIEW_DECIDED" && x.ResourceId == reviewId.ToString()));
        }

        // Journey 2: Primary Transfer Dual-Control Governance
        // Register Target Installation
        using var rsaTarget = RSA.Create(2048);
        var targetInstallId = await AddPersistedInstallationFixtureAsync(
            shopId, licenseId, rsaTarget.ExportSubjectPublicKeyInfoPem(), "HW-TARGET-NEW-PRIMARY", "NEW-POS");

        // An empty operation identity must not create a transfer.
        var emptyInitiateMeta = CreateMeta("primary.transfer");
        emptyInitiateMeta.ClientOperationId = Guid.Empty;
        int initiationOutboxBefore;
        int initiationAuditBefore;
        using (var db = GetDbContext())
        {
            initiationOutboxBefore = await db.CentralOutboxMessages.CountAsync(x => x.EventType == "activation.primary_transfer_initiated");
            initiationAuditBefore = await db.CentralAuditRecords.CountAsync(x => x.Action == "PRIMARY_TRANSFER_INITIATED");
        }
        var emptyInitiateRes = await adminClient1.PostAsJsonAsync("/api/activation/v1/primary-transfers", new InitiatePrimaryTransferCommand
        {
            ShopId = shopId,
            SourceInstallationId = sourceInstallId,
            TargetInstallationId = targetInstallId,
            TransferReason = "Hardware replacement transfer",
            CommandMeta = emptyInitiateMeta
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyInitiateRes.StatusCode);
        using (var db = GetDbContext())
        {
            Assert.False(await db.PrimaryTransfers.AnyAsync(x => x.SourceInstallationId == sourceInstallId && x.TargetInstallationId == targetInstallId));
            Assert.Equal(initiationOutboxBefore, await db.CentralOutboxMessages.CountAsync(x => x.EventType == "activation.primary_transfer_initiated"));
            Assert.Equal(initiationAuditBefore, await db.CentralAuditRecords.CountAsync(x => x.Action == "PRIMARY_TRANSFER_INITIATED"));
        }

        // Admin 1 initiates primary transfer; retrying the exact command must return the same persisted transfer.
        var initiateTransferCommand = new InitiatePrimaryTransferCommand
        {
            ShopId = shopId,
            SourceInstallationId = sourceInstallId,
            TargetInstallationId = targetInstallId,
            TransferReason = "Hardware replacement transfer",
            CommandMeta = CreateMeta("primary.transfer")
        };
        initiateTransferCommand.CommandMeta.PayloadSha256 = _serializer.SerializeAndHash(initiateTransferCommand).Sha256Hex;
        var initTransferRes = await adminClient1.PostAsJsonAsync("/api/activation/v1/primary-transfers", initiateTransferCommand);
        Assert.Equal(HttpStatusCode.OK, initTransferRes.StatusCode);
        var transferJson = await ReadJsonAsync(initTransferRes);
        var transferId = Guid.Parse(transferJson["id"]!.GetValue<string>());
        var initiateReplayRes = await adminClient1.PostAsJsonAsync("/api/activation/v1/primary-transfers", initiateTransferCommand);
        Assert.Equal(HttpStatusCode.OK, initiateReplayRes.StatusCode);
        var replayTransferJson = await ReadJsonAsync(initiateReplayRes);
        Assert.Equal(transferId, Guid.Parse(replayTransferJson["id"]!.GetValue<string>()));
        using (var db = GetDbContext())
        {
            Assert.Equal(1, await db.PrimaryTransfers.CountAsync(x => x.SourceInstallationId == sourceInstallId && x.TargetInstallationId == targetInstallId));
            Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.EventType == "activation.primary_transfer_initiated" && x.AggregateId == transferId.ToString()));
            Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.Action == "PRIMARY_TRANSFER_INITIATED" && x.ResourceId == transferId.ToString()));
        }

        // Invariant: Self-approval is strictly prohibited (Admin 1 cannot approve own transfer)
        var selfApproveRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/approve", new ApprovePrimaryTransferCommand
        {
            SensitiveApprovalId = Guid.NewGuid(),
            CommandMeta = CreateMeta("primary.transfer")
        });
        Assert.Equal(HttpStatusCode.BadRequest, selfApproveRes.StatusCode);
        var selfError = await ReadJsonAsync(selfApproveRes);
        Assert.Equal("SELF_APPROVAL_PROHIBITED", selfError["code"]?.GetValue<string>());

        // Invariant: Nonexistent approval fails closed
        var nonexistentApproveRes = await adminClient2.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/approve", new ApprovePrimaryTransferCommand
        {
            SensitiveApprovalId = Guid.NewGuid(),
            CommandMeta = CreateMeta("primary.transfer")
        });
        Assert.Equal(HttpStatusCode.BadRequest, nonexistentApproveRes.StatusCode);
        var nonexistentError = await ReadJsonAsync(nonexistentApproveRes);
        Assert.Equal("APPROVAL_NOT_FOUND", nonexistentError["code"]?.GetValue<string>());

        // Request sensitive approval for primary transfer (Admin 1 requests, Admin 2 approves)
        var transferDigest = new string('t', 64);
        var transferStepUpGrantId = await CreateStepUpGrantAsync(admin1SessionId, adminClient1, "primary.transfer", transferDigest);
        var reqTransferApprovalRes = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
        {
            OperationType = "PRIMARY_TRANSFER",
            TargetEntityId = transferId,
            TargetPayloadSha256 = transferDigest,
            ActionScope = "primary.transfer",
            Reason = "Synthetic test approval for primary transfer.",
            StepUpGrantId = transferStepUpGrantId
        });
        Assert.Equal(HttpStatusCode.OK, reqTransferApprovalRes.StatusCode);
        var transferApprovalJson = await ReadJsonAsync(reqTransferApprovalRes);
        var transferApprovalId = Guid.Parse(transferApprovalJson["id"]!.GetValue<string>());

        var decideTransferApprovalRes = await adminClient2.PostAsJsonAsync($"/api/lifecycle/v1/sensitive-approvals/{transferApprovalId}/decide", new DecideSensitiveApprovalCommand
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = transferDigest,
            CommandMeta = CreateMeta()
        });
        Assert.Equal(HttpStatusCode.OK, decideTransferApprovalRes.StatusCode);

        // A missing operation identity must not consume approval or create a transfer effect.
        var emptyTransferOperationMeta = CreateMeta("primary.transfer");
        emptyTransferOperationMeta.ClientOperationId = Guid.Empty;
        var emptyTransferOperationRes = await adminClient2.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/approve", new ApprovePrimaryTransferCommand
        {
            SensitiveApprovalId = transferApprovalId,
            CommandMeta = emptyTransferOperationMeta
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyTransferOperationRes.StatusCode);
        using (var db = GetDbContext())
        {
            var unchangedTransfer = await db.PrimaryTransfers.FindAsync(transferId);
            var unchangedApproval = await db.SensitiveApprovalRequests.FindAsync(transferApprovalId);
            Assert.Equal(PrimaryTransfer.StatusPendingApproval, unchangedTransfer!.Status);
            Assert.Equal(1, unchangedTransfer.RowVersion);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, unchangedApproval!.Status);
            Assert.False(await db.CentralOutboxMessages.AnyAsync(x => x.EventType == "activation.primary_transfer_approved" && x.AggregateId == transferId.ToString()));
            Assert.False(await db.CentralAuditRecords.AnyAsync(x => x.Action == "PRIMARY_TRANSFER_APPROVED" && x.ResourceId == transferId.ToString()));
        }

        // Independent Admin 2 approves transfer with valid sensitive approval.
        var transferApprovalCommand = new ApprovePrimaryTransferCommand
        {
            SensitiveApprovalId = transferApprovalId,
            CommandMeta = CreateMeta("primary.transfer")
        };
        transferApprovalCommand.CommandMeta.PayloadSha256 = _serializer.SerializeAndHash(transferApprovalCommand).Sha256Hex;
        var admin2ApproveRes = await adminClient2.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/approve", transferApprovalCommand);
        Assert.Equal(HttpStatusCode.OK, admin2ApproveRes.StatusCode);
        var approveReplayRes = await adminClient2.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/approve", transferApprovalCommand);
        Assert.Equal(HttpStatusCode.OK, approveReplayRes.StatusCode);

        var staleTransferMeta = CreateMeta("primary.transfer");
        staleTransferMeta.ExpectedVersion = 1;
        var staleTransferCommand = new ApprovePrimaryTransferCommand
        {
            SensitiveApprovalId = transferApprovalId,
            CommandMeta = staleTransferMeta
        };
        staleTransferMeta.PayloadSha256 = _serializer.SerializeAndHash(staleTransferCommand).Sha256Hex;
        var staleTransferRes = await adminClient2.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/approve", staleTransferCommand);
        Assert.Equal(HttpStatusCode.Conflict, staleTransferRes.StatusCode);

        using (var db = GetDbContext())
        {
            var approvedTransfer = await db.PrimaryTransfers.FindAsync(transferId);
            var consumedApproval = await db.SensitiveApprovalRequests.FindAsync(transferApprovalId);
            Assert.Equal(PrimaryTransfer.StatusApproved, approvedTransfer!.Status);
            Assert.Equal(2, approvedTransfer.RowVersion);
            Assert.Equal(SensitiveApprovalRequest.StatusConsumed, consumedApproval!.Status);
            Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.EventType == "activation.primary_transfer_approved" && x.AggregateId == transferId.ToString()));
            Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.Action == "PRIMARY_TRANSFER_APPROVED" && x.ResourceId == transferId.ToString()));
        }

        // Completion cannot reach a direct-write fallback without a client operation identity.
        var emptyCompletionCommand = new CompletePrimaryTransferCommand
        {
            CommandMeta = CreateMeta("primary.transfer")
        };
        emptyCompletionCommand.CommandMeta.ClientOperationId = Guid.Empty;
        emptyCompletionCommand.CommandMeta.ExpectedVersion = 2;
        emptyCompletionCommand.CommandMeta.PayloadSha256 = _serializer.SerializeAndHash(emptyCompletionCommand).Sha256Hex;
        var emptyCompletionRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/complete", emptyCompletionCommand);
        Assert.Equal(HttpStatusCode.BadRequest, emptyCompletionRes.StatusCode);
        using (var db = GetDbContext())
        {
            var unchangedTransfer = await db.PrimaryTransfers.FindAsync(transferId);
            Assert.Equal(PrimaryTransfer.StatusApproved, unchangedTransfer!.Status);
            Assert.Equal(2, unchangedTransfer.RowVersion);
            Assert.False(await db.CentralOutboxMessages.AnyAsync(x => x.EventType == "activation.primary_transfer_completed" && x.AggregateId == transferId.ToString()));
            Assert.False(await db.CentralAuditRecords.AnyAsync(x => x.Action == "PRIMARY_TRANSFER_COMPLETED" && x.ResourceId == transferId.ToString()));
        }

        // Completion is an idempotent, versioned Central command with one audit/outbox outcome.
        var completeCommand = new CompletePrimaryTransferCommand
        {
            CommandMeta = CreateMeta("primary.transfer")
        };
        completeCommand.CommandMeta.ExpectedVersion = 2;
        completeCommand.CommandMeta.PayloadSha256 = _serializer.SerializeAndHash(completeCommand).Sha256Hex;
        var completeRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/complete", completeCommand);
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);
        var completeReplayRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/complete", completeCommand);
        Assert.Equal(HttpStatusCode.OK, completeReplayRes.StatusCode);

        var staleCompleteCommand = new CompletePrimaryTransferCommand
        {
            CommandMeta = CreateMeta("primary.transfer")
        };
        staleCompleteCommand.CommandMeta.ExpectedVersion = 2;
        staleCompleteCommand.CommandMeta.PayloadSha256 = _serializer.SerializeAndHash(staleCompleteCommand).Sha256Hex;
        var staleCompleteRes = await adminClient1.PostAsJsonAsync($"/api/activation/v1/primary-transfers/{transferId}/complete", staleCompleteCommand);
        Assert.Equal(HttpStatusCode.Conflict, staleCompleteRes.StatusCode);

        // Verify Invariants: Source is REPLACED, Target is in DB (Single primary capacity enforced)
        using (var db = GetDbContext())
        {
            var src = await db.Installations.FindAsync(sourceInstallId);
            var tgt = await db.Installations.FindAsync(targetInstallId);
            var completedTransfer = await db.PrimaryTransfers.FindAsync(transferId);
            var revokedSourceCertificate = await db.LicenseCertificates.FindAsync(sourceCertificateId);
            Assert.Equal(Installation.StatusReplaced, src!.Status);
            Assert.NotNull(tgt);
            Assert.Equal(PrimaryTransfer.StatusCompleted, completedTransfer!.Status);
            Assert.Equal(LicenseCertificate.StatusRevoked, revokedSourceCertificate!.Status);
            Assert.Equal(3, completedTransfer.RowVersion);
            Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.EventType == "activation.primary_transfer_completed" && x.AggregateId == transferId.ToString()));
            Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.Action == "PRIMARY_TRANSFER_COMPLETED" && x.ResourceId == transferId.ToString()));
        }

        // A distinct certificate is issued to the verified new Primary using its fresh installation identity.
        using (var newCertificateResponse = await adminClient1.PostAsJsonAsync("/api/activation/v1/certificates/issue", new IssueCertificateRequest
        {
            InstallationId = targetInstallId
        }))
        {
            Assert.Equal(HttpStatusCode.OK, newCertificateResponse.StatusCode);
            var newCertificate = await ReadJsonAsync(newCertificateResponse);
            Assert.Equal(LicenseCertificate.StatusActive, newCertificate["status"]?.GetValue<string>());
            Assert.NotEqual(sourceCertificateId, Guid.Parse(newCertificate["id"]!.GetValue<string>()));
        }

        using (var db = GetDbContext())
        {
            var targetCertificates = await db.LicenseCertificates
                .Where(c => c.InstallationId == targetInstallId && c.Status == LicenseCertificate.StatusActive)
                .ToListAsync();
            var targetCertificate = Assert.Single(targetCertificates);
            Assert.Equal(targetInstallId, targetCertificate.InstallationId);
            Assert.Equal(licenseId, targetCertificate.LicenseId);
            Assert.Equal(shopId, targetCertificate.ShopId);
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
            CommandMeta = CreateMeta("license.issue")
        })))["id"]!.GetValue<string>());

        // Journey 1: Sensitive Approval Dual-Control
        var targetPayloadSha256 = new string('e', 64);
        var initialApprovalGrant = await CreateStepUpGrantAsync(admin1SessionId, adminClient1, "license.revoke", targetPayloadSha256);
        int approvalCountBeforeMissingGrant;
        int approvalOutboxCountBeforeMissingGrant;
        using (var db = GetDbContext())
        {
            approvalCountBeforeMissingGrant = await db.SensitiveApprovalRequests.CountAsync();
            approvalOutboxCountBeforeMissingGrant = await db.CentralOutboxMessages.CountAsync(x => x.EventType == "approval.requested");
        }
        var missingGrantResponse = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
        {
            OperationType = "LICENSE_REVOKE",
            TargetEntityId = licenseId,
            TargetPayloadSha256 = targetPayloadSha256,
            ActionScope = "license.revoke",
            Reason = "Synthetic test request used to verify missing step-up denial.",
            StepUpGrantId = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.Forbidden, missingGrantResponse.StatusCode);
        Assert.Equal("STEP_UP_REQUIRED", (await ReadJsonAsync(missingGrantResponse))["code"]?.GetValue<string>());
        using (var db = GetDbContext())
        {
            Assert.Equal(approvalCountBeforeMissingGrant, await db.SensitiveApprovalRequests.CountAsync());
            Assert.Equal(approvalOutboxCountBeforeMissingGrant, await db.CentralOutboxMessages.CountAsync(x => x.EventType == "approval.requested"));
        }
        var reqApprovalRes = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new
        {
            operationType = "LICENSE_REVOKE",
            targetEntityId = licenseId,
            targetPayloadSha256,
            actionScope = "license.revoke",
            reason = "Synthetic test approval request for license revocation.",
            stepUpGrantId = initialApprovalGrant,
            stepUpSessionId = Guid.NewGuid(),
            validityMinutes = 1
        });
        Assert.Equal(HttpStatusCode.OK, reqApprovalRes.StatusCode);
        var approvalJson = await ReadJsonAsync(reqApprovalRes);
        var approvalId = Guid.Parse(approvalJson["id"]!.GetValue<string>());
        using (var db = GetDbContext())
        {
            var persisted = await db.SensitiveApprovalRequests.FindAsync(approvalId);
            Assert.Equal(admin1SessionId, persisted!.StepUpSessionId);
            Assert.Equal(TimeSpan.FromSeconds(3600), persisted.ExpiresAtUtc - persisted.RequestedAtUtc);
        }

        int approvalCountBeforeChangedIntentReplay;
        using (var db = GetDbContext())
        {
            approvalCountBeforeChangedIntentReplay = await db.SensitiveApprovalRequests.CountAsync();
        }
        var changedIntentReplay = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new
        {
            operationType = "LICENSE_REVOKE",
            targetEntityId = licenseId,
            targetPayloadSha256,
            actionScope = "license.revoke",
            reason = "Synthetic replay request verifies the one-use grant denial.",
            stepUpGrantId = initialApprovalGrant
        });
        Assert.Equal(HttpStatusCode.Conflict, changedIntentReplay.StatusCode);
        Assert.Equal("OPERATION_ID_CONFLICT", (await ReadJsonAsync(changedIntentReplay))["code"]?.GetValue<string>());
        using (var db = GetDbContext())
        {
            Assert.Equal(approvalCountBeforeChangedIntentReplay, await db.SensitiveApprovalRequests.CountAsync());
        }

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
            var digest = new string('0', 64);
            var scope = opType.ToLowerInvariant();
            var stepUpGrant = await CreateStepUpGrantAsync(admin1SessionId, adminClient1, scope, digest);
            var reqRes = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
            {
                OperationType = opType,
                TargetEntityId = licenseId,
                TargetPayloadSha256 = digest,
                ActionScope = scope,
                Reason = $"Synthetic test approval for {opType}.",
                StepUpGrantId = stepUpGrant
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
        var canonicalStepUpGrant = await CreateStepUpGrantAsync(admin1SessionId, adminClient1, "license.pause", canonicalPayloadHash);
        var canonicalApprovalResponse = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
        {
            OperationType = "LICENSE_PAUSE",
            TargetEntityId = licenseId,
            TargetPayloadSha256 = canonicalPayloadHash,
            ActionScope = "license.pause",
            Reason = "Synthetic test approval for license pause.",
            StepUpGrantId = canonicalStepUpGrant
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
        _ = await CreateStepUpGrantAsync(
            admin1SessionId,
            adminClient1,
            "license.pause",
            _serializer.SerializeAndHash(tamperedEffectivePayload).Sha256Hex);
        var tamperedResponse = await adminClient1.PostAsJsonAsync($"/api/admin/v1/licenses/{licenseId}/pause", tamperedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, tamperedResponse.StatusCode);
        Assert.Equal("APPROVAL_PAYLOAD_MISMATCH", (await ReadJsonAsync(tamperedResponse))["code"]?.GetValue<string>());
        using (var db = GetDbContext())
        {
            Assert.Equal(CommercialLicense.StatusActive, (await db.CommercialLicenses.FindAsync(licenseId))!.Status);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, (await db.SensitiveApprovalRequests.FindAsync(canonicalApprovalId))!.Status);
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == canonicalOperationId));
        }

        _ = await CreateStepUpGrantAsync(admin1SessionId, adminClient1, "license.pause", canonicalPayloadHash);
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
        var resumeStepUpGrant = await CreateStepUpGrantAsync(admin1SessionId, adminClient1, "license.resume", resumePayloadHash);
        var resumeApprovalResponse = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
        {
            OperationType = "LICENSE_RESUME",
            TargetEntityId = licenseId,
            TargetPayloadSha256 = resumePayloadHash,
            ActionScope = "license.resume",
            Reason = "Synthetic test approval for license resume.",
            StepUpGrantId = resumeStepUpGrant
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
        _ = await CreateStepUpGrantAsync(admin1SessionId, adminClient1, "license.resume", resumePayloadHash);
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
            var stepUpGrant = await CreateStepUpGrantAsync(admin1SessionId, adminClient1, operationType, digest);
            var approvalResponse = await adminClient1.PostAsJsonAsync("/api/lifecycle/v1/sensitive-approvals", new RequestSensitiveApprovalRequest
            {
                OperationType = approvalType,
                TargetEntityId = licenseId,
                TargetPayloadSha256 = digest,
                ActionScope = operationType,
                Reason = $"Synthetic test approval for {operationType}.",
                StepUpGrantId = stepUpGrant
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
            _ = await CreateStepUpGrantAsync(admin1SessionId, adminClient1, operationType, digest);
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
            CommandMeta = CreateMeta("license.issue")
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
        var capabilityPolicyId = Guid.NewGuid();
        var enforcementPolicy = ShopEnforcementPolicy.Create(
            shop.Id, license.Id, 1, ShopEnforcementPolicy.FullStop, null, Guid.NewGuid(),
            86400, 86400, 300, capabilityPolicyId, "{}", "synthetic-development-signature",
            "synthetic-development-key", Guid.NewGuid(), now);
        using (var db = GetDbContext())
        {
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.PlanCatalogItems.Add(plan);
            db.PricingSnapshots.Add(snapshot);
            db.Orders.Add(order);
            db.CommercialLicenses.Add(license);
            db.ShopEnforcementPolicies.Add(enforcementPolicy);
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
        Assert.Equal(capabilityPolicyId.ToString("D"), item["capabilityPolicyId"]!.GetValue<string>());
        Assert.Equal(1, item["policyVersion"]!.GetValue<int>());
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
