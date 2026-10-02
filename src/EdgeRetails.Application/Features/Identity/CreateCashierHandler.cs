using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Identity;

namespace EdgeRetails.Application.Features.Identity;

public sealed record CreateCashierCommand(string DisplayName, string Pin, Guid ClientOperationId,
    Guid ActorId, Guid SessionId, Guid TerminalId, Guid CorrelationId)
{
    public override string ToString() => $"CreateCashierCommand {{ ClientOperationId = {ClientOperationId} }}";
}

public sealed record CreatedCashierDto(Guid UserId, string DisplayName, string RoleName);

/// <summary>Creates Cashier accounts using existing role and credential authorities.</summary>
public sealed class CreateCashierHandler(
    IIdentityAdministrationRepository administration,
    IIdentityReadRepository identity,
    IIdentitySessionRepository sessions,
    ITerminalRepository terminals,
    IApplicationPermissionAuthorizer authorization,
    IPinCredentialService credentials,
    ITransactionRunner transactions,
    IOperationLock operationLock,
    IResourceLock resourceLock,
    IOperationOutcomeLedger outcomes,
    IBusinessAuditWriter audit,
    IClock clock,
    IUnitOfWork unitOfWork)
{
    public const string OperationType = "Identity.Cashier.Create.v1";

    public async Task<Result<CreatedCashierDto>> HandleAsync(CreateCashierCommand command,
        CancellationToken cancellationToken)
    {
        var name = command.DisplayName?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 160 || name.Any(char.IsControl))
        {
            return Reject("identity.invalid_display_name", "Enter a user name of 1 to 160 characters.");
        }
        if (command.Pin is not { Length: 4 } || command.Pin.Any(ch => !char.IsAsciiDigit(ch)))
        {
            return Reject("identity.invalid_pin", "Enter a four-digit PIN.");
        }
        if (command.ClientOperationId == Guid.Empty || command.CorrelationId == Guid.Empty)
        {
            return Reject("identity.operation_id_required", "A stable operation identity is required.");
        }
        if (command.ActorId == Guid.Empty || command.SessionId == Guid.Empty || command.TerminalId == Guid.Empty)
        {
            return Reject("auth.session_required", "An authenticated user session and active terminal are required.");
        }

        // A four-digit PIN must never enter a persisted low-entropy fingerprint.
        var fingerprint = OperationPayloadFingerprint.ComputeSha256(OperationType, name, "Cashier", "Active");
        return await transactions.ExecuteAsync(async ct =>
        {
            var session = await sessions.GetSessionAsync(command.SessionId, ct);
            if (session is null || session.UserId != command.ActorId || session.IsRevoked || session.EndedAt.HasValue)
            {
                return Reject("auth.session_invalid", "The user session is no longer active.");
            }
            var terminal = await terminals.GetByIdAsync(command.TerminalId, ct);
            if (terminal is null || !terminal.CanMutate)
            {
                return Reject("auth.terminal_inactive", "An active terminal is required.");
            }
            var actor = await identity.GetUserAsync(command.ActorId, ct);
            var actorRole = actor is null ? null : await identity.GetRoleAsync(actor.RoleId, ct);
            if (actor?.Status != UserStatus.Active || actorRole is not { IsActive: true } ||
                !string.Equals(actorRole.Name, "Owner", StringComparison.OrdinalIgnoreCase))
            {
                return Reject("authorization.owner_required", "An active Owner must create Cashier accounts.");
            }
            var allowed = await authorization.AuthorizeAsync(command.ActorId, PermissionKeys.SettingsManage, ct);
            if (!allowed.IsSuccess)
            {
                return Reject(allowed.Error!.Code, allowed.Error.Message);
            }

            await operationLock.AcquireAsync(command.ClientOperationId, ct);
            var prior = await outcomes.GetOutcomeAsync(command.ClientOperationId, ct);
            if (prior is not null)
            {
                if (prior.OperationType != OperationType || prior.PayloadFingerprint != fingerprint ||
                    prior.ActorId != command.ActorId || prior.TerminalId != command.TerminalId)
                {
                    return Reject("idempotency.payload_mismatch", "This operation identity belongs to another request.");
                }
                if (prior.State == OperationOutcomeState.Succeeded && prior.EntityId is Guid existingId)
                {
                    var existing = await identity.GetUserAsync(existingId, ct);
                    if (existing is null || !credentials.Verify(command.Pin,
                        new(existing.PinHash, existing.PinSalt, existing.PinIterations, existing.PinAlgorithm)))
                    {
                        return Reject("idempotency.payload_mismatch", "This operation identity belongs to another request.");
                    }
                    return Result<CreatedCashierDto>.Success(new(existing.Id, existing.DisplayName, "Cashier"));
                }
                return Reject("operation.outcome_unknown", "Resolve the previous account creation before submitting again.");
            }

            var roles = await identity.GetRolesAsync(ct);
            var role = roles.SingleOrDefault(row => row.IsActive && row.IsSystem &&
                string.Equals(row.Name, "Cashier", StringComparison.OrdinalIgnoreCase));
            if (role is null)
            {
                return Reject("identity.cashier_role_unavailable", "The canonical Cashier role is unavailable.");
            }
            var normalized = name.ToUpperInvariant();
            await resourceLock.AcquireAsync("IDENTITY_USER_DISPLAY_NAME", normalized, ct);
            if (await administration.DisplayNameExistsAsync(normalized, ct))
            {
                return Reject("identity.display_name_duplicate", "A user with this name already exists.");
            }
            var credential = credentials.Hash(command.Pin);
            var user = new User
            {
                DisplayName = name,
                RoleId = role.Id,
                PinHash = credential.Hash,
                PinSalt = credential.Salt,
                PinIterations = credential.Iterations,
                PinAlgorithm = credential.Algorithm,
                Status = UserStatus.Active,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow,
                Version = 1
            };
            administration.AddUser(user);
            audit.Record("USER_CREATED", "USER", user.Id, command.ActorId, command.CorrelationId,
                "Active Cashier account created.");
            // The ledger shares the transaction with the user and audit; no credential enters its metadata.
            await outcomes.RecordSuccessAsync(command.ClientOperationId, OperationType, user.Id,
                actorId: command.ActorId, terminalId: command.TerminalId, sessionId: command.SessionId,
                payloadFingerprint: fingerprint, cancellationToken: ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result<CreatedCashierDto>.Success(new(user.Id, user.DisplayName, role.Name));
        }, cancellationToken);
    }

    private static Result<CreatedCashierDto> Reject(string code, string message)
        => Result<CreatedCashierDto>.Failure(code, message);
}
