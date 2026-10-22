namespace EdgeRetails.Central.Domain.Identity;

public static class AdminCapabilities
{
    public const string AuthLogin = "auth.login";
    public const string AuthMfa = "auth.mfa";
    public const string AuthStepUp = "auth.stepup";
    public const string AdminManage = "admin.manage";
    public const string AdminUsersRead = "admin.users.read";
    public const string AdminRolesManage = "admin.roles.manage";
    public const string LicensingRead = "licensing.read";
    public const string LicensingManage = "licensing.manage";
    public const string AuditRead = "audit.read";
    public const string SignerSign = "signer.sign";
    public const string TrustSetRead = "trustset.read";
    public const string OperationsLookup = "operation.lookup";
    public const string SecurityManage = "security.manage";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        AuthLogin,
        AuthMfa,
        AuthStepUp,
        AdminManage,
        AdminUsersRead,
        AdminRolesManage,
        LicensingRead,
        LicensingManage,
        AuditRead,
        SignerSign,
        TrustSetRead,
        OperationsLookup,
        SecurityManage
    };
}
