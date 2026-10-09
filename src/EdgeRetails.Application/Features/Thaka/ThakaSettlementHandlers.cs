using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Thaka;
using System.Globalization;

namespace EdgeRetails.Application.Features.Thaka;

public sealed record SettleThakaCommand(
    Guid ClientOperationId,
    Guid ProjectId,
    decimal SettlementDiscount,
    decimal FinalPaymentAmount,
    ThakaPaymentMethod PaymentMethod,
    string? PaymentReference,
    Guid ActorId);

public sealed record SettleThakaResult(
    Guid SettlementId,
    string SettlementNumber,
    decimal SettlementDiscount,
    decimal FinalPaymentAmount,
    bool WasExisting);

public sealed class SettleThakaHandler
{
    private readonly IThakaRepository _thaka;
    private readonly ICashMovementService _cashMovements;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IDocumentNumberService _numbers; private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public SettleThakaHandler(
        IThakaRepository thaka,
        ICashMovementService cashMovements,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IDocumentNumberService numbers,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _thaka = thaka;
        _cashMovements = cashMovements;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _numbers = numbers;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _outcomeLedger = outcomeLedger;
    }

    public async Task<Result<SettleThakaResult>> HandleAsync(
        SettleThakaCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty ||
            command.SettlementDiscount < 0 ||
            command.FinalPaymentAmount < 0)
        {
            return Result<SettleThakaResult>.Failure(
                "thaka.settlement_invalid",
                "Settlement values are invalid.");
        }

        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var payloadFingerprint = OperationPayloadFingerprint.ComputeSha256(
                "ThakaSettlement",
                command.ProjectId.ToString("D"),
                Money(command.SettlementDiscount).ToString("0.00", CultureInfo.InvariantCulture),
                Money(command.FinalPaymentAmount).ToString("0.00", CultureInfo.InvariantCulture),
                command.PaymentMethod.ToString(),
                Normalize(command.PaymentReference));
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ThakaManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<SettleThakaResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var existing = await _thaka.GetSettlementByOperationIdAsync(
                command.ClientOperationId,
                ct);
            if (existing is not null)
            {
                var savedOutcome = _outcomeLedger is null
                    ? null
                    : await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                if (_outcomeLedger is not null &&
                    (savedOutcome is not { State: OperationOutcomeState.Succeeded, OperationType: "ThakaSettlement" } ||
                     !string.Equals(savedOutcome.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal)))
                {
                    return Result<SettleThakaResult>.Failure(
                        "thaka.operation_id_conflict",
                        "This operation id is already associated with another or unresolved settlement.");
                }

                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "ThakaSettlement",
                        existing.Id,
                        existing.SettlementNumber,
                        actorId: command.ActorId,
                        payloadFingerprint: payloadFingerprint,
                        cancellationToken: ct);
                }

                return Result<SettleThakaResult>.Success(new(
                    existing.Id,
                    existing.SettlementNumber,
                    existing.SettlementDiscount,
                    existing.FinalPaymentAmount,
                    true));
            }

            await _resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await _thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null || project.Status is not (ThakaProjectStatus.Active or ThakaProjectStatus.Suspended))
            {
                return Result<SettleThakaResult>.Failure(
                    "thaka.project_not_active",
                    "Only an active Thaka project can be settled.");
            }
            var charges = await _thaka.GetGrossMaterialChargesAsync(project.Id, ct);
            var priorDiscounts = await _thaka.GetSettlementDiscountsAsync(project.Id, ct);
            var payments = await _thaka.GetPaymentsCollectedAsync(project.Id, ct);
            var balanceBefore = Money(
                Math.Max(0m, charges - priorDiscounts - payments));
            var discount = Money(command.SettlementDiscount);

            if (discount > balanceBefore)
            {
                return Result<SettleThakaResult>.Failure(
                    "thaka.discount_exceeds_balance",
                    "Settlement discount cannot exceed the outstanding balance.");
            }

            var expectedPayment = Money(balanceBefore - discount);
            if (Money(command.FinalPaymentAmount) != expectedPayment)
            {
                return Result<SettleThakaResult>.Failure(
                    "thaka.settlement_balance_mismatch",
                    "Final payment plus discount must exactly close the balance.");
            }

            ThakaPayment? finalPayment = null;
            if (expectedPayment > 0)
            {
                finalPayment = new ThakaPayment
                {
                    ProjectId = project.Id,
                    ReceiptNumber = await _numbers.NextAsync("THK-PAY", ct),
                    ClientOperationId = Guid.CreateVersion7(),
                    Amount = expectedPayment,
                    PaymentMethod = command.PaymentMethod,
                    Reference = Normalize(command.PaymentReference),
                    Note = "Final settlement payment",
                    RecordedBy = command.ActorId,
                    RecordedAt = _clock.UtcNow
                };
                _thaka.AddPayment(finalPayment);

                if (command.PaymentMethod == ThakaPaymentMethod.Cash)
                {
                    var cash = await _cashMovements.RecordAsync(
                        new RecordCashMovementRequest(
                            CashMovementType.ThakaPaymentCashIn,
                            CashMovementDirection.In,
                            expectedPayment,
                            command.ActorId,
                            "THAKA_SETTLEMENT",
                            finalPayment.Id,
                            "Thaka final settlement",
                            null),
                        ct);
                    if (!cash.IsSuccess)
                    {
                        return Result<SettleThakaResult>.Failure(
                            cash.Error!.Code,
                            cash.Error.Message);
                    }
                }
            }

            var settlement = new ThakaSettlement
            {
                ProjectId = project.Id,
                SettlementNumber = await _numbers.NextAsync("THK-SET", ct),
                ClientOperationId = command.ClientOperationId,
                GrossMaterialChargesSnapshot = charges,
                PaymentsCollectedSnapshot = payments,
                SettlementDiscount = discount,
                FinalPaymentAmount = expectedPayment,
                BalanceBeforeSettlement = balanceBefore,
                FinalPaymentId = finalPayment?.Id,
                SettledBy = command.ActorId,
                SettledAt = _clock.UtcNow
            };
            _thaka.AddSettlement(settlement);

            project.Status = ThakaProjectStatus.Settled;
            project.Version++;

            _audit.Record(
                "THAKA_SETTLED",
                "THAKA_SETTLEMENT",
                settlement.Id,
                command.ActorId,
                command.ClientOperationId,
                $"{settlement.SettlementNumber}: discount {discount:0.00}");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.ClientOperationId,
                    "ThakaSettlement",
                    settlement.Id,
                    settlement.SettlementNumber,
                    actorId: command.ActorId,
                    payloadFingerprint: payloadFingerprint,
                    cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<SettleThakaResult>.Success(new(
                settlement.Id,
                settlement.SettlementNumber,
                discount,
                expectedPayment,
                false));
        }, cancellationToken);

        if (!result.IsSuccess && _outcomeLedger is not null)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "ThakaSettlement",
                result.Error!.Code,
                result.Error.Message,
                actorId: command.ActorId,
                payloadFingerprint: OperationPayloadFingerprint.ComputeSha256(
                    "ThakaSettlement", command.ProjectId.ToString("D"),
                    Money(command.SettlementDiscount).ToString("0.00", CultureInfo.InvariantCulture),
                    Money(command.FinalPaymentAmount).ToString("0.00", CultureInfo.InvariantCulture),
                    command.PaymentMethod.ToString(), Normalize(command.PaymentReference)),
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
public sealed record ReopenThakaCommand(
    Guid ProjectId,
    string Reason,
    Guid ActorId,
    Guid CorrelationId);

public sealed class ReopenThakaHandler
{
    private readonly IThakaRepository _thaka;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;

    public ReopenThakaHandler(
        IThakaRepository thaka,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _thaka = thaka;
        _resourceLock = resourceLock;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        ReopenThakaCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ThakaManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<Guid>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            if (string.IsNullOrWhiteSpace(command.Reason))
            {
                return Result<Guid>.Failure(
                    "thaka.reopen_reason_required",
                    "Reopening a Thaka project requires a reason.");
            }

            await _resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await _thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null || project.Status != ThakaProjectStatus.Settled)
            {
                return Result<Guid>.Failure(
                    "thaka.project_not_settled",
                    "Only a settled Thaka project can be reopened.");
            }

            var settlement = await _thaka.GetLatestSettlementAsync(project.Id, ct);
            if (settlement is null)
            {
                return Result<Guid>.Failure(
                    "thaka.settlement_missing",
                    "Settlement history was not found.");
            }

            var reopening = new ThakaReopening
            {
                ProjectId = project.Id,
                SettlementId = settlement.Id,
                Reason = command.Reason.Trim(),
                ReopenedBy = command.ActorId,
                ReopenedAt = _clock.UtcNow
            };
            _thaka.AddReopening(reopening); project.Status = ThakaProjectStatus.Active;
            project.Version++;

            _audit.Record(
                "THAKA_REOPENED",
                "THAKA_PROJECT",
                project.Id,
                command.ActorId,
                command.CorrelationId,
                command.Reason.Trim());

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(reopening.Id);
        }, cancellationToken);
}
