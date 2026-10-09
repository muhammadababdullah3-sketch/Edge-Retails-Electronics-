using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Finance;

namespace EdgeRetails.UnitTests;

public sealed class TrackingExpenseVoidReplayTests
{
    [Theory]
    [InlineData("reason")]
    [InlineData("expense")]
    [InlineData("actor")]
    public async Task Same_id_replay_is_bound_to_original_void_intent(string changed)
    {
        var f = new Phase2TestDoubles();
        var expense = new Expense { ExpenseNumber = "VOID-INTENT", Amount = 17m, PaymentMethod = ExpensePaymentMethod.Bank };
        f.Expenses.AddExpense(expense);
        var command = new VoidExpenseCommand(expense.Id, Guid.NewGuid(), Guid.NewGuid(), "Correction");
        var handler = Handler(f);
        Assert.True((await handler.HandleAsync(command, default)).IsSuccess);
        Assert.True((await handler.HandleAsync(command with { Reason = " Correction " }, default)).IsSuccess);
        var changedCommand = changed switch
        {
            "reason" => command with { Reason = "Other intent" },
            "expense" => command with { ExpenseId = Guid.NewGuid() },
            _ => command with { ActorId = Guid.NewGuid() }
        };
        Assert.Equal("idempotency.payload_mismatch", (await handler.HandleAsync(changedCommand, default)).Error?.Code);
        Assert.Single(f.Audit.Records);
        var outcome = await f.OutcomeLedger.GetOutcomeAsync(command.ClientOperationId);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
        Assert.True(outcome.WasCommitted);
        Assert.Equal("ExpenseVoid", outcome.OperationType);
        Assert.Equal(expense.Id, outcome.EntityId);
        Assert.Equal(command.ActorId, outcome.ActorId);
        Assert.False(string.IsNullOrWhiteSpace(outcome.PayloadFingerprint));
    }

    [Fact]
    public async Task Posting_identity_cannot_be_reused_for_void()
    {
        var f = new Phase2TestDoubles();
        var expense = new Expense { ExpenseNumber = "POST-ID", ClientOperationId = Guid.NewGuid(), Amount = 17m,
            PaymentMethod = ExpensePaymentMethod.Bank };
        f.Expenses.AddExpense(expense);
        var result = await Handler(f).HandleAsync(new(expense.Id, Guid.NewGuid(), expense.ClientOperationId, "Correction"), default);
        Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        Assert.Equal(ExpenseStatus.Posted, expense.Status);
        Assert.Empty(f.Audit.Records);
        Assert.Null(await f.OutcomeLedger.GetOutcomeAsync(expense.ClientOperationId));
    }

    [Fact]
    public async Task Expense_status_recovery_preserves_posting_replay()
    {
        var f = new Phase2TestDoubles();
        var actor = Guid.NewGuid();
        var category = new ExpenseCategory { Name = "Replay", IsActive = true };
        f.Expenses.AddCategory(category);
        var command = new PostExpenseCommand(Guid.NewGuid(), category.Id, null, new DateOnly(2026, 10, 7),
            17m, ExpensePaymentMethod.Bank, "Posting", null, actor);
        var post = new PostExpenseHandler(f.Expenses, f.CashMovements, f.OperationLock, f.Numbers,
            f.Audit, f.Clock, f.Transactions, f.Authorization, f.UnitOfWork, f.OutcomeLedger);
        var first = await post.HandleAsync(command, default);
        Assert.True(first.IsSuccess);
        var status = new OperationStatusQueryHandler(f.Sales, f.Purchasing, f.SupplierAccounts,
            expenses: f.Expenses, outcomeLedger: f.OutcomeLedger);
        Assert.True((await status.HandleAsync(new(command.ClientOperationId, ActorId: actor), default)).IsSuccess);
        var replay = await post.HandleAsync(command, default);
        Assert.True(replay.IsSuccess, replay.Error?.ToString());
        Assert.True(replay.Value!.WasExisting);
        Assert.Equal(first.Value!.ExpenseId, replay.Value.ExpenseId);
        Assert.Single(f.Expenses.Expenses);
        Assert.Single(f.Audit.Records);
    }

    [Fact]
    public async Task Void_identity_cannot_be_reused_for_posting()
    {
        var f = new Phase2TestDoubles();
        var expense = new Expense { ExpenseNumber = "VOID-ID", Amount = 17m, PaymentMethod = ExpensePaymentMethod.Bank };
        f.Expenses.AddExpense(expense);
        var actor = Guid.NewGuid();
        var operation = Guid.NewGuid();
        Assert.True((await Handler(f).HandleAsync(new(expense.Id, actor, operation, "Correction"), default)).IsSuccess);
        var category = new ExpenseCategory { Name = "Testing", IsActive = true };
        f.Expenses.AddCategory(category);
        var post = new PostExpenseHandler(f.Expenses, f.CashMovements, f.OperationLock, f.Numbers,
            f.Audit, f.Clock, f.Transactions, f.Authorization, f.UnitOfWork, f.OutcomeLedger);
        var result = await post.HandleAsync(new(operation, category.Id, null, new DateOnly(2026, 10, 7),
            17m, ExpensePaymentMethod.Bank, "Different posting", null, actor), default);
        Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        Assert.Single(f.Expenses.Expenses);
        Assert.Single(f.Audit.Records);
        Assert.Equal("ExpenseVoid", (await f.OutcomeLedger.GetOutcomeAsync(operation))!.OperationType);
    }

    [Fact]
    public async Task Already_voided_new_id_retains_noop_without_financial_or_audit_duplication()
    {
        var f = new Phase2TestDoubles();
        var expense = new Expense { ExpenseNumber = "VOID-NOOP", Status = ExpenseStatus.Voided, Version = 3 };
        f.Expenses.AddExpense(expense);
        var operation = Guid.NewGuid();
        Assert.True((await Handler(f).HandleAsync(new(expense.Id, Guid.NewGuid(), operation, "Already corrected"), default)).IsSuccess);
        Assert.Equal(3, expense.Version);
        Assert.Empty(f.Audit.Records);
        Assert.Equal(expense.Id, (await f.OutcomeLedger.GetOutcomeAsync(operation))!.EntityId);
    }

    [Fact]
    public async Task Failed_void_rechecks_operation_authority_before_recording_failure()
    {
        var f = new Phase2TestDoubles();
        var operation = Guid.NewGuid();
        var winnerActor = Guid.NewGuid();
        var locks = new FailureHandoffLock(f.OutcomeLedger, winnerActor);
        var handler = new VoidExpenseHandler(f.Expenses, f.CashMovements, f.Audit, f.Clock,
            f.Transactions, f.Authorization, f.UnitOfWork, locks, f.OutcomeLedger);
        var result = await handler.HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), operation, "Missing expense"), default);
        Assert.Equal("idempotency.payload_mismatch", result.Error?.Code);
        Assert.Equal(2, locks.Acquisitions);
        var outcome = await f.OutcomeLedger.GetOutcomeAsync(operation);
        Assert.Equal("OtherIntent", outcome!.OperationType);
        Assert.Equal(winnerActor, outcome.ActorId);
        Assert.Equal("winning-fingerprint", outcome.PayloadFingerprint);
        Assert.Equal("winner.failed", outcome.ErrorCode);
        Assert.Empty(f.Audit.Records);
    }

    private sealed class FailureHandoffLock(IOperationOutcomeLedger outcomes, Guid winnerActor) : IOperationLock
    {
        public int Acquisitions { get; private set; }
        public async Task AcquireAsync(Guid operation, CancellationToken cancellationToken)
        {
            if (++Acquisitions == 2)
            {
                await outcomes.RecordFailureAsync(operation, "OtherIntent", "winner.failed", "Winner preserved",
                    actorId: winnerActor, payloadFingerprint: "winning-fingerprint", cancellationToken: cancellationToken);
            }
        }
    }

    [Fact]
    public async Task Missing_operation_identity_is_rejected_before_posting()
    {
        var f = new Phase2TestDoubles();
        var result = await Handler(f).HandleAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "Correction"), default);
        Assert.Equal("expense.void_operation_id_required", result.Error?.Code);
        Assert.Empty(f.Audit.Records);
    }

    private static VoidExpenseHandler Handler(Phase2TestDoubles f) =>
        new(f.Expenses, f.CashMovements, f.Audit, f.Clock, f.Transactions, f.Authorization,
            f.UnitOfWork, f.OperationLock, f.OutcomeLedger);
}
