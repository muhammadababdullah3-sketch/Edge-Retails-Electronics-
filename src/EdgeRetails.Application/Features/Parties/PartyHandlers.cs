using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
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
    string? Notes = null);

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
    string? ExplicitDealerPrefix = null);

public sealed class SaveCustomerHandler
{
    private readonly IPartyRepository _parties;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork; public SaveCustomerHandler(
        IPartyRepository parties,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _parties = parties;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        SaveCustomerCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Task.FromResult(
                Result<Guid>.Failure(
                    "parties.customer_name_required",
                    "Customer name is required."));
        }

        return _transactions.ExecuteAsync(async ct =>
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

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(customer.Id);
        }, cancellationToken);
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

    public SaveSupplierHandler(
        IPartyRepository parties,
        ITraceabilityRepository traceability,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _parties = parties;
        _traceability = traceability;
        _resourceLock = resourceLock;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        SaveSupplierCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Task.FromResult(
                Result<Guid>.Failure(
                    "parties.supplier_name_required",
                    "Supplier name is required."));
        }

        return _transactions.ExecuteAsync(async ct =>
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

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(supplier.Id);
        }, cancellationToken);
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

        var dealerCode = TraceabilityCodeRules.BuildDealerCode(prefix, sequence.NextValue);
        sequence.NextValue = checked(sequence.NextValue + 1);
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
