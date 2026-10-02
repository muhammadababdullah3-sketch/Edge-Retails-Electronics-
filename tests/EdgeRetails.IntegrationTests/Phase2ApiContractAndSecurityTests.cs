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
using EdgeRetails.Domain.Parties;
using System.IO;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.SystemConfiguration;
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

    public void GrantPermission(Guid userId, string permission) =>
        Identity.GrantPermission(userId, permission);

    public void Reset()
    {
        DatabaseReadiness.IsReady = true;
        Sessions.Clear();
        Identity.Clear();
        PartiesRepository.Clear();
        AuditWriter.Clear();

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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=127.0.0.1;Port=5432;Database=edge_retails_test;Username=test");

        builder.ConfigureServices(services =>
        {
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
    public Task<IReadOnlyList<CustomerDirectoryDto>> GetCustomersAsync(string? search, int pageSize = 100, CancellationToken cancellationToken = default, string? beforeName = null, Guid? beforeCustomerId = null) =>
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
