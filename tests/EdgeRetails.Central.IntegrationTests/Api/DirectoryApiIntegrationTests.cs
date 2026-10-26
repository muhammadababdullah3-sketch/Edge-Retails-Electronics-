using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Licensing;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.IntegrationTests.Api;

public sealed class DirectoryApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _host;
    public DirectoryApiIntegrationTests(WebApplicationFactory<Program> host) => _host = host;

    private async Task<HttpClient> Client(params string[] capabilities)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var now = DateTimeOffset.UtcNow;
        var admin = AdminUser.Create($"directory-{Guid.NewGuid():N}", "synthetic", "synthetic", null, false, now);
        var id = Guid.NewGuid();
        var session = AdminSession.Create(admin.Id, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString()))).ToLowerInvariant(),
            now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, admin.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty("Id")!.SetValue(session, id);
        db.AdminUsers.Add(admin); db.AdminSessions.Add(session); await db.SaveChangesAsync();
        var client = _host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Session-Id", id.ToString());
        return client;
    }

    private async Task<(Customer Customer, Shop Shop, string Filter)> Seed()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var term = "directory-" + Guid.NewGuid().ToString("N");
        var customer = Customer.Create("Sensitive Customer Label", "private-directory@example.invalid", "555-PRIVATE", DateTimeOffset.UtcNow);
        var first = Shop.Create(customer.Id, term + "-1", term + " <img src=x onerror=alert(1)>", DateTimeOffset.UtcNow);
        db.Customers.Add(customer); db.Shops.Add(first);
        db.Shops.Add(Shop.Create(customer.Id, term + "-2", term, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return (customer, first, term);
    }

    [Fact]
    public async Task SourceReads_RequireExactCapabilities_AndNeverExposeContactFields()
    {
        var seed = await Seed();
        using var denied = await Client("dashboard.view");
        using var allowed = await Client("customer.view", "shop.view", "search.masked");
        using var anonymous = _host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        foreach (var path in new[] { $"/api/admin/v1/customers/{seed.Customer.Id}", $"/api/admin/v1/shops/{seed.Shop.Id}", $"/api/admin/v1/search?filter={seed.Filter}" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(path)).StatusCode);
            using var response = await allowed.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-directory@example.invalid", text);
            Assert.DoesNotContain("555-PRIVATE", text);
            Assert.DoesNotContain("Sensitive Customer Label", text);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            using var json = JsonDocument.Parse(text);
            Assert.Equal(5, json.RootElement.EnumerateObject().Count());
            Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("items").ValueKind);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await allowed.GetAsync($"/api/admin/v1/customers/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await allowed.GetAsync($"/api/admin/v1/shops/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Search_PagesPersistedRows_AndDetectsInsertWithUnchangedMaximumRowVersion()
    {
        var seed = await Seed();
        using var client = await Client("search.masked");
        var path = $"/api/admin/v1/search?filter={seed.Filter}&pageSize=1";
        using var first = JsonDocument.Parse(await client.GetStringAsync(path));
        var version = first.RootElement.GetProperty("asOfVersion").GetInt64();
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();
        Assert.Equal(2, first.RootElement.GetProperty("authorizedCount").GetInt32());
        Assert.Single(first.RootElement.GetProperty("items").EnumerateArray());
        using var nextResponse = await client.GetAsync(path + $"&cursor={cursor}&asOfVersion={version}");
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        using var next = JsonDocument.Parse(await nextResponse.Content.ReadAsStringAsync());
        Assert.NotEqual(first.RootElement.GetProperty("items")[0].GetProperty("shopId").GetGuid(), next.RootElement.GetProperty("items")[0].GetProperty("shopId").GetGuid());
        Assert.Equal(JsonValueKind.Null, next.RootElement.GetProperty("nextCursor").ValueKind);
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            db.Shops.Add(Shop.Create(seed.Customer.Id, seed.Filter + "-3", seed.Filter, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(path + $"&cursor={cursor}&asOfVersion={version}")).StatusCode);
        foreach (var invalid in new[] { "pageSize=0", "pageSize=101", "cursor=-1", "cursor=100001", "cursor=NaN", "asOfVersion=-1", "actor=forged" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/v1/search?" + invalid)).StatusCode);
        }
    }

    private static object Envelope(string operation, object payload, Guid? id = null, string? hash = null) => new
    {
        meta = new { clientOperationId = id ?? Guid.NewGuid(), operationType = operation, fingerprintVersion = 1,
            payloadSha256 = hash ?? new CanonicalJsonSerializer().SerializeAndHash(payload).Sha256Hex,
            expectedVersion = 0, policyVersion = 0, reason = "Synthetic canonical directory test" },
        payload
    };

    [Fact]
    public async Task CanonicalCreate_StoresOpaqueReferenceWithoutInventingEmail_AndReplaysExactlyOnce()
    {
        using var client = await Client("customer.create", "shop.register");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        var operationId = Guid.NewGuid();
        var payload = new { displayName = "Canonical synthetic customer", contactReference = "opaque:directory:" + Guid.NewGuid() };
        var command = Envelope("customer.create", payload, operationId);
        using var response = await client.PostAsJsonAsync("/api/admin/v1/customers", command);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal("SUCCEEDED", json.RootElement.GetProperty("state").GetString());
        Assert.False(json.RootElement.TryGetProperty("safeError", out _));
        var customerId = Guid.Parse(json.RootElement.GetProperty("resultReference").GetString()!.Split('/')[1]);
        using var replay = await client.PostAsJsonAsync("/api/admin/v1/customers", command);
        Assert.Equal(text, await replay.Content.ReadAsStringAsync());
        var shopOperation = Guid.NewGuid();
        var shopPayload = new { customerId, shopName = "Canonical synthetic shop", installationCapacity = 1, contactReference = "opaque:shop:fixture" };
        using var shopResponse = await client.PostAsJsonAsync("/api/admin/v1/shops", Envelope("shop.register", shopPayload, shopOperation));
        Assert.Equal(HttpStatusCode.OK, shopResponse.StatusCode);
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var customer = await db.Customers.SingleAsync(x => x.Id == customerId);
        Assert.Equal(payload.contactReference, customer.ContactReference);
        Assert.Equal("", customer.ContactEmail);
        Assert.Equal("", customer.ContactPhone);
        var shop = await db.Shops.SingleAsync(x => x.CustomerId == customerId);
        Assert.Equal("opaque:shop:fixture", shop.ContactReference);
        Assert.Equal(1, shop.PrimaryCapacity);
        Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.Id == operationId));
        Assert.Equal(1, await db.CentralOperationRecords.CountAsync(x => x.ClientOperationId == operationId));
        Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.CorrelationId == operationId.ToString() && x.IsSuccess));
        Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.Id == shopOperation));
    }

    [Fact]
    public async Task CanonicalCreate_RejectsSchemaHashCapabilityAndIdentityConflicts_WithoutPartialWrites()
    {
        using var client = await Client("customer.create", "shop.register");
        using var denied = await Client("dashboard.view");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        denied.DefaultRequestHeaders.Add("Origin", "https://localhost");
        var id = Guid.NewGuid();
        var payload = new { displayName = "Synthetic denial " + id };
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.PostAsJsonAsync("/api/admin/v1/customers", Envelope("customer.create", payload, id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/v1/customers", Envelope("shop.register", payload, id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/v1/customers", Envelope("customer.create", new { displayName = "", contactReference = "x" }, id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/v1/customers", Envelope("customer.create", new { displayName = "x", contactEmail = "unapproved@example.invalid" }, id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/v1/customers", Envelope("customer.create", payload, id, new string('0', 64)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/v1/shops", Envelope("shop.register", new { customerId = Guid.NewGuid(), shopName = "x", installationCapacity = "1" }, id))).StatusCode);
        using (var invalid = new StringContent("{\"meta\":{\"operationType\":\"customer.create\"},\"payload\":{\"displayName\":\"x\"}}", Encoding.UTF8, "application/json"))
        { Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/admin/v1/customers", invalid)).StatusCode); }
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == id));
            Assert.False(await db.CentralOutboxMessages.AnyAsync(x => x.Id == id));
            Assert.False(await db.Customers.AnyAsync(x => x.LegalName == payload.displayName));
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/admin/v1/customers", Envelope("customer.create", payload, id))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/v1/customers", Envelope("customer.create", new { displayName = "Changed intent" }, id))).StatusCode);
    }

    [Fact]
    public async Task CanonicalActivationIssue_BindsPersistedInstallation_ProtectsSecret_AndReplaysOnlySafeReference()
    {
        const string keyId = "central-activation-fixture-v1";
        var secretKey = RandomNumberGenerator.GetBytes(32);
        var previous = new Dictionary<string, string?>
        {
            ["CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON"] = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON"),
            ["CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID"] = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID"),
            ["CENTRAL_ACTIVATION_TOKEN_POLICY_ID"] = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_TOKEN_POLICY_ID"),
            ["CENTRAL_ACTIVATION_TOKEN_POLICY_VERSION"] = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_TOKEN_POLICY_VERSION"),
            ["CENTRAL_ACTIVATION_DELIVERY_CHANNEL_ID"] = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_CHANNEL_ID"),
            ["CENTRAL_ACTIVATION_FIRST_REDEMPTION_TTL_SECONDS"] = Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_FIRST_REDEMPTION_TTL_SECONDS")
        };
        using var clientRsa = RSA.Create(2048);
        var publicKey = clientRsa.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var customer = Customer.CreateFromContract("Canonical activation synthetic customer", null, now);
        var shop = Shop.RegisterFromContract(customer.Id, "Canonical activation synthetic shop", null, now);
        var item = PlanCatalogItem.Create($"ACT-{Guid.NewGuid():N}"[..12], "Canonical activation fixture plan",
            PlanCatalogItem.TargetBaseLicense, PlanCatalogItem.TermLifetime, null, 1m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, customer.Id, now);
        order.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), now);
        var license = CommercialLicense.Issue(order, now, null, now);
        var hardwareHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        var installation = Installation.RegisterBootstrap(Guid.NewGuid(), shop.Id, license.Id, hardwareHash,
            publicKey, 1, new string('a', 64), 1, "STRONG", "0.0.0-synthetic", now);
        var policyId = Guid.NewGuid();
        var channelId = Guid.NewGuid();

        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_KEYS_JSON",
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [keyId] = Convert.ToBase64String(secretKey) }));
        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_ACTIVE_KEY_ID", keyId);
        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_TOKEN_POLICY_ID", policyId.ToString("D"));
        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_TOKEN_POLICY_VERSION", "1");
        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_CHANNEL_ID", channelId.ToString("D"));
        Environment.SetEnvironmentVariable("CENTRAL_ACTIVATION_FIRST_REDEMPTION_TTL_SECONDS", "600");

        try
        {
            await using (var seedScope = _host.Services.CreateAsyncScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<CentralDbContext>();
                db.Customers.Add(customer);
                db.Shops.Add(shop);
                db.PlanCatalogItems.Add(item);
                db.PricingSnapshots.Add(snapshot);
                db.Orders.Add(order);
                db.CommercialLicenses.Add(license);
                db.Installations.Add(installation);
                await db.SaveChangesAsync();
            }

            using var client = await Client("activation.issue");
            client.DefaultRequestHeaders.Add("Origin", "https://localhost");
            var payload = new
            {
                licenseId = license.Id,
                installationId = installation.Id,
                installationPublicKeyFingerprint = installation.PublicKeyFingerprintSha256,
                hardwareFingerprintHash = installation.HardwareFingerprintHash,
                tokenPolicyId = policyId,
                deliveryChannelId = channelId
            };
            var operationId = Guid.NewGuid();
            var request = new
            {
                meta = new
                {
                    clientOperationId = operationId,
                    operationType = "activation.issue",
                    fingerprintVersion = 1,
                    payloadSha256 = new CanonicalJsonSerializer().SerializeAndHash(payload).Sha256Hex,
                    expectedVersion = license.RowVersion,
                    policyVersion = 1,
                    reason = "Synthetic isolated canonical activation integration fixture"
                },
                payload
            };

            using var response = await client.PostAsJsonAsync("/api/admin/v1/activation-tokens", request);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"Canonical activation issue failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(5, result.EnumerateObject().Count());
            Assert.Equal(operationId, result.GetProperty("clientOperationId").GetGuid());
            Assert.Equal("DELIVERY_PENDING", result.GetProperty("state").GetString());
            Assert.False(result.TryGetProperty("tokenSecret", out _));

            var tokenId = result.GetProperty("activationTokenId").GetGuid();
            var deliveryReference = result.GetProperty("deliveryReference").GetString();
            Assert.NotNull(deliveryReference);

            await using (var checkScope = _host.Services.CreateAsyncScope())
            {
                var db = checkScope.ServiceProvider.GetRequiredService<CentralDbContext>();
                var token = await db.ActivationTokens.SingleAsync(x => x.Id == tokenId);
                Assert.Equal(license.Id, token.LicenseId);
                Assert.Equal(shop.Id, token.ShopId);
                Assert.Equal(policyId, token.TokenPolicyId);
                Assert.Equal(channelId, token.DeliveryChannelId);
                Assert.NotNull(token.ProtectedDeliverySecret);
                Assert.DoesNotContain(token.TokenHashSha256, token.ProtectedDeliverySecret, StringComparison.Ordinal);
                var context = $"activation:{token.Id:D}:{shop.Id:D}:{installation.Id:D}:{channelId:D}";
                var protector = checkScope.ServiceProvider.GetRequiredService<IActivationDeliverySecretProtector>();
                var plaintext = protector.Unprotect(token.DeliverySecretKeyId!, token.ProtectedDeliverySecret!, context);
                Assert.Equal(token.TokenHashSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))));
                Assert.DoesNotContain(plaintext, result.ToString(), StringComparison.Ordinal);
                Assert.Equal(1, await db.CentralOperationRecords.CountAsync(x => x.ClientOperationId == operationId));
                Assert.Equal(1, await db.CentralAuditRecords.CountAsync(x => x.CorrelationId == operationId.ToString()));
                Assert.Equal(1, await db.CentralOutboxMessages.CountAsync(x => x.Id == operationId));
            }

            using var replayResponse = await client.PostAsJsonAsync("/api/admin/v1/activation-tokens", request);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            var replay = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(tokenId, replay.GetProperty("activationTokenId").GetGuid());
            Assert.Equal(deliveryReference, replay.GetProperty("deliveryReference").GetString());
            Assert.False(replay.TryGetProperty("tokenSecret", out _));

            var forgedPayload = new
            {
                licenseId = license.Id,
                installationId = installation.Id,
                installationPublicKeyFingerprint = installation.PublicKeyFingerprintSha256,
                hardwareFingerprintHash = new string('b', 64),
                tokenPolicyId = policyId,
                deliveryChannelId = channelId
            };
            var forgedRequest = new
            {
                meta = new
                {
                    clientOperationId = Guid.NewGuid(),
                    operationType = "activation.issue",
                    fingerprintVersion = 1,
                    payloadSha256 = new CanonicalJsonSerializer().SerializeAndHash(forgedPayload).Sha256Hex,
                    expectedVersion = license.RowVersion,
                    policyVersion = 1,
                    reason = "Synthetic forged hardware-binding negative"
                },
                payload = forgedPayload
            };
            using var forgedResponse = await client.PostAsJsonAsync("/api/admin/v1/activation-tokens", forgedRequest);
            Assert.Equal(HttpStatusCode.BadRequest, forgedResponse.StatusCode);

            var forgedKeyPayload = new
            {
                licenseId = license.Id,
                installationId = installation.Id,
                installationPublicKeyFingerprint = new string('c', 64),
                hardwareFingerprintHash = installation.HardwareFingerprintHash,
                tokenPolicyId = policyId,
                deliveryChannelId = channelId
            };
            var forgedKeyRequest = new
            {
                meta = new
                {
                    clientOperationId = Guid.NewGuid(),
                    operationType = "activation.issue",
                    fingerprintVersion = 1,
                    payloadSha256 = new CanonicalJsonSerializer().SerializeAndHash(forgedKeyPayload).Sha256Hex,
                    expectedVersion = license.RowVersion,
                    policyVersion = 1,
                    reason = "Synthetic substituted installation-key negative"
                },
                payload = forgedKeyPayload
            };
            using var forgedKeyResponse = await client.PostAsJsonAsync("/api/admin/v1/activation-tokens", forgedKeyRequest);
            Assert.Equal(HttpStatusCode.BadRequest, forgedKeyResponse.StatusCode);

            var alteredDigestRequest = new
            {
                meta = new
                {
                    clientOperationId = Guid.NewGuid(),
                    operationType = "activation.issue",
                    fingerprintVersion = 1,
                    payloadSha256 = new string('0', 64),
                    expectedVersion = license.RowVersion,
                    policyVersion = 1,
                    reason = "Synthetic canonical payload-digest negative"
                },
                payload
            };
            using var alteredDigestResponse = await client.PostAsJsonAsync("/api/admin/v1/activation-tokens", alteredDigestRequest);
            Assert.Equal(HttpStatusCode.BadRequest, alteredDigestResponse.StatusCode);
            using var digestError = JsonDocument.Parse(await alteredDigestResponse.Content.ReadAsStringAsync());
            Assert.Equal("PAYLOAD_HASH_MISMATCH", digestError.RootElement.GetProperty("code").GetString());
            await using (var noPartialScope = _host.Services.CreateAsyncScope())
            {
                var db = noPartialScope.ServiceProvider.GetRequiredService<CentralDbContext>();
                Assert.Equal(1, await db.ActivationTokens.CountAsync(x => x.LicenseId == license.Id));
                Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == forgedRequest.meta.clientOperationId));
                Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == forgedKeyRequest.meta.clientOperationId));
                Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == alteredDigestRequest.meta.clientOperationId));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretKey);
            foreach (var setting in previous)
            {
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            }
        }
    }
}
