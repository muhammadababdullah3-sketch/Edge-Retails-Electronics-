using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Application.Features.Finance;

public sealed record OpenCashSessionCommand(
    decimal OpeningCash,
    Guid ActorId,
    string? Note);

public sealed class OpenCashSessionHandler
{
    private readonly ICashRepository _cash;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IApplicationPermissionAuthorizer? _authorizer;

    public OpenCashSessionHandler(
        ICashRepository cash,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IApplicationPermissionAuthorizer? authorizer = null)
    {
        _cash = cash;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _authorizer = authorizer;
    }

    public Task<Result<Guid>> HandleAsync(
        OpenCashSessionCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            if (_authorizer is null)
            {
                return Result<Guid>.Failure(
                    "security.authorizer_unavailable",
                    "Permission authorizer service is unavailable.");
            }

            if (command.ActorId == Guid.Empty)
            {
                return Result<Guid>.Failure(
                    "auth.actor_required",
                    "Actor id is required to open a cash session.");
            }

            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SalesPosUse,
                ct);
            if (!auth.IsSuccess)
            {
                return Result<Guid>.Failure(auth.Error!.Code, auth.Error.Message);
            }

            if (command.OpeningCash < 0)
            {
                return Result<Guid>.Failure(
                    "cash.opening_negative",
                    "Opening cash cannot be negative.");
            }

            var existing = await _cash.GetOpenSessionForUpdateAsync(ct);
            if (existing is not null)
            {
                return Result<Guid>.Failure(
                    "cash.session_already_open",
                    "A cash session is already open.");
            }

            var session = new CashSession
            {
                BusinessDate = _clock.ShopDate,
                OpenedBy = command.ActorId,
                OpenedAt = _clock.UtcNow,
                OpeningCash = decimal.Round(
                    command.OpeningCash,
                    2,
                    MidpointRounding.AwayFromZero),
                Note = command.Note?.Trim()
            };

            _cash.AddSession(session);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(session.Id);
        }, cancellationToken);
    }
}

public sealed record RecordCashMovementRequest(
    CashMovementType MovementType,
    CashMovementDirection Direction,
    decimal Amount,
    Guid ActorId,
    string? SourceType,
    Guid? SourceId,
    string? Reason,
    string? Note,
    Guid? ClientOperationId = null);

public interface ICashMovementService
{
    Task<Result<Guid>> RecordAsync(
        RecordCashMovementRequest request,
        CancellationToken cancellationToken);
}

public sealed class CashMovementService : ICashMovementService
{
    private readonly ICashRepository _cash;
    private readonly IClock _clock;

    public CashMovementService(ICashRepository cash, IClock clock)
    {
        _cash = cash;
        _clock = clock;
    }

    public async Task<Result<Guid>> RecordAsync(
        RecordCashMovementRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Amount <= 0)
        {
            return Result<Guid>.Failure(
                "cash.amount_positive",
                "Cash movement amount must be greater than zero.");
        }

        if (!DirectionMatchesType(request.MovementType, request.Direction))
        {
            return Result<Guid>.Failure(
                "cash.direction_mismatch",
                "Cash movement direction does not match its movement type.");
        }

        var session = await _cash.GetOpenSessionForUpdateAsync(cancellationToken);
        if (session is null)
        {
            return Result<Guid>.Failure(
                "cash.session_required",
                "Open a cash session before posting a drawer-affecting cash transaction.");
        }

        var movement = new CashMovement
        {
            CashSessionId = session.Id,
            MovementType = request.MovementType,
            Direction = request.Direction,
            Amount = decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
            SourceType = Normalize(request.SourceType),
            SourceId = request.SourceId,
            Reason = Normalize(request.Reason),
            Note = Normalize(request.Note),
            ActorId = request.ActorId,
            OccurredAt = _clock.UtcNow
        };

        _cash.AddMovement(movement);
        return Result<Guid>.Success(movement.Id);
    }

    private static bool DirectionMatchesType(
        CashMovementType type,
        CashMovementDirection direction)
    {
        return type switch
        {
            CashMovementType.SaleCashIn => direction == CashMovementDirection.In,
            CashMovementType.SaleRefundCashOut => direction == CashMovementDirection.Out,
            CashMovementType.ThakaPaymentCashIn => direction == CashMovementDirection.In,
            CashMovementType.ExpenseCashOut => direction == CashMovementDirection.Out,
            CashMovementType.ManualCashIn => direction == CashMovementDirection.In,
            CashMovementType.ManualCashOut => direction == CashMovementDirection.Out,
            CashMovementType.PurchaseCashOut => direction == CashMovementDirection.Out,
            CashMovementType.PurchaseVoidCashIn => direction == CashMovementDirection.In,
            CashMovementType.PurchaseReturnCashIn => direction == CashMovementDirection.In,
            CashMovementType.ThakaPaymentReversalCashOut => direction == CashMovementDirection.Out,
            CashMovementType.SupplierPaymentCashOut => direction == CashMovementDirection.Out,
            CashMovementType.SupplierPaymentReversalCashIn => direction == CashMovementDirection.In,
            CashMovementType.SupplierRefundCashIn => direction == CashMovementDirection.In,
            CashMovementType.SupplierRefundReversalCashOut => direction == CashMovementDirection.Out,
            CashMovementType.ThakaCreditRefundCashOut => direction == CashMovementDirection.Out,
            _ => false
        };
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed record RecordManualCashMovementCommand(
    CashMovementDirection Direction,
    decimal Amount,
    Guid ActorId,
    string Reason,
    string? Note,
    Guid? ClientOperationId = null);

public sealed class RecordManualCashMovementHandler
{
    private readonly ICashMovementService _service;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBusinessAuditWriter _audit;
    private readonly IOperationOutcomeLedger? _outcomeLedger;
    private readonly IOperationLock? _operationLock;

    public RecordManualCashMovementHandler(
        ICashMovementService service,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IBusinessAuditWriter audit,
        IOperationOutcomeLedger? outcomeLedger = null,
        IOperationLock? operationLock = null)
    {
        _service = service;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _audit = audit;
        _outcomeLedger = outcomeLedger;
        _operationLock = operationLock;
    }

    public Task<Result<Guid>> HandleAsync(
        RecordManualCashMovementCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync<Result<Guid>>(async ct =>
        {
            if (command.ActorId == Guid.Empty)
            {
                return Result<Guid>.Failure("cash.actor_required", "Manual cash movement requires an actor.");
            }
            if (command.Direction is not (CashMovementDirection.In or CashMovementDirection.Out))
            {
                return Result<Guid>.Failure("cash.direction_invalid", "Manual cash direction is invalid.");
            }
            var amount = decimal.Round(command.Amount, 2, MidpointRounding.AwayFromZero);
            if (amount <= 0m)
            {
                return Result<Guid>.Failure("cash.amount_positive", "Cash movement amount must be greater than zero at money precision.");
            }
            if (string.IsNullOrWhiteSpace(command.Reason))
            {
                return Result<Guid>.Failure(
                    "cash.manual_reason_required",
                    "Manual cash movement requires a reason.");
            }
            if (!command.ClientOperationId.HasValue || command.ClientOperationId.Value == Guid.Empty)
            {
                return Result<Guid>.Failure(
                    "cash.client_operation_id_required",
                    "Manual cash movement requires a valid immutable client operation id.");
            }

            string? versionedFingerprint = null;
            string? legacyFingerprint = null;
            if (command.ClientOperationId.HasValue && command.ClientOperationId.Value != Guid.Empty)
            {
                var domainProjection = new
                {
                    Direction = command.Direction.ToString(),
                    Amount = amount,
                    command.ActorId,
                    Reason = command.Reason.Trim(),
                    Note = Normalize(command.Note)
                };
                versionedFingerprint = OperationPayloadFingerprint.ComputeVersionedFingerprint("ManualCashMovement", domainProjection);
                legacyFingerprint = OperationPayloadFingerprint.ComputeSha256(
                    "ManualCashMovement",
                    command.Direction.ToString(),
                    amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    command.ActorId.ToString("D"),
                    command.Reason.Trim(),
                    Normalize(command.Note) ?? string.Empty);

                if (_outcomeLedger is not null)
                {
                    var preOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId.Value, ct);
                    if (preOutcome is not null)
                    {
                        var guarded = EvaluateOutcome(preOutcome, command, versionedFingerprint, legacyFingerprint);
                        if (guarded.HasValue)
                        {
                            return guarded.Value;
                        }
                    }
                }

                if (_operationLock is not null)
                {
                    await _operationLock.AcquireAsync(command.ClientOperationId.Value, ct);
                }

                if (_outcomeLedger is not null)
                {
                    var postOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId.Value, ct);
                    if (postOutcome is not null)
                    {
                        var guarded = EvaluateOutcome(postOutcome, command, versionedFingerprint, legacyFingerprint);
                        if (guarded.HasValue)
                        {
                            return guarded.Value;
                        }
                    }
                }
            }

            var result = await _service.RecordAsync(
                new RecordCashMovementRequest(
                    command.Direction == CashMovementDirection.In
                        ? CashMovementType.ManualCashIn
                        : CashMovementType.ManualCashOut,
                    command.Direction,
                    amount,
                    command.ActorId,
                    null,
                    null,
                    command.Reason.Trim(),
                    command.Note,
                    command.ClientOperationId),
                ct);

            if (!result.IsSuccess)
            {
                return result;
            }

            _audit.Record(command.Direction == CashMovementDirection.In ? "MANUAL_CASH_IN" : "MANUAL_CASH_OUT",
                "CASH_MOVEMENT", result.Value, command.ActorId, result.Value, command.Reason.Trim());
            await _unitOfWork.SaveChangesAsync(ct);

            if (command.ClientOperationId.HasValue && command.ClientOperationId.Value != Guid.Empty && _outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.ClientOperationId.Value,
                    "ManualCashMovement",
                    result.Value,
                    actorId: command.ActorId,
                    payloadFingerprint: versionedFingerprint,
                    cancellationToken: ct);
            }

            return result;
        }, cancellationToken);
    }

    private static Result<Guid>? EvaluateOutcome(
        OperationOutcomeRecord outcome,
        RecordManualCashMovementCommand command,
        string versionedFingerprint,
        string legacyFingerprint)
    {
        if (!string.Equals(outcome.OperationType, "ManualCashMovement", StringComparison.OrdinalIgnoreCase))
        {
            return Result<Guid>.Failure(
                "idempotency.operation_type_mismatch",
                $"Operation identity already belongs to another durable operation ({outcome.OperationType}).");
        }

        if (outcome.ActorId.HasValue && command.ActorId != Guid.Empty && outcome.ActorId.Value != command.ActorId)
        {
            return Result<Guid>.Failure(
                "idempotency.context_mismatch",
                "Operation actor mismatch.");
        }

        if (!string.IsNullOrEmpty(outcome.PayloadFingerprint) &&
            !string.Equals(outcome.PayloadFingerprint, versionedFingerprint, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outcome.PayloadFingerprint, legacyFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Result<Guid>.Failure(
                "idempotency.payload_mismatch",
                "Operation was previously submitted with different cash movement parameters.");
        }

        if (outcome.State == OperationOutcomeState.Succeeded && outcome.WasCommitted && outcome.EntityId.HasValue)
        {
            return Result<Guid>.Success(outcome.EntityId.Value);
        }

        return null;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed record CloseCashSessionCommand(
    Guid SessionId,
    decimal CountedCash,
    Guid ActorId,
    string? Note);

public sealed record CashClosingResult(
    Guid SessionId,
    decimal OpeningCash,
    decimal CashIn,
    decimal CashOut,
    decimal ExpectedCash,
    decimal CountedCash,
    decimal Difference);

public sealed class CloseCashSessionHandler
{
    private readonly ICashRepository _cash;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IApplicationPermissionAuthorizer? _authorizer;

    public CloseCashSessionHandler(
        ICashRepository cash,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IApplicationPermissionAuthorizer? authorizer = null)
    {
        _cash = cash;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _authorizer = authorizer;
    }

    public Task<Result<CashClosingResult>> HandleAsync(
        CloseCashSessionCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            if (_authorizer is null)
            {
                return Result<CashClosingResult>.Failure(
                    "security.authorizer_unavailable",
                    "Permission authorizer service is unavailable.");
            }

            if (command.ActorId == Guid.Empty)
            {
                return Result<CashClosingResult>.Failure(
                    "auth.actor_required",
                    "Actor id is required to close a cash session.");
            }

            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SalesPosUse,
                ct);
            if (!auth.IsSuccess)
            {
                return Result<CashClosingResult>.Failure(auth.Error!.Code, auth.Error.Message);
            }

            if (command.SessionId == Guid.Empty)
            {
                return Result<CashClosingResult>.Failure(
                    "cash.session_id_required",
                    "Cash session id is required.");
            }

            if (command.CountedCash < 0)
            {
                return Result<CashClosingResult>.Failure(
                    "cash.counted_negative",
                    "Counted cash cannot be negative.");
            }

            var session = await _cash.GetSessionForUpdateAsync(command.SessionId, ct);
            if (session is null)
            {
                return Result<CashClosingResult>.Failure(
                    "cash.session_not_found",
                    "Cash session was not found.");
            }

            if (session.Status != CashSessionStatus.Open)
            {
                return Result<CashClosingResult>.Failure(
                    "cash.session_not_open",
                    "Cash session is already closed.");
            }

            var movements = await _cash.GetMovementsAsync(session.Id, ct);
            var cashIn = movements
                .Where(x => x.Direction == CashMovementDirection.In)
                .Sum(x => x.Amount);
            var cashOut = movements
                .Where(x => x.Direction == CashMovementDirection.Out)
                .Sum(x => x.Amount);

            if (!string.IsNullOrWhiteSpace(command.Note))
            {
                session.Note = command.Note.Trim();
            }

            session.Close(
                cashIn,
                cashOut,
                command.CountedCash,
                command.ActorId,
                _clock.UtcNow);

            await _unitOfWork.SaveChangesAsync(ct);

            return Result<CashClosingResult>.Success(new CashClosingResult(
                session.Id,
                session.OpeningCash,
                cashIn,
                cashOut,
                session.ExpectedClosingCash!.Value,
                session.CountedClosingCash!.Value,
                session.Difference!.Value));
        }, cancellationToken);
    }
}
