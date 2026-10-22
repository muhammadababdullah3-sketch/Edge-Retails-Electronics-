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
        _authService = new AdminAuthService(_dbContext, _passwordHasher, _totpService);
        _stepUpService = new StepUpService(_dbContext, _totpService);
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

        var challenge = await _authService.StartLoginAsync(request);

        Assert.NotNull(challenge);
        Assert.Equal("MFA_CHALLENGE", challenge.Kind);
        Assert.False(challenge.BusinessAuthority);
        Assert.NotEqual(Guid.Empty, challenge.ChallengeId);
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
        var mfaRequest = new MfaRequestDto
        {
            LoginChallengeId = challenge.Id,
            Proof = validCode
        };

        var session = await _authService.CompleteMfaAsync(mfaRequest);

        Assert.NotNull(session);
        Assert.Equal("AUTHENTICATED", session.Kind);
        Assert.True(session.MfaSatisfied);
        Assert.Contains(AdminCapabilities.AdminManage, session.Capabilities);
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

        var grantDto = await _stepUpService.VerifyAndGrantStepUpAsync(new StepUpRequestDto
        {
            SessionId = session.Id,
            ActionScope = actionScope,
            TargetPayloadSha256 = payloadHash,
            Proof = proof,
            ChallengeId = Guid.NewGuid()
        });

        Assert.NotNull(grantDto);
        Assert.Equal(session.Id, grantDto.SessionId);

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
