using System.Security.Cryptography;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

public class AdminAuthService : IAdminAuthService
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);

    private readonly ICentralDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITotpService _totpService;

    public AdminAuthService(
        ICentralDbContext dbContext,
        IPasswordHasher passwordHasher,
        ITotpService totpService)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _totpService = totpService ?? throw new ArgumentNullException(nameof(totpService));
    }

    public async Task<PreAuthChallengeDto> StartLoginAsync(LoginRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;

        var user = await _dbContext.AdminUsers
            .FirstOrDefaultAsync(u => u.Username == request.Username.Trim().ToLowerInvariant(), cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new CentralDomainException(
                "INVALID_CREDENTIALS",
                "auth.invalid_credentials",
                "Invalid username or password.");
        }

        var isPasswordValid = _passwordHasher.VerifyPassword(request.PasswordOrProviderAssertion, user.PasswordHash);
        if (!isPasswordValid)
        {
            throw new CentralDomainException(
                "INVALID_CREDENTIALS",
                "auth.invalid_credentials",
                "Invalid username or password.");
        }

        var challenge = LoginChallenge.Create(user.Id, now, ChallengeLifetime);
        _dbContext.LoginChallenges.Add(challenge);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new PreAuthChallengeDto
        {
            ChallengeId = challenge.Id,
            ExpiresAtUtc = challenge.ExpiresAtUtc
        };
    }

    public async Task<AdminSessionDto> CompleteMfaAsync(MfaRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, "central-admin-authority", cancellationToken);
        var challenge = await _dbContext.LoginChallenges
            .FirstOrDefaultAsync(c => c.Id == request.LoginChallengeId, cancellationToken);

        if (challenge is not null && _dbContext.Database.IsRelational())
        {
            await ((DbContext)_dbContext).Entry(challenge!).ReloadAsync(cancellationToken);
        }
        if (challenge is null || !challenge.IsValid(now))
        {
            throw new CentralDomainException(
                "INVALID_CHALLENGE",
                "auth.invalid_challenge",
                "Login challenge is invalid or has expired.");
        }

        var user = await _dbContext.AdminUsers
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == challenge.AdminUserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new CentralDomainException(
                "USER_INACTIVE",
                "auth.user_inactive",
                "User account is inactive or not found.");
        }

        if (!user.MfaEnabled || string.IsNullOrWhiteSpace(user.MfaSecret))
        {
            throw new CentralDomainException(
                "MFA_NOT_CONFIGURED",
                "auth.mfa_not_configured",
                "User does not have MFA configured.");
        }

        var isMfaValid = _totpService.VerifyCode(user.MfaSecret, request.Proof, now);
        if (!isMfaValid)
        {
            challenge.RecordFailure();
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            throw new CentralDomainException(
                "INVALID_MFA_PROOF",
                "auth.invalid_mfa_proof",
                "Invalid MFA code provided.");
        }

        challenge.Consume(now);
        user.RecordLogin(now);

        // Aggregate capabilities from assigned roles
        var capabilities = user.UserRoles
            .Select(ur => ur.Role)
            .SelectMany(r => r.GetCapabilities())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Generate session token hash
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(tokenBytes));

        var session = AdminSession.Create(
            adminUserId: user.Id,
            tokenHash: tokenHash,
            createdAtUtc: now,
            idleTimeout: IdleTimeout,
            absoluteLifetime: AbsoluteLifetime,
            mfaSatisfied: true,
            securityRevision: user.SecurityRevision,
            capabilities: capabilities);

        _dbContext.AdminSessions.Add(session);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return new AdminSessionDto
        {
            SessionId = session.Id,
            AdminUserId = session.AdminUserId,
            ExpiresAtUtc = session.ExpiresAtUtc,
            AbsoluteExpiresAtUtc = session.AbsoluteExpiresAtUtc,
            AdminSecurityRevision = session.SecurityRevisionAtCreation,
            Capabilities = capabilities
        };
    }

    public async Task<AdminSessionDto?> ValidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await SecurityTransaction.BeginAsync(_dbContext, "central-admin-authority", cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var session = await _dbContext.AdminSessions
            .Include(s => s.AdminUser)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        if (_dbContext.Database.IsRelational())
        {
            await ((DbContext)_dbContext).Entry(session).ReloadAsync(cancellationToken);
            await ((DbContext)_dbContext).Entry(session.AdminUser).ReloadAsync(cancellationToken);
        }
        if (!session.AdminUser.IsActive || !session.MfaSatisfied || !session.IsValid(now, session.AdminUser.SecurityRevision))
        {
            return null;
        }

        session.Touch(now, IdleTimeout);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new AdminSessionDto
        {
            SessionId = session.Id,
            AdminUserId = session.AdminUserId,
            ExpiresAtUtc = session.ExpiresAtUtc,
            AbsoluteExpiresAtUtc = session.AbsoluteExpiresAtUtc,
            AdminSecurityRevision = session.SecurityRevisionAtCreation,
            Capabilities = session.GetCapabilities().ToList()
        };
    }

    public async Task<bool> LogoutAsync(Guid sessionId, string reason, CancellationToken cancellationToken = default)
    {
        await using var transaction = await SecurityTransaction.BeginAsync(_dbContext, "central-admin-authority", cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var session = await _dbContext.AdminSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return false;
        }

        session.Revoke(reason, now);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return true;
    }
}
