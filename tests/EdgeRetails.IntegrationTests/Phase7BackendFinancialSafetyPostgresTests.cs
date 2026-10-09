using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7BackendFinancialSafetyPostgresTests
{
    [Fact]
    public async Task B01_SupplierPayment_ExactReplaySucceeds_AndMutatedFieldsFailWithPayloadMismatch()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, actor2Id) = await SeedSupplierAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);
        await SeedSupplierPayableBalanceAsync(provider, supplierId, actorId, 5000m);

        var operationId = Guid.NewGuid();
        var baselineCommand = new CreateSupplierPaymentCommand(
            supplierId,
            500m,
            SupplierPaymentPurpose.Settlement,
            SupplierSettlementMethod.CashDrawer,
            actorId,
            operationId,
            "REF-PAY-100",
            "Original payment note"
        );

        // 1. Initial execution
        Guid paymentId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var result = await handler.HandleAsync(baselineCommand, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.False(result.Value!.WasExisting);
            Assert.NotEqual(Guid.Empty, result.Value.PaymentId);
            paymentId = result.Value.PaymentId;
        }

        // Verify durable outcome recorded
        await using (var scope = provider.CreateAsyncScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            var outcome = await ledger.GetOutcomeAsync(operationId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal("SupplierPayment", outcome.OperationType);
        }

        // 2. Exact replay returns success and wasExisting: true
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var replayResult = await handler.HandleAsync(baselineCommand, default);
            Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
            Assert.True(replayResult.Value!.WasExisting);
        }

        // 3. Mutated Purpose fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var mutated = baselineCommand with { Purpose = SupplierPaymentPurpose.Advance };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 4. Mutated Method fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var mutated = baselineCommand with { Method = SupplierSettlementMethod.Bank };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 5. Mutated Amount fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var mutated = baselineCommand with { Amount = 600m };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 6. Mutated ExternalReference fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var mutated = baselineCommand with { ExternalReference = "REF-MUTATED" };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 7. Mutated Note fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var mutated = baselineCommand with { Note = "Mutated note" };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 8. Mutated ActorId fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var mutated = baselineCommand with { ActorId = actor2Id };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 9. Verify single effect in database (1 payment entity, 1 sae from opening + 1 sae from payment)
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Single(await db.SupplierPayments.Where(x => x.ClientOperationId == operationId).ToListAsync());
            Assert.Single(await db.SupplierAccountEntries.Where(x => x.ClientOperationId == operationId).ToListAsync());
            Assert.Single(await db.CashMovements.Where(x => x.SourceType == "SUPPLIER_PAYMENT" && x.SourceId == paymentId).ToListAsync());
        }
    }

    [Fact]
    public async Task B02_B03_PostExpense_ExactReplaySucceeds_MutatedFieldsFail_AndCanonicalOutcomeRecorded()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (categoryId, category2Id, actorId, actor2Id) = await SeedExpenseCategoriesAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);

        var operationId = Guid.NewGuid();
        var baselineCommand = new PostExpenseCommand(
            operationId,
            categoryId,
            null,
            DateOnly.FromDateTime(DateTime.UtcNow),
            350m,
            ExpensePaymentMethod.Cash,
            "EXP-REF-01",
            "Store utility bill",
            actorId
        );

        // 1. Initial execution
        Guid expenseId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var result = await handler.HandleAsync(baselineCommand, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.False(result.Value!.WasExisting);
            expenseId = result.Value.ExpenseId;
            Assert.NotEqual(Guid.Empty, expenseId);
        }

        // B03: Verify canonical outcome recorded atomically
        await using (var scope = provider.CreateAsyncScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            var outcome = await ledger.GetOutcomeAsync(operationId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal("Expense", outcome.OperationType);
            Assert.Equal(expenseId, outcome.EntityId);
        }

        // 2. Exact replay returns success and wasExisting: true
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var replayResult = await handler.HandleAsync(baselineCommand, default);
            Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
            Assert.True(replayResult.Value!.WasExisting);
            Assert.Equal(expenseId, replayResult.Value.ExpenseId);
        }

        // 3. Mutated Amount fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var mutated = baselineCommand with { Amount = 400m };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 4. Mutated PaymentMethod fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var mutated = baselineCommand with { PaymentMethod = ExpensePaymentMethod.Bank };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 5. Mutated Category fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var mutated = baselineCommand with { CategoryId = category2Id };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 6. Mutated Description fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var mutated = baselineCommand with { Description = "Mutated expense description" };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 7. Mutated Reference fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var mutated = baselineCommand with { Reference = "EXP-MUTATED-REF" };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 8. Mutated ActorId fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var mutated = baselineCommand with { ActorId = actor2Id };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 9. Verify single expense row and cash movement
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Single(await db.Expenses.Where(x => x.ClientOperationId == operationId).ToListAsync());
            Assert.Single(await db.CashMovements.Where(x => x.SourceId == expenseId).ToListAsync());
        }
    }

    [Fact]
    public async Task B04_SupplierRefund_ExactReplaySucceeds_MutatedFieldsFail_AndCanonicalOutcomeRecorded()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, _) = await SeedSupplierAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);

        // Pre-requisite for refund: Supplier must have advance/credit balance
        var prePayOp = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var payHandler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var advanceRes = await payHandler.HandleAsync(new CreateSupplierPaymentCommand(
                supplierId, 500m, SupplierPaymentPurpose.Advance, SupplierSettlementMethod.CashDrawer,
                actorId, prePayOp, "PRE-ADV", "Advance for refund"), default);
            Assert.True(advanceRes.IsSuccess, advanceRes.Error?.Message);
        }

        var operationId = Guid.NewGuid();
        var baselineCommand = new CreateSupplierRefundCommand(
            supplierId,
            200m,
            SupplierSettlementMethod.CashDrawer,
            actorId,
            operationId,
            "REFUND-EXT-01",
            "MANUAL",
            null,
            "Defective batch refund note"
        );

        // 1. Initial execution
        Guid refundId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>();
            var result = await handler.HandleAsync(baselineCommand, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.False(result.Value!.WasExisting);
            refundId = result.Value.RefundId;
            Assert.NotEqual(Guid.Empty, refundId);
        }

        // B04: Verify canonical outcome recorded atomically
        await using (var scope = provider.CreateAsyncScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            var outcome = await ledger.GetOutcomeAsync(operationId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal("SupplierRefund", outcome.OperationType);
            Assert.Equal(refundId, outcome.EntityId);
        }

        // 2. Exact replay returns success and wasExisting: true
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>();
            var replayResult = await handler.HandleAsync(baselineCommand, default);
            Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
            Assert.True(replayResult.Value!.WasExisting);
            Assert.Equal(refundId, replayResult.Value.RefundId);
        }

        // 3. Mutated Amount fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>();
            var mutated = baselineCommand with { Amount = 300m };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 4. Mutated Method fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>();
            var mutated = baselineCommand with { Method = SupplierSettlementMethod.Bank };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 5. Mutated Note fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>();
            var mutated = baselineCommand with { Note = "Mutated refund note" };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 6. Mutated ExternalReference fails
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>();
            var mutated = baselineCommand with { ExternalReference = "REFUND-MUTATED-REF" };
            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 7. Verify single refund and ledger entry
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Single(await db.SupplierRefunds.Where(x => x.ClientOperationId == operationId).ToListAsync());
            Assert.Single(await db.SupplierAccountEntries.Where(x => x.ClientOperationId == operationId).ToListAsync());
        }
    }

    [Fact]
    public async Task B05_ReverseSupplierPayment_ReplaySucceeds_CompetingOperationFails_AndOutcomeRecorded()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, _) = await SeedSupplierAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);
        await SeedSupplierPayableBalanceAsync(provider, supplierId, actorId, 5000m);

        // 1. Create payment
        Guid paymentId;
        var payOp = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var res = await handler.HandleAsync(new CreateSupplierPaymentCommand(
                supplierId, 400m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.CashDrawer,
                actorId, payOp, "PAY-FOR-REV", "Pre-reversal payment"), default);
            Assert.True(res.IsSuccess, res.Error?.Message);
            paymentId = res.Value!.PaymentId;
        }

        // 2. Perform reversal with reversalOp1
        var reversalOp1 = Guid.NewGuid();
        var reversalCommand = new ReverseSupplierPaymentCommand(
            paymentId,
            "Reversal due to duplicate charge",
            actorId,
            reversalOp1
        );

        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierPaymentHandler>();
            var result = await handler.HandleAsync(reversalCommand, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }

        // B05: Verify canonical outcome recorded for reversalOp1
        await using (var scope = provider.CreateAsyncScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            var outcome = await ledger.GetOutcomeAsync(reversalOp1);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal("SupplierPaymentReversal", outcome.OperationType);
        }

        // 3. Exact replay of reversalOp1 succeeds idempotently
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierPaymentHandler>();
            var replayResult = await handler.HandleAsync(reversalCommand, default);
            Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
        }

        // 4. Same reversalOp1 with mutated Reason fails with payload_mismatch
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierPaymentHandler>();
            var mutatedResult = await handler.HandleAsync(reversalCommand with { Reason = "Altered reason" }, default);
            Assert.False(mutatedResult.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", mutatedResult.Error?.Code);
        }

        // 5. Competing operation ID racing the same reversal target fails with supplier.already_reversed
        var competingOp = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierPaymentHandler>();
            var competingResult = await handler.HandleAsync(reversalCommand with { ClientOperationId = competingOp }, default);
            Assert.False(competingResult.IsSuccess);
            Assert.Equal("supplier.already_reversed", competingResult.Error?.Code);
        }

        // 6. Verify single reversal row in database
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Single(await db.SupplierPaymentReversals.Where(x => x.SupplierPaymentId == paymentId).ToListAsync());
        }
    }

    [Fact]
    public async Task B05_ReverseSupplierRefund_ReplaySucceeds_CompetingOperationFails_AndOutcomeRecorded()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, _) = await SeedSupplierAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);

        // Pre-requisite: Give supplier advance balance
        var prePayOp = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var payHandler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var advanceRes = await payHandler.HandleAsync(new CreateSupplierPaymentCommand(
                supplierId, 500m, SupplierPaymentPurpose.Advance, SupplierSettlementMethod.CashDrawer,
                actorId, prePayOp, "PRE-ADV-REF", "Advance for refund reversal test"), default);
            Assert.True(advanceRes.IsSuccess);
        }

        // 1. Create refund
        Guid refundId;
        var refundOp = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>();
            var res = await handler.HandleAsync(new CreateSupplierRefundCommand(
                supplierId, 150m, SupplierSettlementMethod.CashDrawer, actorId, refundOp,
                "REFUND-FOR-REV", "MANUAL", null, "Pre-reversal refund"), default);
            Assert.True(res.IsSuccess);
            refundId = res.Value!.RefundId;
        }

        // 2. Perform reversal with reversalOp2
        var reversalOp2 = Guid.NewGuid();
        var reversalCommand = new ReverseSupplierRefundCommand(
            refundId,
            "Reversal of mistaken refund",
            actorId,
            reversalOp2
        );

        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierRefundHandler>();
            var result = await handler.HandleAsync(reversalCommand, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }

        // B05: Verify canonical outcome recorded for reversalOp2
        await using (var scope = provider.CreateAsyncScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            var outcome = await ledger.GetOutcomeAsync(reversalOp2);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal("SupplierRefundReversal", outcome.OperationType);
        }

        // 3. Exact replay of reversalOp2 succeeds idempotently
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierRefundHandler>();
            var replayResult = await handler.HandleAsync(reversalCommand, default);
            Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
        }

        // 4. Same reversalOp2 with mutated Reason fails with payload_mismatch
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierRefundHandler>();
            var mutatedResult = await handler.HandleAsync(reversalCommand with { Reason = "Altered refund reason" }, default);
            Assert.False(mutatedResult.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", mutatedResult.Error?.Code);
        }

        // 5. Competing operation ID racing the same reversal target fails with supplier.already_reversed
        var competingOp = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReverseSupplierRefundHandler>();
            var competingResult = await handler.HandleAsync(reversalCommand with { ClientOperationId = competingOp }, default);
            Assert.False(competingResult.IsSuccess);
            Assert.Equal("supplier.already_reversed", competingResult.Error?.Code);
        }

        // 6. Verify single reversal row in database
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Single(await db.SupplierRefundReversals.Where(x => x.SupplierRefundId == refundId).ToListAsync());
        }
    }

    [Fact]
    public async Task B06_AuthenticatedOperationStatus_EnforcesActorScope_AndPreventsCrossActorLeakage()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actor1Id, actor2Id) = await SeedSupplierAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actor1Id, 10000m);
        await SeedSupplierPayableBalanceAsync(provider, supplierId, actor1Id, 5000m);

        var operationId = Guid.NewGuid();
        var command = new CreateSupplierPaymentCommand(
            supplierId, 300m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.CashDrawer,
            actor1Id, operationId, "REF-B06", "B06 Status Test Note");

        Guid paymentId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var res = await handler.HandleAsync(command, default);
            Assert.True(res.IsSuccess, res.Error?.Message);
            paymentId = res.Value!.PaymentId;
        }

        // 1. Rightful actor query with RequireCanonicalOutcome: true succeeds
        await using (var scope = provider.CreateAsyncScope())
        {
            var statusHandler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: operationId,
                ActorId: actor1Id,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var statusResult = await statusHandler.HandleAsync(query, default);
            Assert.True(statusResult.IsSuccess, statusResult.Error?.Message);
            Assert.True(statusResult.Value!.Found);
            Assert.Equal("Succeeded", statusResult.Value.Status);
            Assert.Equal("SupplierPayment", statusResult.Value.OperationType);
            Assert.Equal(paymentId, statusResult.Value.EntityId);
        }

        // 2. Cross-actor query is strictly forbidden
        await using (var scope = provider.CreateAsyncScope())
        {
            var statusHandler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: operationId,
                ActorId: actor2Id,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var statusResult = await statusHandler.HandleAsync(query, default);
            Assert.False(statusResult.IsSuccess);
            Assert.Equal("authorization.forbidden", statusResult.Error?.Code);
        }

        // 3. Unknown operation ID with RequireCanonicalOutcome: true returns OutcomeUnknown without leaking
        await using (var scope = provider.CreateAsyncScope())
        {
            var statusHandler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: Guid.NewGuid(),
                ActorId: actor1Id,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var statusResult = await statusHandler.HandleAsync(query, default);
            Assert.True(statusResult.IsSuccess);
            Assert.False(statusResult.Value!.Found);
            Assert.Equal("OutcomeUnknown", statusResult.Value.Status);
        }
    }

    [Fact]
    public async Task LegacyRowWithoutCanonicalOutcome_CannotBypassSemanticIdentityChecks_AndBackfillsOutcome()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, _) = await SeedSupplierAndActorsAsync(provider);
        var legacyOp = Guid.NewGuid();

        // 1. Manually insert legacy SupplierPayment row with NO OperationOutcome
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var legacyPayment = new SupplierPayment
            {
                PaymentNumber = "SP-LEGACY-01",
                SupplierId = supplierId,
                Amount = 750m,
                Purpose = SupplierPaymentPurpose.Advance,
                Method = SupplierSettlementMethod.External,
                ExternalReference = "LEGACY-REF",
                PaidAt = DateTimeOffset.UtcNow,
                ActorId = actorId,
                ClientOperationId = legacyOp,
                Note = "Historical un-migrated payment"
            };
            db.SupplierPayments.Add(legacyPayment);
            await db.SaveChangesAsync();

            // Verify no outcome exists
            Assert.False(await db.OperationOutcomes.AnyAsync(x => x.ClientOperationId == legacyOp));
        }

        // 2. Caller replays legacyOp with MUTATED Amount -> must fail closed
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var mutated = new CreateSupplierPaymentCommand(
                supplierId, 800m, SupplierPaymentPurpose.Advance, SupplierSettlementMethod.External,
                actorId, legacyOp, "LEGACY-REF", "Historical un-migrated payment");

            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 3. Caller replays legacyOp with MUTATED Purpose -> must fail closed
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var mutated = new CreateSupplierPaymentCommand(
                supplierId, 750m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.External,
                actorId, legacyOp, "LEGACY-REF", "Historical un-migrated payment");

            var result = await handler.HandleAsync(mutated, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        }

        // 4. Exact replay of legacy row succeeds and backfills canonical outcome
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var exact = new CreateSupplierPaymentCommand(
                supplierId, 750m, SupplierPaymentPurpose.Advance, SupplierSettlementMethod.External,
                actorId, legacyOp, "LEGACY-REF", "Historical un-migrated payment");

            var result = await handler.HandleAsync(exact, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(result.Value!.WasExisting);
            Assert.Equal("SP-LEGACY-01", result.Value.PaymentNumber);
        }

        // 5. Verify outcome is now backfilled and discoverable
        await using (var scope = provider.CreateAsyncScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            var outcome = await ledger.GetOutcomeAsync(legacyOp);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal("SupplierPayment", outcome.OperationType);
        }
    }

    [Fact]
    public async Task SameTransaction_AtomicOutcomePersistence_RollsBackOutcomeOnFailure()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (categoryId, _, actorId, _) = await SeedExpenseCategoriesAndActorsAsync(provider);
        var failingOp = Guid.NewGuid();

        // 1. Post expense with invalid subcategory ID causing handler failure inside transaction
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var failingCommand = new PostExpenseCommand(
                failingOp, categoryId, Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow),
                500m, ExpensePaymentMethod.Cash, "FAIL-REF", "Failing expense", actorId);

            var result = await handler.HandleAsync(failingCommand, default);
            Assert.False(result.IsSuccess);
            Assert.Equal("expense.subcategory_invalid", result.Error?.Code);
        }

        // 2. Verify neither expense nor success outcome was persisted
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.False(await db.Expenses.AnyAsync(x => x.ClientOperationId == failingOp));
            var outcome = await db.OperationOutcomes.FirstOrDefaultAsync(x => x.ClientOperationId == failingOp);
            Assert.True(outcome is null || outcome.Status != OperationOutcomeStatus.Succeeded);
        }
    }

    [Fact]
    public async Task T04_CrossTerminal_AndCrossActor_OperationStatusQuery_RejectsMismatchedContext()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actor1Id, actor2Id) = await SeedSupplierAndActorsAsync(provider);
        var terminal1Id = Guid.NewGuid();
        var terminal2Id = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        // Record a canonical outcome with terminal1Id and actor1Id
        await using (var scope = provider.CreateAsyncScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            await ledger.RecordSuccessAsync(
                operationId,
                "SupplierPayment",
                Guid.NewGuid(),
                "SP-TERM-001",
                actorId: actor1Id,
                terminalId: terminal1Id);
        }

        // 1. Cross-terminal query rejected with authorization.forbidden
        await using (var scope = provider.CreateAsyncScope())
        {
            var statusHandler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: operationId,
                ActorId: actor1Id,
                TerminalId: terminal2Id,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var res = await statusHandler.HandleAsync(query, default);
            Assert.False(res.IsSuccess);
            Assert.Equal("authorization.forbidden", res.Error?.Code);
        }

        // 2. Cross-actor query rejected with authorization.forbidden
        await using (var scope = provider.CreateAsyncScope())
        {
            var statusHandler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: operationId,
                ActorId: actor2Id,
                TerminalId: terminal1Id,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var res = await statusHandler.HandleAsync(query, default);
            Assert.False(res.IsSuccess);
            Assert.Equal("authorization.forbidden", res.Error?.Code);
        }

        // 3. Authorized matching actor and terminal query succeeds
        await using (var scope = provider.CreateAsyncScope())
        {
            var statusHandler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: operationId,
                ActorId: actor1Id,
                TerminalId: terminal1Id,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var res = await statusHandler.HandleAsync(query, default);
            Assert.True(res.IsSuccess, res.Error?.Message);
            Assert.True(res.Value!.Found);
            Assert.Equal("Succeeded", res.Value.Status);
            Assert.Equal("SupplierPayment", res.Value.OperationType);
        }
    }

    [Fact]
    public async Task T05_ConcurrentSameId_SupplierPayment_ExecutesExactlyOnce()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, _) = await SeedSupplierAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);
        await SeedSupplierPayableBalanceAsync(provider, supplierId, actorId, 5000m);

        var operationId = Guid.NewGuid();
        var command = new CreateSupplierPaymentCommand(
            supplierId,
            450m,
            SupplierPaymentPurpose.Settlement,
            SupplierSettlementMethod.CashDrawer,
            actorId,
            operationId,
            "CONCURRENT-PAY-01",
            "Concurrent same-id test"
        );

        // Run two concurrent tasks with the same ClientOperationId
        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            return await handler.HandleAsync(command, default);
        });

        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            return await handler.HandleAsync(command, default);
        });

        var results = await Task.WhenAll(task1, task2);
        Assert.True(results[0].IsSuccess, results[0].Error?.Message);
        Assert.True(results[1].IsSuccess, results[1].Error?.Message);

        var res0 = results[0].Value;
        var res1 = results[1].Value;
        Assert.NotNull(res0);
        Assert.NotNull(res1);
        Assert.Equal(res0.PaymentId, res1.PaymentId);
        Assert.True(res0.WasExisting != res1.WasExisting || (res0.WasExisting && res1.WasExisting));

        // Exactly one payment, one SAE, one cash movement, and one outcome in DB
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Single(await db.SupplierPayments.Where(x => x.ClientOperationId == operationId).ToListAsync());
            Assert.Single(await db.SupplierAccountEntries.Where(x => x.ClientOperationId == operationId).ToListAsync());
            Assert.Single(await db.CashMovements.Where(x => x.SourceType == "SUPPLIER_PAYMENT" && x.SourceId == res0.PaymentId).ToListAsync());
            Assert.Single(await db.OperationOutcomes.Where(x => x.ClientOperationId == operationId).ToListAsync());
        }
    }

    [Fact]
    public async Task T08_Commit_LostResponse_StatusLookup_ExactReplay_SingleCommittedEffect()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, _) = await SeedSupplierAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);
        await SeedSupplierPayableBalanceAsync(provider, supplierId, actorId, 5000m);

        var operationId = Guid.NewGuid();
        var command = new CreateSupplierPaymentCommand(
            supplierId,
            650m,
            SupplierPaymentPurpose.Settlement,
            SupplierSettlementMethod.CashDrawer,
            actorId,
            operationId,
            "LOST-RESP-01",
            "Lost response recovery test"
        );

        // 1. Initial commit
        Guid paymentId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var res = await handler.HandleAsync(command, default);
            Assert.True(res.IsSuccess, res.Error?.Message);
            paymentId = res.Value!.PaymentId;
        }

        // 2. Simulated lost response: client does not receive result, queries status
        await using (var scope = provider.CreateAsyncScope())
        {
            var statusHandler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
            var query = new OperationStatusQuery(
                ClientOperationId: operationId,
                ActorId: actorId,
                RequireIdentityScope: true,
                RequireCanonicalOutcome: true);

            var status = await statusHandler.HandleAsync(query, default);
            Assert.True(status.IsSuccess);
            Assert.True(status.Value!.Found);
            Assert.Equal("Succeeded", status.Value.Status);
            Assert.Equal(paymentId, status.Value.EntityId);
        }

        // 3. Client replays the exact original command
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var replayRes = await handler.HandleAsync(command, default);
            Assert.True(replayRes.IsSuccess);
            Assert.True(replayRes.Value!.WasExisting);
            Assert.Equal(paymentId, replayRes.Value.PaymentId);
        }

        // 4. Assert database retains strictly one effect
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Single(await db.SupplierPayments.Where(x => x.ClientOperationId == operationId).ToListAsync());
            Assert.Single(await db.SupplierAccountEntries.Where(x => x.ClientOperationId == operationId).ToListAsync());
            Assert.Single(await db.CashMovements.Where(x => x.SourceId == paymentId).ToListAsync());
            Assert.Single(await db.OperationOutcomes.Where(x => x.ClientOperationId == operationId).ToListAsync());
        }
    }

    [Fact]
    public async Task T09_ServerRestart_FreshDiContainer_SupportedOutcomeRecovered()
    {
        var operationId = Guid.NewGuid();
        Guid actorId;
        Guid categoryId;
        Guid expenseId;

        // Scope 1: Initial server container posts expense
        await using (var provider1 = Phase2PostgresTestHarness.BuildProvider())
        {
            await using (var seedScope = provider1.CreateAsyncScope())
            {
                var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                await CloseOpenSessionsAsync(seedDb);
            }

            var (cat1, _, act1, _) = await SeedExpenseCategoriesAndActorsAsync(provider1);
            categoryId = cat1;
            actorId = act1;
            await SeedCashSessionAsync(provider1, actorId, 10000m);

            var command = new PostExpenseCommand(
                operationId,
                categoryId,
                null,
                DateOnly.FromDateTime(DateTime.UtcNow),
                275m,
                ExpensePaymentMethod.Cash,
                "RESTART-01",
                "Restart recovery expense",
                actorId);

            await using var scope = provider1.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var res = await handler.HandleAsync(command, default);
            Assert.True(res.IsSuccess, res.Error?.Message);
            expenseId = res.Value!.ExpenseId;
        } // provider1 completely disposed simulating server restart

        // Scope 2: Fresh server container queries status and performs replay
        await using (var provider2 = Phase2PostgresTestHarness.BuildProvider())
        {
            // Status recovery
            await using (var scope = provider2.CreateAsyncScope())
            {
                var statusHandler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
                var status = await statusHandler.HandleAsync(new OperationStatusQuery(
                    ClientOperationId: operationId,
                    ActorId: actorId,
                    RequireIdentityScope: true,
                    RequireCanonicalOutcome: true), default);

                Assert.True(status.IsSuccess);
                Assert.True(status.Value!.Found);
                Assert.Equal("Succeeded", status.Value.Status);
                Assert.Equal(expenseId, status.Value.EntityId);
            }

            // Exact replay after restart
            await using (var scope = provider2.CreateAsyncScope())
            {
                var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
                var replayRes = await handler.HandleAsync(new PostExpenseCommand(
                    operationId,
                    categoryId,
                    null,
                    DateOnly.FromDateTime(DateTime.UtcNow),
                    275m,
                    ExpensePaymentMethod.Cash,
                    "RESTART-01",
                    "Restart recovery expense",
                    actorId), default);

                Assert.True(replayRes.IsSuccess);
                Assert.True(replayRes.Value!.WasExisting);
                Assert.Equal(expenseId, replayRes.Value.ExpenseId);
            }

            // Verify single effect in DB
            await using (var scope = provider2.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                Assert.Single(await db.Expenses.Where(x => x.ClientOperationId == operationId).ToListAsync());
                Assert.Single(await db.CashMovements.Where(x => x.SourceId == expenseId).ToListAsync());
            }
        }
    }

    [Fact]
    public async Task T10_Expense_PostAndVoid_SingleCompensation_ExactRestoredCashBalance()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (categoryId, _, actorId, _) = await SeedExpenseCategoriesAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);

        var postOp = Guid.NewGuid();
        Guid expenseId;
        // 1. Post expense of 400m
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var res = await handler.HandleAsync(new PostExpenseCommand(
                postOp, categoryId, null, DateOnly.FromDateTime(DateTime.UtcNow),
                400m, ExpensePaymentMethod.Cash, "VOID-TEST-EXP", "To be voided", actorId), default);
            Assert.True(res.IsSuccess, res.Error?.Message);
            expenseId = res.Value!.ExpenseId;
        }

        // 2. Void the expense
        var voidOp = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var voidHandler = scope.ServiceProvider.GetRequiredService<VoidExpenseHandler>();
            var voidRes = await voidHandler.HandleAsync(new VoidExpenseCommand(
                expenseId, actorId, voidOp, "Mistaken expense entry"), default);
            Assert.True(voidRes.IsSuccess, voidRes.Error?.Message);
        }

        // 3. Exact replay of void succeeds
        await using (var scope = provider.CreateAsyncScope())
        {
            var voidHandler = scope.ServiceProvider.GetRequiredService<VoidExpenseHandler>();
            var replayRes = await voidHandler.HandleAsync(new VoidExpenseCommand(
                expenseId, actorId, voidOp, "Mistaken expense entry"), default);
            Assert.True(replayRes.IsSuccess);
        }

        // 4. Verify restored cash drawer and DB invariants
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var expense = await db.Expenses.FirstAsync(x => x.Id == expenseId);
            Assert.Equal(ExpenseStatus.Voided, expense.Status);

            var movements = await db.CashMovements.Where(x => x.SourceId == expenseId).ToListAsync();
            Assert.Equal(2, movements.Count);
            var cashOut = movements.First(x => x.Direction == CashMovementDirection.Out).Amount;
            var cashIn = movements.First(x => x.Direction == CashMovementDirection.In).Amount;
            Assert.Equal(400m, cashOut);
            Assert.Equal(400m, cashIn);

            // Outcome recorded for voidOp
            var voidOutcome = await db.OperationOutcomes.FirstOrDefaultAsync(x => x.ClientOperationId == voidOp);
            Assert.NotNull(voidOutcome);
            Assert.Equal(OperationOutcomeStatus.Succeeded, voidOutcome.Status);
        }
    }

    [Fact]
    public async Task T11_SupplierLedgerCashAuditOutcome_ReconciliationZeroVariance()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, _) = await SeedSupplierAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);
        await SeedSupplierPayableBalanceAsync(provider, supplierId, actorId, 5000m);

        var paymentOp = Guid.NewGuid();
        var paymentAmount = 1200m;

        Guid paymentId;
        // Post payment
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var res = await handler.HandleAsync(new CreateSupplierPaymentCommand(
                supplierId, paymentAmount, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.CashDrawer,
                actorId, paymentOp, "RECON-PAY-01", "Reconciliation test payment"), default);
            Assert.True(res.IsSuccess, res.Error?.Message);
            paymentId = res.Value!.PaymentId;
        }

        // Audit full reconciliation across all tables
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

            // 1. Supplier balance: opening (5000) - payment (1200) = 3800
            var accountRepo = scope.ServiceProvider.GetRequiredService<ISupplierAccountRepository>();
            var currentBalance = await accountRepo.GetCurrentBalanceAsync(supplierId, default);
            Assert.Equal(3800m, currentBalance);

            // 2. Supplier account entries sum
            var entries = await db.SupplierAccountEntries.Where(x => x.SupplierId == supplierId).ToListAsync();
            var netEntries = entries.Sum(x => x.Direction == SupplierAccountDirection.IncreasePayable ? x.Amount : -x.Amount);
            Assert.Equal(3800m, netEntries);

            // 3. Cash movement
            var cashMovements = await db.CashMovements.Where(x => x.SourceType == "SUPPLIER_PAYMENT" && x.SourceId == paymentId).ToListAsync();
            Assert.Single(cashMovements);
            Assert.Equal(paymentAmount, cashMovements[0].Amount);
            Assert.Equal(CashMovementDirection.Out, cashMovements[0].Direction);

            // 4. Business audit record
            var audits = await db.BusinessAuditEvents.Where(x => x.CorrelationId == paymentOp).ToListAsync();
            Assert.Single(audits);
            Assert.Equal("SUPPLIER_PAYMENT_POSTED", audits[0].Action);

            // 5. Operation outcome
            var outcome = await db.OperationOutcomes.FirstOrDefaultAsync(x => x.ClientOperationId == paymentOp);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);

            // Variance: 0.00m
            var variance = Math.Abs(currentBalance - netEntries);
            Assert.Equal(0.00m, variance);
        }
    }

    [Fact]
    public async Task T12_CrossType_ClientOperationId_CollisionRejected()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, _) = await SeedSupplierAndActorsAsync(provider);
        var (categoryId, _, _, _) = await SeedExpenseCategoriesAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);
        await SeedSupplierPayableBalanceAsync(provider, supplierId, actorId, 5000m);

        var sharedOpId = Guid.NewGuid();

        // 1. Post SupplierPayment using sharedOpId
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var res = await handler.HandleAsync(new CreateSupplierPaymentCommand(
                supplierId, 300m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.CashDrawer,
                actorId, sharedOpId, "CROSS-TYPE-PAY", "Legitimate payment"), default);
            Assert.True(res.IsSuccess, res.Error?.Message);
        }

        // 2. Attempt to use sharedOpId for PostExpense -> rejected with idempotency.payload_mismatch
        await using (var scope = provider.CreateAsyncScope())
        {
            var expenseHandler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var expenseRes = await expenseHandler.HandleAsync(new PostExpenseCommand(
                sharedOpId, categoryId, null, DateOnly.FromDateTime(DateTime.UtcNow),
                300m, ExpensePaymentMethod.Cash, "CROSS-TYPE-EXP", "Colliding expense", actorId), default);

            Assert.False(expenseRes.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", expenseRes.Error?.Code);
        }

        // 3. Attempt to use sharedOpId for CreateSupplierRefund -> rejected with idempotency.payload_mismatch
        await using (var scope = provider.CreateAsyncScope())
        {
            var refundHandler = scope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>();
            var refundRes = await refundHandler.HandleAsync(new CreateSupplierRefundCommand(
                supplierId, 300m, SupplierSettlementMethod.CashDrawer, actorId, sharedOpId,
                "CROSS-REF", "MANUAL", null, "Colliding refund"), default);

            Assert.False(refundRes.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", refundRes.Error?.Code);
        }

        // 4. Reverse scenario: Create Expense with expenseOpId, then attempt SupplierPayment with same ID
        var expenseOpId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var expenseHandler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
            var res = await expenseHandler.HandleAsync(new PostExpenseCommand(
                expenseOpId, categoryId, null, DateOnly.FromDateTime(DateTime.UtcNow),
                150m, ExpensePaymentMethod.Cash, "LEGIT-EXP", "Legitimate expense", actorId), default);
            Assert.True(res.IsSuccess);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var payHandler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var payRes = await payHandler.HandleAsync(new CreateSupplierPaymentCommand(
                supplierId, 150m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.CashDrawer,
                actorId, expenseOpId, "COLLIDING-PAY", "Colliding payment"), default);

            Assert.False(payRes.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", payRes.Error?.Code);
        }
    }

    [Fact]
    public async Task T14_RetentionSafety_ReplayableOperationsRetainedWithinHorizon()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await CloseOpenSessionsAsync(seedDb);
        }

        var (supplierId, actorId, _) = await SeedSupplierAndActorsAsync(provider);
        await SeedCashSessionAsync(provider, actorId, 10000m);
        await SeedSupplierPayableBalanceAsync(provider, supplierId, actorId, 5000m);

        var retentionOp = Guid.NewGuid();
        var baselineCommand = new CreateSupplierPaymentCommand(
            supplierId, 500m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.CashDrawer,
            actorId, retentionOp, "RETENTION-01", "Retention safety test payment");

        // 1. Initial post
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var res = await handler.HandleAsync(baselineCommand, default);
            Assert.True(res.IsSuccess, res.Error?.Message);
        }

        // 2. Verify outcome is durably retained with immutable fingerprint and timestamps
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcome = await db.OperationOutcomes.FirstOrDefaultAsync(x => x.ClientOperationId == retentionOp);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
            Assert.False(string.IsNullOrWhiteSpace(outcome.PayloadFingerprint));
            Assert.True(outcome.WasCommitted);
            Assert.True(outcome.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        // 3. Replay within retention horizon succeeds idempotently
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>();
            var replayRes = await handler.HandleAsync(baselineCommand, default);
            Assert.True(replayRes.IsSuccess);
            Assert.True(replayRes.Value!.WasExisting);
        }
    }

    private static async Task SeedSupplierPayableBalanceAsync(ServiceProvider provider, Guid supplierId, Guid actorId, decimal amount)
    {
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SupplierOpeningBalanceHandler>();
        var res = await handler.HandleAsync(new SupplierOpeningBalanceCommand(
            supplierId,
            SupplierAccountDirection.IncreasePayable,
            amount,
            DateTimeOffset.UtcNow,
            "Opening payable for settlement tests",
            actorId,
            Guid.NewGuid()
        ), default);
        Assert.True(res.IsSuccess, res.Error?.Message);
    }

    private static async Task<(Guid SupplierId, Guid Actor1Id, Guid Actor2Id)> SeedSupplierAndActorsAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var supplier = await Phase2PostgresTestHarness.SeedSupplierAsync(db);
        var actor1 = await IntegrationIdentitySeeder.CreateActorAsync(db);
        var actor2 = await IntegrationIdentitySeeder.CreateActorAsync(db);
        return (supplier.Id, actor1, actor2);
    }

    private static async Task<(Guid Category1Id, Guid Category2Id, Guid Actor1Id, Guid Actor2Id)> SeedExpenseCategoriesAndActorsAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var cat1 = new ExpenseCategory { Name = "Cat1-" + Guid.NewGuid().ToString("N")[..8], IsActive = true };
        var cat2 = new ExpenseCategory { Name = "Cat2-" + Guid.NewGuid().ToString("N")[..8], IsActive = true };
        db.ExpenseCategories.AddRange(cat1, cat2);
        await db.SaveChangesAsync();
        var actor1 = await IntegrationIdentitySeeder.CreateActorAsync(db);
        var actor2 = await IntegrationIdentitySeeder.CreateActorAsync(db);
        return (cat1.Id, cat2.Id, actor1, actor2);
    }

    private static async Task SeedCashSessionAsync(ServiceProvider provider, Guid actorId, decimal openingCash)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(db, actorId, openingCash);
    }

    private static async Task CloseOpenSessionsAsync(EdgeRetailsDbContext db)
    {
        var sessions = await db.CashSessions.Where(x => x.Status == CashSessionStatus.Open).ToListAsync();
        foreach (var session in sessions)
        {
            var movements = await db.CashMovements.AsNoTracking().Where(x => x.CashSessionId == session.Id).ToListAsync();
            var cashIn = movements.Where(x => x.Direction == CashMovementDirection.In).Sum(x => x.Amount);
            var cashOut = movements.Where(x => x.Direction == CashMovementDirection.Out).Sum(x => x.Amount);
            session.Close(cashIn, cashOut, Math.Max(0m, session.OpeningCash + cashIn - cashOut),
                session.OpenedBy, DateTimeOffset.UtcNow);
        }
        if (sessions.Count > 0)
        {
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
    }
}
