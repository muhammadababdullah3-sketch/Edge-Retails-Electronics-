namespace EdgeRetails.Application.Production.Licensing;

public sealed record LicensePayload(
    string LicenseId,
    string CustomerName,
    string StoreName,
    string Plan,
    DateTimeOffset IssueDate,
    DateTimeOffset? ExpiryDate,
    string DeviceId,
    int? MaxTerminals,
    IReadOnlyList<string> EnabledModules);

public sealed record SignedLicenseEnvelope(string Algorithm, string PayloadBase64, string SignatureBase64);

public enum LicenseValidationStatus
{
    Valid,
    Missing,
    Corrupt,
    UnsupportedAlgorithm,
    InvalidSignature,
    DeviceMismatch,
    Expired,
    ValidationUnavailable
}

public sealed record LicenseValidationResult(
    LicenseValidationStatus Status,
    LicensePayload? Payload,
    string? Message,
    string? ErrorCode = null)
{
    public bool IsValid => Status == LicenseValidationStatus.Valid;
}

public interface ILicenseValidator
{
    Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default);
}

public interface ILicenseStore
{
    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
    Task<string?> ReadRawAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates the initial license only when no artifact exists. Returns false when another
    /// process already created the artifact. It must never overwrite an existing license.
    /// </summary>
    Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default);

    /// <summary>Authorized replacement path. May replace an existing license artifact atomically.</summary>
    Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default);
}

public interface IDeviceIdentityProvider
{
    Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default);
}

public interface ILicensePublicKeyProvider
{
    string GetPublicKeyPem();
}

public interface ILicenseSignatureVerifier
{
    string Algorithm { get; }
    bool Verify(byte[] payload, byte[] signature);
}

public sealed class LicenseImportRequiresAuthorizationException : InvalidOperationException
{
    public LicenseImportRequiresAuthorizationException()
        : base("An installed license already exists. Post-activation license changes must use the authorized replacement flow.") { }
}
