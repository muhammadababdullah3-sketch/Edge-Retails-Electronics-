using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Application.Features.Finance;

public sealed record SupplierPaymentResult(Guid PaymentId, string PaymentNumber, bool WasExisting);

public sealed record CreateSupplierPaymentCommand(
    Guid SupplierId,
    decimal Amount,
    SupplierPaymentPurpose Purpose,
    SupplierSettlementMethod Method,
    Guid ActorId,
    Guid ClientOperationId,
    string? ExternalReference = null,
    string? Note = null);

public sealed class CreateSupplierPaymentHandler
{
    private readonly ISupplierAccountRepository _accounts;
    private readonly IPartyRepository _parties;
    private readonly ICashRepository _cash;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly IBusinessAuditWriter _audit;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public CreateSupplierPaymentHandler(
        ISupplierAccountRepository accounts,
        IPartyRepository parties,
        ICashRepository cash,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IDocumentNumberService numbers,
        IClock clock,
        IBusinessAuditWriter audit,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _accounts = accounts;
        _parties = parties;
        _cash = cash;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _numbers = numbers;
        _clock = clock;
        _audit = audit;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<SupplierPaymentResult>> HandleAsync(
        CreateSupplierPaymentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.SupplierId == Guid.Empty || command.Amount <= 0)
        {
            return Task.FromResult(Result<SupplierPaymentResult>.Failure(
                "supplier.payment_invalid",
                "Supplier, positive amount, and client operation id are required."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var permission = command.Purpose == SupplierPaymentPurpose.Advance
                ? PermissionKeys.SupplierAdvanceCreate
                : PermissionKeys.SupplierPaymentCreate;

            var authorization = await _authorization.AuthorizeAsync(command.ActorId, permission, ct);
            if (!authorization.IsSuccess)
            {
                return Result<SupplierPaymentResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var existing = await _accounts.GetPaymentByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existing is not null)
            {
                if (existing.SupplierId != command.SupplierId || existing.Amount != command.Amount)
                {
                    return Result<SupplierPaymentResult>.Failure(
                        "idempotency.payload_mismatch",
                        "Operation was previously submitted with a different supplier or amount.");
                }

                return Result<SupplierPaymentResult>.Success(
                    new(existing.Id, existing.PaymentNumber, true));
            }

            await _resourceLock.AcquireAsync("supplier-account", command.SupplierId, ct);
            var supplier = await _parties.GetSupplierForUpdateAsync(command.SupplierId, ct);
            if (supplier is null || !supplier.IsActive)
            {
                return Result<SupplierPaymentResult>.Failure(
                    "supplier.not_active",
                    "Supplier was not found or is inactive.");
            }

            var balance = await _accounts.GetCurrentBalanceAsync(command.SupplierId, ct);
            var outstanding = Math.Max(balance, 0m);

            if (command.Purpose == SupplierPaymentPurpose.Settlement && command.Amount > outstanding)
            {
                return Result<SupplierPaymentResult>.Failure(
                    "supplier.payment_exceeds_payable",
                    "Settlement cannot exceed the current outstanding payable.");
            }

            if (command.Purpose == SupplierPaymentPurpose.Advance && string.IsNullOrWhiteSpace(command.Note))
            {
                return Result<SupplierPaymentResult>.Failure(
                    "supplier.advance_reason_required",
                    "Supplier advance requires an explicit reason/note.");
            }

            var payment = new SupplierPayment
            {
                PaymentNumber = await _numbers.NextAsync("SP", ct),
                SupplierId = command.SupplierId,
                Amount = Money(command.Amount),
                Purpose = command.Purpose,
                Method = command.Method,
                ExternalReference = Normalize(command.ExternalReference),
                PaidAt = _clock.UtcNow,
                ActorId = command.ActorId,
                ClientOperationId = command.ClientOperationId,
                Note = Normalize(command.Note)
            };

            if (command.Method == SupplierSettlementMethod.CashDrawer)
            {
                var cashSession = await _cash.GetOpenSessionForUpdateAsync(ct);
                if (cashSession is null)
                {
                    return Result<SupplierPaymentResult>.Failure(
                        "cash.session_required",
                        "An open cash session is required for a cash-drawer supplier payment.");
                }

                payment.CashSessionId = cashSession.Id;
                _cash.AddMovement(new CashMovement
                {
                    CashSessionId = cashSession.Id,
                    MovementType = CashMovementType.SupplierPaymentCashOut,
                    Direction = CashMovementDirection.Out,
                    Amount = payment.Amount,
                    SourceType = "SUPPLIER_PAYMENT",
                    SourceId = payment.Id,
                    ActorId = command.ActorId,
                    OccurredAt = _clock.UtcNow,
                    Reason = payment.PaymentNumber
                });
            }

            _accounts.AddPayment(payment);
            await AddEntryAsync(
                SupplierAccountEntryType.SupplierPayment,
                SupplierAccountDirection.DecreasePayable,
                command.SupplierId,
                payment.Amount,
                "SupplierPayment",
                payment.Id,
                command.ActorId,
                command.ClientOperationId,
                command.Note,
                ct);

            _audit.Record(
                command.Purpose == SupplierPaymentPurpose.Advance
                    ? "SUPPLIER_ADVANCE_POSTED"
                    : "SUPPLIER_PAYMENT_POSTED",
                "SUPPLIER_PAYMENT",
                payment.Id,
                command.ActorId,
                command.ClientOperationId,
                $"{payment.PaymentNumber}; supplier={command.SupplierId:D}; amount={payment.Amount:0.00}; purpose={payment.Purpose}.");

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<SupplierPaymentResult>.Success(
                new(payment.Id, payment.PaymentNumber, false));
        }, cancellationToken);
    }

    private async Task AddEntryAsync(
        SupplierAccountEntryType type,
        SupplierAccountDirection direction,
        Guid supplierId,
        decimal amount,
        string referenceType,
        Guid referenceId,
        Guid actorId,
        Guid? clientOperationId,
        string? note,
        CancellationToken ct)
    {
        var entry = new SupplierAccountEntry
        {
            EntryNumber = await _numbers.NextAsync("SAE", ct),
            SupplierId = supplierId,
            EntryType = type,
            Direction = direction,
            Amount = Money(amount),
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            OccurredAt = _clock.UtcNow,
            ActorId = actorId,
            ClientOperationId = clientOperationId,
            Note = Normalize(note),
            CreatedAt = _clock.UtcNow
        };
        entry.ValidateDirection();
        _accounts.AddEntry(entry);
    }

    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed record ReverseSupplierPaymentCommand(
    Guid PaymentId,
    string Reason,
    Guid ActorId,
    Guid ClientOperationId);

public sealed class ReverseSupplierPaymentHandler
{
    private readonly ISupplierAccountRepository _accounts;
    private readonly ICashRepository _cash;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly IBusinessAuditWriter _audit;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public ReverseSupplierPaymentHandler(
        ISupplierAccountRepository accounts,
        ICashRepository cash,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IDocumentNumberService numbers,
        IClock clock,
        IBusinessAuditWriter audit,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _accounts = accounts;
        _cash = cash;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _numbers = numbers;
        _clock = clock;
        _audit = audit;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(ReverseSupplierPaymentCommand command, CancellationToken cancellationToken)
    {
        if (command.PaymentId == Guid.Empty || command.ClientOperationId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason))
        {
            return Task.FromResult(Result.Failure(
                "supplier.payment_reversal_invalid",
                "Payment, reason, and client operation id are required."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SupplierPaymentReverse,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var payment = await _accounts.GetPaymentForUpdateAsync(command.PaymentId, ct);
            if (payment is null)
            {
                return Result.Failure("supplier.payment_not_found", "Supplier payment was not found.");
            }

            await _resourceLock.AcquireAsync("supplier-account", payment.SupplierId, ct);

            if (await _accounts.GetPaymentReversalByPaymentAsync(payment.Id, ct) is not null)
            {
                return Result.Success();
            }

            var reversal = new SupplierPaymentReversal
            {
                SupplierPaymentId = payment.Id,
                Reason = command.Reason.Trim(),
                ReversedBy = command.ActorId,
                ReversedAt = _clock.UtcNow,
                ClientOperationId = command.ClientOperationId
            };

            payment.Status = SupplierSettlementStatus.Reversed;
            _accounts.AddPaymentReversal(reversal);

            var entry = new SupplierAccountEntry
            {
                EntryNumber = await _numbers.NextAsync("SAE", ct),
                SupplierId = payment.SupplierId,
                EntryType = SupplierAccountEntryType.SupplierPaymentReversal,
                Direction = SupplierAccountDirection.IncreasePayable,
                Amount = payment.Amount,
                ReferenceType = "SupplierPaymentReversal",
                ReferenceId = reversal.Id,
                OccurredAt = _clock.UtcNow,
                ActorId = command.ActorId,
                ClientOperationId = command.ClientOperationId,
                Note = command.Reason.Trim(),
                CreatedAt = _clock.UtcNow
            };
            entry.ValidateDirection();
            _accounts.AddEntry(entry);

            if (payment.Method == SupplierSettlementMethod.CashDrawer)
            {
                var session = await _cash.GetOpenSessionForUpdateAsync(ct);
                if (session is null)
                {
                    return Result.Failure(
                        "cash.session_required",
                        "An active open cash session is required for cash drawer reversal.");
                }

                _cash.AddMovement(new CashMovement
                {
                    CashSessionId = session.Id,
                    MovementType = CashMovementType.SupplierPaymentReversalCashIn,
                    Direction = CashMovementDirection.In,
                    Amount = payment.Amount,
                    SourceType = "SUPPLIER_PAYMENT_REVERSAL",
                    SourceId = reversal.Id,
                    ActorId = command.ActorId,
                    OccurredAt = _clock.UtcNow,
                    Reason = command.Reason.Trim()
                });
            }

            _audit.Record(
                "SUPPLIER_PAYMENT_REVERSED",
                "SUPPLIER_PAYMENT",
                payment.Id,
                command.ActorId,
                command.ClientOperationId,
                command.Reason.Trim());

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}

public sealed record SupplierRefundResult(Guid RefundId, string RefundNumber, bool WasExisting);

public sealed record CreateSupplierRefundCommand(
    Guid SupplierId,
    decimal Amount,
    SupplierSettlementMethod Method,
    Guid ActorId,
    Guid ClientOperationId,
    string? ExternalReference = null,
    string? ReferenceType = null,
    Guid? ReferenceId = null,
    string? Note = null);

public sealed class CreateSupplierRefundHandler
{
    private readonly ISupplierAccountRepository _accounts;
    private readonly IPartyRepository _parties;
    private readonly ICashRepository _cash;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly IBusinessAuditWriter _audit;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public CreateSupplierRefundHandler(
        ISupplierAccountRepository accounts,
        IPartyRepository parties,
        ICashRepository cash,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IDocumentNumberService numbers,
        IClock clock,
        IBusinessAuditWriter audit,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _accounts = accounts;
        _parties = parties;
        _cash = cash;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _numbers = numbers;
        _clock = clock;
        _audit = audit;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<SupplierRefundResult>> HandleAsync(
        CreateSupplierRefundCommand command,
        CancellationToken cancellationToken)
    {
        if (command.SupplierId == Guid.Empty || command.ClientOperationId == Guid.Empty || command.Amount <= 0)
        {
            return Task.FromResult(Result<SupplierRefundResult>.Failure(
                "supplier.refund_invalid",
                "Supplier, positive amount, and client operation id are required."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SupplierRefundCreate,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<SupplierRefundResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var existing = await _accounts.GetRefundByClientOperationIdAsync(command.ClientOperationId, ct);
            if (existing is not null)
            {
                return Result<SupplierRefundResult>.Success(
                    new(existing.Id, existing.RefundNumber, true));
            }

            await _resourceLock.AcquireAsync("supplier-account", command.SupplierId, ct);
            var supplier = await _parties.GetSupplierForUpdateAsync(command.SupplierId, ct);
            if (supplier is null)
            {
                return Result<SupplierRefundResult>.Failure(
                    "supplier.not_found",
                    "Supplier was not found.");
            }

            var balance = await _accounts.GetCurrentBalanceAsync(command.SupplierId, ct);
            var credit = Math.Max(-balance, 0m);
            if (command.Amount > credit)
            {
                return Result<SupplierRefundResult>.Failure(
                    "supplier.refund_exceeds_credit",
                    "Supplier refund cannot exceed the current supplier credit/advance.");
            }

            var refund = new SupplierRefund
            {
                RefundNumber = await _numbers.NextAsync("SR", ct),
                SupplierId = command.SupplierId,
                Amount = decimal.Round(command.Amount, 2, MidpointRounding.AwayFromZero),
                Method = command.Method,
                ExternalReference = Normalize(command.ExternalReference),
                ReferenceType = Normalize(command.ReferenceType),
                ReferenceId = command.ReferenceId,
                ReceivedAt = _clock.UtcNow,
                ActorId = command.ActorId,
                ClientOperationId = command.ClientOperationId,
                Note = Normalize(command.Note)
            };

            if (command.Method == SupplierSettlementMethod.CashDrawer)
            {
                var cashSession = await _cash.GetOpenSessionForUpdateAsync(ct);
                if (cashSession is null)
                {
                    return Result<SupplierRefundResult>.Failure(
                        "cash.session_required",
                        "An open cash session is required for a cash-drawer supplier refund.");
                }

                refund.CashSessionId = cashSession.Id;
                _cash.AddMovement(new CashMovement
                {
                    CashSessionId = cashSession.Id,
                    MovementType = CashMovementType.SupplierRefundCashIn,
                    Direction = CashMovementDirection.In,
                    Amount = refund.Amount,
                    SourceType = "SUPPLIER_REFUND",
                    SourceId = refund.Id,
                    ActorId = command.ActorId,
                    OccurredAt = _clock.UtcNow,
                    Reason = refund.RefundNumber
                });
            }

            _accounts.AddRefund(refund);
            var entry = new SupplierAccountEntry
            {
                EntryNumber = await _numbers.NextAsync("SAE", ct),
                SupplierId = refund.SupplierId,
                EntryType = SupplierAccountEntryType.SupplierRefundReceived,
                Direction = SupplierAccountDirection.IncreasePayable,
                Amount = refund.Amount,
                ReferenceType = "SupplierRefund",
                ReferenceId = refund.Id,
                OccurredAt = _clock.UtcNow,
                ActorId = command.ActorId,
                ClientOperationId = command.ClientOperationId,
                Note = refund.Note,
                CreatedAt = _clock.UtcNow
            };
            entry.ValidateDirection();
            _accounts.AddEntry(entry);

            _audit.Record(
                "SUPPLIER_REFUND_POSTED",
                "SUPPLIER_REFUND",
                refund.Id,
                command.ActorId,
                command.ClientOperationId,
                $"{refund.RefundNumber}; supplier={command.SupplierId:D}; amount={refund.Amount:0.00}.");

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<SupplierRefundResult>.Success(
                new(refund.Id, refund.RefundNumber, false));
        }, cancellationToken);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed record ReverseSupplierRefundCommand(
    Guid RefundId,
    string Reason,
    Guid ActorId,
    Guid ClientOperationId);

public sealed class ReverseSupplierRefundHandler
{
    private readonly ISupplierAccountRepository _accounts;
    private readonly ICashRepository _cash;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly IBusinessAuditWriter _audit;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public ReverseSupplierRefundHandler(
        ISupplierAccountRepository accounts,
        ICashRepository cash,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IDocumentNumberService numbers,
        IClock clock,
        IBusinessAuditWriter audit,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _accounts = accounts;
        _cash = cash;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _numbers = numbers;
        _clock = clock;
        _audit = audit;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(ReverseSupplierRefundCommand command, CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SupplierRefundReverse,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var refund = await _accounts.GetRefundForUpdateAsync(command.RefundId, ct);
            if (refund is null)
            {
                return Result.Failure("supplier.refund_not_found", "Supplier refund was not found.");
            }

            await _resourceLock.AcquireAsync("supplier-account", refund.SupplierId, ct);
            if (await _accounts.GetRefundReversalByRefundAsync(refund.Id, ct) is not null)
            {
                return Result.Success();
            }

            var reversal = new SupplierRefundReversal
            {
                SupplierRefundId = refund.Id,
                Reason = command.Reason.Trim(),
                ReversedBy = command.ActorId,
                ReversedAt = _clock.UtcNow,
                ClientOperationId = command.ClientOperationId
            };
            refund.Status = SupplierSettlementStatus.Reversed;
            _accounts.AddRefundReversal(reversal);

            var entry = new SupplierAccountEntry
            {
                EntryNumber = await _numbers.NextAsync("SAE", ct),
                SupplierId = refund.SupplierId,
                EntryType = SupplierAccountEntryType.SupplierRefundReversal,
                Direction = SupplierAccountDirection.DecreasePayable,
                Amount = refund.Amount,
                ReferenceType = "SupplierRefundReversal",
                ReferenceId = reversal.Id,
                OccurredAt = _clock.UtcNow,
                ActorId = command.ActorId,
                ClientOperationId = command.ClientOperationId,
                Note = command.Reason.Trim(),
                CreatedAt = _clock.UtcNow
            };
            entry.ValidateDirection();
            _accounts.AddEntry(entry);

            if (refund.Method == SupplierSettlementMethod.CashDrawer)
            {
                var session = await _cash.GetOpenSessionForUpdateAsync(ct);
                if (session is null)
                {
                    return Result.Failure(
                        "cash.session_required",
                        "An active open cash session is required for cash drawer reversal.");
                }

                _cash.AddMovement(new CashMovement
                {
                    CashSessionId = session.Id,
                    MovementType = CashMovementType.SupplierRefundReversalCashOut,
                    Direction = CashMovementDirection.Out,
                    Amount = refund.Amount,
                    SourceType = "SUPPLIER_REFUND_REVERSAL",
                    SourceId = reversal.Id,
                    ActorId = command.ActorId,
                    OccurredAt = _clock.UtcNow,
                    Reason = command.Reason.Trim()
                });
            }

            _audit.Record(
                "SUPPLIER_REFUND_REVERSED",
                "SUPPLIER_REFUND",
                refund.Id,
                command.ActorId,
                command.ClientOperationId,
                command.Reason.Trim());

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}

public sealed record SupplierAccountAdjustmentCommand(
    Guid SupplierId,
    decimal Amount,
    SupplierAccountDirection Direction,
    string Reason,
    Guid ActorId,
    Guid ClientOperationId);

public sealed class SupplierAccountAdjustmentHandler
{
    private readonly ISupplierAccountRepository _accounts;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly IBusinessAuditWriter _audit;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SupplierAccountAdjustmentHandler(
        ISupplierAccountRepository accounts,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        IDocumentNumberService numbers,
        IClock clock,
        IBusinessAuditWriter audit,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _accounts = accounts;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _numbers = numbers;
        _clock = clock;
        _audit = audit;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(SupplierAccountAdjustmentCommand command, CancellationToken cancellationToken)
    {
        if (command.SupplierId == Guid.Empty || command.ClientOperationId == Guid.Empty ||
            command.Amount <= 0 || string.IsNullOrWhiteSpace(command.Reason))
        {
            return Task.FromResult(Result.Failure(
                "supplier.adjustment_invalid",
                "Supplier, positive amount, reason, and client operation id are required."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SupplierAccountAdjust,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            await _resourceLock.AcquireAsync("supplier-account", command.SupplierId, ct);

            if (await _accounts.HasSourceEntryAsync(
                    command.Direction == SupplierAccountDirection.IncreasePayable
                        ? SupplierAccountEntryType.AdjustmentIncrease
                        : SupplierAccountEntryType.AdjustmentDecrease,
                    "SupplierAccountAdjustment",
                    command.ClientOperationId,
                    ct))
            {
                return Result.Success();
            }

            var entry = new SupplierAccountEntry
            {
                EntryNumber = await _numbers.NextAsync("SAE", ct),
                SupplierId = command.SupplierId,
                EntryType = command.Direction == SupplierAccountDirection.IncreasePayable
                    ? SupplierAccountEntryType.AdjustmentIncrease
                    : SupplierAccountEntryType.AdjustmentDecrease,
                Direction = command.Direction,
                Amount = decimal.Round(command.Amount, 2, MidpointRounding.AwayFromZero),
                ReferenceType = "SupplierAccountAdjustment",
                ReferenceId = command.ClientOperationId,
                OccurredAt = _clock.UtcNow,
                ActorId = command.ActorId,
                ClientOperationId = command.ClientOperationId,
                Note = command.Reason.Trim(),
                CreatedAt = _clock.UtcNow
            };
            entry.ValidateDirection();
            _accounts.AddEntry(entry);

            _audit.Record(
                "SUPPLIER_ACCOUNT_ADJUSTED",
                "SUPPLIER_ACCOUNT",
                command.SupplierId,
                command.ActorId,
                command.ClientOperationId,
                $"{command.Direction}; amount={entry.Amount:0.00}; reason={command.Reason.Trim()}");

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}
