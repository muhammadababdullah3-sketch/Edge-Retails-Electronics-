namespace EdgeRetails.Central.Application.Services;

/// <summary>
/// Operational approval-request lifetime. A missing or invalid value keeps the
/// approval workflow gated; development tests may supply a labelled fixture.
/// </summary>
public sealed class SensitiveApprovalPolicy
{
    public TimeSpan? RequestLifetime { get; init; }

    public static SensitiveApprovalPolicy FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable("CENTRAL_SENSITIVE_APPROVAL_TTL_SECONDS");
        return long.TryParse(configured, out var seconds) && seconds > 0 && seconds <= (long)TimeSpan.MaxValue.TotalSeconds
            ? new SensitiveApprovalPolicy { RequestLifetime = TimeSpan.FromSeconds(seconds) }
            : new SensitiveApprovalPolicy();
    }
}
