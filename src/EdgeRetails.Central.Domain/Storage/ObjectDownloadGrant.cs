using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Storage;

/// <summary>
/// A short-lived, opaque download capability bound to one installed principal and one immutable object.
/// The bearer token itself is never persisted; only its SHA-256 digest is stored.
/// </summary>
public sealed class ObjectDownloadGrant : Entity
{
    public Guid ShopId { get; private set; }
    public Guid InstallationId { get; private set; }
    public Guid ObjectId { get; private set; }
    public string TokenSha256 { get; private set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    private ObjectDownloadGrant() { }

    public static ObjectDownloadGrant Create(
        Guid id,
        Guid shopId,
        Guid installationId,
        Guid objectId,
        string tokenSha256,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty || shopId == Guid.Empty || installationId == Guid.Empty || objectId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_DOWNLOAD_GRANT", "storage.download_grant_invalid", "Download grant identities are required.");
        }
        if (tokenSha256.Length != 64 || !tokenSha256.All(Uri.IsHexDigit))
        {
            throw new CentralDomainException("INVALID_DOWNLOAD_GRANT", "storage.download_grant_invalid", "Download grant token digest is invalid.");
        }
        if (expiresAtUtc <= issuedAtUtc)
        {
            throw new CentralDomainException("INVALID_DOWNLOAD_GRANT", "storage.download_grant_invalid", "Download grant expiry must follow its issue time.");
        }

        return new ObjectDownloadGrant
        {
            Id = id,
            ShopId = shopId,
            InstallationId = installationId,
            ObjectId = objectId,
            TokenSha256 = tokenSha256.ToLowerInvariant(),
            IssuedAtUtc = issuedAtUtc,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public bool IsValidAt(DateTimeOffset nowUtc) => nowUtc >= IssuedAtUtc && nowUtc < ExpiresAtUtc;
}
