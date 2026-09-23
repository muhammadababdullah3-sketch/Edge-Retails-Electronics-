using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase4GatewayAndTerminalUnitTests
{
    #region 1. ConnectivityStateMachine State Transitions & Invariants

    [Fact]
    public void ConnectivityStateMachine_InitialState_DefaultsToConnectedAndCanMutate()
    {
        var sm = new ConnectivityStateMachine();

        Assert.Equal(ConnectivityState.Connected, sm.CurrentState);
        Assert.True(sm.CanMutate);
        Assert.Equal(0, sm.ConsecutiveHeartbeatFailures);
    }

    [Fact]
    public void ConnectivityStateMachine_FullStateTransitionCycle_FiresEventsWithAccuratePayload()
    {
        var sm = new ConnectivityStateMachine(ConnectivityState.Connected);
        var transitions = new List<ConnectivityStateChangedEventArgs>();
        sm.StateChanged += (_, args) => transitions.Add(args);

        // Connected -> Degraded -> Reconnecting -> Disconnected -> Connected
        sm.TransitionTo(ConnectivityState.Degraded, "High latency detected");
        sm.TransitionTo(ConnectivityState.Reconnecting, "Attempting reconnect");
        sm.TransitionTo(ConnectivityState.Disconnected, "Network interface dropped");
        sm.TransitionTo(ConnectivityState.Connected, "Reconnection and handshake succeeded");

        Assert.Equal(4, transitions.Count);

        Assert.Equal(ConnectivityState.Connected, transitions[0].PreviousState);
        Assert.Equal(ConnectivityState.Degraded, transitions[0].NewState);
        Assert.Equal("High latency detected", transitions[0].Reason);

        Assert.Equal(ConnectivityState.Degraded, transitions[1].PreviousState);
        Assert.Equal(ConnectivityState.Reconnecting, transitions[1].NewState);
        Assert.Equal("Attempting reconnect", transitions[1].Reason);

        Assert.Equal(ConnectivityState.Reconnecting, transitions[2].PreviousState);
        Assert.Equal(ConnectivityState.Disconnected, transitions[2].NewState);
        Assert.Equal("Network interface dropped", transitions[2].Reason);

        Assert.Equal(ConnectivityState.Disconnected, transitions[3].PreviousState);
        Assert.Equal(ConnectivityState.Connected, transitions[3].NewState);
        Assert.Equal("Reconnection and handshake succeeded", transitions[3].Reason);

        Assert.Equal(ConnectivityState.Connected, sm.CurrentState);
        Assert.True(sm.CanMutate);
    }

    [Fact]
    public void ConnectivityStateMachine_TransitionToSameState_DoesNotFireEvent()
    {
        var sm = new ConnectivityStateMachine(ConnectivityState.Connected);
        int eventCount = 0;
        sm.StateChanged += (_, _) => eventCount++;

        sm.TransitionTo(ConnectivityState.Connected, "Redundant transition");

        Assert.Equal(0, eventCount);
        Assert.Equal(ConnectivityState.Connected, sm.CurrentState);
    }

    [Fact]
    public void ConnectivityStateMachine_HeartbeatFailures_ProgressesDegradedThenDisconnected()
    {
        var sm = new ConnectivityStateMachine(ConnectivityState.Connected);
        var transitions = new List<ConnectivityStateChangedEventArgs>();
        sm.StateChanged += (_, args) => transitions.Add(args);

        // Failure 1: Still Connected
        sm.RecordHeartbeatFailure("Missed ping 1");
        Assert.Equal(ConnectivityState.Connected, sm.CurrentState);
        Assert.Equal(1, sm.ConsecutiveHeartbeatFailures);
        Assert.Empty(transitions);

        // Failure 2: Reaches threshold (MaxHeartbeatFailuresBeforeDegraded = 2) -> Degraded
        sm.RecordHeartbeatFailure("Missed ping 2");
        Assert.Equal(ConnectivityState.Degraded, sm.CurrentState);
        Assert.False(sm.CanMutate);
        Assert.Equal(2, sm.ConsecutiveHeartbeatFailures);
        Assert.Single(transitions);
        Assert.Equal(ConnectivityState.Degraded, transitions[0].NewState);

        // Failure 3 & 4: Still Degraded
        sm.RecordHeartbeatFailure("Missed ping 3");
        sm.RecordHeartbeatFailure("Missed ping 4");
        Assert.Equal(ConnectivityState.Degraded, sm.CurrentState);
        Assert.Equal(4, sm.ConsecutiveHeartbeatFailures);
        Assert.Single(transitions);

        // Failure 5: Reaches threshold (MaxHeartbeatFailuresBeforeDisconnected = 5) -> Disconnected
        sm.RecordHeartbeatFailure("Missed ping 5");
        Assert.Equal(ConnectivityState.Disconnected, sm.CurrentState);
        Assert.False(sm.CanMutate);
        Assert.Equal(5, sm.ConsecutiveHeartbeatFailures);
        Assert.Equal(2, transitions.Count);
        Assert.Equal(ConnectivityState.Disconnected, transitions[1].NewState);
    }

    [Fact]
    public void ConnectivityStateMachine_HeartbeatSuccess_ResetsFailuresAndRestoresConnected()
    {
        var sm = new ConnectivityStateMachine(ConnectivityState.Connected);
        sm.RecordHeartbeatFailure("fail 1");
        sm.RecordHeartbeatFailure("fail 2");
        Assert.Equal(ConnectivityState.Degraded, sm.CurrentState);

        sm.RecordHeartbeatSuccess();

        Assert.Equal(ConnectivityState.Connected, sm.CurrentState);
        Assert.True(sm.CanMutate);
        Assert.Equal(0, sm.ConsecutiveHeartbeatFailures);
    }

    [Theory]
    [InlineData(ConnectivityState.Degraded)]
    [InlineData(ConnectivityState.Reconnecting)]
    [InlineData(ConnectivityState.Disconnected)]
    public void ConnectivityStateMachine_AssertCanMutate_ThrowsWhenNotConnected(ConnectivityState nonConnectedState)
    {
        var sm = new ConnectivityStateMachine(nonConnectedState);

        Assert.False(sm.CanMutate);
        var ex = Assert.Throws<InvalidOperationException>(() => sm.AssertCanMutate());
        Assert.Contains("Authoritative mutations are prohibited", ex.Message);
    }

    [Fact]
    public void ConnectivityStateMachine_AssertCanMutate_DoesNotThrowWhenConnected()
    {
        var sm = new ConnectivityStateMachine(ConnectivityState.Connected);
        var exception = Record.Exception(() => sm.AssertCanMutate());
        Assert.Null(exception);
    }

    #endregion

    #region 2. LocalApplicationGateway Offline Mutation Protection & Lifecycle

    [Theory]
    [InlineData(ConnectivityState.Disconnected)]
    [InlineData(ConnectivityState.Reconnecting)]
    [InlineData(ConnectivityState.Degraded)]
    public async Task LocalGateway_NonConnectedState_RejectsAllAuthoritativeMutations(ConnectivityState offlineState)
    {
        var serviceProvider = new TestServiceProvider();
        var gateway = new LocalApplicationGateway(serviceProvider, offlineState);

        Assert.False(gateway.CanMutate);
        Assert.Equal(offlineState, gateway.CurrentState);

        // 1. CompleteSaleAsync
        var saleRes = await gateway.CompleteSaleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, Guid.NewGuid(), null, 0, SalePaymentMethod.Cash, 100m, null, null, Array.Empty<CompleteSaleLineInput>()));
        Assert.False(saleRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", saleRes.Error!.Code);

        // 2. CreateSaleReturnAsync
        var returnRes = await gateway.CreateSaleReturnAsync(new CreateSaleReturnCommand(
            Guid.NewGuid(), "DEFECT", "Damaged", RefundMethod.Cash, Guid.NewGuid(), Guid.NewGuid(), Array.Empty<SaleReturnLineInput>()));
        Assert.False(returnRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", returnRes.Error!.Code);

        // 3. CommercialExchangeAsync
        var exchangeRes = await gateway.ExecuteCommercialExchangeAsync(new CommercialExchangeCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, "SWAP", "Size swap", Array.Empty<SaleReturnLineInput>(), Array.Empty<CompleteSaleLineInput>(), 0m, SalePaymentMethod.Cash, 0m, null));
        Assert.False(exchangeRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", exchangeRes.Error!.Code);

        // 4. CreatePurchaseAsync
        var purchaseRes = await gateway.CreatePurchaseAsync(new CreatePurchaseCommand(
            Guid.NewGuid(), "INV-100", DateOnly.FromDateTime(DateTime.UtcNow), "Note", 0m, PurchaseSettlementMode.External, Guid.NewGuid(), Guid.NewGuid(), Array.Empty<CreatePurchaseLineInput>()));
        Assert.False(purchaseRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", purchaseRes.Error!.Code);

        // 5. CreatePurchaseReturnAsync
        var purReturnRes = await gateway.CreatePurchaseReturnAsync(new CreatePurchaseReturnCommand(
            Guid.NewGuid(), "Defect", null, PurchaseReturnSettlementMode.External, Guid.NewGuid(), Guid.NewGuid(), Array.Empty<PurchaseReturnLineInput>()));
        Assert.False(purReturnRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", purReturnRes.Error!.Code);

        // 6. VoidPurchaseAsync
        var voidRes = await gateway.VoidPurchaseAsync(new VoidPurchaseCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Mistake"));
        Assert.False(voidRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", voidRes.Error!.Code);

        // 7. CreateSupplierPaymentAsync
        var supPayRes = await gateway.CreateSupplierPaymentAsync(new CreateSupplierPaymentCommand(
            Guid.NewGuid(), 100m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.External, Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(supPayRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", supPayRes.Error!.Code);

        // 8. CreateSupplierRefundAsync
        var supRefRes = await gateway.CreateSupplierRefundAsync(new CreateSupplierRefundCommand(
            Guid.NewGuid(), 50m, SupplierSettlementMethod.External, Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(supRefRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", supRefRes.Error!.Code);

        // 9. CreateWarrantyClaimAsync
        var warRes = await gateway.CreateWarrantyClaimAsync(new CreateWarrantyClaimCommand(
            Guid.NewGuid(), null, null, Guid.NewGuid(), Array.Empty<WarrantyClaimItemInput>(), Guid.NewGuid()));
        Assert.False(warRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", warRes.Error!.Code);

        // 10. UpdateTerminalStatusAsync
        var statusRes = await gateway.UpdateTerminalStatusAsync(new UpdateTerminalStatusCommand(
            Guid.NewGuid(), TerminalStatus.Suspended, Guid.NewGuid()));
        Assert.False(statusRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", statusRes.Error!.Code);
    }

    [Fact]
    public void LocalGateway_SetTerminalContext_And_UpdateConnectivityState_UpdatesPropertiesAndFiresEvent()
    {
        var gateway = new LocalApplicationGateway(new TestServiceProvider(), ConnectivityState.Connected);
        var terminalId = Guid.NewGuid();
        gateway.SetTerminalContext(terminalId, "test-secret");

        Assert.Equal(terminalId, gateway.CurrentTerminalId);

        ConnectivityStateChangedEventArgs? receivedArgs = null;
        gateway.ConnectivityChanged += (_, args) => receivedArgs = args;

        gateway.UpdateConnectivityState(ConnectivityState.Disconnected, "Network failure");

        Assert.NotNull(receivedArgs);
        Assert.Equal(ConnectivityState.Connected, receivedArgs!.PreviousState);
        Assert.Equal(ConnectivityState.Disconnected, receivedArgs.NewState);
        Assert.Equal("Network failure", receivedArgs.Reason);
        Assert.False(gateway.CanMutate);
    }

    #endregion

    #region 3. RemoteApplicationGateway Header Propagation & Offline Write Protection

    [Fact]
    public async Task RemoteGateway_SecurityHeaders_PropagatedProperlyOnEveryRequest()
    {
        var terminalId = Guid.NewGuid();
        const string secret = "auth-token-super-secret-xyz";

        var testHandler = new TestHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RegisterTerminalResult(
                    terminalId, "POS-01", "Register 1", TerminalStatus.Active, TerminalProtocol.CurrentProtocolVersion, DateTimeOffset.UtcNow))
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5000") };
        var gateway = new RemoteApplicationGateway(client, ConnectivityState.Connected);
        gateway.SetTerminalContext(terminalId, secret);

        var regCommand = new RegisterTerminalCommand("POS-01", "Register 1", "FP-123", TerminalProtocol.CurrentProtocolVersion, "127.0.0.1", secret);
        var result = await gateway.RegisterTerminalAsync(regCommand);

        Assert.True(result.IsSuccess);
        Assert.Single(testHandler.DispatchedRequests);

        var sentRequest = testHandler.DispatchedRequests[0];
        Assert.True(sentRequest.Headers.Contains("X-Terminal-Id"));
        Assert.Equal(terminalId.ToString("D"), sentRequest.Headers.GetValues("X-Terminal-Id").First());

        Assert.True(sentRequest.Headers.Contains("X-Terminal-Secret"));
        Assert.Equal(secret, sentRequest.Headers.GetValues("X-Terminal-Secret").First());

        Assert.True(sentRequest.Headers.Contains("X-Protocol-Version"));
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, sentRequest.Headers.GetValues("X-Protocol-Version").First());
    }

    [Fact]
    public async Task RemoteGateway_WithoutTerminalContext_OmitsTerminalIdAndSecret_SendsProtocolVersion()
    {
        var testHandler = new TestHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RegisterTerminalResult(
                    Guid.NewGuid(), "POS-02", "Register 2", TerminalStatus.Active, TerminalProtocol.CurrentProtocolVersion, DateTimeOffset.UtcNow))
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5000") };
        var gateway = new RemoteApplicationGateway(client, ConnectivityState.Connected);

        var regCommand = new RegisterTerminalCommand("POS-02", "Register 2", null, TerminalProtocol.CurrentProtocolVersion, null, null);
        var result = await gateway.RegisterTerminalAsync(regCommand);

        Assert.True(result.IsSuccess);
        Assert.Single(testHandler.DispatchedRequests);

        var sentRequest = testHandler.DispatchedRequests[0];
        Assert.False(sentRequest.Headers.Contains("X-Terminal-Id"));
        Assert.False(sentRequest.Headers.Contains("X-Terminal-Secret"));
        Assert.True(sentRequest.Headers.Contains("X-Protocol-Version"));
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, sentRequest.Headers.GetValues("X-Protocol-Version").First());
    }

    [Theory]
    [InlineData(ConnectivityState.Disconnected)]
    [InlineData(ConnectivityState.Reconnecting)]
    public async Task RemoteGateway_OfflineOrReconnecting_RejectsMutationsWithoutNetworkCall(ConnectivityState nonConnectedState)
    {
        var testHandler = new TestHttpMessageHandler(req =>
            throw new InvalidOperationException("Network call should never be reached in offline state!"));

        using var client = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5000") };
        var gateway = new RemoteApplicationGateway(client, nonConnectedState);

        Assert.False(gateway.CanMutate);

        // Attempt CompleteSale
        var saleRes = await gateway.CompleteSaleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, Guid.NewGuid(), null, 0, SalePaymentMethod.Cash, 100m, null, null, Array.Empty<CompleteSaleLineInput>()));

        Assert.False(saleRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", saleRes.Error!.Code);
        Assert.Empty(testHandler.DispatchedRequests);

        // Attempt UpdateTerminalStatus
        var statusRes = await gateway.UpdateTerminalStatusAsync(new UpdateTerminalStatusCommand(
            Guid.NewGuid(), TerminalStatus.Suspended, Guid.NewGuid()));

        Assert.False(statusRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", statusRes.Error!.Code);
        Assert.Empty(testHandler.DispatchedRequests);

        // Attempt CreatePurchase
        var purRes = await gateway.CreatePurchaseAsync(new CreatePurchaseCommand(
            Guid.NewGuid(), "INV-1", DateOnly.FromDateTime(DateTime.UtcNow), "Note", 0m, PurchaseSettlementMode.External, Guid.NewGuid(), Guid.NewGuid(), Array.Empty<CreatePurchaseLineInput>()));

        Assert.False(purRes.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", purRes.Error!.Code);
        Assert.Empty(testHandler.DispatchedRequests);
    }

    [Fact]
    public async Task RemoteGateway_HeartbeatSuccess_RestoresStateToConnected()
    {
        var testHandler = new TestHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new TerminalHeartbeatResult(
                    Guid.NewGuid(), TerminalStatus.Active, true, DateTimeOffset.UtcNow, TerminalProtocol.CurrentProtocolVersion))
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5000") };
        var gateway = new RemoteApplicationGateway(client, ConnectivityState.Degraded);

        Assert.Equal(ConnectivityState.Degraded, gateway.CurrentState);

        var result = await gateway.SendHeartbeatAsync(new TerminalHeartbeatCommand(
            Guid.NewGuid(), TerminalProtocol.CurrentProtocolVersion, "127.0.0.1"));

        Assert.True(result.IsSuccess);
        Assert.Equal(ConnectivityState.Connected, gateway.CurrentState);
        Assert.True(gateway.CanMutate);
    }

    #endregion

    #region 4. Terminal Registration Handlers

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RegisterTerminal_ValidatesTerminalCode_RejectsWhenEmpty(string? code)
    {
        var repo = new FakeTerminalRepository();
        var license = CreateFakeLicenseService(maxTerminals: 5);
        var handler = new RegisterTerminalHandler(repo, license);

        var command = new RegisterTerminalCommand(code!, "Counter 1", "FP", TerminalProtocol.CurrentProtocolVersion, "127.0.0.1", null);
        var result = await handler.HandleAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.code_required", result.Error!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RegisterTerminal_ValidatesTerminalName_RejectsWhenEmpty(string? name)
    {
        var repo = new FakeTerminalRepository();
        var license = CreateFakeLicenseService(maxTerminals: 5);
        var handler = new RegisterTerminalHandler(repo, license);

        var command = new RegisterTerminalCommand("POS-01", name!, "FP", TerminalProtocol.CurrentProtocolVersion, "127.0.0.1", null);
        var result = await handler.HandleAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.name_required", result.Error!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.9.0")]
    [InlineData("2.0.0")]
    [InlineData("v1.0.0")]
    public async Task RegisterTerminal_ValidatesProtocolVersion_RejectsWhenIncompatible(string? version)
    {
        var repo = new FakeTerminalRepository();
        var license = CreateFakeLicenseService(maxTerminals: 5);
        var handler = new RegisterTerminalHandler(repo, license);

        var command = new RegisterTerminalCommand("POS-01", "Counter 1", "FP", version, "127.0.0.1", null);
        var result = await handler.HandleAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.protocol_incompatible", result.Error!.Code);
    }

    [Fact]
    public async Task RegisterTerminal_WhenActiveTerminalsReachQuota_RejectsRegistration()
    {
        var repo = new FakeTerminalRepository();
        // Seed 2 existing active terminals
        await repo.AddAsync(new Terminal { TerminalCode = "TERM-1", Name = "Term 1", Status = TerminalStatus.Active });
        await repo.AddAsync(new Terminal { TerminalCode = "TERM-2", Name = "Term 2", Status = TerminalStatus.Active });

        // License allows max 2 terminals
        var license = CreateFakeLicenseService(maxTerminals: 2);
        var handler = new RegisterTerminalHandler(repo, license);

        var command = new RegisterTerminalCommand("TERM-3", "Term 3", "FP-3", TerminalProtocol.CurrentProtocolVersion, "127.0.0.1", "quota-secret");
        var result = await handler.HandleAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.capacity_exceeded", result.Error!.Code);
        Assert.Contains("limit of 2 has been reached", result.Error.Message);
    }

    [Fact]
    public async Task RegisterTerminal_HashesClientAuthSecretWithSha256()
    {
        var repo = new FakeTerminalRepository();
        var license = CreateFakeLicenseService(maxTerminals: 5);
        var handler = new RegisterTerminalHandler(repo, license);

        const string rawSecret = "terminal-secret-alpha-99";
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawSecret)));

        var command = new RegisterTerminalCommand("POS-SECURE", "Counter Secure", "FP-SEC", TerminalProtocol.CurrentProtocolVersion, "192.168.1.50", rawSecret);
        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        var registered = await repo.GetByCodeAsync("POS-SECURE");
        Assert.NotNull(registered);
        Assert.Equal(expectedHash, registered!.AuthSecretHash);
        Assert.True(registered.VerifySecret(rawSecret));
        Assert.False(registered.VerifySecret("wrong-secret"));
    }

    [Fact]
    public async Task RegisterTerminal_ExistingRevokedTerminal_RejectsReRegistration()
    {
        var repo = new FakeTerminalRepository();
        await repo.AddAsync(new Terminal
        {
            TerminalCode = "POS-REVOKED",
            Name = "Revoked Terminal",
            Status = TerminalStatus.Revoked
        });

        var license = CreateFakeLicenseService(maxTerminals: 5);
        var handler = new RegisterTerminalHandler(repo, license);

        var command = new RegisterTerminalCommand("POS-REVOKED", "Revoked Terminal", null, TerminalProtocol.CurrentProtocolVersion, null, "revoked-test-secret");
        var result = await handler.HandleAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.revoked", result.Error!.Code);
    }

    [Fact]
    public async Task RegisterTerminal_ExistingActiveTerminal_WrongCredential_IsRejected()
    {
        var repo = new FakeTerminalRepository();
        await repo.AddAsync(new Terminal
        {
            TerminalCode = "POS-WRONG-SECRET",
            Name = "Protected Terminal",
            Status = TerminalStatus.Active,
            AuthSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("correct-secret")))
        });

        var license = CreateFakeLicenseService(maxTerminals: 5);
        var handler = new RegisterTerminalHandler(repo, license);
        var command = new RegisterTerminalCommand("POS-WRONG-SECRET", "Protected Terminal", null, TerminalProtocol.CurrentProtocolVersion, null, "wrong-secret");

        var result = await handler.HandleAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.invalid_secret", result.Error!.Code);
    }

    [Fact]
    public async Task RegisterTerminal_ExistingActiveTerminal_UpdatesMetadataAndSucceeds()
    {
        var repo = new FakeTerminalRepository();
        var existing = new Terminal
        {
            TerminalCode = "POS-EXISTING",
            Name = "Terminal 1",
            Status = TerminalStatus.Active,
            LastSeenAt = DateTimeOffset.UtcNow.AddHours(-5),
            AuthSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("existing-test-secret")))
        };
        await repo.AddAsync(existing);

        var license = CreateFakeLicenseService(maxTerminals: 1); // Quota is 1, existing terminal does not count as new
        var handler = new RegisterTerminalHandler(repo, license);

        var command = new RegisterTerminalCommand("POS-EXISTING", "Terminal 1 Renamed", "FP-NEW", TerminalProtocol.CurrentProtocolVersion, "10.0.0.99", "existing-test-secret");
        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value!.TerminalId);
        Assert.Equal("10.0.0.99", existing.LastKnownIpAddress);
        Assert.Equal("FP-NEW", existing.HardwareFingerprint);
    }

    #endregion

    #region 5. Terminal Heartbeat Handler

    [Fact]
    public async Task TerminalHeartbeat_UnknownTerminal_ReturnsNotFound()
    {
        var repo = new FakeTerminalRepository();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var handler = new TerminalHeartbeatHandler(repo, clock);

        var unknownId = Guid.NewGuid();
        var result = await handler.HandleAsync(new TerminalHeartbeatCommand(unknownId, TerminalProtocol.CurrentProtocolVersion, "127.0.0.1"));

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task TerminalHeartbeat_RevokedTerminal_ReturnsRevoked()
    {
        var repo = new FakeTerminalRepository();
        var revokedId = Guid.NewGuid();
        await repo.AddAsync(new Terminal
        {
            Id = revokedId,
            TerminalCode = "REV-01",
            Name = "Revoked",
            Status = TerminalStatus.Revoked
        });

        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var handler = new TerminalHeartbeatHandler(repo, clock);

        var result = await handler.HandleAsync(new TerminalHeartbeatCommand(revokedId, TerminalProtocol.CurrentProtocolVersion, "127.0.0.1"));

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.revoked", result.Error!.Code);
    }

    [Fact]
    public async Task TerminalHeartbeat_IncompatibleProtocol_ReturnsIncompatible()
    {
        var repo = new FakeTerminalRepository();
        var terminalId = Guid.NewGuid();
        await repo.AddAsync(new Terminal
        {
            Id = terminalId,
            TerminalCode = "POS-01",
            Name = "Counter",
            Status = TerminalStatus.Active
        });

        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var handler = new TerminalHeartbeatHandler(repo, clock);

        var result = await handler.HandleAsync(new TerminalHeartbeatCommand(terminalId, "0.9.9", "127.0.0.1"));

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.protocol_incompatible", result.Error!.Code);
    }

    [Fact]
    public async Task TerminalHeartbeat_ActiveTerminal_UpdatesLastSeenAt_AndCanMutateIsTrue()
    {
        var repo = new FakeTerminalRepository();
        var terminalId = Guid.NewGuid();
        var terminal = new Terminal
        {
            Id = terminalId,
            TerminalCode = "POS-01",
            Name = "Counter",
            Status = TerminalStatus.Active,
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        await repo.AddAsync(terminal);

        var heartbeatTime = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(heartbeatTime);
        var handler = new TerminalHeartbeatHandler(repo, clock);

        var result = await handler.HandleAsync(new TerminalHeartbeatCommand(terminalId, TerminalProtocol.CurrentProtocolVersion, "192.168.1.100"));

        Assert.True(result.IsSuccess);
        Assert.Equal(terminalId, result.Value!.TerminalId);
        Assert.Equal(TerminalStatus.Active, result.Value.Status);
        Assert.True(result.Value.CanMutate);
        Assert.Equal(heartbeatTime, result.Value.ServerTimeUtc);
        Assert.Equal(heartbeatTime, terminal.LastSeenAt);
        Assert.Equal("192.168.1.100", terminal.LastKnownIpAddress);
    }

    [Fact]
    public async Task TerminalHeartbeat_SuspendedTerminal_UpdatesLastSeenAt_AndCanMutateIsFalse()
    {
        var repo = new FakeTerminalRepository();
        var terminalId = Guid.NewGuid();
        var terminal = new Terminal
        {
            Id = terminalId,
            TerminalCode = "POS-SUSPENDED",
            Name = "Suspended Counter",
            Status = TerminalStatus.Suspended,
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-30)
        };
        await repo.AddAsync(terminal);

        var heartbeatTime = DateTimeOffset.UtcNow;
        var clock = new FakeClock(heartbeatTime);
        var handler = new TerminalHeartbeatHandler(repo, clock);

        var result = await handler.HandleAsync(new TerminalHeartbeatCommand(terminalId, TerminalProtocol.CurrentProtocolVersion, null));

        Assert.True(result.IsSuccess);
        Assert.Equal(TerminalStatus.Suspended, result.Value!.Status);
        Assert.False(result.Value.CanMutate);
        Assert.Equal(heartbeatTime, terminal.LastSeenAt);
    }

    #endregion

    #region 6. UpdateTerminalStatus Handlers, Status Transitions & Permissions

    [Fact]
    public async Task UpdateTerminalStatus_StatusTransitions_ActiveToSuspendedToRevokedAndBack()
    {
        var repo = new FakeTerminalRepository();
        var terminalId = Guid.NewGuid();
        var terminal = new Terminal
        {
            Id = terminalId,
            TerminalCode = "TERM-LIFE",
            Name = "Lifecycle Terminal",
            Status = TerminalStatus.Active
        };
        await repo.AddAsync(terminal);

        var authorizer = new FakePermissionAuthorizer(isAuthorized: true);
        var handler = new UpdateTerminalStatusHandler(repo, authorizer);
        var actorId = Guid.NewGuid();

        // 1. Active -> Suspended
        var res1 = await handler.HandleAsync(new UpdateTerminalStatusCommand(terminalId, TerminalStatus.Suspended, actorId));
        Assert.True(res1.IsSuccess);
        Assert.Equal(TerminalStatus.Suspended, res1.Value!.Status);
        Assert.Equal(TerminalStatus.Suspended, terminal.Status);
        Assert.False(terminal.CanMutate);

        // 2. Suspended -> Revoked
        var res2 = await handler.HandleAsync(new UpdateTerminalStatusCommand(terminalId, TerminalStatus.Revoked, actorId));
        Assert.True(res2.IsSuccess);
        Assert.Equal(TerminalStatus.Revoked, res2.Value!.Status);
        Assert.Equal(TerminalStatus.Revoked, terminal.Status);
        Assert.False(terminal.CanMutate);

        // 3. Revoked -> Active
        var res3 = await handler.HandleAsync(new UpdateTerminalStatusCommand(terminalId, TerminalStatus.Active, actorId));
        Assert.True(res3.IsSuccess);
        Assert.Equal(TerminalStatus.Active, res3.Value!.Status);
        Assert.Equal(TerminalStatus.Active, terminal.Status);
        Assert.True(terminal.CanMutate);
    }

    [Fact]
    public async Task UpdateTerminalStatus_UnknownTerminal_ReturnsNotFound()
    {
        var repo = new FakeTerminalRepository();
        var authorizer = new FakePermissionAuthorizer(isAuthorized: true);
        var handler = new UpdateTerminalStatusHandler(repo, authorizer);

        var result = await handler.HandleAsync(new UpdateTerminalStatusCommand(Guid.NewGuid(), TerminalStatus.Suspended, Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("terminals.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task UpdateTerminalStatus_PermissionDenied_RejectsStatusUpdate()
    {
        var repo = new FakeTerminalRepository();
        var terminalId = Guid.NewGuid();
        var terminal = new Terminal
        {
            Id = terminalId,
            TerminalCode = "POS-AUTH",
            Name = "Auth Terminal",
            Status = TerminalStatus.Active
        };
        await repo.AddAsync(terminal);

        // Disallow settings.manage
        var authorizer = new FakePermissionAuthorizer(isAuthorized: false);
        var handler = new UpdateTerminalStatusHandler(repo, authorizer);
        var actorId = Guid.NewGuid();

        var result = await handler.HandleAsync(new UpdateTerminalStatusCommand(terminalId, TerminalStatus.Revoked, actorId));

        Assert.False(result.IsSuccess);
        Assert.Equal("authorization.denied", result.Error!.Code);
        // Terminal status remains Active in repository
        Assert.Equal(TerminalStatus.Active, terminal.Status);
    }

    [Fact]
    public async Task UpdateTerminalStatus_PermissionGranted_AllowsStatusUpdate()
    {
        var repo = new FakeTerminalRepository();
        var terminalId = Guid.NewGuid();
        var terminal = new Terminal
        {
            Id = terminalId,
            TerminalCode = "POS-AUTH2",
            Name = "Auth Terminal 2",
            Status = TerminalStatus.Active
        };
        await repo.AddAsync(terminal);

        var authorizer = new FakePermissionAuthorizer(isAuthorized: true);
        var handler = new UpdateTerminalStatusHandler(repo, authorizer);
        var actorId = Guid.NewGuid();

        var result = await handler.HandleAsync(new UpdateTerminalStatusCommand(terminalId, TerminalStatus.Suspended, actorId));

        Assert.True(result.IsSuccess);
        Assert.Equal(TerminalStatus.Suspended, result.Value!.Status);
        Assert.Equal(TerminalStatus.Suspended, terminal.Status);
    }

    #endregion

    #region Test Doubles & Fakes

    private static RuntimeLicenseService CreateFakeLicenseService(int maxTerminals)
    {
        var store = new FakeLicenseStore();
        var validator = new FakeLicenseValidator(new LicensePayload(
            LicenseId: "LIC-123",
            CustomerName: "Test Store",
            StoreName: "Branch 1",
            Plan: "MultiTerminal",
            IssueDate: DateTimeOffset.UtcNow.AddDays(-10),
            ExpiryDate: DateTimeOffset.UtcNow.AddYears(1),
            DeviceId: "DEVICE-001",
            MaxTerminals: maxTerminals,
            EnabledModules: new[] { "sales", "terminals" }));

        return new RuntimeLicenseService(store, validator);
    }

    private sealed class FakeTerminalRepository : ITerminalRepository
    {
        private readonly Dictionary<Guid, Terminal> _terminalsById = new();
        private readonly Dictionary<string, Terminal> _terminalsByCode = new(StringComparer.OrdinalIgnoreCase);

        public Task<Terminal?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _terminalsById.TryGetValue(id, out var terminal);
            return Task.FromResult(terminal);
        }

        public Task<Terminal?> GetByCodeAsync(string terminalCode, CancellationToken cancellationToken = default)
        {
            _terminalsByCode.TryGetValue(terminalCode.Trim(), out var terminal);
            return Task.FromResult(terminal);
        }

        public Task<IReadOnlyList<Terminal>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Terminal>>(_terminalsById.Values.ToList());
        }

        public Task<int> GetActiveCountAsync(CancellationToken cancellationToken = default)
        {
            var count = _terminalsById.Values.Count(t => t.Status == TerminalStatus.Active);
            return Task.FromResult(count);
        }

        public Task AddAsync(Terminal terminal, CancellationToken cancellationToken = default)
        {
            _terminalsById[terminal.Id] = terminal;
            _terminalsByCode[terminal.TerminalCode] = terminal;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Terminal terminal, CancellationToken cancellationToken = default)
        {
            _terminalsById[terminal.Id] = terminal;
            _terminalsByCode[terminal.TerminalCode] = terminal;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
        public DateOnly ShopDate => DateOnly.FromDateTime(UtcNow.DateTime);

        public FakeClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }
    }

    private sealed class FakePermissionAuthorizer : IApplicationPermissionAuthorizer
    {
        private readonly bool _isAuthorized;

        public FakePermissionAuthorizer(bool isAuthorized)
        {
            _isAuthorized = isAuthorized;
        }

        public Task<Result> AuthorizeAsync(Guid actorId, string permissionKey, CancellationToken cancellationToken)
        {
            if (_isAuthorized)
            {
                return Task.FromResult(Result.Success());
            }

            return Task.FromResult(Result.Failure(
                "authorization.denied",
                $"Permission '{permissionKey}' was denied for actor '{actorId}'."));
        }
    }

    private sealed class FakeLicenseStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("{\"raw\": true}");
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeLicenseValidator : ILicenseValidator
    {
        private readonly LicensePayload _payload;

        public FakeLicenseValidator(LicensePayload payload)
        {
            _payload = payload;
        }

        public Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LicenseValidationResult(
                LicenseValidationStatus.Valid,
                _payload,
                "Valid license"));
        }
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public List<HttpRequestMessage> DispatchedRequests { get; } = new();

        public TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            DispatchedRequests.Add(request);
            return await _handler(request);
        }
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();

        public void Register<T>(T service) where T : notnull
        {
            _services[typeof(T)] = service;
        }

        public object? GetService(Type serviceType)
        {
            _services.TryGetValue(serviceType, out var service);
            return service;
        }
    }

    #endregion
}
