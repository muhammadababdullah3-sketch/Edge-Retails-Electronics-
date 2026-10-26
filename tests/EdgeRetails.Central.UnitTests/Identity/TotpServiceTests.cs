using EdgeRetails.Central.Application.Services;

namespace EdgeRetails.Central.UnitTests.Identity;

public class TotpServiceTests
{
    private readonly TotpService _totp = new();

    [Fact]
    public void GenerateSecret_ReturnsValidBase32String()
    {
        var secret = _totp.GenerateSecret();
        Assert.NotNull(secret);
        Assert.NotEmpty(secret);
        Assert.Matches("^[A-Z2-7]+$", secret);
    }

    [Fact]
    public void GenerateAndVerifyCode_MatchesCurrentTimestamp()
    {
        var secret = _totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;

        var code = _totp.GenerateCode(secret, now);
        Assert.Equal(6, code.Length);

        var isValid = _totp.VerifyCode(secret, code, now);
        Assert.True(isValid);
    }

    [Fact]
    public void VerifyCode_ToleratesOneTimeStepDrift()
    {
        var secret = _totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var pastTime = now.AddSeconds(-30);

        var pastCode = _totp.GenerateCode(secret, pastTime);
        var isValid = _totp.VerifyCode(secret, pastCode, now, toleranceSteps: 1);

        Assert.True(isValid);
    }

    [Fact]
    public void VerifyCode_RejectsStaleCodeBeyondTolerance()
    {
        var secret = _totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var ancientTime = now.AddMinutes(-5);

        var ancientCode = _totp.GenerateCode(secret, ancientTime);
        var isValid = _totp.VerifyCode(secret, ancientCode, now, toleranceSteps: 1);

        Assert.False(isValid);
    }
}
