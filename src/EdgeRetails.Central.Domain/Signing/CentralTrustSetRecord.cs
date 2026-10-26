namespace EdgeRetails.Central.Domain.Signing;

public class CentralTrustSetRecord
{
    public int TrustSetVersion { get; private set; }
    public int EmergencyRolloverRevision { get; private set; }
    public string SignedByTrustedRootKeyId { get; private set; } = string.Empty;
    public string Signature { get; private set; } = string.Empty;
    public string KeysJson { get; private set; } = "[]";
    public string RevokedKeyIdsJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private CentralTrustSetRecord() { }

    public static CentralTrustSetRecord Create(
        int trustSetVersion,
        int emergencyRolloverRevision,
        string signedByTrustedRootKeyId,
        string signature,
        string keysJson,
        string revokedKeyIdsJson,
        DateTimeOffset createdAtUtc)
    {
        return new CentralTrustSetRecord
        {
            TrustSetVersion = trustSetVersion,
            EmergencyRolloverRevision = emergencyRolloverRevision,
            SignedByTrustedRootKeyId = signedByTrustedRootKeyId,
            Signature = signature,
            KeysJson = keysJson,
            RevokedKeyIdsJson = revokedKeyIdsJson,
            CreatedAtUtc = createdAtUtc
        };
    }
}
