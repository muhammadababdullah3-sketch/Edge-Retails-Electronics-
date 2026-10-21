using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Thaka;
using System.Globalization;

namespace EdgeRetails.Application.Features.Thaka;

public sealed record RefundThakaCreditCommand(
    Guid ClientOperationId,
    Guid ProjectId,
    decimal Amount,
    string Reason,
    Guid ActorId);

public sealed record RefundThakaCreditResult(
    Guid RefundId,
    string RefundNumber,
    decimal RefundedAmount,
    decimal RemainingCredit,
    bool WasExisting);

public sealed class RefundThakaCreditHandler
{
    private readonly IThakaRepository _thaka;
    private readonly ICashMovementService _cashMovements;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public RefundThakaCreditHandler(
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

    public async Task<Result<RefundThakaCreditResult>> HandleAsync(
        RefundThakaCreditCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.Amount <= 0)
        {
            return Result<RefundThakaCreditResult>.Failure(
                "thaka.refund_invalid",
                "Credit refund requires a valid operation id and positive amount.");
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result<RefundThakaCreditResult>.Failure(
                "thaka.refund_reason_required",
                "Credit refund requires a valid reason.");
        }

        var domainProjection = new
        {
            command.ProjectId,
            Amount = Money(command.Amount),
            Reason = command.Reason.Trim(),
            command.ActorId
        };
        var versionedFingerprint = OperationPayloadFingerprint.ComputeVersionedFingerprint("ThakaCreditRefund", domainProjection);
        var payloadFingerprint = OperationPayloadFingerprint.ComputeSha256(
            "ThakaCreditRefund",
            command.ProjectId.ToString("D"),
            Money(command.Amount).ToString("0.00", CultureInfo.InvariantCulture),
            command.Reason.Trim());

        bool FingerprintMatches(string? fp) =>
            string.Equals(fp, versionedFingerprint, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fp, payloadFingerprint, StringComparison.OrdinalIgnoreCase);

        if (_outcomeLedger is not null)
        {
            var preOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, cancellationToken);
            if (preOutcome is not null)
            {
                if (!string.Equals(preOutcome.OperationType, "ThakaCreditRefund", StringComparison.Ordinal))
                {
                    return Result<RefundThakaCreditResult>.Failure(
                        "idempotency.operation_type_mismatch",
                        $"Operation identity was already used for '{preOutcome.OperationType}'.");
                }

                if (preOutcome.ActorId.HasValue && command.ActorId != Guid.Empty && preOutcome.ActorId.Value != command.ActorId)
                {
                    return Result<RefundThakaCreditResult>.Failure(
                        "idempotency.context_mismatch",
                        "Operation actor mismatch.");
                }

                if (!string.IsNullOrEmpty(preOutcome.PayloadFingerprint) &&
                    !FingerprintMatches(preOutcome.PayloadFingerprint))
                {
                    return Result<RefundThakaCreditResult>.Failure(
                        "idempotency.payload_mismatch",
                        "This operation id is already associated with another or unresolved credit refund.");
                }

                if (preOutcome.State == OperationOutcomeState.Succeeded && preOutcome.WasCommitted && preOutcome.EntityId.HasValue)
                {
                    var existingCredit = await GetAvailableCreditAsync(command.ProjectId, cancellationToken);
                    return Result<RefundThakaCreditResult>.Success(new(
                        preOutcome.EntityId.Value,
                        preOutcome.DocumentNumber ?? string.Empty,
                        Money(command.Amount),
                        existingCredit,
                        true));
                }

                if (preOutcome.State == OperationOutcomeState.Failed)
                {
                    return Result<RefundThakaCreditResult>.Failure(
                        preOutcome.ErrorCode ?? "idempotency.outcome_unknown",
                        preOutcome.ErrorMessage ?? "Credit refund operation previously failed.");
                }
            }
        }

        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ThakaManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<RefundThakaCreditResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var existing = await _thaka.GetCreditRefundByOperationIdAsync(
                command.ClientOperationId,
                ct);
            if (existing is not null)
            {
                var savedOutcome = _outcomeLedger is null
                    ? null
                    : await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                if (_outcomeLedger is not null && savedOutcome is not null)
                {
                    if (!string.Equals(savedOutcome.OperationType, "ThakaCreditRefund", StringComparison.Ordinal))
                    {
                        return Result<RefundThakaCreditResult>.Failure(
                            "idempotency.operation_type_mismatch",
                            $"Operation identity was already used for '{savedOutcome.OperationType}'.");
                    }

                    if (savedOutcome.ActorId.HasValue && command.ActorId != Guid.Empty && savedOutcome.ActorId.Value != command.ActorId)
                    {
                        return Result<RefundThakaCreditResult>.Failure(
                            "idempotency.context_mismatch",
                            "Operation actor mismatch.");
                    }

                    if (!string.IsNullOrEmpty(savedOutcome.PayloadFingerprint) &&
                        !FingerprintMatches(savedOutcome.PayloadFingerprint))
                    {
                        return Result<RefundThakaCreditResult>.Failure(
                            "idempotency.payload_mismatch",
                            "This operation id is already associated with another or unresolved credit refund.");
                    }
                }

                var remainingCredit = await GetAvailableCreditAsync(existing.ProjectId, ct);
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "ThakaCreditRefund",
                        existing.Id,
                        existing.RefundNumber,
                        actorId: command.ActorId,
                        payloadFingerprint: versionedFingerprint,
                        cancellationToken: ct);
                }

                return Result<RefundThakaCreditResult>.Success(new(
                    existing.Id,
                    existing.RefundNumber,
                    existing.Amount,
                    remainingCredit,
                    true));
            }

            await _resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await _thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null || project.Status is not (ThakaProjectStatus.Active or ThakaProjectStatus.Suspended))
            {
                return Result<RefundThakaCreditResult>.Failure(
                    "thaka.project_not_active",
                    "Credit refund can only be issued for an active or suspended Thaka project.");
            }

            var availableCredit = await GetAvailableCreditAsync(project.Id, ct);
            var refundAmount = Money(command.Amount);
            if (availableCredit <= 0m)
            {
                return Result<RefundThakaCreditResult>.Failure(
                    "thaka.no_credit_balance",
                    "Project has no available customer credit to refund.");
            }

            if (refundAmount > availableCredit)
            {
                return Result<RefundThakaCreditResult>.Failure(
                    "thaka.refund_exceeds_credit",
                    "Requested refund exceeds available customer credit.");
            }

            var refund = new ThakaCreditRefund
            {
                Id = Guid.CreateVersion7(),
                ProjectId = project.Id,
                RefundNumber = await _numbers.NextAsync("THK-CRD", ct),
                ClientOperationId = command.ClientOperationId,
                Amount = refundAmount,
                Reason = command.Reason.Trim(),
                RefundedBy = command.ActorId,
                RefundedAt = _clock.UtcNow
            };
            _thaka.AddCreditRefund(refund);

            var cash = await _cashMovements.RecordAsync(
                new RecordCashMovementRequest(
                    CashMovementType.ThakaCreditRefundCashOut,
                    CashMovementDirection.Out,
                    refundAmount,
                    command.ActorId,
                    "THAKA_CREDIT_REFUND",
                    refund.Id,
                    refund.Reason,
                    refund.RefundNumber,
                    command.ClientOperationId),
                ct);

            if (!cash.IsSuccess)
            {
                return Result<RefundThakaCreditResult>.Failure(
                    cash.Error!.Code,
                    cash.Error.Message);
            }

            _audit.Record(
                "THAKA_CREDIT_REFUNDED",
                "THAKA_CREDIT_REFUND",
                refund.Id,
                command.ActorId,
                command.ClientOperationId,
                $"{refund.RefundNumber}: {refund.Amount:0.00}");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.ClientOperationId,
                    "ThakaCreditRefund",
                    refund.Id,
                    refund.RefundNumber,
                    actorId: command.ActorId,
                    payloadFingerprint: versionedFingerprint,
                    cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            var remainingAfter = Money(availableCredit - refundAmount);
            return Result<RefundThakaCreditResult>.Success(new(
                refund.Id,
                refund.RefundNumber,
                refund.Amount,
                remainingAfter,
                false));
        }, cancellationToken);

        if (!result.IsSuccess && _outcomeLedger is not null &&
            result.Error?.Code != "thaka.operation_id_conflict" &&
            result.Error?.Code?.StartsWith("idempotency.") != true)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "ThakaCreditRefund",
                result.Error!.Code,
                result.Error.Message,
                actorId: command.ActorId,
                payloadFingerprint: versionedFingerprint,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private async Task<decimal> GetAvailableCreditAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var charges = await _thaka.GetGrossMaterialChargesAsync(
            projectId,
            cancellationToken);
        var discounts = await _thaka.GetSettlementDiscountsAsync(
            projectId,
            cancellationToken);
        var payments = await _thaka.GetPaymentsCollectedAsync(
            projectId,
            cancellationToken);

        var balance = charges - discounts - payments;
        // Credit is negative balance (i.e. payments collected exceed net charges)
        return balance < 0m ? Money(-balance) : 0m;
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
