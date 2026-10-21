using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Thaka;
using System.Globalization;

namespace EdgeRetails.Application.Features.Thaka;

public sealed record RecordThakaPaymentCommand(
    Guid ClientOperationId,
    Guid ProjectId,
    decimal Amount,
    ThakaPaymentMethod PaymentMethod,
    string? Reference,
    string? Note,
    Guid ActorId);

public sealed record RecordThakaPaymentResult(
    Guid PaymentId,
    string ReceiptNumber,
    decimal BalanceAfter,
    bool WasExisting);

public sealed class RecordThakaPaymentHandler
{
    private readonly IThakaRepository _thaka;
    private readonly ICashMovementService _cashMovements;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit; private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public RecordThakaPaymentHandler(
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

    public async Task<Result<RecordThakaPaymentResult>> HandleAsync(
        RecordThakaPaymentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.Amount <= 0)
        {
            return Result<RecordThakaPaymentResult>.Failure(
                "thaka.payment_invalid",
                "Payment requires operation id and positive amount.");
        }

        var domainProjection = new
        {
            command.ProjectId,
            Amount = Money(command.Amount),
            PaymentMethod = command.PaymentMethod.ToString(),
            Reference = Normalize(command.Reference),
            Note = Normalize(command.Note),
            command.ActorId
        };
        var versionedFingerprint = OperationPayloadFingerprint.ComputeVersionedFingerprint("ThakaPayment", domainProjection);
        var payloadFingerprint = OperationPayloadFingerprint.ComputeSha256(
            "ThakaPayment",
            command.ProjectId.ToString("D"),
            Money(command.Amount).ToString("0.00", CultureInfo.InvariantCulture),
            command.PaymentMethod.ToString(),
            Normalize(command.Reference),
            Normalize(command.Note));

        bool FingerprintMatches(string? fp) =>
            string.Equals(fp, versionedFingerprint, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fp, payloadFingerprint, StringComparison.OrdinalIgnoreCase);

        if (_outcomeLedger is not null)
        {
            var preOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, cancellationToken);
            if (preOutcome is not null)
            {
                if (!string.Equals(preOutcome.OperationType, "ThakaPayment", StringComparison.Ordinal))
                {
                    return Result<RecordThakaPaymentResult>.Failure(
                        "idempotency.operation_type_mismatch",
                        $"Operation identity was already used for '{preOutcome.OperationType}'.");
                }

                if (preOutcome.ActorId.HasValue && command.ActorId != Guid.Empty && preOutcome.ActorId.Value != command.ActorId)
                {
                    return Result<RecordThakaPaymentResult>.Failure(
                        "idempotency.context_mismatch",
                        "Operation actor mismatch.");
                }

                if (!string.IsNullOrEmpty(preOutcome.PayloadFingerprint) &&
                    !FingerprintMatches(preOutcome.PayloadFingerprint))
                {
                    return Result<RecordThakaPaymentResult>.Failure(
                        "idempotency.payload_mismatch",
                        "This operation id is already associated with another or unresolved payment.");
                }

                if (preOutcome.State == OperationOutcomeState.Succeeded && preOutcome.WasCommitted && preOutcome.EntityId.HasValue)
                {
                    var existingBalance = await GetBalanceAsync(command.ProjectId, cancellationToken);
                    return Result<RecordThakaPaymentResult>.Success(new(
                        preOutcome.EntityId.Value,
                        preOutcome.DocumentNumber ?? string.Empty,
                        existingBalance,
                        true));
                }

                if (preOutcome.State == OperationOutcomeState.Failed)
                {
                    return Result<RecordThakaPaymentResult>.Failure(
                        preOutcome.ErrorCode ?? "idempotency.outcome_unknown",
                        preOutcome.ErrorMessage ?? "Payment operation previously failed.");
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
                return Result<RecordThakaPaymentResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var existing = await _thaka.GetPaymentByOperationIdAsync(
                command.ClientOperationId,
                ct);
            if (existing is not null)
            {
                var savedOutcome = _outcomeLedger is null
                    ? null
                    : await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                if (_outcomeLedger is not null && savedOutcome is not null)
                {
                    if (!string.Equals(savedOutcome.OperationType, "ThakaPayment", StringComparison.Ordinal))
                    {
                        return Result<RecordThakaPaymentResult>.Failure(
                            "idempotency.operation_type_mismatch",
                            $"Operation identity was already used for '{savedOutcome.OperationType}'.");
                    }

                    if (savedOutcome.ActorId.HasValue && command.ActorId != Guid.Empty && savedOutcome.ActorId.Value != command.ActorId)
                    {
                        return Result<RecordThakaPaymentResult>.Failure(
                            "idempotency.context_mismatch",
                            "Operation actor mismatch.");
                    }

                    if (!string.IsNullOrEmpty(savedOutcome.PayloadFingerprint) &&
                        !FingerprintMatches(savedOutcome.PayloadFingerprint))
                    {
                        return Result<RecordThakaPaymentResult>.Failure(
                            "idempotency.payload_mismatch",
                            "This operation id is already associated with another or unresolved payment.");
                    }
                }

                var existingBalance = await GetBalanceAsync(existing.ProjectId, ct);
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        command.ClientOperationId,
                        "ThakaPayment",
                        existing.Id,
                        existing.ReceiptNumber,
                        actorId: command.ActorId,
                        payloadFingerprint: versionedFingerprint,
                        cancellationToken: ct);
                }

                return Result<RecordThakaPaymentResult>.Success(new(
                    existing.Id,
                    existing.ReceiptNumber,
                    existingBalance,
                    true));
            }

            await _resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await _thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null || project.Status is not (ThakaProjectStatus.Active or ThakaProjectStatus.Suspended))
            {
                return Result<RecordThakaPaymentResult>.Failure(
                    "thaka.project_not_active",
                    "Payments can only be recorded against an active Thaka project.");
            }
            var balanceBefore = await GetBalanceAsync(project.Id, ct);
            var amount = Money(command.Amount);
            if (amount > balanceBefore)
            {
                return Result<RecordThakaPaymentResult>.Failure(
                    "thaka.payment_exceeds_balance",
                    "Payment exceeds the current outstanding balance.");
            }

            var payment = new ThakaPayment
            {
                ProjectId = project.Id,
                ReceiptNumber = await _numbers.NextAsync("THK-PAY", ct),
                ClientOperationId = command.ClientOperationId,
                Amount = amount,
                PaymentMethod = command.PaymentMethod,
                Reference = Normalize(command.Reference),
                Note = Normalize(command.Note),
                RecordedBy = command.ActorId,
                RecordedAt = _clock.UtcNow
            };
            _thaka.AddPayment(payment);

            if (command.PaymentMethod == ThakaPaymentMethod.Cash)
            {
                var cash = await _cashMovements.RecordAsync(
                    new RecordCashMovementRequest(
                        CashMovementType.ThakaPaymentCashIn,
                        CashMovementDirection.In,
                        amount,
                        command.ActorId,
                        "THAKA_PAYMENT",
                        payment.Id,
                        "Thaka payment",
                        payment.ReceiptNumber),
                    ct); if (!cash.IsSuccess)
                {
                    return Result<RecordThakaPaymentResult>.Failure(
                        cash.Error!.Code,
                        cash.Error.Message);
                }
            }

            _audit.Record(
                "THAKA_PAYMENT_RECORDED",
                "THAKA_PAYMENT",
                payment.Id,
                command.ActorId,
                command.ClientOperationId,
                $"{payment.ReceiptNumber}: {payment.Amount:0.00}");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.ClientOperationId,
                    "ThakaPayment",
                    payment.Id,
                    payment.ReceiptNumber,
                    actorId: command.ActorId,
                    payloadFingerprint: versionedFingerprint,
                    cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<RecordThakaPaymentResult>.Success(new(
                payment.Id,
                payment.ReceiptNumber,
                Money(balanceBefore - amount),
                false));
        }, cancellationToken);

        if (!result.IsSuccess && _outcomeLedger is not null &&
            result.Error?.Code != "thaka.operation_id_conflict" &&
            result.Error?.Code?.StartsWith("idempotency.") != true)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "ThakaPayment",
                result.Error!.Code,
                result.Error.Message,
                actorId: command.ActorId,
                payloadFingerprint: versionedFingerprint,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    private async Task<decimal> GetBalanceAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var charges = await _thaka.GetGrossMaterialChargesAsync(
            projectId,
            cancellationToken);
        var discounts = await _thaka.GetSettlementDiscountsAsync(
            projectId,
            cancellationToken); var payments = await _thaka.GetPaymentsCollectedAsync(
            projectId,
            cancellationToken);

        return Money(charges - discounts - payments);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
