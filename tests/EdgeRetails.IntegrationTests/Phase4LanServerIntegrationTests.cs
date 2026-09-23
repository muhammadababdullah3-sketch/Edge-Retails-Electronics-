using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[CollectionDefinition("Phase4LanServerCollection", DisableParallelization = true)]
public class Phase4LanServerCollectionDefinition : ICollectionFixture<CustomLanServerWebApplicationFactory>
{
}

[Collection("Phase4LanServerCollection")]
public sealed class Phase4LanServerIntegrationTests : IDisposable
{
    private readonly CustomLanServerWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public Phase4LanServerIntegrationTests(CustomLanServerWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.Reset();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EDGE_RETAILS_MAINTENANCE_MODE", null);
        _factory.Reset();
        _client.Dispose();
    }

    // ------------------------------------------------------------------------
    // 1. System Health & Version Endpoints (200 OK, anonymous)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Health_Endpoint_Returns_200_OK_And_CurrentProtocolVersion()
    {
        var response = await _client.GetAsync("/api/system/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadFromJsonAsync<HealthResponsePayload>(JsonOptions);
        Assert.NotNull(content);
        Assert.Equal("Healthy", content.Status);
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, content.ProtocolVersion);
        Assert.True(content.Timestamp <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Version_Endpoint_Returns_200_OK_And_VersionInfo()
    {
        var response = await _client.GetAsync("/api/system/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadFromJsonAsync<VersionResponsePayload>(JsonOptions);
        Assert.NotNull(content);
        Assert.Equal("EdgeRetails.Server", content.Server);
        Assert.Equal("1.0.0", content.Version);
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, content.ProtocolVersion);
        Assert.Equal(TerminalProtocol.MinimumSupportedProtocolVersion, content.MinSupportedProtocolVersion);
    }

    // ------------------------------------------------------------------------
    // 2. Missing Terminal Header Rejection (401 Unauthorized)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task AuthenticatedEndpoint_MissingTerminalHeader_Returns_401_Unauthorized()
    {
        var command = new TerminalHeartbeatCommand(Guid.NewGuid(), TerminalProtocol.CurrentProtocolVersion, "127.0.0.1");

        // Calling heartbeat without X-Terminal-Id
        var response = await _client.PostAsJsonAsync("/api/terminals/heartbeat", command, JsonOptions);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("auth.terminal_id_missing", error.Code);
    }

    [Fact]
    public async Task AuthenticatedEndpoint_InvalidGuidTerminalHeader_Returns_401_Unauthorized()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/terminals/heartbeat")
        {
            Content = JsonContent.Create(new TerminalHeartbeatCommand(Guid.NewGuid(), TerminalProtocol.CurrentProtocolVersion, "127.0.0.1"), options: JsonOptions)
        };
        request.Headers.Add("X-Terminal-Id", "not-a-valid-guid");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("auth.terminal_id_missing", error.Code);
    }

    [Fact]
    public async Task AuthenticatedMutation_SalesComplete_MissingTerminalHeader_Returns_401_Unauthorized()
    {
        // Calling sales endpoint without X-Terminal-Id
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sales/complete")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("auth.terminal_id_missing", error.Code);
    }

    // ------------------------------------------------------------------------
    // 3. Unknown Terminal ID Rejection (401 Unauthorized)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task AuthenticatedEndpoint_UnknownTerminalId_Returns_401_Unauthorized()
    {
        var unknownId = Guid.NewGuid();
        var command = new TerminalHeartbeatCommand(unknownId, TerminalProtocol.CurrentProtocolVersion, "127.0.0.1");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/terminals/heartbeat")
        {
            Content = JsonContent.Create(command, options: JsonOptions)
        };
        request.Headers.Add("X-Terminal-Id", unknownId.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("auth.terminal_unknown", error.Code);
        Assert.Contains(unknownId.ToString(), error.Message);
    }

    // ------------------------------------------------------------------------
    // 4. Protocol Version Negotiation (Compatible accepted, Incompatible rejected with 400)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ProtocolVersion_IncompatibleVersion_Returns_400_BadRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/health");
        request.Headers.Add("X-Protocol-Version", "999.0.0");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("protocol.incompatible", error.Code);
        Assert.Contains("999.0.0", error.Message);
    }

    [Fact]
    public async Task ProtocolVersion_CompatibleVersion_Accepted_Returns_200_OK()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/health");
        request.Headers.Add("X-Protocol-Version", TerminalProtocol.CurrentProtocolVersion);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------------
    // 5. Suspended Terminal Rejection (403 Forbidden)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task AuthenticatedEndpoint_SuspendedTerminal_Returns_403_Forbidden()
    {
        var suspendedTerminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "SUSP-01",
            Name = "Suspended Register",
            Status = TerminalStatus.Suspended,
            ProtocolVersion = TerminalProtocol.CurrentProtocolVersion,
            RegisteredAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        _factory.TerminalRepository.Seed(suspendedTerminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/terminals/heartbeat")
        {
            Content = JsonContent.Create(new TerminalHeartbeatCommand(suspendedTerminal.Id, TerminalProtocol.CurrentProtocolVersion, "10.0.0.5"), options: JsonOptions)
        };
        request.Headers.Add("X-Terminal-Id", suspendedTerminal.Id.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("auth.terminal_suspended", error.Code);
    }

    // ------------------------------------------------------------------------
    // 6. Revoked Terminal Rejection (403 Forbidden)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task AuthenticatedEndpoint_RevokedTerminal_Returns_403_Forbidden()
    {
        var revokedTerminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "REVK-01",
            Name = "Revoked Register",
            Status = TerminalStatus.Revoked,
            ProtocolVersion = TerminalProtocol.CurrentProtocolVersion,
            RegisteredAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        _factory.TerminalRepository.Seed(revokedTerminal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/terminals/heartbeat")
        {
            Content = JsonContent.Create(new TerminalHeartbeatCommand(revokedTerminal.Id, TerminalProtocol.CurrentProtocolVersion, "10.0.0.6"), options: JsonOptions)
        };
        request.Headers.Add("X-Terminal-Id", revokedTerminal.Id.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("auth.terminal_revoked", error.Code);
    }

    // ------------------------------------------------------------------------
    // 7. Maintenance Mode Guard (503 Service Unavailable on POST mutations)
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public async Task MaintenanceMode_PostMutations_Return_503_ServiceUnavailable(string envValue)
    {
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_MAINTENANCE_MODE", envValue);

            var regCommand = new RegisterTerminalCommand(
                TerminalCode: "TERM-MAINT-1",
                Name: "Maint POS",
                HardwareFingerprint: "HW-M1",
                ProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
                LastKnownIpAddress: "127.0.0.1",
                ClientAuthSecret: null);

            var response = await _client.PostAsJsonAsync("/api/terminals/register", regCommand, JsonOptions);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
            Assert.NotNull(error);
            Assert.Equal("system.maintenance_mode", error.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_MAINTENANCE_MODE", null);
        }
    }

    [Fact]
    public async Task MaintenanceMode_Allows_HealthCheck_With_200_OK()
    {
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_MAINTENANCE_MODE", "1");

            var response = await _client.GetAsync("/api/system/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_MAINTENANCE_MODE", null);
        }
    }

    // ------------------------------------------------------------------------
    // 8. Terminal Registration Endpoint (/api/terminals/register)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task RegisterTerminal_Anonymous_ValidCommand_Returns_200_OK_And_RegistersTerminal()
    {
        var command = new RegisterTerminalCommand(
            TerminalCode: "TERM-REG-01",
            Name: "Checkout Counter 1",
            HardwareFingerprint: "FP-100293",
            ProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
            LastKnownIpAddress: "192.168.1.101",
            ClientAuthSecret: "my_secret_token_123");

        var response = await _client.PostAsJsonAsync("/api/terminals/register", command, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<RegisterTerminalResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.TerminalId);
        Assert.Equal("TERM-REG-01", result.TerminalCode);
        Assert.Equal("Checkout Counter 1", result.Name);
        Assert.Equal(TerminalStatus.Active, result.Status);
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, result.ServerProtocolVersion);

        // Verify terminal was persisted in repository
        var saved = await _factory.TerminalRepository.GetByIdAsync(result.TerminalId);
        Assert.NotNull(saved);
        Assert.Equal("TERM-REG-01", saved.TerminalCode);
        Assert.True(saved.VerifySecret("my_secret_token_123"));
        Assert.False(saved.VerifySecret("wrong_secret"));
    }

    [Fact]
    public async Task RegisterTerminal_MissingCode_Returns_400_BadRequest()
    {
        var command = new RegisterTerminalCommand(
            TerminalCode: "",
            Name: "Checkout Counter",
            HardwareFingerprint: null,
            ProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
            LastKnownIpAddress: null,
            ClientAuthSecret: null);

        var response = await _client.PostAsJsonAsync("/api/terminals/register", command, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("terminals.code_required", error.Code);
    }

    [Fact]
    public async Task RegisterTerminal_MissingName_Returns_400_BadRequest()
    {
        var command = new RegisterTerminalCommand(
            TerminalCode: "TERM-02",
            Name: "",
            HardwareFingerprint: null,
            ProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
            LastKnownIpAddress: null,
            ClientAuthSecret: null);

        var response = await _client.PostAsJsonAsync("/api/terminals/register", command, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("terminals.name_required", error.Code);
    }

    [Fact]
    public async Task RegisterTerminal_IncompatibleProtocol_Returns_400_BadRequest()
    {
        var command = new RegisterTerminalCommand(
            TerminalCode: "TERM-03",
            Name: "Checkout Counter 3",
            HardwareFingerprint: null,
            ProtocolVersion: "99.0.0",
            LastKnownIpAddress: null,
            ClientAuthSecret: null);

        var response = await _client.PostAsJsonAsync("/api/terminals/register", command, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("terminals.protocol_incompatible", error.Code);
    }

    [Fact]
    public async Task RegisterTerminal_ExistingTerminal_Updates_LastSeen_And_Returns_200_OK()
    {
        var initialCommand = new RegisterTerminalCommand(
            TerminalCode: "TERM-RECONNECT",
            Name: "Station Reconnect",
            HardwareFingerprint: "FP-ORIG",
            ProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
            LastKnownIpAddress: "192.168.1.50",
            ClientAuthSecret: "reconnect-secret");

        var initialResponse = await _client.PostAsJsonAsync("/api/terminals/register", initialCommand, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initialResult = await initialResponse.Content.ReadFromJsonAsync<RegisterTerminalResult>(JsonOptions);
        Assert.NotNull(initialResult);

        // Reconnect with new IP
        var reconnectCommand = new RegisterTerminalCommand(
            TerminalCode: "TERM-RECONNECT",
            Name: "Station Reconnect",
            HardwareFingerprint: "FP-NEW",
            ProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
            LastKnownIpAddress: "192.168.1.99",
            ClientAuthSecret: "reconnect-secret");

        var reconnectResponse = await _client.PostAsJsonAsync("/api/terminals/register", reconnectCommand, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, reconnectResponse.StatusCode);
        var reconnectResult = await reconnectResponse.Content.ReadFromJsonAsync<RegisterTerminalResult>(JsonOptions);
        Assert.NotNull(reconnectResult);
        Assert.Equal(initialResult.TerminalId, reconnectResult.TerminalId);

        var updated = await _factory.TerminalRepository.GetByIdAsync(initialResult.TerminalId);
        Assert.NotNull(updated);
        Assert.Equal("192.168.1.99", updated.LastKnownIpAddress);
        Assert.Equal("FP-NEW", updated.HardwareFingerprint);
    }

    // ------------------------------------------------------------------------
    // 9. Terminal Heartbeat Endpoint (/api/terminals/heartbeat)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task TerminalHeartbeat_ActiveTerminal_Returns_200_OK_And_Updates_Heartbeat()
    {
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "HB-01",
            Name = "Heartbeat Terminal",
            Status = TerminalStatus.Active,
            ProtocolVersion = TerminalProtocol.CurrentProtocolVersion,
            RegisteredAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            AuthSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("heartbeat-secret")))
        };
        _factory.TerminalRepository.Seed(terminal);

        var command = new TerminalHeartbeatCommand(
            TerminalId: terminal.Id,
            ProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
            CurrentIpAddress: "192.168.1.77");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/terminals/heartbeat")
        {
            Content = JsonContent.Create(command, options: JsonOptions)
        };
        request.Headers.Add("X-Terminal-Id", terminal.Id.ToString());
        request.Headers.Add("X-Terminal-Secret", "heartbeat-secret");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<TerminalHeartbeatResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(terminal.Id, result.TerminalId);
        Assert.Equal(TerminalStatus.Active, result.Status);
        Assert.True(result.CanMutate);
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, result.ServerProtocolVersion);

        // Verify last seen and IP were updated in repository
        var updated = await _factory.TerminalRepository.GetByIdAsync(terminal.Id);
        Assert.NotNull(updated);
        Assert.Equal("192.168.1.77", updated.LastKnownIpAddress);
    }

    [Fact]
    public async Task TerminalHeartbeat_With_Missing_Secret_Returns_401_Unauthorized()
    {
        const string secret = "missing-secret-test";
        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "HB-SEC-MISSING",
            Name = "Missing Secret Terminal",
            Status = TerminalStatus.Active,
            AuthSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            ProtocolVersion = TerminalProtocol.CurrentProtocolVersion
        };
        _factory.TerminalRepository.Seed(terminal);

        var command = new TerminalHeartbeatCommand(terminal.Id, TerminalProtocol.CurrentProtocolVersion, "192.168.1.82");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/terminals/heartbeat")
        {
            Content = JsonContent.Create(command, options: JsonOptions)
        };
        request.Headers.Add("X-Terminal-Id", terminal.Id.ToString());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("auth.terminal_secret_missing", error.Code);
    }

    [Fact]
    public async Task TerminalHeartbeat_With_Invalid_Secret_Returns_401_Unauthorized()
    {
        var secret = "super-secret-terminal-key";
        var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "HB-SEC-01",
            Name = "Secured Heartbeat Terminal",
            Status = TerminalStatus.Active,
            AuthSecretHash = secretHash,
            ProtocolVersion = TerminalProtocol.CurrentProtocolVersion
        };
        _factory.TerminalRepository.Seed(terminal);

        var command = new TerminalHeartbeatCommand(
            TerminalId: terminal.Id,
            ProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
            CurrentIpAddress: "192.168.1.80");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/terminals/heartbeat")
        {
            Content = JsonContent.Create(command, options: JsonOptions)
        };
        request.Headers.Add("X-Terminal-Id", terminal.Id.ToString());
        request.Headers.Add("X-Terminal-Secret", "wrong-secret-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("auth.invalid_secret", error.Code);
    }

    [Fact]
    public async Task TerminalHeartbeat_With_Valid_Secret_Returns_200_OK()
    {
        var secret = "super-secret-terminal-key";
        var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

        var terminal = new Terminal
        {
            Id = Guid.NewGuid(),
            TerminalCode = "HB-SEC-02",
            Name = "Secured Heartbeat Terminal 2",
            Status = TerminalStatus.Active,
            AuthSecretHash = secretHash,
            ProtocolVersion = TerminalProtocol.CurrentProtocolVersion
        };
        _factory.TerminalRepository.Seed(terminal);

        var command = new TerminalHeartbeatCommand(
            TerminalId: terminal.Id,
            ProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
            CurrentIpAddress: "192.168.1.81");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/terminals/heartbeat")
        {
            Content = JsonContent.Create(command, options: JsonOptions)
        };
        request.Headers.Add("X-Terminal-Id", terminal.Id.ToString());
        request.Headers.Add("X-Terminal-Secret", secret);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<TerminalHeartbeatResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(terminal.Id, result.TerminalId);
        Assert.True(result.CanMutate);
    }

    // ------------------------------------------------------------------------
    // 10. Operation Query Endpoint (/api/system/operations/{id})
    // ------------------------------------------------------------------------

    [Fact]
    public async Task OperationStatus_Anonymous_UnknownOperation_Returns_200_OK_With_Found_False()
    {
        var clientOpId = Guid.NewGuid();

        // Anonymous query without X-Terminal-Id header
        var response = await _client.GetAsync($"/api/system/operations/{clientOpId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<OperationStatusResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(clientOpId, result.ClientOperationId);
        Assert.False(result.Found);
        Assert.False(result.WasCommitted);
        Assert.Null(result.OperationType);
        Assert.Null(result.EntityId);
        Assert.Null(result.DocumentNumber);
    }

    [Fact]
    public async Task OperationStatus_CommittedSale_Returns_200_OK_With_Sale_Details()
    {
        var clientOpId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var invoiceNumber = "INV-TEST-2026-8801";

        var sale = new Sale
        {
            Id = saleId,
            ClientOperationId = clientOpId,
            InvoiceNumber = invoiceNumber,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            Subtotal = 1000m,
            GrandTotal = 1000m,
            CompletedAt = DateTimeOffset.UtcNow
        };
        _factory.SalesRepository.SeedSale(sale);

        var response = await _client.GetAsync($"/api/system/operations/{clientOpId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<OperationStatusResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(clientOpId, result.ClientOperationId);
        Assert.True(result.Found);
        Assert.True(result.WasCommitted);
        Assert.Equal("Sale", result.OperationType);
        Assert.Equal(saleId, result.EntityId);
        Assert.Equal(invoiceNumber, result.DocumentNumber);
    }

    [Fact]
    public async Task OperationStatus_CommittedPurchase_Returns_200_OK_With_Purchase_Details()
    {
        var clientOpId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var purchaseNumber = "PUR-TEST-2026-5501";

        var purchase = new Purchase
        {
            Id = purchaseId,
            ClientOperationId = clientOpId,
            PurchaseNumber = purchaseNumber,
            Status = PurchaseStatus.Completed,
            GrandTotal = 5000m,
            PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTimeOffset.UtcNow
        };
        _factory.PurchasingRepository.SeedPurchase(purchase);

        var response = await _client.GetAsync($"/api/system/operations/{clientOpId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<OperationStatusResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(clientOpId, result.ClientOperationId);
        Assert.True(result.Found);
        Assert.True(result.WasCommitted);
        Assert.Equal("Purchase", result.OperationType);
        Assert.Equal(purchaseId, result.EntityId);
        Assert.Equal(purchaseNumber, result.DocumentNumber);
    }

    [Fact]
    public async Task OperationStatus_CommittedSupplierPayment_Returns_200_OK_With_Payment_Details()
    {
        var clientOpId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var paymentNumber = "SPAY-TEST-2026-3301";

        var payment = new SupplierPayment
        {
            Id = paymentId,
            ClientOperationId = clientOpId,
            PaymentNumber = paymentNumber,
            Amount = 1500m,
            PaidAt = DateTimeOffset.UtcNow
        };
        _factory.SupplierAccountRepository.SeedPayment(payment);

        var response = await _client.GetAsync($"/api/system/operations/{clientOpId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<OperationStatusResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(clientOpId, result.ClientOperationId);
        Assert.True(result.Found);
        Assert.True(result.WasCommitted);
        Assert.Equal("SupplierPayment", result.OperationType);
        Assert.Equal(paymentId, result.EntityId);
        Assert.Equal(paymentNumber, result.DocumentNumber);
    }

    [Fact]
    public async Task OperationStatus_EmptyGuid_Returns_400_BadRequest()
    {
        var response = await _client.GetAsync($"/api/system/operations/{Guid.Empty}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiErrorPayload>(JsonOptions);
        Assert.NotNull(error);
        Assert.Equal("validation.client_operation_id_required", error.Code);
    }

    // ------------------------------------------------------------------------
    // Helper DTOs for deserialization
    // ------------------------------------------------------------------------

    private sealed record HealthResponsePayload(string Status, DateTimeOffset Timestamp, string ProtocolVersion);
    private sealed record VersionResponsePayload(string Server, string Version, string ProtocolVersion, string MinSupportedProtocolVersion);
    private sealed record ApiErrorPayload(string? Code, string? Message);
}

// ============================================================================
// Custom WebApplicationFactory & In-Memory Test Doubles
// ============================================================================

public sealed class CustomLanServerWebApplicationFactory : WebApplicationFactory<Program>
{
    public InMemoryTerminalRepository TerminalRepository { get; } = new();
    public InMemorySalesRepository SalesRepository { get; } = new();
    public InMemoryPurchasingRepository PurchasingRepository { get; } = new();
    public InMemorySupplierAccountRepository SupplierAccountRepository { get; } = new();

    public void Reset()
    {
        TerminalRepository.Clear();
        SalesRepository.Clear();
        PurchasingRepository.Clear();
        SupplierAccountRepository.Clear();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace terminal repository
            services.RemoveAll<ITerminalRepository>();
            services.AddSingleton<ITerminalRepository>(TerminalRepository);

            // Replace sales repository
            services.RemoveAll<ISalesRepository>();
            services.AddSingleton<ISalesRepository>(SalesRepository);

            // Replace purchasing repository
            services.RemoveAll<IPurchasingRepository>();
            services.AddSingleton<IPurchasingRepository>(PurchasingRepository);

            // Replace supplier accounts repository
            services.RemoveAll<ISupplierAccountRepository>();
            services.AddSingleton<ISupplierAccountRepository>(SupplierAccountRepository);

            // Replace transaction runner & resource locks so tests run hermetically without requiring active DB
            services.RemoveAll<ITransactionRunner>();
            services.AddSingleton<ITransactionRunner, NoOpTransactionRunner>();

            services.RemoveAll<IResourceLock>();
            services.AddSingleton<IResourceLock, NoOpResourceLock>();
        });
    }
}

public sealed class NoOpTransactionRunner : ITransactionRunner
{
    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        return operation(cancellationToken);
    }
}

public sealed class NoOpResourceLock : IResourceLock
{
    public Task AcquireAsync(string resourceType, Guid resourceId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task AcquireAsync(string resourceType, string resourceKey, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

public sealed class InMemoryTerminalRepository : ITerminalRepository
{
    private readonly ConcurrentDictionary<Guid, Terminal> _terminals = new();

    public Task<Terminal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _terminals.TryGetValue(id, out var terminal);
        return Task.FromResult(terminal);
    }

    public Task<Terminal?> GetByCodeAsync(string terminalCode, CancellationToken cancellationToken = default)
    {
        var terminal = _terminals.Values.FirstOrDefault(t =>
            string.Equals(t.TerminalCode, terminalCode, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(terminal);
    }

    public Task<IReadOnlyList<Terminal>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Terminal> list = _terminals.Values.ToList();
        return Task.FromResult(list);
    }

    public Task<int> GetActiveCountAsync(CancellationToken cancellationToken = default)
    {
        var count = _terminals.Values.Count(t => t.Status == TerminalStatus.Active);
        return Task.FromResult(count);
    }

    public Task AddAsync(Terminal terminal, CancellationToken cancellationToken = default)
    {
        _terminals[terminal.Id] = terminal;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Terminal terminal, CancellationToken cancellationToken = default)
    {
        _terminals[terminal.Id] = terminal;
        return Task.CompletedTask;
    }

    public void Seed(Terminal terminal)
    {
        _terminals[terminal.Id] = terminal;
    }

    public void Clear()
    {
        _terminals.Clear();
    }
}

public sealed class InMemorySalesRepository : ISalesRepository
{
    private readonly ConcurrentDictionary<Guid, Sale> _salesByOpId = new();
    private readonly ConcurrentDictionary<Guid, SaleReturn> _returnsByOpId = new();

    public void SeedSale(Sale sale) => _salesByOpId[sale.ClientOperationId] = sale;
    public void SeedReturn(SaleReturn saleReturn) => _returnsByOpId[saleReturn.ClientOperationId] = saleReturn;
    public void Clear()
    {
        _salesByOpId.Clear();
        _returnsByOpId.Clear();
    }

    public Task<Sale?> GetSaleByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken)
    {
        _salesByOpId.TryGetValue(clientOperationId, out var sale);
        return Task.FromResult(sale);
    }

    public Task<SaleReturn?> GetReturnByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken)
    {
        _returnsByOpId.TryGetValue(clientOperationId, out var saleReturn);
        return Task.FromResult(saleReturn);
    }

    public Task<Sale?> GetSaleForUpdateAsync(Guid saleId, CancellationToken cancellationToken) => Task.FromResult<Sale?>(null);
    public Task<IReadOnlyList<SaleItem>> GetSaleItemsAsync(Guid saleId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SaleItem>>([]);
    public Task<SaleItem?> GetSaleItemForUpdateAsync(Guid saleItemId, CancellationToken cancellationToken) => Task.FromResult<SaleItem?>(null);
    public Task<IReadOnlyList<SaleItemUnit>> GetSaleItemUnitsAsync(Guid saleItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SaleItemUnit>>([]);
    public Task<SalePayment?> GetSalePaymentAsync(Guid saleId, CancellationToken cancellationToken) => Task.FromResult<SalePayment?>(null);
    public Task<decimal> GetReturnedBaseQuantityAsync(Guid saleItemId, CancellationToken cancellationToken) => Task.FromResult(0m);
    public Task<decimal> GetRefundedAmountAsync(Guid saleItemId, CancellationToken cancellationToken) => Task.FromResult(0m);
    public Task<decimal> GetReturnedOriginalCostAmountAsync(Guid saleItemId, CancellationToken cancellationToken) => Task.FromResult(0m);
    public Task<IReadOnlySet<Guid>> GetReturnedInventoryUnitIdsAsync(Guid saleItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    public void AddSale(Sale sale) => _salesByOpId[sale.ClientOperationId] = sale;
    public void AddSaleItem(SaleItem item) { }
    public void AddSalePayment(SalePayment payment) { }
    public void AddSaleItemUnit(SaleItemUnit itemUnit) { }
    public void AddReturn(SaleReturn saleReturn) => _returnsByOpId[saleReturn.ClientOperationId] = saleReturn;
    public void AddReturnItem(SaleReturnItem item) { }
    public void AddReturnItemUnit(SaleReturnItemUnit itemUnit) { }
}

public sealed class InMemoryPurchasingRepository : IPurchasingRepository
{
    private readonly ConcurrentDictionary<Guid, Purchase> _purchasesByOpId = new();
    private readonly ConcurrentDictionary<Guid, PurchaseReturn> _returnsByOpId = new();
    private readonly ConcurrentDictionary<Guid, PurchaseVoid> _voidsByOpId = new();

    public void SeedPurchase(Purchase purchase) => _purchasesByOpId[purchase.ClientOperationId] = purchase;
    public void SeedReturn(PurchaseReturn purchaseReturn) => _returnsByOpId[purchaseReturn.ClientOperationId] = purchaseReturn;
    public void SeedVoid(PurchaseVoid purchaseVoid) => _voidsByOpId[purchaseVoid.ClientOperationId] = purchaseVoid;
    public void Clear()
    {
        _purchasesByOpId.Clear();
        _returnsByOpId.Clear();
        _voidsByOpId.Clear();
    }

    public Task<Purchase?> GetPurchaseByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken)
    {
        _purchasesByOpId.TryGetValue(clientOperationId, out var purchase);
        return Task.FromResult(purchase);
    }

    public Task<PurchaseReturn?> GetReturnByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken)
    {
        _returnsByOpId.TryGetValue(clientOperationId, out var r);
        return Task.FromResult(r);
    }

    public Task<PurchaseVoid?> GetVoidByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken)
    {
        _voidsByOpId.TryGetValue(clientOperationId, out var v);
        return Task.FromResult(v);
    }

    public Task<Purchase?> GetPurchaseForUpdateAsync(Guid purchaseId, CancellationToken cancellationToken) => Task.FromResult<Purchase?>(null);
    public Task<Purchase?> GetPurchaseBySupplierInvoiceAsync(Guid supplierId, string normalizedSupplierInvoiceNumber, CancellationToken cancellationToken) => Task.FromResult<Purchase?>(null);
    public Task<IReadOnlyList<PurchaseItem>> GetPurchaseItemsAsync(Guid purchaseId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PurchaseItem>>([]);
    public Task<PurchaseItem?> GetPurchaseItemForUpdateAsync(Guid purchaseItemId, CancellationToken cancellationToken) => Task.FromResult<PurchaseItem?>(null);
    public Task<IReadOnlyList<PurchaseItemUnit>> GetPurchaseItemUnitsAsync(Guid purchaseItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PurchaseItemUnit>>([]);
    public Task<decimal> GetReturnedBaseQuantityAsync(Guid purchaseItemId, CancellationToken cancellationToken) => Task.FromResult(0m);
    public Task<bool> HasCompletedReturnAsync(Guid purchaseId, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> HasVoidAsync(Guid purchaseId, CancellationToken cancellationToken) => Task.FromResult(false);
    public void AddPurchase(Purchase purchase) => _purchasesByOpId[purchase.ClientOperationId] = purchase;
    public void AddPurchaseItem(PurchaseItem item) { }
    public void AddPurchaseItemUnit(PurchaseItemUnit itemUnit) { }
    public void AddReturn(PurchaseReturn purchaseReturn) => _returnsByOpId[purchaseReturn.ClientOperationId] = purchaseReturn;
    public void AddReturnItem(PurchaseReturnItem item) { }
    public void AddReturnItemUnit(PurchaseReturnItemUnit itemUnit) { }
    public void AddVoid(PurchaseVoid purchaseVoid) => _voidsByOpId[purchaseVoid.ClientOperationId] = purchaseVoid;
}

public sealed class InMemorySupplierAccountRepository : ISupplierAccountRepository
{
    private readonly ConcurrentDictionary<Guid, SupplierPayment> _paymentsByOpId = new();
    private readonly ConcurrentDictionary<Guid, SupplierRefund> _refundsByOpId = new();

    public void SeedPayment(SupplierPayment payment) => _paymentsByOpId[payment.ClientOperationId] = payment;
    public void SeedRefund(SupplierRefund refund) => _refundsByOpId[refund.ClientOperationId] = refund;
    public void Clear()
    {
        _paymentsByOpId.Clear();
        _refundsByOpId.Clear();
    }

    public Task<SupplierPayment?> GetPaymentByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken)
    {
        _paymentsByOpId.TryGetValue(clientOperationId, out var payment);
        return Task.FromResult(payment);
    }

    public Task<SupplierRefund?> GetRefundByClientOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken)
    {
        _refundsByOpId.TryGetValue(clientOperationId, out var refund);
        return Task.FromResult(refund);
    }

    public Task<decimal> GetCurrentBalanceAsync(Guid supplierId, CancellationToken cancellationToken) => Task.FromResult(0m);
    public Task<SupplierPayment?> GetPaymentForUpdateAsync(Guid paymentId, CancellationToken cancellationToken) => Task.FromResult<SupplierPayment?>(null);
    public Task<SupplierPaymentReversal?> GetPaymentReversalByPaymentAsync(Guid paymentId, CancellationToken cancellationToken) => Task.FromResult<SupplierPaymentReversal?>(null);
    public Task<SupplierRefund?> GetRefundForUpdateAsync(Guid refundId, CancellationToken cancellationToken) => Task.FromResult<SupplierRefund?>(null);
    public Task<SupplierRefundReversal?> GetRefundReversalByRefundAsync(Guid refundId, CancellationToken cancellationToken) => Task.FromResult<SupplierRefundReversal?>(null);
    public Task<bool> HasSourceEntryAsync(SupplierAccountEntryType entryType, string referenceType, Guid referenceId, CancellationToken cancellationToken) => Task.FromResult(false);
    public void AddEntry(SupplierAccountEntry entry) { }
    public void AddPayment(SupplierPayment payment) => _paymentsByOpId[payment.ClientOperationId] = payment;
    public void AddPaymentReversal(SupplierPaymentReversal reversal) { }
    public void AddRefund(SupplierRefund refund) => _refundsByOpId[refund.ClientOperationId] = refund;
    public void AddRefundReversal(SupplierRefundReversal reversal) { }
}
