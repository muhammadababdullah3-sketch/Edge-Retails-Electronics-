namespace EdgeRetails.Central.Domain.Signing;

public static class SigningPurpose
{
    public const string License = "LICENSE";
    public const string Recovery = "RECOVERY";
    public const string Update = "UPDATE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        License,
        Recovery,
        Update
    };
}
