using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Posting;

namespace EdgeRetails.Application.Features.Posting;

public sealed class ShopPostingValidationService : IShopPostingValidationService
{
    private readonly IShopClock _clock;
    private readonly IPeriodLockRepository _periodLocks;
    private readonly IPostingAuditRepository _postingAudits;
    private readonly IIdentityReadRepository _identity;
    private readonly IPinCredentialService _pinCredentials;
    private readonly IBusinessAuditWriter _auditWriter;

    public ShopPostingValidationService(
        IShopClock clock,
        IPeriodLockRepository periodLocks,
        IPostingAuditRepository postingAudits,
        IIdentityReadRepository identity,
        IPinCredentialService pinCredentials,
        IBusinessAuditWriter auditWriter)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _periodLocks = periodLocks ?? throw new ArgumentNullException(nameof(periodLocks));
        _postingAudits = postingAudits ?? throw new ArgumentNullException(nameof(postingAudits));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _pinCredentials = pinCredentials ?? throw new ArgumentNullException(nameof(pinCredentials));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    }

    public async Task<Result<PostingAuditEntry>> ValidateAndAuditPostingAsync(
        PostingValidationCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.OperationType))
        {
            return Result<PostingAuditEntry>.Failure(
                "posting.operation_type_required",
                "Financial operation type is required.");
        }

        if (command.EntityId == Guid.Empty)
        {
            return Result<PostingAuditEntry>.Failure(
                "posting.entity_id_required",
                "Target entity identifier is required.");
        }

        if (command.ActorId == Guid.Empty)
        {
            return Result<PostingAuditEntry>.Failure(
                "posting.actor_id_required",
                "Authenticated actor identifier is required.");
        }

        // Rule 091: Caller-supplied boolean authority is strictly rejected
        if (command.CallerSuppliedBooleanOverride == true)
        {
            return Result<PostingAuditEntry>.Failure(
                "posting.caller_boolean_authority_rejected",
                "Caller-supplied boolean authorization is strictly forbidden. A verified cryptographic supervisor PIN or approval token is required.");
        }

        var shopDate = _clock.ShopDate;
        var eventTimestampUtc = _clock.UtcNow;

        // Rule 090: Future date validation
        if (command.PostingDate > shopDate)
        {
            return Result<PostingAuditEntry>.Failure(
                "posting.future_date_forbidden",
                $"Posting date {command.PostingDate:yyyy-MM-dd} cannot be in the future. Authoritative shop date is {shopDate:yyyy-MM-dd}.");
        }

        // Rule 090: 7-calendar-day backdate limit
        var earliestAllowed = shopDate.AddDays(-ShopPostingPolicy.MaxBackdateCalendarDays);
        if (command.PostingDate < earliestAllowed)
        {
            return Result<PostingAuditEntry>.Failure(
                "posting.backdate_exceeds_window",
                $"Posting date {command.PostingDate:yyyy-MM-dd} is older than the allowed {ShopPostingPolicy.MaxBackdateCalendarDays}-calendar-day window (earliest allowed: {earliestAllowed:yyyy-MM-dd}).");
        }

        // Rule 092: Period-lock precedence over supervisor override
        var lockedPeriod = await _periodLocks.GetLockedPeriodForDateAsync(command.PostingDate, cancellationToken);
        if (lockedPeriod is not null)
        {
            return Result<PostingAuditEntry>.Failure(
                "posting.accounting_period_locked",
                $"Posting date {command.PostingDate:yyyy-MM-dd} falls within locked accounting period '{lockedPeriod.PeriodName}'. Accounting period locks cannot be overridden by supervisor authorization.");
        }

        // Rule 090 & 091: Backdating requires supervisor authorization
        Guid? authorizedSupervisorId = null;
        string? approvalReason = null;

        if (command.PostingDate < shopDate)
        {
            if (command.SupervisorApproval is null)
            {
                return Result<PostingAuditEntry>.Failure(
                    "posting.supervisor_authorization_required",
                    $"Historical posting date {command.PostingDate:yyyy-MM-dd} requires supervisor authorization.");
            }

            try
            {
                command.SupervisorApproval.ValidateInvariants();
            }
            catch (BusinessRuleException ex)
            {
                return Result<PostingAuditEntry>.Failure(ex.Code, ex.Message);
            }

            var supervisorUser = await _identity.GetUserAsync(command.SupervisorApproval.SupervisorId, cancellationToken);
            if (supervisorUser is null || supervisorUser.Status != UserStatus.Active)
            {
                return Result<PostingAuditEntry>.Failure(
                    "authorization.supervisor_not_found_or_inactive",
                    "Supervisor account could not be found or is inactive.");
            }

            // Verify supervisor PIN
            var credential = new PinCredential(
                supervisorUser.PinHash,
                supervisorUser.PinSalt,
                supervisorUser.PinIterations,
                supervisorUser.PinAlgorithm);

            if (!_pinCredentials.Verify(command.SupervisorApproval.SupervisorPin, credential))
            {
                return Result<PostingAuditEntry>.Failure(
                    "authorization.invalid_supervisor_pin",
                    "Supervisor PIN verification failed. Incorrect credentials.");
            }

            // Verify supervisor permissions
            var permissions = await _identity.GetEffectivePermissionKeysAsync(supervisorUser.Id, cancellationToken);
            var role = await _identity.GetRoleAsync(supervisorUser.RoleId, cancellationToken);

            var isOwner = role is not null && string.Equals(role.Name, "Owner", StringComparison.OrdinalIgnoreCase);
            var hasOverridePermission = permissions.Contains(PermissionKeys.SupervisorPostingOverride) ||
                                        permissions.Contains(PermissionKeys.SettingsManage);

            if (!isOwner && !hasOverridePermission)
            {
                return Result<PostingAuditEntry>.Failure(
                    "authorization.supervisor_permission_denied",
                    "Supervisor user lacks the required posting override permission.");
            }

            // Anti-spoofing check: if cashier supplied their own ID as supervisor without being an actual supervisor
            if (command.ActorId == supervisorUser.Id && !isOwner && !hasOverridePermission)
            {
                return Result<PostingAuditEntry>.Failure(
                    "authorization.spoofed_supervisor_rejected",
                    "Cashier cannot self-authorize backdated posting without supervisor role.");
            }

            authorizedSupervisorId = supervisorUser.Id;
            approvalReason = command.SupervisorApproval.Reason;
        }

        // Create atomic posting audit entry
        var auditEntry = new PostingAuditEntry
        {
            Id = Guid.CreateVersion7(),
            OperationType = command.OperationType,
            EntityId = command.EntityId,
            EntityNumber = command.EntityNumber,
            ActorId = command.ActorId,
            SupervisorId = authorizedSupervisorId,
            ShopDate = shopDate,
            PostingDate = command.PostingDate,
            SupplierDocumentDate = command.SupplierDocumentDate,
            EventTimestampUtc = eventTimestampUtc,
            Reason = approvalReason,
            CreatedAt = eventTimestampUtc
        };

        auditEntry.ValidateInvariants();

        // Stage in audit repository and business audit writer (atomic unit of work)
        _postingAudits.Record(auditEntry);

        _auditWriter.Record(
            "FINANCIAL_POSTING",
            command.OperationType,
            command.EntityId,
            command.ActorId,
            Guid.CreateVersion7(),
            $"Posted {command.OperationType} {command.EntityNumber} for date {command.PostingDate:yyyy-MM-dd} (ShopDate: {shopDate:yyyy-MM-dd})" +
            (authorizedSupervisorId.HasValue ? $" [Supervisor: {authorizedSupervisorId.Value}, Reason: {approvalReason}]" : ""));

        return Result<PostingAuditEntry>.Success(auditEntry);
    }
}
