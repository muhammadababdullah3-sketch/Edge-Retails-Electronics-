using EdgeRetails.Application.Common;
using EdgeRetails.Application.Exceptions;
using EdgeRetails.Application.Features.Terminals;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase12CanonicalPayloadTests
{
    [Fact]
    public void CanonicalPayloadSerializer_SortsKeysAlphabetically()
    {
        var jsonA = "{\"zebra\": 1, \"alpha\": 2, \"beta\": 3}";
        var jsonB = "{\"beta\": 3, \"zebra\": 1, \"alpha\": 2}";

        var canonicalA = CanonicalPayloadSerializer.SerializeJsonStringToCanonicalString(jsonA);
        var canonicalB = CanonicalPayloadSerializer.SerializeJsonStringToCanonicalString(jsonB);

        Assert.Equal(canonicalA, canonicalB);
        Assert.Equal("{\"alpha\":2,\"beta\":3,\"zebra\":1}", canonicalA);
    }

    [Fact]
    public void CanonicalPayloadSerializer_NormalizesDecimals()
    {
        var jsonA = "{\"amount\": 100.00, \"rate\": 0.50}";
        var jsonB = "{\"amount\": 100.0, \"rate\": 0.5}";

        var hashA = OperationPayloadFingerprint.ComputeCanonicalSha256(jsonA);
        var hashB = OperationPayloadFingerprint.ComputeCanonicalSha256(jsonB);

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void CanonicalPayloadSerializer_StripsExcludedHeaders()
    {
        var jsonWithHeaders = "{\"Authorization\": \"Bearer token123\", \"X-Correlation-Id\": \"abc-123\", \"payload\": \"test_order\"}";
        var jsonClean = "{\"payload\": \"test_order\"}";

        var canonicalWith = CanonicalPayloadSerializer.SerializeJsonStringToCanonicalString(jsonWithHeaders);
        var canonicalClean = CanonicalPayloadSerializer.SerializeJsonStringToCanonicalString(jsonClean);

        Assert.Equal(canonicalClean, canonicalWith);
        Assert.DoesNotContain("Authorization", canonicalWith);
        Assert.DoesNotContain("X-Correlation-Id", canonicalWith);
    }

    [Fact]
    public void CanonicalPayloadSerializer_StripsXForwardedForHeader()
    {
        var jsonWithXff = "{\"X-Forwarded-For\": \"192.168.1.1\", \"saleId\": \"abc-123\"}";
        var jsonClean = "{\"saleId\": \"abc-123\"}";

        var canonicalWith = CanonicalPayloadSerializer.SerializeJsonStringToCanonicalString(jsonWithXff);
        var canonicalClean = CanonicalPayloadSerializer.SerializeJsonStringToCanonicalString(jsonClean);

        Assert.Equal(canonicalClean, canonicalWith);
        Assert.DoesNotContain("X-Forwarded-For", canonicalWith);
    }

    [Fact]
    public void CanonicalPayloadSerializer_HandlesNestedObjectsAndArrays()
    {
        var complexA = "{\"items\": [{\"sku\": \"B\", \"qty\": 1}, {\"sku\": \"A\", \"qty\": 2}], \"customer\": {\"zip\": \"12345\", \"city\": \"Lahore\"}}";
        var complexB = "{\"customer\": {\"city\": \"Lahore\", \"zip\": \"12345\"}, \"items\": [{\"sku\": \"B\", \"qty\": 1}, {\"sku\": \"A\", \"qty\": 2}]}";

        var hashA = OperationPayloadFingerprint.ComputeCanonicalSha256(complexA);
        var hashB = OperationPayloadFingerprint.ComputeCanonicalSha256(complexB);

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void CanonicalPayloadSerializer_ComputesConsistentSha256Prefix()
    {
        var sample = new { ProductId = Guid.NewGuid(), Quantity = 5.0m, UnitPrice = 120.00m };
        var hash = OperationPayloadFingerprint.ComputeCanonicalSha256(sample);

        Assert.StartsWith("sha256:", hash);
        Assert.Equal(71, hash.Length); // "sha256:" (7) + 64 hex characters
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_ClaimAsync_AcquiresSlot()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff";

        var claimResult = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);

        Assert.Equal(OperationClaimStatus.Acquired, claimResult.Status);
        Assert.NotNull(claimResult.Outcome);
        Assert.Equal(clientOpId, claimResult.Outcome.ClientOperationId);
        Assert.Equal(OperationOutcomeState.Pending, claimResult.Outcome.State);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_ClaimAsync_ReturnsInFlightWhenPending()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff";

        var firstClaim = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);
        var secondClaim = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);

        Assert.Equal(OperationClaimStatus.Acquired, firstClaim.Status);
        Assert.Equal(OperationClaimStatus.InFlight, secondClaim.Status);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_ClaimAsync_ReturnsAlreadySucceededWithResponse()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff";
        var entityId = Guid.NewGuid();
        var responseJson = "{\"invoiceNumber\": \"INV-2026-001\", \"grandTotal\": 1500.00}";

        await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);
        await ledger.RecordSuccessWithResponseAsync(
            clientOpId,
            "Sale",
            entityId,
            responseJson,
            responseStatusCode: 200,
            responseContentType: "application/json",
            documentNumber: "INV-2026-001",
            payloadFingerprint: fingerprint);

        var replayClaim = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);

        Assert.Equal(OperationClaimStatus.AlreadySucceeded, replayClaim.Status);
        Assert.NotNull(replayClaim.Outcome);
        Assert.Equal(OperationOutcomeState.Succeeded, replayClaim.Outcome.State);
        Assert.Equal(responseJson, replayClaim.Outcome.ResponsePayload);
        Assert.Equal(200, replayClaim.Outcome.ResponseStatusCode);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_ClaimAsync_ThrowsTamperExceptionOnDivergentFingerprint()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var originalFingerprint = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        var alteredFingerprint = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

        await ledger.ClaimAsync(clientOpId, "Sale", originalFingerprint);

        var ex = await Assert.ThrowsAsync<IdempotencyPayloadMismatchException>(async () =>
        {
            await ledger.ClaimAsync(clientOpId, "Sale", alteredFingerprint);
        });

        Assert.Equal("idempotency.payload_mismatch", ex.Code);
        Assert.Equal(clientOpId, ex.ClientOperationId);
        Assert.Equal(originalFingerprint, ex.ExistingFingerprint);
        Assert.Equal(alteredFingerprint, ex.IncomingFingerprint);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_ClaimAsync_ThrowsOperationTypeExceptionOnDivergentType()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

        await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);

        var ex = await Assert.ThrowsAsync<IdempotencyOperationTypeMismatchException>(async () =>
        {
            await ledger.ClaimAsync(clientOpId, "Purchase", fingerprint);
        });

        Assert.Equal("idempotency.operation_type_mismatch", ex.Code);
        Assert.Equal(clientOpId, ex.ClientOperationId);
        Assert.Equal("Sale", ex.ExistingOperationType);
        Assert.Equal("Purchase", ex.IncomingOperationType);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_RecordFailureWithResponse_StoresFailureMetadata()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
        var failureResponse = "{\"code\": \"sales.insufficient_stock\", \"message\": \"Not enough sellable stock\"}";

        await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);
        await ledger.RecordFailureWithResponseAsync(
            clientOpId,
            "Sale",
            "sales.insufficient_stock",
            "Not enough sellable stock",
            responsePayload: failureResponse,
            responseStatusCode: 400,
            payloadFingerprint: fingerprint);

        var outcome = await ledger.GetOutcomeAsync(clientOpId);

        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Failed, outcome.State);
        Assert.Equal("sales.insufficient_stock", outcome.ErrorCode);
        Assert.Equal(failureResponse, outcome.ResponsePayload);
        Assert.Equal(400, outcome.ResponseStatusCode);
    }

    [Fact]
    public void CompleteSaleCanonicalPayload_IgnoresSupervisorPinAndRequestedAtUtc()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();
        var supervisorId = Guid.NewGuid();

        var lines = new[]
        {
            new EdgeRetails.Application.Features.Sales.CompleteSaleLineInput(
                productId,
                unitId,
                1.0m,
                50.00m,
                Array.Empty<Guid>())
        };

        var cmd1 = new EdgeRetails.Application.Features.Sales.CompleteSaleCommand(
            Guid.NewGuid(),
            null,
            cashierId,
            null,
            0m,
            EdgeRetails.Domain.Sales.SalePaymentMethod.Cash,
            50.00m,
            "REF-001",
            null,
            lines,
            new DateOnly(2026, 10, 15),
            new EdgeRetails.Domain.Posting.SupervisorApprovalRequest(
                supervisorId,
                "1234",
                "Price check exception",
                DateTimeOffset.UtcNow));

        var cmd2 = cmd1 with
        {
            SupervisorApproval = new EdgeRetails.Domain.Posting.SupervisorApprovalRequest(
                supervisorId,
                "9999", // Different PIN
                "Price check exception",
                DateTimeOffset.UtcNow.AddMinutes(15)) // Different timestamp
        };

        var fp1 = EdgeRetails.Application.Features.Sales.CompleteSaleCanonicalPayload.ComputeFingerprint(cmd1);
        var fp2 = EdgeRetails.Application.Features.Sales.CompleteSaleCanonicalPayload.ComputeFingerprint(cmd2);

        Assert.Equal(fp1, fp2);
        Assert.StartsWith("v1:sha256:", fp1);
    }

    [Fact]
    public void CompleteSaleCanonicalPayload_IgnoresPaymentReference()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();

        var lines = new[]
        {
            new EdgeRetails.Application.Features.Sales.CompleteSaleLineInput(
                productId,
                unitId,
                2.0m,
                100.00m,
                Array.Empty<Guid>())
        };

        var cmd1 = new EdgeRetails.Application.Features.Sales.CompleteSaleCommand(
            Guid.NewGuid(),
            null,
            cashierId,
            null,
            0m,
            EdgeRetails.Domain.Sales.SalePaymentMethod.Bank,
            200.00m,
            "AUTH-REF-111",
            null,
            lines);

        var cmd2 = cmd1 with
        {
            PaymentReference = "AUTH-REF-222" // Different payment reference
        };

        var fp1 = EdgeRetails.Application.Features.Sales.CompleteSaleCanonicalPayload.ComputeFingerprint(cmd1);
        var fp2 = EdgeRetails.Application.Features.Sales.CompleteSaleCanonicalPayload.ComputeFingerprint(cmd2);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void CompleteSaleCanonicalPayload_OrdersInventoryUnitIdsDeterministically()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();
        var unitA = Guid.NewGuid();
        var unitB = Guid.NewGuid();

        var lines1 = new[]
        {
            new EdgeRetails.Application.Features.Sales.CompleteSaleLineInput(
                productId,
                unitId,
                2.0m,
                50.00m,
                new[] { unitA, unitB })
        };

        var lines2 = new[]
        {
            new EdgeRetails.Application.Features.Sales.CompleteSaleLineInput(
                productId,
                unitId,
                2.0m,
                50.00m,
                new[] { unitB, unitA }) // Inverted order
        };

        var cmd1 = new EdgeRetails.Application.Features.Sales.CompleteSaleCommand(
            Guid.NewGuid(),
            null,
            cashierId,
            null,
            0m,
            EdgeRetails.Domain.Sales.SalePaymentMethod.Cash,
            100.00m,
            null,
            null,
            lines1);

        var cmd2 = cmd1 with { Lines = lines2 };

        var fp1 = EdgeRetails.Application.Features.Sales.CompleteSaleCanonicalPayload.ComputeFingerprint(cmd1);
        var fp2 = EdgeRetails.Application.Features.Sales.CompleteSaleCanonicalPayload.ComputeFingerprint(cmd2);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void CompleteSaleCanonicalPayload_DetectsChangedMeaningfulField()
    {
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();

        var lines = new[]
        {
            new EdgeRetails.Application.Features.Sales.CompleteSaleLineInput(
                productId,
                unitId,
                1.0m,
                50.00m,
                Array.Empty<Guid>())
        };

        var cmd1 = new EdgeRetails.Application.Features.Sales.CompleteSaleCommand(
            Guid.NewGuid(),
            null,
            cashierId,
            null,
            0m,
            EdgeRetails.Domain.Sales.SalePaymentMethod.Cash,
            50.00m,
            null,
            null,
            lines);

        var cmd2 = cmd1 with
        {
            AmountTendered = 60.00m // Changed business amount
        };

        var fp1 = EdgeRetails.Application.Features.Sales.CompleteSaleCanonicalPayload.ComputeFingerprint(cmd1);
        var fp2 = EdgeRetails.Application.Features.Sales.CompleteSaleCanonicalPayload.ComputeFingerprint(cmd2);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void OperationPayloadFingerprint_VersionAndTypeSeparation()
    {
        var sample = new { Name = "StandardSale", Quantity = 1.0m };

        var fpSaleV1 = OperationPayloadFingerprint.ComputeVersionedFingerprint("Sale", sample, "v1");
        var fpPurchaseV1 = OperationPayloadFingerprint.ComputeVersionedFingerprint("Purchase", sample, "v1");
        var fpSaleV2 = OperationPayloadFingerprint.ComputeVersionedFingerprint("Sale", sample, "v2");

        Assert.StartsWith("v1:sha256:", fpSaleV1);
        Assert.StartsWith("v1:sha256:", fpPurchaseV1);
        Assert.StartsWith("v2:sha256:", fpSaleV2);

        Assert.NotEqual(fpSaleV1, fpPurchaseV1);
        Assert.NotEqual(fpSaleV1, fpSaleV2);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_ClaimAsync_ThrowsContextMismatchExceptionOnDivergentActor()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:1111111111111111111111111111111111111111111111111111111111111111";
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();

        await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA);

        var ex = await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorB);
        });

        Assert.Equal("idempotency.context_mismatch", ex.Code);
        Assert.Equal(clientOpId, ex.ClientOperationId);
        Assert.Equal("ActorUserId", ex.ContextKey);
        Assert.Equal(actorA.ToString(), ex.ExistingValue);
        Assert.Equal(actorB.ToString(), ex.IncomingValue);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_RecordSuccess_RejectsIdentityMutation()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:1111111111111111111111111111111111111111111111111111111111111111";
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();

        await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA);

        // Attempt divergent operation type
        await Assert.ThrowsAsync<IdempotencyOperationTypeMismatchException>(async () =>
        {
            await ledger.RecordSuccessWithResponseAsync(
                clientOpId,
                "Purchase",
                Guid.NewGuid(),
                "{}",
                payloadFingerprint: fingerprint,
                actorId: actorA);
        });

        // Attempt divergent payload fingerprint
        await Assert.ThrowsAsync<IdempotencyPayloadMismatchException>(async () =>
        {
            await ledger.RecordSuccessWithResponseAsync(
                clientOpId,
                "Sale",
                Guid.NewGuid(),
                "{}",
                payloadFingerprint: "v1:sha256:9999999999999999999999999999999999999999999999999999999999999999",
                actorId: actorA);
        });

        // Attempt divergent actor context
        await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.RecordSuccessWithResponseAsync(
                clientOpId,
                "Sale",
                Guid.NewGuid(),
                "{}",
                payloadFingerprint: fingerprint,
                actorId: actorB);
        });
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_ClaimAsync_ThrowsContextMismatchExceptionOnSuppliedNullActor()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:1111111111111111111111111111111111111111111111111111111111111111";
        var actorA = Guid.NewGuid();

        await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA);

        var ex = await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: null);
        });

        Assert.Equal("idempotency.context_mismatch", ex.Code);
        Assert.Equal(clientOpId, ex.ClientOperationId);
        Assert.Equal("ActorUserId", ex.ContextKey);
        Assert.Equal(actorA.ToString(), ex.ExistingValue);
        Assert.Equal("null", ex.IncomingValue);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_ClaimAsync_ThrowsContextMismatchExceptionOnTerminalMismatchOrSuppliedNull()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:1111111111111111111111111111111111111111111111111111111111111111";
        var actorA = Guid.NewGuid();
        var terminalT1 = Guid.NewGuid();
        var terminalT2 = Guid.NewGuid();

        await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1);

        // Divergent terminal
        var exDivergent = await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT2);
        });
        Assert.Equal("TerminalId", exDivergent.ContextKey);
        Assert.Equal(terminalT1.ToString(), exDivergent.ExistingValue);
        Assert.Equal(terminalT2.ToString(), exDivergent.IncomingValue);

        // Supplied-null terminal
        var exNull = await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: null);
        });
        Assert.Equal("TerminalId", exNull.ContextKey);
        Assert.Equal(terminalT1.ToString(), exNull.ExistingValue);
        Assert.Equal("null", exNull.IncomingValue);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_AuthorizedSameIntentRecovery_SucceedsAfterLegitimateSessionRefresh()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:1111111111111111111111111111111111111111111111111111111111111111";
        var actorA = Guid.NewGuid();
        var terminalT1 = Guid.NewGuid();
        var sessionS1 = Guid.NewGuid();
        var sessionS2 = Guid.NewGuid();

        // 1. Initial claim under session S1
        var claim1 = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1, sessionId: sessionS1);
        Assert.Equal(OperationClaimStatus.Acquired, claim1.Status);

        // 2. Network reconnect / session refresh on same terminal with same cashier: authorized recovery under session S2
        var claim2 = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1, sessionId: sessionS2);
        Assert.Equal(OperationClaimStatus.InFlight, claim2.Status);

        // 3. Complete sale with refreshed session S2
        var entityId = Guid.NewGuid();
        await ledger.RecordSuccessWithResponseAsync(
            clientOpId,
            "Sale",
            entityId,
            "{\"saleId\":\"" + entityId + "\"}",
            documentNumber: "INV-100",
            actorId: actorA,
            terminalId: terminalT1,
            sessionId: sessionS2,
            payloadFingerprint: fingerprint);

        var outcome = await ledger.GetOutcomeAsync(clientOpId);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
        Assert.Equal(actorA, outcome.ActorId);
        Assert.Equal(terminalT1, outcome.TerminalId);
        Assert.Equal(sessionS2, outcome.SessionId);

        // 4. Subsequent replay under yet another refreshed session: returns authoritative cached result
        var sessionS3 = Guid.NewGuid();
        var claim3 = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1, sessionId: sessionS3);
        Assert.Equal(OperationClaimStatus.AlreadySucceeded, claim3.Status);
    }

    [Fact]
    public async Task InMemoryOperationOutcomeLedger_RecordMethods_RejectContextMutationsAcrossAllStatusTransitions()
    {
        var ledger = new InMemoryOperationOutcomeLedger();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:1111111111111111111111111111111111111111111111111111111111111111";
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();
        var terminalT1 = Guid.NewGuid();
        var terminalT2 = Guid.NewGuid();

        await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1);

        // RecordPendingAsync: divergent actor & divergent terminal
        await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.RecordPendingAsync(clientOpId, "Sale", actorId: actorB, terminalId: terminalT1, payloadFingerprint: fingerprint);
        });
        await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.RecordPendingAsync(clientOpId, "Sale", actorId: actorA, terminalId: terminalT2, payloadFingerprint: fingerprint);
        });

        // RecordFailureWithResponseAsync: divergent actor & divergent terminal
        await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.RecordFailureWithResponseAsync(clientOpId, "Sale", "ERR", "Fail", actorId: actorB, terminalId: terminalT1, payloadFingerprint: fingerprint);
        });
        await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.RecordFailureWithResponseAsync(clientOpId, "Sale", "ERR", "Fail", actorId: actorA, terminalId: terminalT2, payloadFingerprint: fingerprint);
        });

        // RecordOutcomeUnknownAsync: divergent actor & divergent terminal
        await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.RecordOutcomeUnknownAsync(clientOpId, "Sale", actorId: actorB, terminalId: terminalT1, payloadFingerprint: fingerprint);
        });
        await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.RecordOutcomeUnknownAsync(clientOpId, "Sale", actorId: actorA, terminalId: terminalT2, payloadFingerprint: fingerprint);
        });

        // RecordSuccessWithResponseAsync: divergent actor & divergent terminal
        await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.RecordSuccessWithResponseAsync(clientOpId, "Sale", Guid.NewGuid(), "{}", actorId: actorB, terminalId: terminalT1, payloadFingerprint: fingerprint);
        });
        await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
        {
            await ledger.RecordSuccessWithResponseAsync(clientOpId, "Sale", Guid.NewGuid(), "{}", actorId: actorA, terminalId: terminalT2, payloadFingerprint: fingerprint);
        });

        // Verify unmutated state
        var outcome = await ledger.GetOutcomeAsync(clientOpId);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Pending, outcome.State);
        Assert.Equal(actorA, outcome.ActorId);
        Assert.Equal(terminalT1, outcome.TerminalId);
    }
}
