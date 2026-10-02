using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Parties;

namespace EdgeRetails.Application.Features.Parties;

public sealed record SaveCustomerCommand(
    Guid? CustomerId,
    string Name,
    string? Phone,
    string? Address,
    bool IsActive,
    Guid ActorId,
    Guid CorrelationId,
    string? Notes = null,
    Guid? ClientOperationId = null,
    Guid? TerminalId = null,
    Guid? SessionId = null);

public sealed record SaveSupplierCommand(
    Guid? SupplierId,
    string Name,
    string? Phone,
    string? City,
    string? Address,
    bool IsActive,
    Guid ActorId,
    Guid CorrelationId,
    string? Notes = null,
    string? ExplicitDealerPrefix = null,
    Guid? ClientOperationId = null,
    Guid? TerminalId = null,
    Guid? SessionId = null);

public sealed class SaveCustomerHandler
{
    private readonly IPartyRepository _parties;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public SaveCustomerHandler(
        IPartyRepository parties,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _parties = parties;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public async Task<Result<Guid>> HandleAsync(
        SaveCustomerCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Result<Guid>.Failure(
                "parties.customer_name_required",
                "Customer name is required.");
        }

        var fingerprint = OperationPayloadFingerprint.ComputeSha256(
            "Customer.Save.v1", command.CustomerId?.ToString("D"), command.Name.Trim(),
            Normalize(command.Phone), Normalize(command.Address), command.IsActive.ToString(), Normalize(command.Notes));
        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.CustomersManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<Guid>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            var replay = await GetPartyReplayAsync(command.ClientOperationId, "Customer.Save.v1", fingerprint,
                command.ActorId, command.TerminalId, command.SessionId, ct);
            if (replay.Error is not null)
            {
                return Result<Guid>.Failure(replay.Error.Code, replay.Error.Message);
            }
            if (replay.EntityId is Guid replayId)
            {
                return Result<Guid>.Success(replayId);
            }
            if (command.ClientOperationId is Guid operationId)
            {
                await _outcomeLedger!.RecordPendingAsync(operationId, "Customer.Save.v1", command.ActorId,
                    command.TerminalId, command.SessionId, fingerprint, ct);
            }

            Customer customer;
            var action = "CUSTOMER_CREATED";
            if (command.CustomerId is null)
            {
                customer = new Customer
                {
                    CreatedAt = _clock.UtcNow
                };
                _parties.AddCustomer(customer);
            }
            else
            {
                customer = await _parties.GetCustomerForUpdateAsync(
                    command.CustomerId.Value,
                    ct) ?? throw new InvalidOperationException(
                        "Customer was not found.");

                if (customer.IsWalkIn)
                {
                    return Result<Guid>.Failure(
                        "parties.walk_in_protected",
                        "Protected Walk-in Customer cannot be edited here.");
                }

                action = "CUSTOMER_UPDATED";
            }

            customer.Name = command.Name.Trim();
            customer.Phone = Normalize(command.Phone);
            customer.Address = Normalize(command.Address);
            customer.Notes = Normalize(command.Notes);
            customer.IsActive = command.IsActive;
            customer.Version++;

            _audit.Record(
                action,
                "CUSTOMER",
                customer.Id,
                command.ActorId,
                command.CorrelationId,
                customer.Name);

            if (command.ClientOperationId is Guid clientOperationId)
            {
                await _outcomeLedger!.RecordSuccessAsync(clientOperationId, "Customer.Save.v1", customer.Id,
                    actorId: command.ActorId, terminalId: command.TerminalId, sessionId: command.SessionId,
                    payloadFingerprint: fingerprint, cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(customer.Id);
        }, cancellationToken);
        if (!result.IsSuccess && command.ClientOperationId is Guid failedOperationId && _outcomeLedger is not null)
        {
            await _outcomeLedger.RecordFailureAsync(failedOperationId, "Customer.Save.v1",
                result.Error?.Code ?? "customer.save_failed", result.Error?.Message ?? "Customer could not be saved.",
                command.ActorId, command.TerminalId, command.SessionId, fingerprint, cancellationToken);
        }
        return result;
    }

    private async Task<(Guid? EntityId, Error? Error)> GetPartyReplayAsync(Guid? operationId, string operationType,
        string fingerprint, Guid actorId, Guid? terminalId, Guid? sessionId, CancellationToken cancellationToken)
    {
        if (!operationId.HasValue)
        {
            return (null, null);
        }
        if (operationId.Value == Guid.Empty || _operationLock is null || _outcomeLedger is null)
        {
            return (null, new Error("operation.ledger_unavailable", "Party mutation replay protection is unavailable."));
        }
        await _operationLock.AcquireAsync(operationId.Value, cancellationToken);
        var prior = await _outcomeLedger.GetOutcomeAsync(operationId.Value, cancellationToken);
        if (prior is null)
        {
            return (null, null);
        }
        if (prior.OperationType != operationType || prior.PayloadFingerprint != fingerprint || prior.ActorId != actorId ||
            (prior.TerminalId.HasValue && prior.TerminalId != terminalId) ||
            (prior.SessionId.HasValue && prior.SessionId != sessionId))
        {
            return (null, new Error("idempotency.payload_mismatch", "Operation identity was already used with a different party payload or actor."));
        }
        if (prior.State == OperationOutcomeState.Succeeded && prior.EntityId is Guid entityId)
        {
            return (entityId, null);
        }
        if (prior.State == OperationOutcomeState.Failed)
        {
            return (null, new Error(prior.ErrorCode ?? "party.save_failed", prior.ErrorMessage ?? "The original party operation failed."));
        }
        return (null, new Error("operation.outcome_unknown", "The previous party operation has no final result. Resolve its status before retrying."));
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
public sealed class SaveSupplierHandler
{
    private readonly IPartyRepository _parties;
    private readonly ITraceabilityRepository _traceability;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISequenceHighWaterService _highWaterService;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public SaveSupplierHandler(
        IPartyRepository parties,
        ITraceabilityRepository traceability,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        ISequenceHighWaterService? highWaterService = null,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _parties = parties;
        _traceability = traceability;
        _resourceLock = resourceLock;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _highWaterService = highWaterService ?? NullSequenceHighWaterService.Instance;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public async Task<Result<Guid>> HandleAsync(
        SaveSupplierCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Result<Guid>.Failure(
                "parties.supplier_name_required",
                "Supplier name is required.");
        }

        var fingerprint = OperationPayloadFingerprint.ComputeSha256(
            "Supplier.Save.v1", command.SupplierId?.ToString("D"), command.Name.Trim(),
            Normalize(command.Phone), Normalize(command.City), Normalize(command.Address), command.IsActive.ToString(),
            Normalize(command.Notes), Normalize(command.ExplicitDealerPrefix));
        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SuppliersManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<Guid>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            var replay = await GetSupplierReplayAsync(command, fingerprint, ct);
            if (replay.Error is not null)
            {
                return Result<Guid>.Failure(replay.Error.Code, replay.Error.Message);
            }
            if (replay.EntityId is Guid replayId)
            {
                return Result<Guid>.Success(replayId);
            }
            if (command.ClientOperationId is Guid operationId)
            {
                await _outcomeLedger!.RecordPendingAsync(operationId, "Supplier.Save.v1", command.ActorId,
                    command.TerminalId, command.SessionId, fingerprint, ct);
            }

            Supplier supplier;
            var action = "SUPPLIER_CREATED";
            if (command.SupplierId is null)
            {
                supplier = new Supplier
                {
                    DealerCode = await AllocateDealerCodeAsync(command.Name, command.ExplicitDealerPrefix, ct),
                    CreatedAt = _clock.UtcNow
                };
                _parties.AddSupplier(supplier);
            }
            else
            {
                supplier = await _parties.GetSupplierForUpdateAsync(
                    command.SupplierId.Value,
                    ct) ?? throw new InvalidOperationException(
                        "Supplier was not found.");
                action = "SUPPLIER_UPDATED";

                if (string.IsNullOrWhiteSpace(supplier.DealerCode))
                {
                    supplier.DealerCode = await AllocateDealerCodeAsync(command.Name, command.ExplicitDealerPrefix, ct);
                }
            }

            supplier.Name = command.Name.Trim();
            supplier.Phone = Normalize(command.Phone);
            supplier.City = Normalize(command.City);
            supplier.Address = Normalize(command.Address);
            supplier.Notes = Normalize(command.Notes);
            supplier.IsActive = command.IsActive;
            supplier.Version++;

            _audit.Record(
                action,
                "SUPPLIER",
                supplier.Id,
                command.ActorId,
                command.CorrelationId,
                supplier.Name);

            if (command.ClientOperationId is Guid clientOperationId)
            {
                await _outcomeLedger!.RecordSuccessAsync(clientOperationId, "Supplier.Save.v1", supplier.Id,
                    actorId: command.ActorId, terminalId: command.TerminalId, sessionId: command.SessionId,
                    payloadFingerprint: fingerprint, cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(supplier.Id);
        }, cancellationToken);
        if (!result.IsSuccess && command.ClientOperationId is Guid failedOperationId && _outcomeLedger is not null)
        {
            await _outcomeLedger.RecordFailureAsync(failedOperationId, "Supplier.Save.v1",
                result.Error?.Code ?? "supplier.save_failed", result.Error?.Message ?? "Supplier could not be saved.",
                command.ActorId, command.TerminalId, command.SessionId, fingerprint, cancellationToken);
        }
        return result;
    }

    private async Task<(Guid? EntityId, Error? Error)> GetSupplierReplayAsync(
        SaveSupplierCommand command, string fingerprint, CancellationToken cancellationToken)
    {
        if (!command.ClientOperationId.HasValue)
        {
            return (null, null);
        }
        if (command.ClientOperationId.Value == Guid.Empty || _operationLock is null || _outcomeLedger is null)
        {
            return (null, new Error("operation.ledger_unavailable", "Supplier mutation replay protection is unavailable."));
        }
        await _operationLock.AcquireAsync(command.ClientOperationId.Value, cancellationToken);
        var prior = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId.Value, cancellationToken);
        if (prior is null)
        {
            return (null, null);
        }
        if (prior.OperationType != "Supplier.Save.v1" || prior.PayloadFingerprint != fingerprint || prior.ActorId != command.ActorId ||
            (prior.TerminalId.HasValue && prior.TerminalId != command.TerminalId) ||
            (prior.SessionId.HasValue && prior.SessionId != command.SessionId))
        {
            return (null, new Error("idempotency.payload_mismatch", "Operation identity was already used with a different supplier payload or actor."));
        }
        if (prior.State == OperationOutcomeState.Succeeded && prior.EntityId is Guid entityId)
        {
            return (entityId, null);
        }
        if (prior.State == OperationOutcomeState.Failed)
        {
            return (null, new Error(prior.ErrorCode ?? "supplier.save_failed", prior.ErrorMessage ?? "The original supplier operation failed."));
        }
        return (null, new Error("operation.outcome_unknown", "The previous supplier operation has no final result. Resolve its status before retrying."));
    }

    private async Task<string> AllocateDealerCodeAsync(
        string supplierName,
        string? explicitDealerPrefix,
        CancellationToken cancellationToken)
    {
        var prefix = TraceabilityCodeRules.DeriveDealerPrefix(supplierName, explicitDealerPrefix);
        await _resourceLock.AcquireAsync("supplier-code-prefix", prefix, cancellationToken);

        var sequence = await _traceability.GetSupplierCodeSequenceForUpdateAsync(
            prefix,
            cancellationToken);

        if (sequence is null)
        {
            sequence = new SupplierCodeSequence
            {
                Prefix = prefix,
                NextValue = 1
            };
            _traceability.AddSupplierCodeSequence(sequence);
        }

        var machineVal = _highWaterService.GetDealerPrefixHighWater(prefix);
        if (machineVal > sequence.NextValue)
        {
            sequence.NextValue = machineVal;
        }

        var dealerCode = TraceabilityCodeRules.BuildDealerCode(prefix, sequence.NextValue);
        sequence.NextValue = checked(sequence.NextValue + 1);
        _highWaterService.RecordDealerPrefixHighWater(prefix, sequence.NextValue);
        return dealerCode;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
public sealed record PartyListItemDto(
    Guid Id,
    string Name,
    string? Phone,
    string? Location,
    bool IsActive,
    bool IsProtected);

public sealed class GetCustomersHandler
{
    private readonly IPartyRepository _parties;

    public GetCustomersHandler(IPartyRepository parties) => _parties = parties;

    public async Task<IReadOnlyList<PartyListItemDto>> HandleAsync(
        bool includeInactive,
        CancellationToken cancellationToken) =>
        (await _parties.GetCustomersAsync(includeInactive, cancellationToken))
            .Select(x => new PartyListItemDto(
                x.Id,
                x.Name,
                x.Phone,
                x.Address,
                x.IsActive,
                x.IsWalkIn))
            .ToArray();
}

public sealed class GetSuppliersHandler
{
    private readonly IPartyRepository _parties;

    public GetSuppliersHandler(IPartyRepository parties) => _parties = parties;

    public async Task<IReadOnlyList<PartyListItemDto>> HandleAsync(
        bool includeInactive,
        CancellationToken cancellationToken) =>
        (await _parties.GetSuppliersAsync(includeInactive, cancellationToken))
            .Select(x => new PartyListItemDto(
                x.Id,
                x.Name,
                x.Phone,
                x.City ?? x.Address,
                x.IsActive,
                false))
            .ToArray();
}
