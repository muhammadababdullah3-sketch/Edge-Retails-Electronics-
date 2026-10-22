using System.Security.Cryptography;
using System.Data;
using System.Text;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EdgeRetails.Central.Application.Services;

public class Wave3StorageAndSupportService
{
    private readonly ICentralDbContext _dbContext;
    private readonly IObjectStorageProvider _storageProvider;
    private readonly IOperationLedgerService _ledger;
    private readonly ILogger<Wave3StorageAndSupportService>? _logger;

    public Wave3StorageAndSupportService(
        ICentralDbContext dbContext,
        IObjectStorageProvider storageProvider,
        IOperationLedgerService ledger,
        ILogger<Wave3StorageAndSupportService>? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger;
    }

    // CONE-T15: Encrypted object upload sessions & resumable retry. Policy values
    // are explicit Central configuration; a client-supplied UUID never selects policy.
    public async Task<(OutcomeDto Outcome, UploadSessionResponseDto Response)> InitUploadAsync(
        CommandMetaDto meta,
        UploadInitDto dto,
        string callerScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerScope);
        cancellationToken.ThrowIfCancellationRequested();
        if (dto.RetentionPolicyId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_RETENTION_POLICY", "upload.invalid_retention_policy", "An explicit retention policy ID is required.");
        }
        if (!StorageUploadLimits.TryRead(out var limits))
        {
            throw new CentralDomainException("STORAGE_CONFIG_NOT_ACTIVE", "upload.configuration_required", "Explicit upload object and chunk bounds are required.");
        }
        if (dto.ContentLength <= 0 || dto.ContentLength > limits.MaxObjectBytes)
        {
            throw new CentralDomainException("OVERSIZE_UPLOAD", "upload.oversize", "Upload length must be positive and within the configured object bound.");
        }
        if (dto.Purpose != EncryptedObjectRecord.PurposeEncryptedBackup &&
            dto.Purpose != EncryptedObjectRecord.PurposeSupportAttachment &&
            dto.Purpose != EncryptedObjectRecord.PurposePaymentProof)
        {
            throw new CentralDomainException("INVALID_PURPOSE", "upload.invalid_purpose", "Unsupported ciphertext storage purpose.");
        }
        if (string.IsNullOrWhiteSpace(dto.ContentType) || dto.ContentType.Length > 128 || dto.ContentType.Any(char.IsControl))
        {
            throw new CentralDomainException("INVALID_CONTENT_TYPE", "upload.invalid_content_type", "A bounded content type without control characters is required.");
        }
        if (string.IsNullOrWhiteSpace(dto.CiphertextSha256) || dto.CiphertextSha256.Length != 64 || !dto.CiphertextSha256.All(Uri.IsHexDigit))
        {
            throw new CentralDomainException("INVALID_SHA256", "upload.invalid_sha256", "A 64-character hexadecimal SHA-256 is required.");
        }
        if (dto.LocalManifestAuthenticationOpaque?.Length > 512)
        {
            throw new CentralDomainException("INVALID_MANIFEST_METADATA", "upload.invalid_manifest_metadata", "Opaque manifest authentication metadata exceeds the supported length.");
        }

        if (!Guid.TryParse(Environment.GetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_POLICY_ID"), out var configuredPolicyId) ||
            configuredPolicyId == Guid.Empty || configuredPolicyId != dto.RetentionPolicyId ||
            !int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_DAYS"), out var retentionDays) || retentionDays is < 1 or > 36500 ||
            !bool.TryParse(Environment.GetEnvironmentVariable("CENTRAL_STORAGE_LEGAL_HOLD_ENABLED"), out var legalHoldEnabled) ||
            !int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_UPLOAD_SESSION_TTL_SECONDS"), out var uploadTtlSeconds) || uploadTtlSeconds is < 60 or > 86400)
        {
            throw new CentralDomainException(
                "RETENTION_POLICY_AUTHORITY_NOT_ACTIVE",
                "upload.retention_policy_unavailable",
                "An explicit Central retention policy, retention term, and upload session lifetime are required.");
        }

        if (!callerScope.StartsWith("installation:", StringComparison.Ordinal) ||
            !Guid.TryParse(callerScope["installation:".Length..], out var authenticatedInstallationId) ||
            authenticatedInstallationId != dto.InstallationId)
        {
            throw new CentralDomainException("INSTALLATION_AUTHORITY_NOT_ACTIVE", "auth.installation_required", "Upload initialization must be bound to the authenticated installation.");
        }
        var installation = await _dbContext.Installations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == authenticatedInstallationId, cancellationToken)
            ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "auth.installation_required", "Authenticated installation was not found.");
        if (installation.Status != Installation.StatusActive || installation.ShopId != dto.ShopId)
        {
            throw new CentralDomainException("INSTALLATION_AUTHORITY_NOT_ACTIVE", "auth.installation_required", "Upload shop scope does not match the active installation.");
        }

        meta.OperationType = "storage.upload_init";
        var now = DateTimeOffset.UtcNow;
        var expiresAtUtc = now.AddSeconds(uploadTtlSeconds);
        var (outcome, response) = await _ledger.ExecuteAndCaptureOrReplayAsync<UploadInitDto, UploadSessionResponseDto>(
            actorScope: callerScope,
            shopId: installation.ShopId,
            meta: meta,
            payload: dto,
            executeAsync: async command =>
            {
                await StorageConcurrency.LockObjectAsync(_dbContext, command.ObjectId, cancellationToken);
                if (await _dbContext.EncryptedObjects.AnyAsync(x => x.Id == command.ObjectId, cancellationToken) ||
                    await _dbContext.UploadSessions.AnyAsync(x => x.TargetEntityId == command.ObjectId && x.ShopId != installation.ShopId, cancellationToken))
                {
                    throw new CentralDomainException("STORAGE_OBJECT_CONFLICT", "storage.object_conflict", "Object identity is already published, tombstoned or reserved to another persisted shop.");
                }
                var session = UploadSession.Create(
                    purpose: command.Purpose,
                    shopId: installation.ShopId,
                    uploaderPrincipalId: installation.Id,
                    targetEntityId: command.ObjectId,
                    expectedBytes: command.ContentLength,
                    expectedSha256: command.CiphertextSha256,
                    storagePath: $"objects/{installation.ShopId:N}/{command.ObjectId:N}.bin",
                    expiresAtUtc: expiresAtUtc,
                    createdAtUtc: now,
                    contentType: command.ContentType,
                    retentionPolicyId: configuredPolicyId,
                    localManifestAuthenticationOpaque: command.LocalManifestAuthenticationOpaque,
                    retentionUntilUtc: now.AddDays(retentionDays),
                    isUnderLegalHold: legalHoldEnabled);
                _dbContext.UploadSessions.Add(session);
                await _dbContext.SaveChangesAsync(cancellationToken);
                var result = new UploadSessionResponseDto
                {
                    UploadId = session.Id,
                    AuthorizedObjectId = command.ObjectId,
                    MaxBytes = session.ExpectedBytes,
                    AcceptedOffset = 0,
                    ExpiresAtUtc = session.ExpiresAtUtc,
                    ChunkRoute = $"/api/licensing/v1/uploads/{session.Id:D}/content",
                    FinalizeRoute = $"/api/licensing/v1/backups/uploads/{session.Id:D}/finalize"
                };
                return (result, session.Id.ToString("D"), session.RowVersion, 1);
            },
            recoverResultAsync: async record =>
            {
                if (!Guid.TryParse(record.ResultReference, out var uploadId))
                {
                    throw new CentralDomainException("OPERATION_RESULT_UNAVAILABLE", "operation.result_unavailable", "Persisted upload operation result cannot be recovered.");
                }
                var session = await _dbContext.UploadSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == uploadId, cancellationToken)
                    ?? throw new CentralDomainException("OPERATION_RESULT_UNAVAILABLE", "operation.result_unavailable", "Persisted upload session cannot be recovered.");
                return new UploadSessionResponseDto
                {
                    UploadId = session.Id,
                    AuthorizedObjectId = session.TargetEntityId,
                    MaxBytes = session.ExpectedBytes,
                    AcceptedOffset = session.AcceptedOffset,
                    ExpiresAtUtc = session.ExpiresAtUtc,
                    ChunkRoute = $"/api/licensing/v1/uploads/{session.Id:D}/content",
                    FinalizeRoute = $"/api/licensing/v1/backups/uploads/{session.Id:D}/finalize"
                };
            },
            targetResource: $"uploads/init/{dto.ObjectId:D}",
            cancellationToken: cancellationToken);
        return (outcome, response);
    }

    public async Task<long> AppendUploadChunkAsync(
        Guid uploadId,
        long offset,
        long totalLength,
        string chunkSha256,
        Stream content,
        string callerScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerScope);
        if (uploadId == Guid.Empty || offset < 0 || totalLength <= 0 ||
            string.IsNullOrWhiteSpace(chunkSha256) || chunkSha256.Length != 64 || !chunkSha256.All(Uri.IsHexDigit))
        {
            throw new CentralDomainException("INVALID_UPLOAD_CHUNK", "upload.chunk_invalid", "Upload ID, byte range, total length, and SHA-256 are required.");
        }

        if (!StorageUploadLimits.TryRead(out var limits))
        {
            throw new CentralDomainException("STORAGE_CONFIG_NOT_ACTIVE", "upload.configuration_required", "Explicit upload object and chunk bounds are required.");
        }
        var maximumChunkBytes = limits.MaxChunkBytes;
        using var buffer = new MemoryStream();
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[81920];
        while (true)
        {
            var remaining = maximumChunkBytes - buffer.Length;
            var read = await content.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, remaining + 1)), cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (read > remaining)
            {
                throw new CentralDomainException("CHUNK_SIZE_EXCEEDED", "upload.chunk_size_exceeded", "Chunk size exceeds the configured bound.");
            }
            sha256.AppendData(bytes, 0, read);
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }
        if (buffer.Length == 0)
        {
            throw new CentralDomainException("INVALID_CHUNK_LENGTH", "upload.chunk_invalid", "Upload chunk cannot be empty.");
        }
        var actualChunkSha256 = Convert.ToHexStringLower(sha256.GetHashAndReset());
        if (!string.Equals(actualChunkSha256, chunkSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException("CHUNK_SHA256_MISMATCH", "upload.chunk_digest_mismatch", "Chunk digest does not match the received bytes.");
        }

        const string installationScopePrefix = "installation:";
        if (!callerScope.StartsWith(installationScopePrefix, StringComparison.Ordinal) ||
            !Guid.TryParse(callerScope[installationScopePrefix.Length..], out var callerInstallationId) ||
            callerInstallationId == Guid.Empty)
        {
            throw new CentralDomainException("FORBIDDEN", "authorization.denied", "A validated installation identity is required for upload chunks.");
        }

        // Leave an uncommitted filesystem tail for the next locked append or reconciliation.
        // Compensation after releasing this transaction could truncate another request's committed bytes.
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({uploadId.ToString("D")}, 0))",
                cancellationToken);

            var session = await _dbContext.UploadSessions.SingleOrDefaultAsync(x => x.Id == uploadId, cancellationToken)
                ?? throw new CentralDomainException("UPLOAD_SESSION_NOT_FOUND", "upload.session_not_found", "Upload session was not found.");
            if (session.UploaderPrincipalId != callerInstallationId)
            {
                throw new CentralDomainException("FORBIDDEN", "authorization.denied", "Only the installation that owns this upload session may append content.");
            }
            if (session.Status != UploadSession.StatusInitiated && session.Status != UploadSession.StatusUploading)
            {
                throw new CentralDomainException("INVALID_UPLOAD_STATE", "upload.invalid_state", "Upload session is no longer accepting chunks.");
            }
            if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                throw new CentralDomainException("UPLOAD_EXPIRED", "upload.expired", "Upload session has expired.");
            }
            if (totalLength != session.ExpectedBytes || offset > session.ExpectedBytes - buffer.Length)
            {
                throw new CentralDomainException("UPLOAD_RANGE_INVALID", "upload.range_invalid", "Chunk range exceeds the authorized upload length.");
            }

            if (offset < session.AcceptedOffset)
            {
                var exactRetry = await _storageProvider.AppendUploadChunkAsync(
                    session.ShopId, session.Id, session.AcceptedOffset, offset, buffer.ToArray(), cancellationToken);
                if (!exactRetry)
                {
                    throw new CentralDomainException("UPLOAD_CHUNK_CONFLICT", "upload.chunk_conflict", "Retry bytes differ from the previously accepted chunk.");
                }
                var acceptedOffset = session.AcceptedOffset;
                await transaction.CommitAsync(cancellationToken);
                return acceptedOffset;
            }
            if (offset != session.AcceptedOffset)
            {
                throw new CentralDomainException("UPLOAD_OFFSET_CONFLICT", "upload.offset_conflict", "Chunk offset does not equal the server's accepted offset.");
            }

            await _storageProvider.AppendUploadChunkAsync(
                session.ShopId, session.Id, session.AcceptedOffset, offset, buffer.ToArray(), cancellationToken);
            session.AppendChunk(buffer.Length);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return session.AcceptedOffset;
    }

    public async Task<(OutcomeDto Outcome, bool Success)> FinalizeUploadAsync(
        CommandMetaDto meta,
        UploadFinalizeDto dto,
        Stream ciphertextStream,
        string callerScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(ciphertextStream);

        ArgumentException.ThrowIfNullOrWhiteSpace(callerScope);
        var sessionOwner = await _dbContext.UploadSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == dto.UploadId, cancellationToken)
            ?? throw new CentralDomainException("UPLOAD_SESSION_NOT_FOUND", "upload.session_not_found", "Upload session was not found.");
        const string installationScopePrefix = "installation:";
        if (!callerScope.StartsWith(installationScopePrefix, StringComparison.Ordinal) ||
            !Guid.TryParse(callerScope[installationScopePrefix.Length..], out var callerInstallationId) ||
            callerInstallationId == Guid.Empty || sessionOwner.UploaderPrincipalId != callerInstallationId)
        {
            throw new CentralDomainException("FORBIDDEN", "authorization.denied", "Only the installation that owns this upload session may finalize it.");
        }
        if (string.IsNullOrWhiteSpace(meta.OperationType))
        {
            meta.OperationType = "storage.upload_finalize";
        }

        int? observedIntegrityFailureVersion = null;
        (OutcomeDto Outcome, bool Result) result;
        try
        {
            result = await _ledger.ExecuteAndCaptureOrReplayAsync(
            actorScope: callerScope,
            shopId: sessionOwner.ShopId,
            meta: meta,
            payload: dto,
            executeAsync: async cmd =>
            {
                if (_dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({cmd.UploadId.ToString("D")}, 0))",
                        cancellationToken);
                }
                var session = await _dbContext.UploadSessions.FirstOrDefaultAsync(s => s.Id == cmd.UploadId, cancellationToken)
                    ?? throw new CentralDomainException("UPLOAD_SESSION_NOT_FOUND", "upload.session_not_found", $"Upload session '{cmd.UploadId}' not found.");

                if (session.TargetEntityId != cmd.ObjectId)
                {
                    throw new CentralDomainException("OBJECT_ID_MISMATCH", "upload.object_mismatch", "Session target object ID does not match finalize object ID.");
                }
                await StorageConcurrency.LockObjectAsync(_dbContext, session.TargetEntityId, cancellationToken);
                if (await _dbContext.EncryptedObjects.AnyAsync(x => x.Id == session.TargetEntityId, cancellationToken))
                {
                    throw new CentralDomainException("STORAGE_OBJECT_CONFLICT", "storage.object_conflict", "Object identity has already been published or tombstoned.");
                }

                var chunkedUpload = session.UploadedBytes > 0;
                if (chunkedUpload
                    ? session.UploadedBytes != session.ExpectedBytes || session.Status != UploadSession.StatusUploading
                    : session.Status != UploadSession.StatusInitiated)
                {
                    throw new CentralDomainException("INVALID_UPLOAD_STATE", "upload.invalid_state", "Upload must be complete and active before finalization.");
                }
                if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    throw new CentralDomainException("UPLOAD_EXPIRED", "upload.expired", "Upload session has expired.");
                }
                if (cmd.ExpectedLength != session.ExpectedBytes)
                {
                    throw new CentralDomainException("UPLOAD_LENGTH_MISMATCH", "upload.length_mismatch", "Declared length does not match the authorized upload session.");
                }
                if (!string.Equals(cmd.ExpectedSha256, session.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CentralDomainException("SHA256_MISMATCH", "upload.sha256_mismatch", "Declared digest does not match the authorized upload session.");
                }
                if (session.RetentionPolicyId is not Guid retentionPolicyId || retentionPolicyId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(session.ContentType))
                {
                    throw new CentralDomainException("UPLOAD_METADATA_REINITIALIZATION_REQUIRED", "upload.metadata_reinitialization_required", "Legacy upload metadata is incomplete; initialize a new authorized upload.");
                }
                var contentType = session.ContentType;

                // Bound each read before buffering; Central verifies ciphertext bytes, not customer-key authentication.
                await using var stagedStream = chunkedUpload
                    ? await _storageProvider.OpenUploadContentAsync(session.ShopId, session.Id, cancellationToken)
                    : Stream.Null;
                var sourceStream = chunkedUpload ? stagedStream : ciphertextStream;
                using var memoryStream = new MemoryStream();
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                while (true)
                {
                    var remaining = session.ExpectedBytes - memoryStream.Length;
                    var read = await sourceStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining + 1)), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    if (read > remaining)
                    {
                        observedIntegrityFailureVersion = session.RowVersion;
                        throw new CentralDomainException("OVERSIZE_UPLOAD", "upload.oversize", "Ciphertext exceeds the authorized upload length.");
                    }
                    sha256.AppendData(buffer, 0, read);
                    await memoryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (memoryStream.Length != session.ExpectedBytes)
                {
                    observedIntegrityFailureVersion = session.RowVersion;
                    throw new CentralDomainException("INCOMPLETE_UPLOAD", "upload.incomplete", "Ciphertext length does not match the authorized upload length.");
                }
                var computedSha256 = Convert.ToHexStringLower(sha256.GetHashAndReset());
                if (!string.Equals(computedSha256, session.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    observedIntegrityFailureVersion = session.RowVersion;
                    throw new CentralDomainException("SHA256_MISMATCH", "upload.sha256_mismatch", "Computed ciphertext digest does not match the authorized upload.");
                }

                if (!chunkedUpload)
                {
                    session.AppendChunk(memoryStream.Length);
                }
                session.FinalizeUpload(computedSha256);
                memoryStream.Position = 0;
                var storagePath = await _storageProvider.SaveCiphertextAsync(session.ShopId, session.TargetEntityId, memoryStream, cancellationToken);
                session.SetStoragePath(storagePath);

                // Create and persist EncryptedObjectRecord without asserting client-side authentication.
                var objectRecord = EncryptedObjectRecord.Create(
                    objectId: session.TargetEntityId,
                    shopId: session.ShopId,
                    installationId: session.UploaderPrincipalId,
                    purpose: session.Purpose,
                    contentLength: memoryStream.Length,
                    contentType: contentType,
                    ciphertextSha256: computedSha256,
                    storagePath: storagePath,
                    retentionPolicyId: retentionPolicyId,
                    localManifestAuthenticationOpaque: session.LocalManifestAuthenticationOpaque,
                    createdAtUtc: DateTimeOffset.UtcNow,
                    retentionUntilUtc: session.RetentionUntilUtc,
                    isUnderLegalHold: session.IsUnderLegalHold);

                _dbContext.EncryptedObjects.Add(objectRecord);
                await _dbContext.SaveChangesAsync(cancellationToken);

                return (true, $"objects/{objectRecord.Id}", 1, 1);
            },
            recoverResultAsync: _ => Task.FromResult(true),
            targetResource: $"uploads/finalize/{dto.UploadId}",
            cancellationToken: cancellationToken);
        }
        catch (CentralDomainException exception) when (observedIntegrityFailureVersion.HasValue)
        {
            // The failed ledger transaction has rolled back. Persist the independently observed
            // integrity failure under the upload lock, without changing a newer committed revision.
            await QuarantineObservedUploadAsync(dto.UploadId, observedIntegrityFailureVersion.Value, exception.Code, callerScope, cancellationToken);
            throw;
        }
        if (string.Equals(result.Outcome.State, "SUCCEEDED", StringComparison.Ordinal))
        {
            try
            {
                await _storageProvider.DeleteUploadContentAsync(sessionOwner.ShopId, dto.UploadId, cancellationToken);
            }
            catch (CentralDomainException exception)
            {
                // Publication has committed. Preserve its authoritative result; reconciliation retries cleanup.
                _logger?.LogWarning("Committed upload staging cleanup deferred ({Code}).", exception.Code);
            }
        }
        return result;
    }

    private async Task QuarantineObservedUploadAsync(Guid uploadId, int observedVersion, string reason,
        string callerScope, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (_dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({uploadId.ToString("D")}, 0))", cancellationToken);
        }
        var session = await _dbContext.UploadSessions.SingleOrDefaultAsync(x => x.Id == uploadId, cancellationToken);
        if (session is not null && session.RowVersion == observedVersion &&
            session.Status is UploadSession.StatusInitiated or UploadSession.StatusUploading)
        {
            session.Quarantine(reason);
            _dbContext.CentralAuditRecords.Add(CentralAuditRecord.Create(callerScope, null, null,
                "storage.upload_quarantined", "upload", uploadId.ToString("D"), reason, Guid.NewGuid().ToString("D"),
                null, null, session.ExpectedSha256, "{}", false, DateTimeOffset.UtcNow));
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    // CONE-T16: Ciphertext retention and download
    public async Task<ObjectDownloadGrantResponseDto> CreateBackupDownloadGrantAsync(
        Guid objectId,
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        if (!StorageDownloadGrantPolicy.TryReadTtlSeconds(out var ttlSeconds))
        {
            throw new CentralDomainException("SECURITY_POLICY_REQUIRED", "policy.configuration_required", "Explicit backup download grant lifetime configuration is required.");
        }

        var installation = await _dbContext.Installations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        var record = await _dbContext.EncryptedObjects.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == objectId, cancellationToken);
        if (installation is null || installation.Status != Installation.StatusActive || record is null ||
            record.ShopId != installation.ShopId || record.InstallationId != installation.Id ||
            record.Purpose != EncryptedObjectRecord.PurposeEncryptedBackup || record.IsTombstoned)
        {
            throw new CentralDomainException("OBJECT_NOT_FOUND", "storage.object_not_found", "The requested backup is not available to this installation.");
        }

        var now = DateTimeOffset.UtcNow;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var grant = ObjectDownloadGrant.Create(Guid.NewGuid(), installation.ShopId, installation.Id, record.Id,
            tokenHash, now, now.AddSeconds(ttlSeconds));
        var expiredGrants = await _dbContext.ObjectDownloadGrants
            .Where(x => x.ExpiresAtUtc <= now)
            .OrderBy(x => x.ExpiresAtUtc).ThenBy(x => x.Id)
            .Take(32)
            .ToListAsync(cancellationToken);
        _dbContext.ObjectDownloadGrants.RemoveRange(expiredGrants);
        _dbContext.ObjectDownloadGrants.Add(grant);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (expiredGrants.Count > 0)
        {
            _logger?.LogInformation("Removed {ExpiredCount} expired backup download grants while issuing a new grant.", expiredGrants.Count);
        }

        return new ObjectDownloadGrantResponseDto
        {
            GrantId = grant.Id,
            DownloadToken = token,
            ContentRoute = $"/api/licensing/v1/backups/download-grants/{grant.Id:D}/content",
            ExpiresAtUtc = grant.ExpiresAtUtc
        };
    }

    public async Task<(Stream ContentStream, EncryptedObjectRecord Record)> DownloadWithGrantAsync(
        Guid grantId,
        string? bearerToken,
        CancellationToken cancellationToken = default)
    {
        if (grantId == Guid.Empty || string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new CentralDomainException("DOWNLOAD_GRANT_NOT_FOUND", "storage.download_grant_not_found", "The download grant is invalid or expired.");
        }

        var grant = await _dbContext.ObjectDownloadGrants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grantId, cancellationToken);
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken));
        if (grant is null || !grant.IsValidAt(DateTimeOffset.UtcNow) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(grant.TokenSha256), suppliedHash))
        {
            throw new CentralDomainException("DOWNLOAD_GRANT_NOT_FOUND", "storage.download_grant_not_found", "The download grant is invalid or expired.");
        }

        var installation = await _dbContext.Installations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grant.InstallationId && x.ShopId == grant.ShopId, cancellationToken);
        var record = await _dbContext.EncryptedObjects.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == grant.ObjectId && x.ShopId == grant.ShopId && x.InstallationId == grant.InstallationId &&
                x.Purpose == EncryptedObjectRecord.PurposeEncryptedBackup && !x.IsTombstoned,
                cancellationToken);
        if (installation is null || installation.Status != Installation.StatusActive || record is null)
        {
            throw new CentralDomainException("DOWNLOAD_GRANT_NOT_FOUND", "storage.download_grant_not_found", "The download grant is invalid or expired.");
        }

        var stream = await _storageProvider.GetCiphertextAsync(record.StoragePath, cancellationToken);
        return (stream, record);
    }

    public async Task<(Stream ContentStream, EncryptedObjectRecord Record)> DownloadCiphertextAsync(
        Guid objectId,
        Guid requestingShopId,
        CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.EncryptedObjects
            .FirstOrDefaultAsync(r => r.Id == objectId, cancellationToken)
            ?? throw new CentralDomainException("OBJECT_NOT_FOUND", "storage.object_not_found", $"Object '{objectId}' was not found.");

        if (record.IsTombstoned)
        {
            throw new CentralDomainException("OBJECT_TOMBSTONED", "storage.object_tombstoned", "Requested object has been tombstoned by retention policy.");
        }

        // Cross-shop isolation check
        if (record.ShopId != requestingShopId)
        {
            throw new CentralDomainException("CROSS_SHOP_ACCESS_DENIED", "storage.cross_shop_denied", "Cross-shop object access is strictly prohibited.");
        }
        if (record.Purpose != EncryptedObjectRecord.PurposeEncryptedBackup)
        {
            throw new CentralDomainException("FORBIDDEN", "storage.purpose_denied", "Backup download authority does not authorize another object purpose.");
        }

        var stream = await _storageProvider.GetCiphertextAsync(record.StoragePath, cancellationToken);
        return (stream, record);
    }

    public async Task<(Stream ContentStream, EncryptedObjectRecord Record)> DownloadSupportAttachmentAsync(
        Guid ticketId,
        Guid objectId,
        CancellationToken cancellationToken = default)
    {
        if (ticketId == Guid.Empty || objectId == Guid.Empty)
        {
            throw new CentralDomainException("SUPPORT_ATTACHMENT_NOT_FOUND", "support.attachment_not_found", "The requested support attachment was not found.");
        }

        var ticket = await _dbContext.SupportTickets.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == ticketId, cancellationToken);
        if (ticket is null)
        {
            throw new CentralDomainException("SUPPORT_ATTACHMENT_NOT_FOUND", "support.attachment_not_found", "The requested support attachment was not found.");
        }

        var attachmentIds = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(ticket.AttachmentObjectIdsJson) ?? new List<Guid>();
        if (!attachmentIds.Contains(objectId))
        {
            throw new CentralDomainException("SUPPORT_ATTACHMENT_NOT_FOUND", "support.attachment_not_found", "The requested support attachment was not found.");
        }

        var record = await _dbContext.EncryptedObjects.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == objectId && x.ShopId == ticket.ShopId &&
                x.Purpose == EncryptedObjectRecord.PurposeSupportAttachment && !x.IsTombstoned,
                cancellationToken);
        if (record is null)
        {
            throw new CentralDomainException("SUPPORT_ATTACHMENT_NOT_FOUND", "support.attachment_not_found", "The requested support attachment was not found.");
        }

        var stream = await _storageProvider.GetCiphertextAsync(record.StoragePath, cancellationToken);
        return (stream, record);
    }

    // CONE-T18: Support ticket, replies, and attachments
    public async Task<(OutcomeDto Outcome, Guid TicketId)> CreateSupportTicketAsync(
        CommandMetaDto meta,
        TicketDto dto,
        Guid requesterPrincipalId,
        string callerScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerScope);
        cancellationToken.ThrowIfCancellationRequested();

        if (requesterPrincipalId == Guid.Empty || meta.ClientOperationId == Guid.Empty)
        {
            throw new CentralDomainException("SUPPORT_REQUESTER_REQUIRED", "support.requester_required", "A validated admin session and operation identifier are required.");
        }
        meta.OperationType = "SUPPORT_TICKET_CREATE";
        var (outcome, ticketId) = await _ledger.ExecuteAndCaptureOrReplayAsync(
            actorScope: callerScope,
            shopId: dto.ShopId,
            meta: meta,
            payload: dto,
            executeAsync: async command =>
            {
                var shop = await _dbContext.Shops.SingleOrDefaultAsync(x => x.Id == command.ShopId, cancellationToken)
                    ?? throw new CentralDomainException("SHOP_NOT_FOUND", "support.shop_not_found", "The requested shop does not exist.");
                var attachments = command.AttachmentObjectIds ?? new List<Guid>();
                if (attachments.Count > 20 || attachments.Distinct().Count() != attachments.Count)
                {
                    throw new CentralDomainException("SUPPORT_ATTACHMENTS_INVALID", "support.attachments_invalid", "Ticket attachments must be unique and limited to 20 objects.");
                }
                if (attachments.Count > 0)
                {
                    var records = await _dbContext.EncryptedObjects
                        .Where(x => attachments.Contains(x.Id))
                        .ToListAsync(cancellationToken);
                    if (records.Count != attachments.Count || records.Any(x => x.ShopId != shop.Id || x.IsTombstoned || x.Purpose != EncryptedObjectRecord.PurposeSupportAttachment))
                    {
                        throw new CentralDomainException("SUPPORT_ATTACHMENT_ACCESS_DENIED", "support.attachment_access_denied", "Every attachment must be an active support object owned by this persisted shop.");
                    }
                }

                var ticket = SupportTicket.Create(Guid.NewGuid(), shop.Id, requesterPrincipalId, command.Subject, command.Summary,
                    attachments, command.RedactionProfileVersion, DateTimeOffset.UtcNow);
                _dbContext.SupportTickets.Add(ticket);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return (ticket.Id, ticket.Id.ToString("D"), ticket.RowVersion, 1);
            },
            recoverResultAsync: record => Task.FromResult(Guid.Parse(record.ResultReference!)),
            targetResource: $"support-shop:{dto.ShopId:D}",
            cancellationToken: cancellationToken);
        return (outcome, ticketId);
    }

    public async Task<(OutcomeDto Outcome, bool Success)> ReplySupportTicketAsync(
        CommandMetaDto meta,
        TicketActionDto dto,
        string author,
        string callerScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerScope);
        cancellationToken.ThrowIfCancellationRequested();

        if (dto.Action.Equals("REPLY", StringComparison.OrdinalIgnoreCase) || dto.Action.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
        {
            var existingTicket = await _dbContext.SupportTickets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == dto.TicketId, cancellationToken)
                ?? throw new CentralDomainException("SUPPORT_TICKET_NOT_FOUND", "support.ticket_not_found", "The requested support ticket does not exist.");
            var isStatus = dto.Action.Equals("STATUS", StringComparison.OrdinalIgnoreCase);
            meta.OperationType = isStatus ? "SUPPORT_TICKET_STATUS" : "SUPPORT_TICKET_REPLY";
            var (outcome, _) = await _ledger.ExecuteAndCaptureOrReplayAsync(
                actorScope: callerScope,
                shopId: existingTicket.ShopId,
                meta: meta,
                payload: dto,
                executeAsync: async command =>
                {
                    var ticket = await _dbContext.SupportTickets.SingleAsync(x => x.Id == command.TicketId, cancellationToken);
                    if (isStatus)
                    {
                        ticket.SetStatus(command.Status ?? string.Empty, author, DateTimeOffset.UtcNow);
                    }
                    else
                    {
                        ticket.AddReply(author, command.ReplyText ?? string.Empty, DateTimeOffset.UtcNow);
                    }
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return (true, ticket.Id.ToString("D"), ticket.RowVersion, 1);
                },
                recoverResultAsync: _ => Task.FromResult(true),
                targetResource: $"support-ticket:{dto.TicketId:D}",
                cancellationToken: cancellationToken);
            return (outcome, true);
        }

        throw new CentralDomainException("INVALID_SUPPORT_ACTION", "support.invalid_action", "Only persisted staff replies and ticket status transitions are supported by this route.");
    }

    // CONE-T17 customer backup restore is owned by the authorized edge/offline verifier.
    // This compatibility shim fails closed; it does not implement customer restore or CONE-T19 Central recovery.
    public Task<bool> RehearseRestoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Customer ciphertext is restored by an authorized edge/offline verifier, never by Central.
        // Central control-plane recovery is a separate operator-owned workflow.
        throw new CentralDomainException(
            "CUSTOMER_RESTORE_NOT_SUPPORTED",
            "restore.edge_verifier_required",
            "Customer backup decryption and restore must run in the authorized edge/offline verifier.");
    }
}
