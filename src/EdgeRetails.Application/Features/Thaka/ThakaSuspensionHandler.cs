using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Thaka;

namespace EdgeRetails.Application.Features.Thaka;

public sealed record SetThakaSuspensionCommand(Guid ClientOperationId, Guid ProjectId,
    bool IsSuspended, string Reason, Guid ActorId, Guid? TerminalId = null, Guid? SessionId = null);

public sealed class SetThakaSuspensionHandler(IThakaRepository thaka, ITransactionRunner transactions,
    IApplicationPermissionAuthorizer authorization, IOperationLock operationLock, IResourceLock resourceLock,
    IOperationOutcomeLedger outcomes, IBusinessAuditWriter audit, IUnitOfWork unitOfWork)
{
    public Task<Result<Guid>> HandleAsync(SetThakaSuspensionCommand command, CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.ProjectId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 500)
        {
            return Task.FromResult(Result<Guid>.Failure("thaka.suspension_invalid", "Select a project and provide a suspension/resume reason (maximum 500 characters)."));
        }

        return transactions.ExecuteAsync(async ct =>
        {
            var permission = await authorization.AuthorizeAsync(command.ActorId, PermissionKeys.ThakaManage, ct);
            if (!permission.IsSuccess)
            {
                return Result<Guid>.Failure(permission.Error!.Code, permission.Error.Message);
            }

            var fingerprint = OperationPayloadFingerprint.ComputeSha256("Thaka.Suspension.v1",
                command.ProjectId.ToString("D"), command.IsSuspended.ToString(), command.Reason.Trim());
            await operationLock.AcquireAsync(command.ClientOperationId, ct);
            var saved = await outcomes.GetOutcomeAsync(command.ClientOperationId, ct);
            if (saved is not null)
            {
                return saved.State == OperationOutcomeState.Succeeded && saved.OperationType == "Thaka.Suspension.v1"
                    && saved.EntityId == command.ProjectId && saved.ActorId == command.ActorId
                    && saved.TerminalId == command.TerminalId && saved.PayloadFingerprint == fingerprint
                    ? Result<Guid>.Success(command.ProjectId)
                    : Result<Guid>.Failure("thaka.operation_id_conflict", "This operation id belongs to another account status change.");
            }

            await resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null)
            {
                return Result<Guid>.Failure("thaka.project_not_found", "Thaka project was not found.");
            }
            if (project.Status is not (ThakaProjectStatus.Active or ThakaProjectStatus.Suspended))
            {
                return Result<Guid>.Failure("thaka.suspension_invalid_state", "Only active or suspended accounts can change suspension status.");
            }

            var target = command.IsSuspended ? ThakaProjectStatus.Suspended : ThakaProjectStatus.Active;
            if (project.Status != target)
            {
                project.Status = target;
                project.Version++;
                audit.Record(command.IsSuspended ? "THAKA_SUSPENDED" : "THAKA_RESUMED", "THAKA_PROJECT",
                    project.Id, command.ActorId, command.ClientOperationId, command.Reason.Trim());
            }
            await outcomes.RecordSuccessAsync(command.ClientOperationId, "Thaka.Suspension.v1", project.Id,
                project.ProjectNumber, command.ActorId, command.TerminalId, command.SessionId, fingerprint, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(project.Id);
        }, cancellationToken);
    }
}
