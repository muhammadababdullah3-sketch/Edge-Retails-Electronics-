using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;

namespace EdgeRetails.Application.Features.Parties;

public sealed record SetCustomerSuspensionCommand(Guid ClientOperationId, Guid CustomerId,
    bool IsSuspended, Guid ActorId, Guid? TerminalId = null, Guid? SessionId = null);

public sealed class SetCustomerSuspensionHandler(IPartyRepository parties, ITransactionRunner transactions,
    IApplicationPermissionAuthorizer authorization, IOperationLock operationLock,
    IOperationOutcomeLedger outcomes, IBusinessAuditWriter audit, IUnitOfWork unitOfWork)
{
    public Task<Result<Guid>> HandleAsync(SetCustomerSuspensionCommand command, CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.CustomerId == Guid.Empty)
        {
            return Task.FromResult(Result<Guid>.Failure("parties.suspension_invalid", "A customer and operation id are required."));
        }
        return transactions.ExecuteAsync(async ct =>
        {
            var permission = await authorization.AuthorizeAsync(command.ActorId, PermissionKeys.CustomersManage, ct);
            if (!permission.IsSuccess)
            {
                return Result<Guid>.Failure(permission.Error!.Code, permission.Error.Message);
            }
            var fingerprint = OperationPayloadFingerprint.ComputeSha256("Customer.Suspension.v1",
                command.CustomerId.ToString("D"), command.IsSuspended.ToString());
            await operationLock.AcquireAsync(command.ClientOperationId, ct);
            var saved = await outcomes.GetOutcomeAsync(command.ClientOperationId, ct);
            if (saved is not null)
            {
                return saved.State == OperationOutcomeState.Succeeded && saved.OperationType == "Customer.Suspension.v1"
                    && saved.EntityId == command.CustomerId && saved.ActorId == command.ActorId
                    && saved.TerminalId == command.TerminalId && saved.PayloadFingerprint == fingerprint
                    ? Result<Guid>.Success(command.CustomerId)
                    : Result<Guid>.Failure("parties.operation_id_conflict", "Operation id belongs to another customer status change.");
            }
            var customer = await parties.GetCustomerForUpdateAsync(command.CustomerId, ct);
            if (customer is null || customer.IsWalkIn)
            {
                return Result<Guid>.Failure("parties.customer_not_available", "Customer was not found or is the protected Walk-in Customer.");
            }
            if (customer.IsActive == command.IsSuspended)
            {
                customer.IsActive = !command.IsSuspended;
                customer.Version++;
                audit.Record(command.IsSuspended ? "CUSTOMER_SUSPENDED" : "CUSTOMER_RESUMED", "CUSTOMER",
                    customer.Id, command.ActorId, command.ClientOperationId, "Customer status changed from directory.");
            }
            await outcomes.RecordSuccessAsync(command.ClientOperationId, "Customer.Suspension.v1", customer.Id,
                actorId: command.ActorId, terminalId: command.TerminalId, sessionId: command.SessionId,
                payloadFingerprint: fingerprint, cancellationToken: ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(customer.Id);
        }, cancellationToken);
    }
}
