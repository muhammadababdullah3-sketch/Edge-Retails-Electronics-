using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

public sealed record SupervisorPostingApprovalRequest(
    Guid SupervisorId,
    string SupervisorPin,
    DateOnly PostingDate,
    string OperationType,
    string Reason);

public sealed record SupervisorPostingApprovalResponse(
    bool Authorized,
    Guid SupervisorId,
    string SupervisorDisplayName,
    DateTimeOffset AuthorizedAtUtc,
    string ApprovalToken);

[ApiController]
[Route("api/posting")]
public sealed class PostingController : ControllerBase
{
    private readonly IShopClock _clock;
    private readonly IPeriodLockRepository _periodLocks;
    private readonly IIdentityReadRepository _identity;
    private readonly IPinCredentialService _pinCredentials;

    public PostingController(
        IShopClock clock,
        IPeriodLockRepository periodLocks,
        IIdentityReadRepository identity,
        IPinCredentialService pinCredentials)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _periodLocks = periodLocks ?? throw new ArgumentNullException(nameof(periodLocks));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _pinCredentials = pinCredentials ?? throw new ArgumentNullException(nameof(pinCredentials));
    }

    [HttpGet("period-lock-check")]
    public async Task<IActionResult> CheckPeriodLock(
        [FromQuery] DateOnly postingDate,
        CancellationToken cancellationToken = default)
    {
        var lockedPeriod = await _periodLocks.GetLockedPeriodForDateAsync(postingDate, cancellationToken);
        var shopDate = _clock.ShopDate;
        var earliestAllowed = shopDate.AddDays(-ShopPostingPolicy.MaxBackdateCalendarDays);
        var exceedsWindow = postingDate < earliestAllowed;
        var isFuture = postingDate > shopDate;

        return Ok(new
        {
            postingDate,
            shopDate,
            isLocked = lockedPeriod is not null,
            lockedPeriodName = lockedPeriod?.PeriodName,
            isFuture,
            exceedsWindow,
            earliestAllowed
        });
    }

    [HttpPost("supervisor-approval")]
    public async Task<IActionResult> RequestSupervisorApproval(
        [FromBody] SupervisorPostingApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SupervisorId == Guid.Empty)
        {
            return BadRequest(new
            {
                code = "authorization.supervisor_id_required",
                message = "Supervisor identifier is required for backdated posting approval."
            });
        }

        if (string.IsNullOrWhiteSpace(request.SupervisorPin) || request.SupervisorPin.Length != 4 || request.SupervisorPin.Any(ch => !char.IsAsciiDigit(ch)))
        {
            return BadRequest(new
            {
                code = "authorization.invalid_supervisor_pin",
                message = "Supervisor approval requires an exact 4-digit cryptographic PIN."
            });
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new
            {
                code = "authorization.reason_required",
                message = "A clear business justification reason is required for backdated posting approval."
            });
        }

        var shopDate = _clock.ShopDate;

        // Rule 090: Future date check
        if (request.PostingDate > shopDate)
        {
            return UnprocessableEntity(new
            {
                code = "posting.future_date_forbidden",
                message = $"Posting date {request.PostingDate:yyyy-MM-dd} cannot be in the future. Authoritative shop date is {shopDate:yyyy-MM-dd}."
            });
        }

        // Rule 090: 7-calendar-day backdate limit
        var earliestAllowed = shopDate.AddDays(-ShopPostingPolicy.MaxBackdateCalendarDays);
        if (request.PostingDate < earliestAllowed)
        {
            return UnprocessableEntity(new
            {
                code = "posting.backdate_exceeds_window",
                message = $"Posting date {request.PostingDate:yyyy-MM-dd} is older than the allowed {ShopPostingPolicy.MaxBackdateCalendarDays}-calendar-day window (earliest allowed: {earliestAllowed:yyyy-MM-dd})."
            });
        }

        // Rule 092: Period-lock precedence over supervisor override
        var lockedPeriod = await _periodLocks.GetLockedPeriodForDateAsync(request.PostingDate, cancellationToken);
        if (lockedPeriod is not null)
        {
            return Conflict(new
            {
                code = "posting.accounting_period_locked",
                message = $"Posting date {request.PostingDate:yyyy-MM-dd} falls within locked accounting period '{lockedPeriod.PeriodName}'. Accounting period locks cannot be overridden by supervisor authorization."
            });
        }

        // Look up supervisor
        var supervisorUser = await _identity.GetUserAsync(request.SupervisorId, cancellationToken);
        if (supervisorUser is null || supervisorUser.Status != UserStatus.Active)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "authorization.supervisor_not_found_or_inactive",
                message = "Supervisor account could not be found or is inactive."
            });
        }

        // Verify cryptographic supervisor PIN
        var credential = new PinCredential(
            supervisorUser.PinHash,
            supervisorUser.PinSalt,
            supervisorUser.PinIterations,
            supervisorUser.PinAlgorithm);

        if (!_pinCredentials.Verify(request.SupervisorPin, credential))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "authorization.invalid_supervisor_pin",
                message = "Supervisor PIN verification failed. Incorrect credentials."
            });
        }

        // Verify supervisor permissions
        var permissions = await _identity.GetEffectivePermissionKeysAsync(supervisorUser.Id, cancellationToken);
        var role = await _identity.GetRoleAsync(supervisorUser.RoleId, cancellationToken);

        var isOwner = role is not null && string.Equals(role.Name, "Owner", StringComparison.OrdinalIgnoreCase);
        var hasOverridePermission = permissions.Contains(PermissionKeys.SupervisorPostingOverride) ||
                                    permissions.Contains(PermissionKeys.SettingsManage);

        // Anti-spoofing check: if authenticated caller supplied their own ID as supervisor without being an actual supervisor
        var caller = HttpContext?.GetActorContext();
        if (caller is not null && caller.UserId == supervisorUser.Id && !isOwner && !hasOverridePermission)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "authorization.spoofed_supervisor_rejected",
                message = "Cashier cannot self-authorize backdated posting without supervisor role."
            });
        }

        if (!isOwner && !hasOverridePermission)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "authorization.supervisor_permission_denied",
                message = "Supervisor user lacks the required posting override permission."
            });
        }

        var approvalToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{supervisorUser.Id}:{request.PostingDate:yyyyMMdd}:{_clock.UtcNow:O}")));

        return Ok(new SupervisorPostingApprovalResponse(
            Authorized: true,
            SupervisorId: supervisorUser.Id,
            SupervisorDisplayName: supervisorUser.DisplayName,
            AuthorizedAtUtc: _clock.UtcNow,
            ApprovalToken: approvalToken));
    }
}
