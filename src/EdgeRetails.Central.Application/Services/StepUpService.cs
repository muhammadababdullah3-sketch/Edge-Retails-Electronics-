using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

public class StepUpService : IStepUpService
{
    private readonly ICentralDbContext _dbContext;
    private readonly ITotpService _totpService;
    private readonly AdminAuthenticationPolicy _policy;

    public StepUpService(
        ICentralDbContext dbContext,
        ITotpService totpService,
        AdminAuthenticationPolicy policy)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _totpService = totpService ?? throw new ArgumentNullException(nameof(totpService));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<StepUpGrantDto> VerifyAndGrantStepUpAsync(StepUpRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _policy.EnsureConfigured();
        var now = DateTimeOffset.UtcNow;

        var session = await _dbContext.AdminSessions
            .Include(s => s.AdminUser)
            .FirstOrDefaultAsync(s => s.Id == request.SessionId, cancellationToken);

        if (session is null || !session.AdminUser.IsActive || !session.MfaSatisfied || !session.IsValid(now, session.AdminUser.SecurityRevision))
        {
            throw new CentralDomainException(
                "SESSION_INVALID",
                "auth.session_invalid",
                "Active valid session required for step-up authentication.");
        }

        if (!session.AdminUser.MfaEnabled || string.IsNullOrWhiteSpace(session.AdminUser.MfaSecret))
        {
            throw new CentralDomainException(
                "MFA_NOT_CONFIGURED",
                "auth.mfa_not_configured",
                "User does not have MFA configured.");
        }

        var isProofValid = _totpService.VerifyCode(session.AdminUser.MfaSecret, request.Proof, now);
        if (!isProofValid)
        {
            throw new CentralDomainException(
                "INVALID_STEPUP_PROOF",
                "auth.invalid_stepup_proof",
                "Invalid MFA proof for step-up authorization.");
        }

        var grant = StepUpGrant.Create(
            sessionId: session.Id,
            actionScope: request.ActionScope,
            targetPayloadSha256: request.TargetPayloadSha256,
            challengeId: request.ChallengeId,
            createdAtUtc: now,
            validity: _policy.StepUpGrantLifetime!.Value);

        _dbContext.StepUpGrants.Add(grant);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new StepUpGrantDto
        {
            SessionId = grant.SessionId,
            ActionScope = grant.ActionScope,
            TargetPayloadSha256 = grant.TargetPayloadSha256,
            ExpiresAtUtc = grant.ExpiresAtUtc,
            StepUpGrantId = grant.Id
        };
    }

    public async Task<bool> ValidateStepUpGrantAsync(
        Guid sessionId,
        string actionScope,
        string targetPayloadSha256,
        CancellationToken cancellationToken = default)
    {
        if (!_policy.IsConfigured)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        var grant = await _dbContext.StepUpGrants
            .FirstOrDefaultAsync(g =>
                g.SessionId == sessionId &&
                g.ActionScope == actionScope &&
                g.TargetPayloadSha256 == targetPayloadSha256,
                cancellationToken);

        if (grant is null)
        {
            return false;
        }

        return grant.IsValid(sessionId, actionScope, targetPayloadSha256, now);
    }

    public async Task<bool> ValidateAndConsumeStepUpGrantAsync(
        Guid sessionId,
        string actionScope,
        string targetPayloadSha256,
        Guid? grantId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_policy.IsConfigured)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        await using var transaction = await SecurityTransaction.BeginAsync(_dbContext, "central-admin-authority", cancellationToken);
        var session = await _dbContext.AdminSessions.Include(s => s.AdminUser).AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null || !session.AdminUser.IsActive || !session.MfaSatisfied || !session.IsValid(now, session.AdminUser.SecurityRevision))
        {
            return false;
        }

        var query = _dbContext.StepUpGrants.AsQueryable();
        if (grantId.HasValue)
        {
            query = query.Where(g => g.Id == grantId.Value);
        }

        var grant = await query
            .OrderByDescending(g => g.CreatedAtUtc)
            .FirstOrDefaultAsync(g =>
                g.SessionId == sessionId &&
                g.ActionScope == actionScope &&
                g.TargetPayloadSha256 == targetPayloadSha256,
                cancellationToken);

        if (grant is not null && _dbContext.Database.IsRelational())
        {
            await ((DbContext)_dbContext).Entry(grant!).ReloadAsync(cancellationToken);
        }

        if (grant is null || !grant.IsValid(sessionId, actionScope, targetPayloadSha256, now))
        {
            return false;
        }

        grant.Consume(now);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return true;
    }
}
