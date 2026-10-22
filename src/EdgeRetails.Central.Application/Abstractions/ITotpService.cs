namespace EdgeRetails.Central.Application.Abstractions;

public interface ITotpService
{
    string GenerateSecret();
    string GenerateCode(string secret, DateTimeOffset timestamp);
    bool VerifyCode(string secret, string code, DateTimeOffset timestamp, int toleranceSteps = 1);
}
