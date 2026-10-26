using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Commercial;

public class UploadSession : Entity
{
    public const string PurposePaymentProof = "PAYMENT_PROOF";
    public const string PurposeBackupArchive = "BACKUP_ARCHIVE";
    public const string PurposeSupportBundle = "SUPPORT_BUNDLE";
    public const string PurposeEncryptedBackup = "ENCRYPTED_BACKUP";
    public const string PurposeSupportAttachment = "SUPPORT_ATTACHMENT";

    public const string StatusInitiated = "INITIATED";
    public const string StatusUploading = "UPLOADING";
    public const string StatusQuarantined = "QUARANTINED";
    public const string StatusFinalized = "FINALIZED";
    public const string StatusExpired = "EXPIRED";

    public string Purpose { get; private set; } = PurposePaymentProof;
    public Guid ShopId { get; private set; }
    public Guid UploaderPrincipalId { get; private set; }
    public Guid TargetEntityId { get; private set; }
    public long ExpectedBytes { get; private set; }
    public long UploadedBytes { get; private set; }
    public long AcceptedOffset => UploadedBytes;
    public string Status { get; private set; } = StatusInitiated;
    public string ExpectedSha256 { get; private set; } = string.Empty;
    public string ActualSha256 { get; private set; } = string.Empty;
    public string StoragePath { get; private set; } = string.Empty;
    public string? ContentType { get; private set; }
    public Guid? RetentionPolicyId { get; private set; }
    public DateTimeOffset? RetentionUntilUtc { get; private set; }
    public bool IsUnderLegalHold { get; private set; }
    public string? LocalManifestAuthenticationOpaque { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private UploadSession() { }

    public static UploadSession Create(
        string purpose,
        Guid shopId,
        Guid uploaderPrincipalId,
        Guid targetEntityId,
        long expectedBytes,
        string expectedSha256,
        string storagePath,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset createdAtUtc,
        string? contentType = null,
        Guid? retentionPolicyId = null,
        string? localManifestAuthenticationOpaque = null,
        DateTimeOffset? retentionUntilUtc = null,
        bool isUnderLegalHold = false)
    {
        if (purpose != PurposePaymentProof && purpose != PurposeBackupArchive && purpose != PurposeSupportBundle && purpose != PurposeEncryptedBackup && purpose != PurposeSupportAttachment)
        {
            throw new CentralDomainException("INVALID_PURPOSE", "upload.invalid_purpose", $"Invalid purpose: {purpose}");
        }
        if (shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SHOP_ID", "upload.invalid_shop_id", "Shop ID is required.");
        }
        if (string.IsNullOrWhiteSpace(storagePath) || storagePath.Length > 512)
        {
            throw new CentralDomainException("INVALID_STORAGE_PATH", "upload.invalid_storage_path", "Storage path is required and limited to 512 characters.");
        }
        if (expectedBytes <= 0 || expectedBytes > 100 * 1024 * 1024) // 100MB max limit
        {
            throw new CentralDomainException("INVALID_BYTE_LENGTH", "upload.invalid_byte_length", "Expected byte length must be between 1 and 100MB.");
        }
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new CentralDomainException("INVALID_SHA256", "upload.invalid_sha256", "Valid 64-character hex SHA-256 is required.");
        }
        if (uploaderPrincipalId == Guid.Empty || targetEntityId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_UPLOAD_IDENTITY", "upload.invalid_identity", "Uploader and target object IDs are required.");
        }
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new CentralDomainException("INVALID_UPLOAD_EXPIRY", "upload.invalid_expiry", "Upload expiry must follow creation.");
        }
        if (contentType is not null && (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 128 || contentType.Any(char.IsControl)))
        {
            throw new CentralDomainException("INVALID_CONTENT_TYPE", "upload.invalid_content_type", "A bounded content type without control characters is required.");
        }
        if (retentionPolicyId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_RETENTION_POLICY", "upload.invalid_retention_policy", "An explicit retention policy ID is required.");
        }
        if (localManifestAuthenticationOpaque?.Length > 512)
        {
            throw new CentralDomainException("INVALID_MANIFEST_METADATA", "upload.invalid_manifest_metadata", "Opaque manifest authentication metadata exceeds the supported length.");
        }
        if (purpose == PurposeEncryptedBackup && (contentType is null || retentionPolicyId is null))
        {
            throw new CentralDomainException("UPLOAD_METADATA_REQUIRED", "upload.metadata_required", "Encrypted backup uploads require explicit content type and retention policy metadata.");
        }

        return new UploadSession
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            ShopId = shopId,
            UploaderPrincipalId = uploaderPrincipalId,
            TargetEntityId = targetEntityId,
            ExpectedBytes = expectedBytes,
            UploadedBytes = 0,
            Status = StatusInitiated,
            ExpectedSha256 = expectedSha256.ToLowerInvariant(),
            ActualSha256 = string.Empty,
            StoragePath = storagePath,
            ContentType = contentType,
            RetentionPolicyId = retentionPolicyId,
            RetentionUntilUtc = retentionUntilUtc,
            IsUnderLegalHold = isUnderLegalHold,
            LocalManifestAuthenticationOpaque = localManifestAuthenticationOpaque,
            ExpiresAtUtc = expiresAtUtc,
            CreatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }

    public void AppendChunk(long chunkBytes)
    {
        if (Status != StatusInitiated && Status != StatusUploading)
        {
            throw new CentralDomainException("INVALID_UPLOAD_STATE", "upload.invalid_state", $"Cannot append chunk in status: {Status}");
        }
        if (chunkBytes <= 0)
        {
            throw new CentralDomainException("INVALID_CHUNK_LENGTH", "upload.invalid_chunk_length", "Chunk length must be positive.");
        }
        if (chunkBytes > ExpectedBytes - UploadedBytes)
        {
            Status = StatusQuarantined;
            RowVersion++;
            throw new CentralDomainException("OVERSIZE_UPLOAD", "upload.oversize", "Uploaded bytes exceeded expected bytes; quarantined.");
        }

        UploadedBytes += chunkBytes;
        Status = StatusUploading;
        RowVersion++;
    }

    public void FinalizeUpload(string computedSha256)
    {
        if (Status != StatusUploading && Status != StatusInitiated)
        {
            throw new CentralDomainException("INVALID_UPLOAD_STATE", "upload.invalid_state", $"Cannot finalize upload in status: {Status}");
        }
        if (UploadedBytes != ExpectedBytes)
        {
            Status = StatusQuarantined;
            RowVersion++;
            throw new CentralDomainException("INCOMPLETE_UPLOAD", "upload.incomplete", "Uploaded bytes do not match expected bytes; quarantined.");
        }
        if (string.IsNullOrWhiteSpace(computedSha256) || computedSha256.Length != 64 || !computedSha256.All(Uri.IsHexDigit))
        {
            throw new CentralDomainException("INVALID_SHA256", "upload.invalid_sha256", "Computed digest must be a 64-character hexadecimal SHA-256.");
        }

        ActualSha256 = computedSha256.ToLowerInvariant();
        if (!string.Equals(ActualSha256, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            Status = StatusQuarantined;
            RowVersion++;
            throw new CentralDomainException("SHA256_MISMATCH", "upload.sha256_mismatch", "Computed SHA-256 does not match declared digest; quarantined.");
        }

        Status = StatusFinalized;
        RowVersion++;
    }

    public void SetStoragePath(string storagePath)
    {
        if (Status != StatusFinalized || string.IsNullOrWhiteSpace(storagePath) || storagePath.Length > 512)
        {
            throw new CentralDomainException("INVALID_STORAGE_PATH", "upload.invalid_storage_path", "Finalized storage path is required and limited to 512 characters.");
        }

        StoragePath = storagePath;
        RowVersion++;
    }

    public void Quarantine(string reason)
    {
        Status = StatusQuarantined;
        RowVersion++;
    }

    public void Expire(DateTimeOffset now)
    {
        if (now < ExpiresAtUtc || Status is not (StatusInitiated or StatusUploading))
        {
            throw new CentralDomainException("INVALID_UPLOAD_STATE", "upload.invalid_state", "Only an expired active upload may transition to expired.");
        }
        Status = StatusExpired;
        RowVersion++;
    }
}
