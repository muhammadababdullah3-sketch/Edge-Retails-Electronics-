namespace EdgeRetails.Application.Features.Identity;

/// <summary>Phase 8 single-machine operator session bounds.</summary>
public static class ShopSessionPolicy
{
    /// <summary>Full sign-out when the operator is away (server-side).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(8);

    /// <summary>Screen lock prompt; session remains valid until idle timeout.</summary>
    public static readonly TimeSpan ScreenIdleTimeout = TimeSpan.FromMinutes(15);
}
