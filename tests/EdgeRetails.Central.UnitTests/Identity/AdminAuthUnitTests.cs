using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.UnitTests.Identity;

public class AdminAuthUnitTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly PasswordHasher _passwordHasher = new();
    private readonly TotpService _totpService = new();
    private readonly AdminAuthService _authService;
    private readonly StepUpService _stepUpService;

    public AdminAuthUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new CentralDbContext(options);
        var policy = AdminAuthenticationPolicy.FromValues("300", "900", "28800", "300");
        _authService = new AdminAuthService(_dbContext, _passwordHasher, _totpService, policy);
        _stepUpService = new StepUpService(_dbContext, _totpService, policy);
    }

    [Fact]
    public async Task StartLogin_ValidCredentials_ReturnsPreAuthChallengeWithNoBusinessAuthority()
    {
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create(
            "admin_test",
            _passwordHasher.HashPassword("AdminPass#123"),
            "salt",
            _totpService.GenerateSecret(),
            true,
            now);
        _dbContext.AdminUsers.Add(user);
        await _dbContext.SaveChangesAsync();

        var request = new LoginRequestDto
        {
            Username = "admin_test",
            PasswordOrProviderAssertion = "AdminPass#123"
        };

        var beforeChallenge = DateTimeOffset.UtcNow;
        var challenge = await _authService.StartLoginAsync(request);
        var afterChallenge = DateTimeOffset.UtcNow;

        Assert.NotNull(challenge);
        Assert.Equal("MFA_CHALLENGE", challenge.Kind);
        Assert.False(challenge.BusinessAuthority);
        Assert.NotEqual(Guid.Empty, challenge.ChallengeId);
        Assert.InRange(challenge.ExpiresAtUtc, beforeChallenge.AddMinutes(5), afterChallenge.AddMinutes(5));
    }

    [Fact]
    public async Task StartLogin_MissingOrInvalidLifetimeConfiguration_FailsClosedBeforePersistence()
    {
        var policy = AdminAuthenticationPolicy.FromValues("300", "0", "28800", "invalid");
        Assert.False(policy.IsConfigured);

        var service = new AdminAuthService(_dbContext, _passwordHasher, _totpService, policy);
        var exception = await Assert.ThrowsAsync<CentralDomainException>(() =>
            service.StartLoginAsync(new LoginRequestDto
            {
                Username = "no-admin-needed-before-policy-check",
                PasswordOrProviderAssertion = "synthetic"
            }));

        Assert.Equal("AUTH_POLICY_NOT_CONFIGURED", exception.Code);
        Assert.Empty(await _dbContext.LoginChallenges.ToListAsync());
    }

    [Fact]
    public async Task AuthenticationPolicy_UsesOnlyExplicitPositiveIntegerSeconds()
    {
        var valid = AdminAuthenticationPolicy.FromValues("300", "900", "28800", "300");
        Assert.True(valid.IsConfigured);
        Assert.Equal(TimeSpan.FromMinutes(5), valid.LoginChallengeLifetime);
        Assert.Equal(TimeSpan.FromMinutes(15), valid.SessionIdleTimeout);
        Assert.Equal(TimeSpan.FromHours(8), valid.SessionAbsoluteLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), valid.StepUpGrantLifetime);

        Assert.False(AdminAuthenticationPolicy.FromValues("", "900", "28800", "300").IsConfigured);
        Assert.False(AdminAuthenticationPolicy.FromValues("300", "-1", "28800", "300").IsConfigured);
        Assert.False(AdminAuthenticationPolicy.FromValues("300", "900", "28800", "0").IsConfigured);
        Assert.False(AdminAuthenticationPolicy.FromValues("300.0", "900", "28800", "300").IsConfigured);
    }

    [Fact]
    public async Task StartLogin_WrongPassword_ThrowsInvalidCredentials()
    {
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create(
            "admin_test",
            _passwordHasher.HashPassword("AdminPass#123"),
            "salt",
            _totpService.GenerateSecret(),
            true,
            now);
        _dbContext.AdminUsers.Add(user);
        await _dbContext.SaveChangesAsync();

        var request = new LoginRequestDto
        {
            Username = "admin_test",
            PasswordOrProviderAssertion = "WrongPass"
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _authService.StartLoginAsync(request));

        Assert.Equal("INVALID_CREDENTIALS", ex.Code);
    }

    [Fact]
    public async Task CompleteMfa_ValidTotp_CreatesAuthenticatedSession()
    {
        var now = DateTimeOffset.UtcNow;
        var totpSecret = _totpService.GenerateSecret();
        var user = AdminUser.Create(
            "admin_mfa",
            _passwordHasher.HashPassword("Pass#123"),
            "salt",
            totpSecret,
            true,
            now);

        var role = AdminRole.Create("AdminManager", "Admin role", new[] { AdminCapabilities.AdminManage });
        _dbContext.AdminRoles.Add(role);
        _dbContext.AdminUsers.Add(user);
        _dbContext.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, now));

        var challenge = LoginChallenge.Create(user.Id, now, TimeSpan.FromMinutes(5));
        _dbContext.LoginChallenges.Add(challenge);
        await _dbContext.SaveChangesAsync();

        var validCode = _totpService.GenerateCode(totpSecret, now);
        var beforeSession = DateTimeOffset.UtcNow;
        var mfaRequest = new MfaRequestDto
        {
            LoginChallengeId = challenge.Id,
            Proof = validCode
        };

        var session = await _authService.CompleteMfaAsync(mfaRequest);
        var afterSession = DateTimeOffset.UtcNow;

        Assert.NotNull(session);
        Assert.Equal("AUTHENTICATED", session.Kind);
        Assert.True(session.MfaSatisfied);
        Assert.Contains(AdminCapabilities.AdminManage, session.Capabilities);
        Assert.InRange(session.ExpiresAtUtc, beforeSession.AddMinutes(15), afterSession.AddMinutes(15));
        Assert.InRange(session.AbsoluteExpiresAtUtc, beforeSession.AddHours(8), afterSession.AddHours(8));
    }

    [Fact]
    public async Task CompleteMfa_WrongProof_RecordsFailureAndDoesNotCreateSession()
    {
        var now = DateTimeOffset.UtcNow;
        var totpSecret = _totpService.GenerateSecret();
        var user = AdminUser.Create(
            "admin_wrong_mfa",
            _passwordHasher.HashPassword("Pass#123"),
            "salt",
            totpSecret,
            true,
            now);
        var challenge = LoginChallenge.Create(user.Id, now, TimeSpan.FromMinutes(5));
        _dbContext.AdminUsers.Add(user);
        _dbContext.LoginChallenges.Add(challenge);
        await _dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _authService.CompleteMfaAsync(new MfaRequestDto
            {
                LoginChallengeId = challenge.Id,
                Proof = "ABCDEF"
            }));

        Assert.Equal("INVALID_MFA_PROOF", exception.Code);
        Assert.Equal(1, (await _dbContext.LoginChallenges.SingleAsync()).FailedAttempts);
        Assert.Empty(await _dbContext.AdminSessions.ToListAsync());
    }

    [Fact]
    public async Task CompleteMfa_ExpiredChallenge_IsRejectedWithoutCreatingSession()
    {
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create(
            "admin_expired_mfa",
            _passwordHasher.HashPassword("Pass#123"),
            "salt",
            _totpService.GenerateSecret(),
            true,
            now);
        var challenge = LoginChallenge.Create(user.Id, now.AddMinutes(-6), TimeSpan.FromMinutes(5));
        _dbContext.AdminUsers.Add(user);
        _dbContext.LoginChallenges.Add(challenge);
        await _dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _authService.CompleteMfaAsync(new MfaRequestDto
            {
                LoginChallengeId = challenge.Id,
                Proof = "000000"
            }));

        Assert.Equal("INVALID_CHALLENGE", exception.Code);
        Assert.Empty(await _dbContext.AdminSessions.ToListAsync());
    }

    [Fact]
    public async Task CompleteMfa_ConsumedChallengeReplay_IsRejectedWithoutCreatingSession()
    {
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create(
            "admin_replayed_mfa",
            _passwordHasher.HashPassword("Pass#123"),
            "salt",
            _totpService.GenerateSecret(),
            true,
            now);
        var challenge = LoginChallenge.Create(user.Id, now, TimeSpan.FromMinutes(5));
        challenge.Consume(now);
        _dbContext.AdminUsers.Add(user);
        _dbContext.LoginChallenges.Add(challenge);
        await _dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _authService.CompleteMfaAsync(new MfaRequestDto
            {
                LoginChallengeId = challenge.Id,
                Proof = "000000"
            }));

        Assert.Equal("INVALID_CHALLENGE", exception.Code);
        Assert.Empty(await _dbContext.AdminSessions.ToListAsync());
    }

    [Fact]
    public async Task ValidateSession_StaleSecurityRevision_InvalidatesSessionImmediately()
    {
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create("rev_user", "hash", "salt", "secret", true, now);
        _dbContext.AdminUsers.Add(user);
        await _dbContext.SaveChangesAsync();

        var session = AdminSession.Create(
            user.Id,
            "token_hash",
            now,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(8),
            true,
            user.SecurityRevision,
            new[] { AdminCapabilities.AdminManage });

        _dbContext.AdminSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        // Valid initially
        var validated1 = await _authService.ValidateSessionAsync(session.Id);
        Assert.NotNull(validated1);

        // Privilege change or password reset bumps security revision
        user.BumpSecurityRevision();
        await _dbContext.SaveChangesAsync();

        // Session must now be immediately rejected
        var validated2 = await _authService.ValidateSessionAsync(session.Id);
        Assert.Null(validated2);
    }

    [Fact]
    public async Task StepUp_CreatesBoundGrant_AndValidatesCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        var totpSecret = _totpService.GenerateSecret();
        var user = AdminUser.Create("stepup_user", "hash", "salt", totpSecret, true, now);
        _dbContext.AdminUsers.Add(user);

        var session = AdminSession.Create(
            user.Id,
            "token_hash_stepup",
            now,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(8),
            true,
            user.SecurityRevision,
            new[] { AdminCapabilities.AdminManage });

        _dbContext.AdminSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        var proof = _totpService.GenerateCode(totpSecret, now);
        const string actionScope = "admin.user.disable";
        const string payloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var beforeGrant = DateTimeOffset.UtcNow;
        var grantDto = await _stepUpService.VerifyAndGrantStepUpAsync(new StepUpRequestDto
        {
            SessionId = session.Id,
            ActionScope = actionScope,
            TargetPayloadSha256 = payloadHash,
            Proof = proof,
            ChallengeId = Guid.NewGuid()
        });
        var afterGrant = DateTimeOffset.UtcNow;

        Assert.NotNull(grantDto);
        Assert.Equal(session.Id, grantDto.SessionId);
        Assert.InRange(grantDto.ExpiresAtUtc, beforeGrant.AddMinutes(5), afterGrant.AddMinutes(5));

        // Validation succeeds for matching scope and hash
        var isValid = await _stepUpService.ValidateStepUpGrantAsync(session.Id, actionScope, payloadHash);
        Assert.True(isValid);

        // Validation fails for different payload hash
        var isInvalidHash = await _stepUpService.ValidateStepUpGrantAsync(session.Id, actionScope, "different_hash");
        Assert.False(isInvalidHash);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }
}
