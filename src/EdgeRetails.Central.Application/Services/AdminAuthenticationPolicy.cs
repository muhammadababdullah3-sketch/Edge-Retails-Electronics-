using System.Globalization;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Application.Services;

/// <summary>
/// Operational lifetimes for the Central admin authentication boundary.
/// No lifetime is supplied by code; missing or invalid configuration denies auth.
/// </summary>
public sealed record AdminAuthenticationPolicy(
    TimeSpan? LoginChallengeLifetime,
    TimeSpan? SessionIdleTimeout,
    TimeSpan? SessionAbsoluteLifetime,
    TimeSpan? StepUpGrantLifetime)
{
    public static AdminAuthenticationPolicy FromEnvironment() => FromValues(
        Environment.GetEnvironmentVariable("CENTRAL_ADMIN_AUTH_CHALLENGE_TTL_SECONDS"),
        Environment.GetEnvironmentVariable("CENTRAL_ADMIN_AUTH_SESSION_IDLE_TIMEOUT_SECONDS"),
        Environment.GetEnvironmentVariable("CENTRAL_ADMIN_AUTH_SESSION_ABSOLUTE_LIFETIME_SECONDS"),
        Environment.GetEnvironmentVariable("CENTRAL_ADMIN_STEP_UP_TTL_SECONDS"));

    public static AdminAuthenticationPolicy FromValues(
        string? loginChallengeSeconds,
        string? sessionIdleSeconds,
        string? sessionAbsoluteSeconds,
        string? stepUpSeconds) => new(
            ParsePositiveSeconds(loginChallengeSeconds),
            ParsePositiveSeconds(sessionIdleSeconds),
            ParsePositiveSeconds(sessionAbsoluteSeconds),
            ParsePositiveSeconds(stepUpSeconds));

    public bool IsConfigured => IsPositive(LoginChallengeLifetime) &&
                                IsPositive(SessionIdleTimeout) &&
                                IsPositive(SessionAbsoluteLifetime) &&
                                IsPositive(StepUpGrantLifetime);

    public void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new CentralDomainException(
                "AUTH_POLICY_NOT_CONFIGURED",
                "auth.policy_unavailable",
                "Admin authentication policy is not configured.");
        }
    }

    private static TimeSpan? ParsePositiveSeconds(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static bool IsPositive(TimeSpan? value) => value.HasValue && value.Value > TimeSpan.Zero;
}
