using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Parties;
using System.IO;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[CollectionDefinition("Phase2ApiContractSecurityCollection", DisableParallelization = true)]
public class Phase2ApiContractSecurityCollectionDefinition : ICollectionFixture<Phase2ServerWebApplicationFactory>
{
}

[Collection("Phase2ApiContractSecurityCollection")]
public sealed class Phase2ApiContractAndSecurityTests : IDisposable
{
    private readonly Phase2ServerWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public Phase2ApiContractAndSecurityTests(Phase2ServerWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.Reset();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Reset();
        _client.Dispose();
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string uri, Guid? sessionId = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Terminal-Id", _factory.ActiveTerminalId.ToString());
        request.Headers.Add("X-Terminal-Secret", _factory.ActiveTerminalSecret);
        if (sessionId.HasValue)
        {
            request.Headers.Add("X-Session-Id", sessionId.Value.ToString());
        }
        return request;
    }

    // ------------------------------------------------------------------------
    // 1. System Health & Readiness Probes
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Liveness_HealthEndpoint_Returns_200_OK_Anonymously()
    {
        var response = await _client.GetAsync("/api/system/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_WhenDbHealthy_Returns_200_OK_Anonymously()
    {
        _factory.DatabaseReadiness.IsReady = true;

        var response = await _client.GetAsync("/api/system/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Ready", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("canConnect").GetBoolean());
    }

    [Fact]
    public async Task Readiness_WhenDbUnhealthy_Returns_503_ServiceUnavailable_Anonymously()
    {
        _factory.DatabaseReadiness.IsReady = false;

        var response = await _client.GetAsync("/api/system/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("NotReady", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("canConnect").GetBoolean());
        Assert.Equal("Database connection refused.", body.GetProperty("failureReason").GetString());
    }

    // ------------------------------------------------------------------------
    // 2. Anonymous Request Rejection (401 Unauthorized) across all domains
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/purchasing")]
    [InlineData("/api/sales")]
    [InlineData("/api/customers")]
    [InlineData("/api/suppliers")]
    [InlineData("/api/expenses")]
    [InlineData("/api/warranty/dashboard")]
    [InlineData("/api/thaka/projects")]
    [InlineData("/api/inventory/stock")]
    public async Task ProtectedEndpoints_WithoutSessionHeader_Return_401_Unauthorized(string endpoint)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, endpoint, sessionId: null);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.session_missing", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/sales")]
    [InlineData("/api/reports/snapshot")]
    [InlineData("/api/warranty/dashboard")]
    [InlineData("/api/finance/suppliers/{id}/workspace")]
    [InlineData("/api/suppliers/{id}/workspace")]
    public async Task SensitiveReadEndpoints_RequireTheirViewPermission(string endpoint)
    {
        var sessionId = _factory.SeedValidSession();
        var path = endpoint.Replace("{id}", Guid.NewGuid().ToString("D"), StringComparison.Ordinal);

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, path, sessionId);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("authorization.denied", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProtectedEndpoint_OperationsRecovery_WithoutSessionHeader_Returns_401_Unauthorized()
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/operations/{Guid.NewGuid()}", sessionId: null);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.session_missing", body.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------------
    // 3. Invalid & Revoked Session Rejection (401 Unauthorized)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ProtectedEndpoint_UnknownSession_Returns_401_Unauthorized()
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/customers", sessionId: Guid.NewGuid());

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.session_invalid", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProtectedEndpoint_RevokedSession_Returns_401_Unauthorized()
    {
        var revokedSessionId = _factory.SeedRevokedSession();

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/customers", sessionId: revokedSessionId);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.session_invalid", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProtectedEndpoint_DisabledUser_Returns_401_Unauthorized()
    {
        var disabledUserSessionId = _factory.SeedDisabledUserSession();

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/customers", sessionId: disabledUserSessionId);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.user_disabled", body.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------------
    // 4. Role Deactivation Rejection (403 Forbidden - Finding M-01)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ProtectedEndpoint_DeactivatedRole_Returns_403_Forbidden()
    {
        var deactivatedRoleSessionId = _factory.SeedDeactivatedRoleSession();

        using var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/customers", sessionId: deactivatedRoleSessionId);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.role_disabled", body.GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------------
    // 5. Actor Spoofing Prevention
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ActorSpoofing_CallerProvidedActorId_IsOverriddenBySessionActor()
    {
        var validSessionId = _factory.SeedValidSession();
        var forgedActorId = Guid.NewGuid();

        var requestBody = new
        {
            name = "Test Customer For Spoof Check",
            phone = "03001234567",
            actorId = forgedActorId
        };

        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/customers", sessionId: validSessionId);
        request.Content = JsonContent.Create(requestBody);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify that the customer was created with the session's user ID, NOT the forged ID
        Assert.Equal(_factory.ValidUserId, _factory.AuditWriter.LastRecordedActorId);
    }

    // ------------------------------------------------------------------------
    // 6. Production DI Resolution Verification
    // ------------------------------------------------------------------------

    [Fact]
    public void ProductionDI_Resolves_AllDomainHandlers_And_ReadServices()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // Core Handlers
        Assert.NotNull(sp.GetService<CompleteSaleHandler>());
        Assert.NotNull(sp.GetService<CreatePurchaseHandler>());
        Assert.NotNull(sp.GetService<ReceiveProductIntakeHandler>());
        Assert.NotNull(sp.GetService<CreateWarrantyClaimHandler>());
        Assert.NotNull(sp.GetService<SaveCustomerHandler>());
        Assert.NotNull(sp.GetService<SetCustomerSuspensionHandler>());
        Assert.NotNull(sp.GetService<SetThakaSuspensionHandler>());
        Assert.NotNull(sp.GetService<SaveSupplierHandler>());
        Assert.NotNull(sp.GetService<PostExpenseHandler>());
        Assert.NotNull(sp.GetService<CreateThakaProjectHandler>());
        Assert.NotNull(sp.GetService<CreateProductHandler>());
        Assert.NotNull(sp.GetService<CreateStocktakeHandler>());

        // Read Services
        Assert.NotNull(sp.GetService<ISalesReadService>());
        Assert.NotNull(sp.GetService<IPurchasingReadService>());
        Assert.NotNull(sp.GetService<IPartyDirectoryReadService>());
        Assert.NotNull(sp.GetService<ISupplierAccountReadService>());
        Assert.NotNull(sp.GetService<IExpenseReadService>());
        Assert.NotNull(sp.GetService<IThakaReadService>());
        Assert.NotNull(sp.GetService<IWarrantyReadService>());
        Assert.NotNull(sp.GetService<IReportingReadService>());
        Assert.NotNull(sp.GetService<IInventoryProvenanceReadService>());
        Assert.NotNull(sp.GetService<IProductManagementReadService>());
    }

    [Theory]
    [InlineData("/api/customers/{id}/suspension")]
    [InlineData("/api/thaka/projects/{id}/suspension")]
    public async Task SuspensionEndpoints_RequireSessionAndManagePermission(string endpoint)
    {
        var path = endpoint.Replace("{id}", Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
        using var missingSession = CreateAuthenticatedRequest(HttpMethod.Post, path);
        missingSession.Content = JsonContent.Create(new { ClientOperationId = Guid.NewGuid(), IsSuspended = true, Reason = "test" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(missingSession)).StatusCode);
        _factory.Identity.SetPermissions(_factory.ValidUserId, new HashSet<string>());
        using var denied = CreateAuthenticatedRequest(HttpMethod.Post, path, _factory.SeedValidSession());
        denied.Content = JsonContent.Create(new { ClientOperationId = Guid.NewGuid(), IsSuspended = true, Reason = "test" });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task CustomerSuspensionUsesSessionActor_AndLegacyProfileEditCannotResume()
    {
        var session = _factory.SeedValidSession();
        using var create = CreateAuthenticatedRequest(HttpMethod.Post, "/api/customers", session);
        create.Content = JsonContent.Create(new { Name = "Suspension API customer", IsActive = true, ClientOperationId = Guid.NewGuid() });
        var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = await created.Content.ReadFromJsonAsync<Guid>();
        using var suspend = CreateAuthenticatedRequest(HttpMethod.Post, $"/api/customers/{id}/suspension", session);
        suspend.Content = JsonContent.Create(new { ClientOperationId = Guid.NewGuid(), IsSuspended = true, ActorId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(suspend)).StatusCode);
        Assert.Equal(_factory.ValidUserId, _factory.AuditWriter.LastRecordedActorId);
        using var edit = CreateAuthenticatedRequest(HttpMethod.Put, $"/api/customers/{id}", session);
        edit.Content = JsonContent.Create(new { Name = "Updated customer", IsActive = true, PreserveActivityStatus = false, ClientOperationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(edit)).StatusCode);
        var customer = await _factory.PartiesRepository.GetCustomerAsync(id, default);
        Assert.False(customer!.IsActive);
        Assert.Equal("Updated customer", customer.Name);
    }

    // ------------------------------------------------------------------------
    // 7. Legacy & Canonical Operation Route Security & Parity Tests
    // ------------------------------------------------------------------------

    [Fact]
    public async Task LegacyOperationRoute_RequiresValidTerminal()
    {
        var opId = Guid.NewGuid();

        // 1. Calling legacy route without terminal headers -> 401 auth.terminal_id_missing
        var anonResponse = await _client.GetAsync($"/api/system/operations/{opId}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);
        var anonBody = await anonResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.terminal_id_missing", anonBody.GetProperty("code").GetString());

        // 2. Calling legacy route with valid terminal headers -> 200 OK
        using var validTerminalReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/system/operations/{opId}", sessionId: null);
        var validResponse = await _client.SendAsync(validTerminalReq);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
    }

    [Fact]
    public async Task MissingTerminalCredentials_AreRejected()
    {
        var opId = Guid.NewGuid();

        // 1. Missing X-Terminal-Id completely -> 401 auth.terminal_id_missing
        using (var noTerminalIdReq = new HttpRequestMessage(HttpMethod.Get, $"/api/system/operations/{opId}"))
        {
            noTerminalIdReq.Headers.Add("X-Terminal-Secret", _factory.ActiveTerminalSecret);
            var res = await _client.SendAsync(noTerminalIdReq);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.terminal_id_missing", body.GetProperty("code").GetString());
        }

        // 2. Has X-Terminal-Id but missing X-Terminal-Secret -> 401 auth.terminal_secret_missing
        using (var noSecretReq = new HttpRequestMessage(HttpMethod.Get, $"/api/system/operations/{opId}"))
        {
            noSecretReq.Headers.Add("X-Terminal-Id", _factory.ActiveTerminalId.ToString());
            var res = await _client.SendAsync(noSecretReq);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.terminal_secret_missing", body.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task InvalidTerminalSecret_IsRejected()
    {
        var opId = Guid.NewGuid();

        using var invalidSecretReq = new HttpRequestMessage(HttpMethod.Get, $"/api/system/operations/{opId}");
        invalidSecretReq.Headers.Add("X-Terminal-Id", _factory.ActiveTerminalId.ToString());
        invalidSecretReq.Headers.Add("X-Terminal-Secret", "wrong-secret-token");

        var response = await _client.SendAsync(invalidSecretReq);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.invalid_secret", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RevokedTerminal_IsRejected()
    {
        var opId = Guid.NewGuid();

        // 1. Revoked terminal -> 403 auth.terminal_revoked
        using (var revokedReq = new HttpRequestMessage(HttpMethod.Get, $"/api/system/operations/{opId}"))
        {
            revokedReq.Headers.Add("X-Terminal-Id", _factory.RevokedTerminalId.ToString());
            revokedReq.Headers.Add("X-Terminal-Secret", _factory.ActiveTerminalSecret);

            var response = await _client.SendAsync(revokedReq);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.terminal_revoked", body.GetProperty("code").GetString());
        }

        // 2. Suspended terminal -> 403 auth.terminal_suspended
        using (var suspendedReq = new HttpRequestMessage(HttpMethod.Get, $"/api/system/operations/{opId}"))
        {
            suspendedReq.Headers.Add("X-Terminal-Id", _factory.SuspendedTerminalId.ToString());
            suspendedReq.Headers.Add("X-Terminal-Secret", _factory.ActiveTerminalSecret);

            var response = await _client.SendAsync(suspendedReq);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.terminal_suspended", body.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ValidTerminalWithoutSession_ReturnsOnlyMinimalResult()
    {
        var opId = Guid.NewGuid();

        // Case A: Uncommitted / NotFound operation
        using (var notFoundReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/system/operations/{opId}", sessionId: null))
        {
            var response = await _client.SendAsync(notFoundReq);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal(opId, body.GetProperty("clientOperationId").GetGuid());
            Assert.False(body.GetProperty("found").GetBoolean());
            Assert.False(body.GetProperty("wasCommitted").GetBoolean());
            Assert.Equal("OutcomeUnknown", body.GetProperty("status").GetString());

            // Safe recovery reference only: sensitive fields and references are omitted
            Assert.False(body.TryGetProperty("entityId", out _));
            Assert.False(body.TryGetProperty("documentNumber", out _));
            Assert.False(body.TryGetProperty("payloadFingerprint", out _));
            Assert.False(body.TryGetProperty("userId", out _));
            Assert.False(body.TryGetProperty("actorId", out _));
        }

        // Case B: Committed operation
        var committedOpId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        const string docNumber = "INV-2026-MINIMAL";

        await _factory.OutcomeLedger.RecordSuccessAsync(
            committedOpId,
            "Sale",
            entityId,
            documentNumber: docNumber,
            actorId: _factory.ValidUserId,
            terminalId: _factory.ActiveTerminalId,
            payloadFingerprint: "fingerprint-secret-hash-12345");

        using (var committedReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/system/operations/{committedOpId}", sessionId: null))
        {
            var response = await _client.SendAsync(committedReq);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal(committedOpId, body.GetProperty("clientOperationId").GetGuid());
            Assert.True(body.GetProperty("found").GetBoolean());
            Assert.True(body.GetProperty("wasCommitted").GetBoolean());
            Assert.Equal("Succeeded", body.GetProperty("status").GetString());
            Assert.Equal("Sale", body.GetProperty("operationType").GetString());

            // Safe reference present:
            Assert.Equal(docNumber, body.GetProperty("documentNumber").GetString());
            Assert.Equal(entityId, body.GetProperty("entityId").GetGuid());

            // Sensitive business, payload and user details MUST NOT be exposed:
            Assert.False(body.TryGetProperty("payloadFingerprint", out _));
            Assert.False(body.TryGetProperty("userId", out _));
            Assert.False(body.TryGetProperty("actorId", out _));
            Assert.False(body.TryGetProperty("customer", out _));
            Assert.False(body.TryGetProperty("supplier", out _));
            Assert.False(body.TryGetProperty("total", out _));
        }
    }

    [Fact]
    public async Task AuthenticatedOperationRoute_RequiresSession()
    {
        var opId = Guid.NewGuid();

        // 1. Without session header -> 401 auth.session_missing
        using (var noSessionReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/operations/{opId}", sessionId: null))
        {
            var response = await _client.SendAsync(noSessionReq);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.session_missing", body.GetProperty("code").GetString());
        }

        // 2. With invalid session header -> 401 auth.session_invalid
        using (var invalidSessionReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/operations/{opId}", sessionId: Guid.NewGuid()))
        {
            var response = await _client.SendAsync(invalidSessionReq);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.session_invalid", body.GetProperty("code").GetString());
        }

        // 3. With revoked session -> 401 auth.session_invalid
        var revokedSessionId = _factory.SeedRevokedSession();
        using (var revokedSessionReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/operations/{opId}", sessionId: revokedSessionId))
        {
            var response = await _client.SendAsync(revokedSessionReq);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.session_invalid", body.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task CrossActorOperationLookup_IsForbidden()
    {
        var validSessionId = _factory.SeedValidSession();
        var clientOpId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        // Seed outcome belonging to ValidUserId
        await _factory.OutcomeLedger.RecordSuccessAsync(
            clientOpId,
            "Sale",
            entityId,
            documentNumber: "INV-2026-TRUSTED",
            actorId: _factory.ValidUserId,
            terminalId: _factory.ActiveTerminalId);

        // 1. Same actor queries operation: returns 200 with full outcome details
        using (var req = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/operations/{clientOpId}", validSessionId))
        {
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal(clientOpId, body.GetProperty("clientOperationId").GetGuid());
            Assert.True(body.GetProperty("found").GetBoolean());
            Assert.True(body.GetProperty("wasCommitted").GetBoolean());
            Assert.Equal("INV-2026-TRUSTED", body.GetProperty("documentNumber").GetString());
            Assert.Equal("Succeeded", body.GetProperty("status").GetString());
        }

        // 2. Querying operation belonging to another actor: rejects with 403 authorization.forbidden
        var otherOpId = Guid.NewGuid();
        var otherActorId = Guid.NewGuid();
        await _factory.OutcomeLedger.RecordSuccessAsync(
            otherOpId,
            "Sale",
            Guid.NewGuid(),
            documentNumber: "INV-2026-OTHER",
            actorId: otherActorId,
            terminalId: _factory.ActiveTerminalId);

        using (var crossActorReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/operations/{otherOpId}", validSessionId))
        {
            var res = await _client.SendAsync(crossActorReq);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("authorization.forbidden", body.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task OperationLookup_RejectsCanonicalOutcomeWithoutActorOrTerminalOwner()
    {
        var clientOpId = Guid.NewGuid();
        await _factory.OutcomeLedger.RecordSuccessAsync(
            clientOpId,
            "Sale",
            Guid.NewGuid(),
            documentNumber: "INV-UNSCOPED",
            actorId: null,
            terminalId: null);

        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/system/operations/{clientOpId}",
            sessionId: null);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("authorization.forbidden", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task BothRoutes_UseSameCanonicalOutcomeAuthority()
    {
        var validSessionId = _factory.SeedValidSession();
        var clientOpId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        // Seed outcome in canonical ledger
        await _factory.OutcomeLedger.RecordSuccessAsync(
            clientOpId,
            "Purchase",
            entityId,
            documentNumber: "PO-CANONICAL-01",
            actorId: _factory.ValidUserId,
            terminalId: _factory.ActiveTerminalId);

        // Query canonical route with session
        using var canonicalReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/operations/{clientOpId}", validSessionId);
        var canonicalRes = await _client.SendAsync(canonicalReq);
        Assert.Equal(HttpStatusCode.OK, canonicalRes.StatusCode);
        var canonicalBody = await canonicalRes.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        // Query legacy route with session
        using var legacyReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/system/operations/{clientOpId}", validSessionId);
        var legacyRes = await _client.SendAsync(legacyReq);
        Assert.Equal(HttpStatusCode.OK, legacyRes.StatusCode);
        var legacyBody = await legacyRes.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        // Assert zero contradiction between routes: identical document number, commit state, and status
        Assert.Equal(canonicalBody.GetProperty("documentNumber").GetString(), legacyBody.GetProperty("documentNumber").GetString());
        Assert.Equal(canonicalBody.GetProperty("wasCommitted").GetBoolean(), legacyBody.GetProperty("wasCommitted").GetBoolean());
        Assert.Equal(canonicalBody.GetProperty("status").GetString(), legacyBody.GetProperty("status").GetString());
        Assert.Equal(canonicalBody.GetProperty("operationType").GetString(), legacyBody.GetProperty("operationType").GetString());

        // Query legacy route without session (terminal-only authority): minimal representation matches canonical committed state
        using var terminalOnlyReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/system/operations/{clientOpId}", sessionId: null);
        var terminalOnlyRes = await _client.SendAsync(terminalOnlyReq);
        Assert.Equal(HttpStatusCode.OK, terminalOnlyRes.StatusCode);
        var terminalOnlyBody = await terminalOnlyRes.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(canonicalBody.GetProperty("wasCommitted").GetBoolean(), terminalOnlyBody.GetProperty("wasCommitted").GetBoolean());
        Assert.Equal(canonicalBody.GetProperty("status").GetString(), terminalOnlyBody.GetProperty("status").GetString());
        Assert.Equal(canonicalBody.GetProperty("documentNumber").GetString(), terminalOnlyBody.GetProperty("documentNumber").GetString());
        Assert.Equal(canonicalBody.GetProperty("entityId").GetString(), terminalOnlyBody.GetProperty("entityId").GetString());
    }

    [Fact]
    public async Task UsersManagement_RequiresPermission()
    {
        var sessionId = _factory.SeedValidSession();

        // 1. Session without settings.manage permission -> 403 Forbidden
        using (var req = CreateAuthenticatedRequest(HttpMethod.Get, "/api/users", sessionId))
        {
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("authorization.denied", body.GetProperty("code").GetString());
        }

        // 2. Session with settings.manage permission -> 200 OK
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SettingsManage);
        using (var authorizedReq = CreateAuthenticatedRequest(HttpMethod.Get, "/api/users", sessionId))
        {
            var res = await _client.SendAsync(authorizedReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var users = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.True(users.GetArrayLength() > 0);
        }
    }

    [Fact]
    public async Task RolesManagement_RequiresPermission()
    {
        var sessionId = _factory.SeedValidSession();

        // 1. Session without settings.manage permission -> 403 Forbidden
        using (var req = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/roles/{_factory.ValidRoleId}", sessionId))
        {
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("authorization.denied", body.GetProperty("code").GetString());
        }

        // 2. Session with settings.manage permission -> 200 OK
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SettingsManage);
        using (var authorizedReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/roles/{_factory.ValidRoleId}", sessionId))
        {
            var res = await _client.SendAsync(authorizedReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var role = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("Cashier", role.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task RolesManagement_ListRoles_RequiresPermission()
    {
        var sessionId = _factory.SeedValidSession();

        // 1. Session without settings.manage permission -> 403 Forbidden
        using (var req = CreateAuthenticatedRequest(HttpMethod.Get, "/api/roles", sessionId))
        {
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("authorization.denied", body.GetProperty("code").GetString());
        }

        // 2. Session with settings.manage permission -> 200 OK
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SettingsManage);
        using (var authorizedReq = CreateAuthenticatedRequest(HttpMethod.Get, "/api/roles", sessionId))
        {
            var res = await _client.SendAsync(authorizedReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var roles = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.True(roles.GetArrayLength() > 0);
            Assert.Equal("Cashier", roles[0].GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task RolesManagement_RolePermissions_RequiresPermission()
    {
        var sessionId = _factory.SeedValidSession();

        // 1. Session without settings.manage permission -> 403 Forbidden
        using (var req = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/roles/{_factory.ValidRoleId}/permissions", sessionId))
        {
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("authorization.denied", body.GetProperty("code").GetString());
        }

        // 2. Session with settings.manage permission -> 200 OK
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SettingsManage);
        using (var authorizedReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/roles/{_factory.ValidRoleId}/permissions", sessionId))
        {
            var res = await _client.SendAsync(authorizedReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var result = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal(_factory.ValidRoleId, result.GetProperty("roleId").GetGuid());
            Assert.True(result.GetProperty("permissions").GetArrayLength() > 0);
        }
    }

    [Fact]
    public async Task RolesManagement_AllPermissions_RequiresPermission()
    {
        var sessionId = _factory.SeedValidSession();

        // 1. Session without settings.manage permission -> 403 Forbidden
        using (var req = CreateAuthenticatedRequest(HttpMethod.Get, "/api/roles/permissions", sessionId))
        {
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("authorization.denied", body.GetProperty("code").GetString());
        }

        // 2. Session with settings.manage permission -> 200 OK
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SettingsManage);
        using (var authorizedReq = CreateAuthenticatedRequest(HttpMethod.Get, "/api/roles/permissions", sessionId))
        {
            var res = await _client.SendAsync(authorizedReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var permissions = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.True(permissions.GetArrayLength() > 0);
        }
    }

    [Fact]
    public async Task PermissionManagement_RequiresPermission()
    {
        var sessionId = _factory.SeedValidSession();

        // 1. Session without settings.manage permission -> 403 Forbidden
        using (var req = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/users/{_factory.ValidUserId}/permissions", sessionId))
        {
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("authorization.denied", body.GetProperty("code").GetString());
        }

        // 2. Session with settings.manage permission -> 200 OK
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SettingsManage);
        using (var authorizedReq = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/users/{_factory.ValidUserId}/permissions", sessionId))
        {
            var res = await _client.SendAsync(authorizedReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var result = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal(_factory.ValidUserId, result.GetProperty("userId").GetGuid());
        }
    }

    [Fact]
    public async Task BackupRestore_Authorization_IsCorrect()
    {
        // 1. Verify authorizer validates settings.manage permission correctly
        using var scope = _factory.Services.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IApplicationPermissionAuthorizer>();

        var unauthorizedResult = await authorizer.AuthorizeAsync(_factory.ValidUserId, PermissionKeys.SettingsManage, CancellationToken.None);
        Assert.False(unauthorizedResult.IsSuccess);
        Assert.Equal("authorization.denied", unauthorizedResult.Error?.Code);

        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SettingsManage);
        var authorizedResult = await authorizer.AuthorizeAsync(_factory.ValidUserId, PermissionKeys.SettingsManage, CancellationToken.None);
        Assert.True(authorizedResult.IsSuccess);

        // 2. Verify parity matrix certifies Backup and Restore boundary
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null && !File.Exists(Path.Combine(currentDir.FullName, "docs", "Phase2_Shop_Server_API_Parity_Matrix_2026-09-26.md")))
        {
            currentDir = currentDir.Parent;
        }
        var matrixPath = Path.Combine(currentDir!.FullName, "docs", "Phase2_Shop_Server_API_Parity_Matrix_2026-09-26.md");
        if (File.Exists(matrixPath))
        {
            var content = await File.ReadAllTextAsync(matrixPath);
            Assert.Contains("Backup Creation", content);
            Assert.Contains("Restore Pipeline", content);
            Assert.Contains("settings.manage", content);
        }
    }

    [Fact]
    public void PhysicalPrinting_RemainsDesktopResponsibility()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // Server container must NOT reference or resolve WPF physical printer engines
        // (Windows Print Spooler / System.Printing / WpfPhysicalStickerPrintEngine belongs exclusively to Desktop WPF)
        var wpfEngineType = Type.GetType("EdgeRetails.Desktop.Production.Printing.WpfPhysicalStickerPrintEngine, EdgeRetails.Desktop");
        Assert.Null(wpfEngineType); // Desktop assembly is not referenced by Server

        // Server resolves headless/simulated physical sticker engine, not WPF
        var resolvedStickerEngine = sp.GetService<IPhysicalStickerPrintEngine>();
        Assert.NotNull(resolvedStickerEngine);
        Assert.Equal("SimulatedPhysicalStickerPrintEngine", resolvedStickerEngine!.GetType().Name);
        Assert.DoesNotContain("Wpf", resolvedStickerEngine.GetType().Name);

        // Server must resolve authoritative document configuration and print handlers
        Assert.NotNull(sp.GetService<PrintPhysicalStickersHandler>());
    }

    [Fact]
    public async Task AllCriticalParityAreas_HaveExplicitClassification()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null && !File.Exists(Path.Combine(currentDir.FullName, "docs", "Phase2_Shop_Server_API_Parity_Matrix_2026-09-26.md")))
        {
            currentDir = currentDir.Parent;
        }
        var matrixPath = Path.Combine(currentDir!.FullName, "docs", "Phase2_Shop_Server_API_Parity_Matrix_2026-09-26.md");

        Assert.True(File.Exists(matrixPath), $"Matrix file not found at: {matrixPath}");
        var text = await File.ReadAllTextAsync(matrixPath);

        string[] criticalAreas =
        [
            "Users",
            "Roles",
            "Permission",
            "Dashboard",
            "Backup",
            "Printing",
            "Operations",
            "System",
            "Terminals",
            "Auth",
            "Purchasing",
            "Sales",
            "Warranty",
            "Finance",
            "Catalog",
            "Inventory",
            "Customers",
            "Suppliers",
            "Expenses",
            "Thaka",
            "Reports"
        ];

        foreach (var area in criticalAreas)
        {
            Assert.Contains(area, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("`MISSING`", text);
        Assert.DoesNotContain("| MISSING |", text);

        Assert.Contains("`API_COMPLETE`", text);
        Assert.Contains("`COMPLETE`", text);
        Assert.Contains("`SERVER_API_COMPLETE`", text);
        Assert.Contains("`NOT_PART_OF_CURRENT_V1`", text);
        Assert.Contains("`LOCAL_INFRASTRUCTURE_BY_DESIGN`", text);
        Assert.Contains("`DESKTOP_WPF_EXCLUSIVE_BY_DESIGN`", text);
        Assert.Contains("`PASS`", text);
        Assert.Contains("`BLOCKED_ENVIRONMENT`", text);
    }

    // ========================================================================
    // B03: Manual Cash HTTP / Auth Transport Proof (Section 32)
    // ========================================================================

    [Fact]
    public async Task B03_ManualCash_WithoutSessionHeader_Returns_401_Unauthorized()
    {
        var body = new
        {
            movementType = "ManualCashIn",
            direction = "In",
            amount = 50.00m,
            reason = "Change float"
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", sessionId: null);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.session_missing", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task B03_ManualCash_RevokedOrDisabledSession_Returns_Unauthorized_Or_Forbidden()
    {
        var body = new
        {
            movementType = "ManualCashIn",
            direction = "In",
            amount = 50.00m,
            reason = "Change float"
        };

        // 1. Revoked session -> 401 auth.session_invalid
        var revoked = _factory.SeedRevokedSession();
        using (var req = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", revoked))
        {
            req.Content = JsonContent.Create(body);
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.session_invalid", json.GetProperty("code").GetString());
        }

        // 2. Disabled user -> 401 auth.user_disabled
        var disabled = _factory.SeedDisabledUserSession();
        using (var req = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", disabled))
        {
            req.Content = JsonContent.Create(body);
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.user_disabled", json.GetProperty("code").GetString());
        }

        // 3. Deactivated role -> 403 auth.role_disabled
        var inactiveRole = _factory.SeedDeactivatedRoleSession();
        using (var req = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", inactiveRole))
        {
            req.Content = JsonContent.Create(body);
            var res = await _client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("auth.role_disabled", json.GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("SaleCashIn", "In")]
    [InlineData("SaleRefundCashOut", "Out")]
    [InlineData("PurchaseCashOut", "Out")]
    [InlineData("PurchaseVoidCashIn", "In")]
    [InlineData("PurchaseReturnCashIn", "In")]
    [InlineData("SupplierPaymentCashOut", "Out")]
    [InlineData("SupplierRefundCashIn", "In")]
    [InlineData("ExpenseCashOut", "Out")]
    [InlineData("ManualCashIn", "Out")] // Inverted direction
    [InlineData("ManualCashOut", "In")] // Inverted direction
    public async Task B03_ManualCash_CommercialEventTypes_And_MismatchedDirections_AreRejected_With_400(string movementType, string direction)
    {
        var session = _factory.SeedValidSession();
        _factory.SeedOpenCashSession();

        var body = new
        {
            movementType,
            direction,
            amount = 100.00m,
            reason = "Attempted invalid movement"
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", session);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("cash.manual_type_required", json.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(0, "Float", "cash.amount_positive")]
    [InlineData(-10.50, "Float", "cash.amount_positive")]
    [InlineData(50.00, "", "cash.manual_reason_required")]
    [InlineData(50.00, "   ", "cash.manual_reason_required")]
    public async Task B03_ManualCash_Validation_RejectsInvalidAmounts_And_EmptyReason(decimal amount, string reason, string expectedCode)
    {
        var session = _factory.SeedValidSession();
        _factory.SeedOpenCashSession();

        var body = new
        {
            movementType = "ManualCashIn",
            direction = "In",
            amount,
            reason
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", session);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(expectedCode, json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task B03_ManualCash_WhenNoOpenCashSession_RejectsWith_400_BadRequest()
    {
        var session = _factory.SeedValidSession();
        // Do NOT seed open cash drawer session

        var body = new
        {
            movementType = "ManualCashIn",
            direction = "In",
            amount = 50.00m,
            reason = "Change float"
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", session);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("cash.session_required", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task B03_ManualCash_Authorized_ManualCashIn_And_CashOut_Succeed_And_Persist_CanonicalMovement()
    {
        var session = _factory.SeedValidSession();
        var cashSessionId = _factory.SeedOpenCashSession();

        // 1. Manual Cash In
        var cashInBody = new
        {
            movementType = "ManualCashIn",
            direction = "In",
            amount = 75.50m,
            reason = "Drawer change float",
            note = "Morning shift"
        };
        using (var inReq = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", session))
        {
            inReq.Content = JsonContent.Create(cashInBody);
            var inRes = await _client.SendAsync(inReq);
            Assert.Equal(HttpStatusCode.OK, inRes.StatusCode);
            var inMovementId = await inRes.Content.ReadFromJsonAsync<Guid>();
            Assert.NotEqual(Guid.Empty, inMovementId);

            var movements = _factory.CashRepository.GetAllMovements();
            var movement = Assert.Single(movements);
            Assert.Equal(inMovementId, movement.Id);
            Assert.Equal(cashSessionId, movement.CashSessionId);
            Assert.Equal(CashMovementType.ManualCashIn, movement.MovementType);
            Assert.Equal(CashMovementDirection.In, movement.Direction);
            Assert.Equal(75.50m, movement.Amount);
            Assert.Equal("Drawer change float", movement.Reason);
            Assert.Equal("Morning shift", movement.Note);
            Assert.Equal(_factory.ValidUserId, movement.ActorId);
            Assert.Equal(_factory.ValidUserId, _factory.AuditWriter.LastRecordedActorId);
        }

        // 2. Manual Cash Out
        var cashOutBody = new
        {
            movementType = "ManualCashOut",
            direction = "Out",
            amount = 25.00m,
            reason = "Midday safe drop",
            note = "Envelope #12"
        };
        using (var outReq = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", session))
        {
            outReq.Content = JsonContent.Create(cashOutBody);
            var outRes = await _client.SendAsync(outReq);
            Assert.Equal(HttpStatusCode.OK, outRes.StatusCode);
            var outMovementId = await outRes.Content.ReadFromJsonAsync<Guid>();
            Assert.NotEqual(Guid.Empty, outMovementId);

            var all = _factory.CashRepository.GetAllMovements();
            Assert.Equal(2, all.Count);
            var outMovement = Assert.Single(all, m => m.Id == outMovementId);
            Assert.Equal(outMovementId, outMovement.Id);
            Assert.Equal(CashMovementType.ManualCashOut, outMovement.MovementType);
            Assert.Equal(CashMovementDirection.Out, outMovement.Direction);
            Assert.Equal(25.00m, outMovement.Amount);
            Assert.Equal("Midday safe drop", outMovement.Reason);
            Assert.Equal(_factory.ValidUserId, outMovement.ActorId);
            Assert.Equal(_factory.ValidUserId, _factory.AuditWriter.LastRecordedActorId);
        }
    }

    [Fact]
    public async Task B03_ManualCash_ActorSpoofing_CallerProvidedActorId_IsOverriddenBySessionActor()
    {
        var session = _factory.SeedValidSession();
        _factory.SeedOpenCashSession();
        var spoofedActorId = Guid.NewGuid();

        var body = new
        {
            movementType = "ManualCashIn",
            direction = "In",
            amount = 40.00m,
            actorId = spoofedActorId,
            reason = "Change replenishment"
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/cash-movement", session);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var movement = Assert.Single(_factory.CashRepository.GetAllMovements());
        Assert.Equal(_factory.ValidUserId, movement.ActorId);
        Assert.NotEqual(spoofedActorId, movement.ActorId);
        Assert.Equal(_factory.ValidUserId, _factory.AuditWriter.LastRecordedActorId);
    }

    // ========================================================================
    // C29: Supplier Opening Balance HTTP / Auth Transport Proof (Section 33)
    // ========================================================================

    [Fact]
    public async Task C29_SupplierOpeningBalance_WithoutSessionHeader_Returns_401_Unauthorized()
    {
        var body = new
        {
            supplierId = Guid.NewGuid(),
            direction = "IncreasePayable",
            amount = 100.00m,
            effectiveAt = DateTimeOffset.UtcNow,
            reason = "Opening balance",
            clientOperationId = Guid.NewGuid()
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", sessionId: null);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.session_missing", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task C29_SupplierOpeningBalance_WithoutAdjustPermission_Returns_403_Forbidden()
    {
        var session = _factory.SeedValidSession();
        // ValidUserId has Sales/Purchasing permissions by default, but NOT PermissionKeys.SupplierAccountAdjust
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Supplier C29 Auth Test", IsActive = true };
        _factory.PartiesRepository.AddSupplier(supplier);

        var body = new
        {
            supplierId = supplier.Id,
            direction = "IncreasePayable",
            amount = 100.00m,
            effectiveAt = DateTimeOffset.UtcNow,
            reason = "Opening AP balance",
            clientOperationId = Guid.NewGuid()
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", session);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("authorization.denied", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task C29_SupplierOpeningBalance_WithAdjustPermission_Authorized_Succeeds()
    {
        var session = _factory.SeedValidSession();
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SupplierAccountAdjust);

        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Supplier C29 Success", IsActive = true };
        _factory.PartiesRepository.AddSupplier(supplier);
        var opId = Guid.NewGuid();
        var effective = DateTimeOffset.UtcNow.AddDays(-10);

        var body = new
        {
            supplierId = supplier.Id,
            direction = "IncreasePayable",
            amount = 250.75m,
            effectiveAt = effective,
            reason = "Cutover verified AP ledger balance",
            cutoverReference = "CUTOVER-2026-001",
            clientOperationId = opId
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", session);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<SupplierOpeningBalanceResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.EntryId);
        Assert.False(result.WasExisting);

        // Verify append-only entry state in repository
        var entry = Assert.Single(_factory.SupplierAccountRepository.GetAllEntries());
        Assert.Equal(result.EntryId, entry.Id);
        Assert.Equal(supplier.Id, entry.SupplierId);
        Assert.Equal(SupplierAccountEntryType.OpeningBalance, entry.EntryType);
        Assert.Equal(SupplierAccountDirection.IncreasePayable, entry.Direction);
        Assert.Equal(250.75m, entry.Amount);
        Assert.Equal("SupplierOpeningBalance", entry.ReferenceType);
        Assert.Equal(opId, entry.ReferenceId);
        Assert.Equal(_factory.ValidUserId, entry.ActorId);
        Assert.Equal(_factory.ValidUserId, _factory.AuditWriter.LastRecordedActorId);

        // Verify running balance derives correctly
        var balance = await _factory.SupplierAccountRepository.GetCurrentBalanceAsync(supplier.Id, default);
        Assert.Equal(250.75m, balance);

        // Verify NOT represented as fake Purchase, fake Payment, or Cash movement
        Assert.Empty(_factory.SupplierAccountRepository.GetAllPayments());
        Assert.Empty(_factory.SupplierAccountRepository.GetAllRefunds());
        Assert.Empty(_factory.CashRepository.GetAllMovements());
    }

    [Fact]
    public async Task C29_SupplierOpeningBalance_ReplaySafe_And_AppendOnly_Idempotent()
    {
        var session = _factory.SeedValidSession();
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SupplierAccountAdjust);

        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Supplier C29 Replay", IsActive = true };
        _factory.PartiesRepository.AddSupplier(supplier);
        var opId = Guid.NewGuid();
        var effective = DateTimeOffset.UtcNow;

        var body = new
        {
            supplierId = supplier.Id,
            direction = "DecreasePayable",
            amount = 120.00m,
            effectiveAt = effective,
            reason = "Pre-cutover advance adjustment",
            clientOperationId = opId
        };

        // 1. Initial invocation
        using (var req1 = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", session))
        {
            req1.Content = JsonContent.Create(body);
            var res1 = await _client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
            var resDto1 = await res1.Content.ReadFromJsonAsync<SupplierOpeningBalanceResult>(JsonOptions);
            Assert.False(resDto1!.WasExisting);

            // 2. Exact Replay
            using (var req2 = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", session))
            {
                req2.Content = JsonContent.Create(body);
                var res2 = await _client.SendAsync(req2);
                Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
                var resDto2 = await res2.Content.ReadFromJsonAsync<SupplierOpeningBalanceResult>(JsonOptions);
                Assert.True(resDto2!.WasExisting);
                Assert.Equal(resDto1.EntryId, resDto2.EntryId);
            }
        }

        // Verify exactly one entry exists (did not duplicate)
        var entries = _factory.SupplierAccountRepository.GetAllEntries();
        Assert.Single(entries);

        // Running balance for DecreasePayable is -120.00m
        var balance = await _factory.SupplierAccountRepository.GetCurrentBalanceAsync(supplier.Id, default);
        Assert.Equal(-120.00m, balance);
    }

    [Fact]
    public async Task C29_SupplierOpeningBalance_PayloadMismatch_Returns_409_Conflict()
    {
        var session = _factory.SeedValidSession();
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SupplierAccountAdjust);

        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Supplier C29 Conflict", IsActive = true };
        _factory.PartiesRepository.AddSupplier(supplier);
        var opId = Guid.NewGuid();

        var body1 = new
        {
            supplierId = supplier.Id,
            direction = "IncreasePayable",
            amount = 100.00m,
            effectiveAt = DateTimeOffset.UtcNow,
            reason = "Original opening balance",
            clientOperationId = opId
        };
        using (var req1 = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", session))
        {
            req1.Content = JsonContent.Create(body1);
            var res1 = await _client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        }

        // Contender with different amount under same ClientOperationId
        var body2 = new
        {
            supplierId = supplier.Id,
            direction = "IncreasePayable",
            amount = 200.00m,
            effectiveAt = DateTimeOffset.UtcNow,
            reason = "Original opening balance",
            clientOperationId = opId
        };
        using (var req2 = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", session))
        {
            req2.Content = JsonContent.Create(body2);
            var res2 = await _client.SendAsync(req2);
            Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);

            var json = await res2.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.Equal("idempotency.payload_mismatch", json.GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData(0, "Opening", "supplier.opening_invalid")]
    [InlineData(-50, "Opening", "supplier.opening_invalid")]
    [InlineData(100, "", "supplier.opening_invalid")]
    [InlineData(100, "   ", "supplier.opening_invalid")]
    public async Task C29_SupplierOpeningBalance_Validation_RejectsInvalidPayloads(decimal amount, string reason, string expectedCode)
    {
        var session = _factory.SeedValidSession();
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SupplierAccountAdjust);

        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Supplier Validations", IsActive = true };
        _factory.PartiesRepository.AddSupplier(supplier);

        var body = new
        {
            supplierId = supplier.Id,
            direction = "IncreasePayable",
            amount,
            effectiveAt = DateTimeOffset.UtcNow,
            reason,
            clientOperationId = Guid.NewGuid()
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", session);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(expectedCode, json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task C29_SupplierOpeningBalance_WhenSupplierDoesNotExist_Returns_404_NotFound()
    {
        var session = _factory.SeedValidSession();
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SupplierAccountAdjust);

        var nonExistentSupplierId = Guid.NewGuid();
        var body = new
        {
            supplierId = nonExistentSupplierId,
            direction = "IncreasePayable",
            amount = 100.00m,
            effectiveAt = DateTimeOffset.UtcNow,
            reason = "Opening for unknown supplier",
            clientOperationId = Guid.NewGuid()
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", session);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("supplier.not_found", json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task C29_SupplierOpeningBalance_ActorSpoofing_CallerProvidedActorId_IsOverriddenBySessionActor()
    {
        var session = _factory.SeedValidSession();
        _factory.GrantPermission(_factory.ValidUserId, PermissionKeys.SupplierAccountAdjust);

        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Supplier Spoof Check", IsActive = true };
        _factory.PartiesRepository.AddSupplier(supplier);
        var spoofedActorId = Guid.NewGuid();

        var body = new
        {
            supplierId = supplier.Id,
            direction = "IncreasePayable",
            amount = 100.00m,
            effectiveAt = DateTimeOffset.UtcNow,
            reason = "Anti-spoof check",
            actorId = spoofedActorId,
            clientOperationId = Guid.NewGuid()
        };
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/finance/supplier-opening-balance", session);
        request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = Assert.Single(_factory.SupplierAccountRepository.GetAllEntries());
        Assert.Equal(_factory.ValidUserId, entry.ActorId);
        Assert.NotEqual(spoofedActorId, entry.ActorId);
        Assert.Equal(_factory.ValidUserId, _factory.AuditWriter.LastRecordedActorId);
    }
}

// ============================================================================
// Phase 2 Test Factory & Test Doubles
// ============================================================================

public sealed class Phase2ServerWebApplicationFactory : WebApplicationFactory<Program>
{
    public Guid ActiveTerminalId { get; } = Guid.NewGuid();
    public string ActiveTerminalSecret { get; } = "phase2-test-secret";
    public Guid RevokedTerminalId { get; } = Guid.NewGuid();
    public Guid SuspendedTerminalId { get; } = Guid.NewGuid();
    public Guid ValidUserId { get; } = Guid.NewGuid();
    public Guid ValidRoleId { get; } = Guid.NewGuid();

    public InMemoryOperationOutcomeLedger OutcomeLedger { get; } = new();
    public TestDatabaseReadinessService DatabaseReadiness { get; } = new();
    public TestIdentitySessionRepository Sessions { get; } = new();
    public TestIdentityReadRepository Identity { get; } = new();
    public TestPartyRepository PartiesRepository { get; } = new();
    public TestBusinessAuditWriter AuditWriter { get; } = new();
    public TestCashRepository CashRepository { get; } = new();
    public TestSupplierAccountRepository SupplierAccountRepository { get; } = new();
    public TestDocumentNumberService DocumentNumbers { get; } = new();

    public void GrantPermission(Guid userId, string permission) =>
        Identity.GrantPermission(userId, permission);

    public void Reset()
    {
        DatabaseReadiness.IsReady = true;
        Sessions.Clear();
        Identity.Clear();
        PartiesRepository.Clear();
        AuditWriter.Clear();
        CashRepository.Clear();
        SupplierAccountRepository.Clear();

        // Seed default active role and user
        Identity.SeedRole(new Role
        {
            Id = ValidRoleId,
            Name = "Cashier",
            IsActive = true
        });

        Identity.SeedUser(new User
        {
            Id = ValidUserId,
            RoleId = ValidRoleId,
            DisplayName = "Valid Test User",
            Status = UserStatus.Active
        });

        Identity.SetPermissions(ValidUserId, new HashSet<string>
        {
            PermissionKeys.SalesCreate,
            PermissionKeys.SalesPosUse,
            PermissionKeys.CustomersManage,
            PermissionKeys.SuppliersManage,
            PermissionKeys.ExpensesManage,
            PermissionKeys.ThakaManage,
            PermissionKeys.InventoryManage
        });
    }

    public Guid SeedValidSession()
    {
        var sessionId = Guid.NewGuid();
        Sessions.SeedSession(new UserSession
        {
            Id = sessionId,
            UserId = ValidUserId,
            StartedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        });
        return sessionId;
    }

    public Guid SeedRevokedSession()
    {
        var sessionId = Guid.NewGuid();
        Sessions.SeedSession(new UserSession
        {
            Id = sessionId,
            UserId = ValidUserId,
            StartedAt = DateTimeOffset.UtcNow,
            IsRevoked = true
        });
        return sessionId;
    }

    public Guid SeedDisabledUserSession()
    {
        var disabledUserId = Guid.NewGuid();
        Identity.SeedUser(new User
        {
            Id = disabledUserId,
            RoleId = ValidRoleId,
            DisplayName = "Disabled User",
            Status = UserStatus.Disabled
        });

        var sessionId = Guid.NewGuid();
        Sessions.SeedSession(new UserSession
        {
            Id = sessionId,
            UserId = disabledUserId,
            StartedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        });
        return sessionId;
    }

    public Guid SeedDeactivatedRoleSession()
    {
        var inactiveRoleId = Guid.NewGuid();
        Identity.SeedRole(new Role
        {
            Id = inactiveRoleId,
            Name = "InactiveRole",
            IsActive = false
        });

        var userWithInactiveRoleId = Guid.NewGuid();
        Identity.SeedUser(new User
        {
            Id = userWithInactiveRoleId,
            RoleId = inactiveRoleId,
            DisplayName = "User With Inactive Role",
            Status = UserStatus.Active
        });

        var sessionId = Guid.NewGuid();
        Sessions.SeedSession(new UserSession
        {
            Id = sessionId,
            UserId = userWithInactiveRoleId,
            StartedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        });
        return sessionId;
    }

    public Guid SeedOpenCashSession()
    {
        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            OpenedBy = ValidUserId,
            OpenedAt = DateTimeOffset.UtcNow,
            OpeningCash = 1000m,
            Status = CashSessionStatus.Open
        };
        CashRepository.SeedSession(session);
        return session.Id;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=127.0.0.1;Port=5432;Database=edge_retails_test;Username=test");

        builder.ConfigureServices(services =>
        {
            // This API factory owns all its authorities; never open workstation sequence state.
            var authorityRoot = Directory.CreateTempSubdirectory("EdgeRetailsApiSequenceFixture_").FullName;
            var manifest = Path.Combine(authorityRoot, "highwater.manifest");
            var custody = new OwnedSequenceAuthorityCustody(authorityRoot);
            MachineSequenceHighWaterService.InitializeOwnedFixture(manifest, custody);
            services.RemoveAll<ISequenceHighWaterService>();
            services.AddSingleton<ISequenceHighWaterService>(new MachineSequenceHighWaterService(manifest, custody));
            // Seed active terminal with hashed secret
            var terminalRepo = new InMemoryTerminalRepository();
            var terminal = new Terminal
            {
                Id = ActiveTerminalId,
                TerminalCode = "TERM-TEST-01",
                Status = TerminalStatus.Active,
                AuthSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ActiveTerminalSecret)))
            };
            terminalRepo.Seed(terminal);

            var revokedTerminal = new Terminal
            {
                Id = RevokedTerminalId,
                TerminalCode = "TERM-TEST-REVOKED",
                Status = TerminalStatus.Revoked,
                AuthSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ActiveTerminalSecret)))
            };
            terminalRepo.Seed(revokedTerminal);

            var suspendedTerminal = new Terminal
            {
                Id = SuspendedTerminalId,
                TerminalCode = "TERM-TEST-SUSPENDED",
                Status = TerminalStatus.Suspended,
                AuthSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ActiveTerminalSecret)))
            };
            terminalRepo.Seed(suspendedTerminal);

            services.RemoveAll<ITerminalRepository>();
            services.AddSingleton<ITerminalRepository>(terminalRepo);

            // Replace outcome ledger with shared test double
            services.RemoveAll<IOperationOutcomeLedger>();
            services.AddSingleton<IOperationOutcomeLedger>(OutcomeLedger);

            // Replace session & identity repositories
            services.RemoveAll<IIdentitySessionRepository>();
            services.AddSingleton<IIdentitySessionRepository>(Sessions);

            services.RemoveAll<IIdentityReadRepository>();
            services.AddSingleton<IIdentityReadRepository>(Identity);

            // Replace party repository
            services.RemoveAll<IPartyRepository>();
            services.AddSingleton<IPartyRepository>(PartiesRepository);

            // Replace database readiness service
            services.RemoveAll<IDatabaseReadinessService>();
            services.AddSingleton<IDatabaseReadinessService>(DatabaseReadiness);

            // Replace cash repository
            services.RemoveAll<ICashRepository>();
            services.AddSingleton<ICashRepository>(CashRepository);

            // Replace supplier account repository
            services.RemoveAll<ISupplierAccountRepository>();
            services.AddSingleton<ISupplierAccountRepository>(SupplierAccountRepository);

            // Replace document number service
            services.RemoveAll<IDocumentNumberService>();
            services.AddSingleton<IDocumentNumberService>(DocumentNumbers);

            // Replace transaction runner, locks, UoW
            services.RemoveAll<ITransactionRunner>();
            services.AddSingleton<ITransactionRunner, NoOpTransactionRunner>();

            services.RemoveAll<IResourceLock>();
            services.AddSingleton<IResourceLock, NoOpResourceLock>();

            services.RemoveAll<IOperationLock>();
            services.AddSingleton<IOperationLock, NoOpOperationLock>();

            services.RemoveAll<IUnitOfWork>();
            services.AddSingleton<IUnitOfWork, NoOpUnitOfWork>();

            // Business audit writer stub to verify actor attribution
            services.RemoveAll<IBusinessAuditWriter>();
            services.AddSingleton<IBusinessAuditWriter>(AuditWriter);

            // Party directory read service stub
            services.RemoveAll<IPartyDirectoryReadService>();
            services.AddSingleton<IPartyDirectoryReadService, StubPartyDirectoryReadService>();
        });
    }
}

public sealed class TestDatabaseReadinessService : IDatabaseReadinessService
{
    public bool IsReady { get; set; } = true;

    public Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var result = new DatabaseReadinessResult(
            CanConnect: IsReady,
            HasPendingMigrations: false,
            PendingMigrations: Array.Empty<string>(),
            FailureReason: IsReady ? null : "Database connection refused.");
        return Task.FromResult(result);
    }
}

public sealed class TestIdentitySessionRepository : IIdentitySessionRepository
{
    private readonly ConcurrentDictionary<Guid, UserSession> _sessions = new();

    public void SeedSession(UserSession session) => _sessions[session.Id] = session;
    public void Clear() => _sessions.Clear();

    public void AddSession(UserSession session)
    {
        _sessions[session.Id] = session;
    }

    public Task<UserSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task<UserSession?> GetSessionForUpdateAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }
}

public sealed class TestIdentityReadRepository : IIdentityReadRepository
{
    private readonly ConcurrentDictionary<Guid, User> _users = new();
    private readonly ConcurrentDictionary<Guid, Role> _roles = new();
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _permissions = new();

    public void SeedUser(User user) => _users[user.Id] = user;
    public void SeedRole(Role role) => _roles[role.Id] = role;
    public void SetPermissions(Guid userId, HashSet<string> permissions) => _permissions[userId] = permissions;
    public void GrantPermission(Guid userId, string permission)
    {
        _permissions.AddOrUpdate(
            userId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { permission },
            (_, existing) =>
            {
                existing.Add(permission);
                return existing;
            });
    }
    public void Clear()
    {
        _users.Clear();
        _roles.Clear();
        _permissions.Clear();
    }

    public Task<User?> GetUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _users.TryGetValue(id, out var user);
        return Task.FromResult(user);
    }

    public Task<Role?> GetRoleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _roles.TryGetValue(id, out var role);
        return Task.FromResult(role);
    }

    public Task<IReadOnlyList<User>> GetActiveUsersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<User>>(_users.Values.Where(u => u.Status == UserStatus.Active).ToList());

    public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Role>>(_roles.Values.OrderBy(r => r.Name).ToList());

    public Task<IReadOnlyList<string>> GetRolePermissionKeysAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(
            _roles.ContainsKey(roleId) ? new List<string> { PermissionKeys.SalesCreate, PermissionKeys.SalesPosUse } : new List<string>());
    }

    public Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<Permission>
        {
            new() { Id = Guid.NewGuid(), Key = PermissionKeys.SalesCreate, Description = PermissionKeys.SalesCreate, IsActive = true },
            new() { Id = Guid.NewGuid(), Key = PermissionKeys.SettingsManage, Description = PermissionKeys.SettingsManage, IsActive = true }
        };
        return Task.FromResult<IReadOnlyList<Permission>>(list);
    }

    public Task<IReadOnlySet<string>> GetEffectivePermissionKeysAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _permissions.TryGetValue(userId, out var perms);
        return Task.FromResult<IReadOnlySet<string>>(perms ?? new HashSet<string>());
    }
}

public sealed class TestPartyRepository : IPartyRepository
{
    private readonly ConcurrentDictionary<Guid, Customer> _customers = new();
    private readonly ConcurrentDictionary<Guid, Supplier> _suppliers = new();

    public void Clear()
    {
        _customers.Clear();
        _suppliers.Clear();
    }

    public Task<Customer?> GetCustomerAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _customers.TryGetValue(id, out var c);
        return Task.FromResult(c);
    }

    public Task<Customer?> GetCustomerForUpdateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _customers.TryGetValue(id, out var c);
        return Task.FromResult(c);
    }

    public Task<IReadOnlyList<Customer>> GetCustomersAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Customer>>(_customers.Values.ToList());

    public void AddCustomer(Customer customer)
    {
        _customers[customer.Id] = customer;
    }

    public Task<Supplier?> GetSupplierAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _suppliers.TryGetValue(id, out var s);
        return Task.FromResult(s);
    }

    public Task<Supplier?> GetSupplierForUpdateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _suppliers.TryGetValue(id, out var s);
        return Task.FromResult(s);
    }

    public Task<IReadOnlyList<Supplier>> GetSuppliersAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Supplier>>(_suppliers.Values.ToList());

    public void AddSupplier(Supplier supplier)
    {
        _suppliers[supplier.Id] = supplier;
    }
}

public sealed class StubPartyDirectoryReadService : IPartyDirectoryReadService
{
    public Task<IReadOnlyList<CustomerDirectoryDto>> GetCustomersAsync(string? search, int pageSize = 100, CancellationToken cancellationToken = default, string? beforeName = null, Guid? beforeCustomerId = null, bool includeInactive = false) =>
        Task.FromResult<IReadOnlyList<CustomerDirectoryDto>>([]);

    public Task<IReadOnlyList<SupplierDirectoryDto>> GetSuppliersAsync(string? search, int pageSize = 100, CancellationToken cancellationToken = default, string? beforeName = null, Guid? beforeSupplierId = null) =>
        Task.FromResult<IReadOnlyList<SupplierDirectoryDto>>([]);
}

public sealed class NoOpOperationLock : IOperationLock
{
    public Task AcquireAsync(Guid clientOperationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
}

public sealed class TestBusinessAuditWriter : IBusinessAuditWriter
{
    public Guid LastRecordedActorId { get; private set; }

    public void Record(string action, string entityType, Guid? entityId, Guid actorId, Guid correlationId, string? summary = null)
    {
        LastRecordedActorId = actorId;
    }

    public void Clear() => LastRecordedActorId = Guid.Empty;
}

// ----------------------------------------------------------------------------
// Test Doubles for Finance & Cash Repositories
// ----------------------------------------------------------------------------

public sealed class TestCashRepository : ICashRepository
{
    private readonly ConcurrentDictionary<Guid, CashSession> _sessions = new();
    private readonly ConcurrentBag<CashMovement> _movements = new();

    public void SeedSession(CashSession session) => _sessions[session.Id] = session;
    public void Clear()
    {
        _sessions.Clear();
        _movements.Clear();
    }

    public Task<CashSession?> GetOpenSessionForUpdateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.Values.FirstOrDefault(s => s.Status == CashSessionStatus.Open));

    public Task<CashSession?> GetSessionForUpdateAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.TryGetValue(sessionId, out var s) ? s : null);

    public Task<IReadOnlyList<CashMovement>> GetMovementsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CashMovement>>(_movements.Where(m => m.CashSessionId == sessionId).ToList());

    public void AddSession(CashSession session) => _sessions[session.Id] = session;
    public void AddMovement(CashMovement movement) => _movements.Add(movement);
    public IReadOnlyList<CashMovement> GetAllMovements() => _movements.ToList();
}

public sealed class TestSupplierAccountRepository : ISupplierAccountRepository
{
    private readonly ConcurrentBag<SupplierAccountEntry> _entries = new();
    private readonly ConcurrentDictionary<Guid, SupplierPayment> _payments = new();
    private readonly ConcurrentBag<SupplierPaymentReversal> _paymentReversals = new();
    private readonly ConcurrentDictionary<Guid, SupplierRefund> _refunds = new();
    private readonly ConcurrentBag<SupplierRefundReversal> _refundReversals = new();

    public void Clear()
    {
        _entries.Clear();
        _payments.Clear();
        _paymentReversals.Clear();
        _refunds.Clear();
        _refundReversals.Clear();
    }

    public Task<decimal> GetCurrentBalanceAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        var entries = _entries.Where(e => e.SupplierId == supplierId);
        var balance = entries.Sum(e =>
            e.Direction == SupplierAccountDirection.IncreasePayable
                ? e.Amount
                : -e.Amount);
        return Task.FromResult(balance);
    }

    public Task<SupplierPayment?> GetPaymentByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(_payments.Values.FirstOrDefault(p => p.ClientOperationId == clientOperationId));

    public Task<SupplierPayment?> GetPaymentForUpdateAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(_payments.TryGetValue(paymentId, out var p) ? p : null);

    public Task<SupplierPaymentReversal?> GetPaymentReversalByPaymentAsync(Guid paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(_paymentReversals.FirstOrDefault(r => r.SupplierPaymentId == paymentId));

    public Task<SupplierRefund?> GetRefundByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        Task.FromResult(_refunds.Values.FirstOrDefault(r => r.ClientOperationId == clientOperationId));

    public Task<SupplierRefund?> GetRefundForUpdateAsync(Guid refundId, CancellationToken cancellationToken) =>
        Task.FromResult(_refunds.TryGetValue(refundId, out var r) ? r : null);

    public Task<SupplierRefundReversal?> GetRefundReversalByRefundAsync(Guid refundId, CancellationToken cancellationToken) =>
        Task.FromResult(_refundReversals.FirstOrDefault(r => r.SupplierRefundId == refundId));

    public Task<bool> HasSourceEntryAsync(SupplierAccountEntryType entryType, string referenceType, Guid referenceId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.Any(e => e.EntryType == entryType && e.ReferenceType == referenceType && e.ReferenceId == referenceId));

    public void AddEntry(SupplierAccountEntry entry) => _entries.Add(entry);
    public void AddPayment(SupplierPayment payment) => _payments[payment.Id] = payment;
    public void AddPaymentReversal(SupplierPaymentReversal reversal) => _paymentReversals.Add(reversal);
    public void AddRefund(SupplierRefund refund) => _refunds[refund.Id] = refund;
    public void AddRefundReversal(SupplierRefundReversal reversal) => _refundReversals.Add(reversal);

    public IReadOnlyList<SupplierAccountEntry> GetAllEntries() => _entries.ToList();
    public IReadOnlyList<SupplierPayment> GetAllPayments() => _payments.Values.ToList();
    public IReadOnlyList<SupplierRefund> GetAllRefunds() => _refunds.Values.ToList();
}

public sealed class TestDocumentNumberService : IDocumentNumberService
{
    private long _counter;
    public Task<string> NextAsync(string series, CancellationToken cancellationToken = default)
    {
        var next = Interlocked.Increment(ref _counter);
        return Task.FromResult($"{series}-{next:D6}");
    }
}
