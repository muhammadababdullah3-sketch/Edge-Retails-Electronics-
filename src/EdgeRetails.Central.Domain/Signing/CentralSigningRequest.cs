using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Signing;

public class CentralSigningRequest : Entity
{
    public Guid IssuanceRequestId { get; private set; }
    public string Purpose { get; private set; } = string.Empty;
    public string KeyId { get; private set; } = string.Empty;
    public int KeyVersion { get; private set; }
    public int AuthorityRevision { get; private set; }
    public string PayloadSha256 { get; private set; } = string.Empty;
    public string CanonicalPayload { get; private set; } = string.Empty;
    public string Signature { get; private set; } = string.Empty;
    public string Status { get; private set; } = "PENDING";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? SignedAtUtc { get; private set; }

    private CentralSigningRequest() { }

    public static CentralSigningRequest Create(
        Guid signingRequestId,
        Guid issuanceRequestId,
        string purpose,
        string keyId,
        int keyVersion,
        int authorityRevision,
        string payloadSha256,
        string canonicalPayload,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPayload);

        return new CentralSigningRequest
        {
            Id = signingRequestId,
            IssuanceRequestId = issuanceRequestId,
            Purpose = purpose,
            KeyId = keyId,
            KeyVersion = keyVersion,
            AuthorityRevision = authorityRevision,
            PayloadSha256 = payloadSha256,
            CanonicalPayload = canonicalPayload,
            Status = "PENDING",
            CreatedAtUtc = createdAtUtc
        };
    }

    public void CompleteSigning(string signature, DateTimeOffset signedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        Signature = signature;
        Status = "SIGNED";
        SignedAtUtc = signedAtUtc;
    }

    public void Reject(string reason)
    {
        Status = "REJECTED";
    }
}
