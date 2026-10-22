namespace EdgeRetails.Central.Domain.Signing;

public static class KeyStatus
{
    public const string Pending = "PENDING";
    public const string Active = "ACTIVE";
    public const string VerifyOnly = "VERIFY_ONLY";
    public const string Retired = "RETIRED";
    public const string Revoked = "REVOKED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Active,
        VerifyOnly,
        Retired,
        Revoked
    };
}
