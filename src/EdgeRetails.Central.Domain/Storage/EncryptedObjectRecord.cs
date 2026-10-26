using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Storage;

public class EncryptedObjectRecord : Entity
{
    public const string PurposeEncryptedBackup = "ENCRYPTED_BACKUP";
    public const string PurposeSupportAttachment = "SUPPORT_ATTACHMENT";
    public const string PurposePaymentProof = "PAYMENT_PROOF";

    public Guid ShopId { get; private set; }
    public Guid InstallationId { get; private set; }
    public string Purpose { get; private set; } = PurposeEncryptedBackup;
    public long ContentLength { get; private set; }
    public string ContentType { get; private set; } = "application/octet-stream";
    public string CiphertextSha256 { get; private set; } = string.Empty;
    public string StoragePath { get; private set; } = string.Empty;
    public Guid RetentionPolicyId { get; private set; }
    public DateTimeOffset? RetentionUntilUtc { get; private set; }
    public bool IsUnderLegalHold { get; private set; }
    public string? LocalManifestAuthenticationOpaque { get; private set; }
    public bool IsTombstoned { get; private set; }
    public DateTimeOffset? TombstonedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private EncryptedObjectRecord() { }

    public static EncryptedObjectRecord Create(
        Guid objectId,
        Guid shopId,
        Guid installationId,
        string purpose,
        long contentLength,
        string contentType,
        string ciphertextSha256,
        string storagePath,
        Guid retentionPolicyId,
        string? localManifestAuthenticationOpaque,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? retentionUntilUtc = null,
        bool isUnderLegalHold = false)
    {
        if (objectId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_OBJECT_ID", "object.invalid_id", "Object ID is required.");
        }
        if (shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SHOP_ID", "object.invalid_shop_id", "Shop ID is required.");
        }
        if (installationId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_INSTALLATION_ID", "object.invalid_installation_id", "Installation ID is required.");
        }
        if (purpose != PurposeEncryptedBackup && purpose != PurposeSupportAttachment && purpose != PurposePaymentProof)
        {
            throw new CentralDomainException("INVALID_PURPOSE", "object.invalid_purpose", $"Unsupported storage purpose: '{purpose}'.");
        }
        if (contentLength <= 0 || contentLength > 100 * 1024 * 1024)
        {
            throw new CentralDomainException("INVALID_CONTENT_LENGTH", "object.invalid_content_length", "Content length must be between 1 byte and 100MB.");
        }
        if (string.IsNullOrWhiteSpace(ciphertextSha256) || ciphertextSha256.Length != 64 || !ciphertextSha256.All(Uri.IsHexDigit))
        {
            throw new CentralDomainException("INVALID_SHA256", "object.invalid_sha256", "Valid 64-character hex SHA-256 is required.");
        }
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 128 || contentType.Any(char.IsControl))
        {
            throw new CentralDomainException("INVALID_CONTENT_TYPE", "object.invalid_content_type", "Content type must be nonempty, bounded, and contain no control characters.");
        }
        if (localManifestAuthenticationOpaque?.Length > 512)
        {
            throw new CentralDomainException("INVALID_MANIFEST_METADATA", "object.invalid_manifest_metadata", "Opaque manifest authentication metadata exceeds the supported length.");
        }
        if (string.IsNullOrWhiteSpace(storagePath) || storagePath.Length > 512 || storagePath.Contains(".."))
        {
            throw new CentralDomainException("PATH_TRAVERSAL_DETECTED", "object.path_traversal", "Storage path is invalid or exceeds the supported length.");
        }
        if (retentionPolicyId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_RETENTION_POLICY", "object.invalid_retention_policy", "Retention policy ID is required.");
        }

        return new EncryptedObjectRecord
        {
            Id = objectId,
            ShopId = shopId,
            InstallationId = installationId,
            Purpose = purpose,
            ContentLength = contentLength,
            ContentType = contentType,
            CiphertextSha256 = ciphertextSha256.ToLowerInvariant(),
            StoragePath = storagePath.Trim(),
            RetentionPolicyId = retentionPolicyId,
            RetentionUntilUtc = retentionUntilUtc,
            IsUnderLegalHold = isUnderLegalHold,
            LocalManifestAuthenticationOpaque = localManifestAuthenticationOpaque,
            IsTombstoned = false,
            TombstonedAtUtc = null,
            CreatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }

    public void Tombstone(DateTimeOffset atUtc)
    {
        if (IsUnderLegalHold)
        {
            throw new CentralDomainException("LEGAL_HOLD_ACTIVE", "storage.legal_hold_active", "Object cannot be tombstoned while a legal hold is active.");
        }
        if (RetentionUntilUtc is DateTimeOffset retainUntil && atUtc < retainUntil)
        {
            throw new CentralDomainException("RETENTION_PERIOD_ACTIVE", "storage.retention_active", "Object cannot be tombstoned before its retention period ends.");
        }
        if (IsTombstoned)
        {
            return;
        }
        IsTombstoned = true;
        TombstonedAtUtc = atUtc;
        RowVersion++;
    }
}
