using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
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

    public OpenCashSessionHandler(
        ICashRepository cash,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _cash = cash;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        OpenCashSessionCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
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
    string? Note);

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
    string? Note);

public sealed class RecordManualCashMovementHandler
{
    private readonly ICashMovementService _service;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public RecordManualCashMovementHandler(
        ICashMovementService service,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _service = service;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        RecordManualCashMovementCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            if (string.IsNullOrWhiteSpace(command.Reason))
            {
                return Result<Guid>.Failure(
                    "cash.manual_reason_required",
                    "Manual cash movement requires a reason.");
            }

            var result = await _service.RecordAsync(
                new RecordCashMovementRequest(
                    command.Direction == CashMovementDirection.In
                        ? CashMovementType.ManualCashIn
                        : CashMovementType.ManualCashOut,
                    command.Direction,
                    command.Amount,
                    command.ActorId,
                    null,
                    null,
                    command.Reason,
                    command.Note),
                ct);

            if (!result.IsSuccess)
            {
                return result;
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
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

    public CloseCashSessionHandler(
        ICashRepository cash,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _cash = cash;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<CashClosingResult>> HandleAsync(
        CloseCashSessionCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
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
