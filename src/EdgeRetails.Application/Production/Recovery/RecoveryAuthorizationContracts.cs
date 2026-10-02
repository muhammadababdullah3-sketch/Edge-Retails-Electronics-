namespace EdgeRetails.Application.Production.Recovery;

public sealed record SignedRecoveryAuthorization(
    string Algorithm,
    string PayloadBase64,
    string SignatureBase64);

public sealed record RecoveryAuthorizationPayload(
    string IssuerId,
    string LicenseId,
    string DeviceId,
    string Action,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    Guid Nonce,
    Guid TargetUserId);

public sealed record RecoveryAuthorizationTrust(
    string? PublicKeyPem,
    string? IssuerId);

public sealed record VerifiedRecoveryAuthorization(
    string IssuerId,
    Guid Nonce,
    Guid TargetUserId,
    DateTimeOffset ExpiresAt);

public sealed record RecoveryAuthorizationValidation(
    VerifiedRecoveryAuthorization? Authorization,
    string FailureCode)
{
    public bool IsValid => Authorization is not null;

    public static RecoveryAuthorizationValidation Valid(VerifiedRecoveryAuthorization value) =>
        new(value, string.Empty);

    public static RecoveryAuthorizationValidation Failed(string code) =>
        new(null, code);
}

public interface IRecoveryAuthorizationTrustProvider
{
    RecoveryAuthorizationTrust GetTrust();
}

public interface IRecoveryAuthorizationValidator
{
    Task<RecoveryAuthorizationValidation> ValidateAsync(
        string? signedAuthorization,
        Guid targetUserId,
        CancellationToken cancellationToken);
}
