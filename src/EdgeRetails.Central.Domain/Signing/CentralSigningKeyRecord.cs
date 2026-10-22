namespace EdgeRetails.Central.Domain.Signing;

public class CentralSigningKeyRecord
{
    public string KeyId { get; private set; } = string.Empty;
    public int KeyVersion { get; private set; }
    public string AlgorithmProfileId { get; private set; } = "RS256";
    public string Purpose { get; private set; } = SigningPurpose.License;
    public string PublicKeyPem { get; private set; } = string.Empty;
    public DateTimeOffset ValidFromUtc { get; private set; }
    public DateTimeOffset ValidUntilUtc { get; private set; }
    public string Status { get; private set; } = KeyStatus.Active;
    public int EmergencyRolloverRevision { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private CentralSigningKeyRecord() { }

    public static CentralSigningKeyRecord Create(
        string keyId,
        int keyVersion,
        string algorithmProfileId,
        string purpose,
        string publicKeyPem,
        DateTimeOffset validFromUtc,
        DateTimeOffset validUntilUtc,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);

        return new CentralSigningKeyRecord
        {
            KeyId = keyId,
            KeyVersion = keyVersion,
            AlgorithmProfileId = algorithmProfileId,
            Purpose = purpose,
            PublicKeyPem = publicKeyPem,
            ValidFromUtc = validFromUtc,
            ValidUntilUtc = validUntilUtc,
            Status = KeyStatus.Active,
            EmergencyRolloverRevision = 0,
            CreatedAtUtc = createdAtUtc
        };
    }

    public void RotateToVerifyOnly()
    {
        if (Status == KeyStatus.Active)
        {
            Status = KeyStatus.VerifyOnly;
        }
    }

    public void Retire()
    {
        Status = KeyStatus.Retired;
    }

    public void Revoke(int emergencyRevision)
    {
        Status = KeyStatus.Revoked;
        EmergencyRolloverRevision = emergencyRevision;
    }
}
