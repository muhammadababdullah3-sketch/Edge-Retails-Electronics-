using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Thaka;

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
        IUnitOfWork unitOfWork)
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
    }

    public Task<Result<RecordThakaPaymentResult>> HandleAsync(
        RecordThakaPaymentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.Amount <= 0)
        {
            return Task.FromResult(Result<RecordThakaPaymentResult>.Failure(
                "thaka.payment_invalid",
                "Payment requires operation id and positive amount."));
        }

        return _transactions.ExecuteAsync(async ct =>
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
                var existingBalance = await GetBalanceAsync(existing.ProjectId, ct);
                return Result<RecordThakaPaymentResult>.Success(new(
                    existing.Id,
                    existing.ReceiptNumber,
                    existingBalance,
                    true));
            }

            await _resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await _thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null || project.Status != ThakaProjectStatus.Active)
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

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<RecordThakaPaymentResult>.Success(new(
                payment.Id,
                payment.ReceiptNumber,
                Money(balanceBefore - amount),
                false));
        }, cancellationToken);
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

        return Money(Math.Max(0m, charges - discounts - payments));
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
