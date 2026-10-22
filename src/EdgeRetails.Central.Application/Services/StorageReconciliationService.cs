using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Storage;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

public sealed record StorageReconciliationResult(int SessionsScanned, int TombstonesScanned, int Inconsistent, int PendingRemoved, int PendingDeferred, int ExpiredDownloadGrantsRemoved);

public sealed class StorageReconciliationService(ICentralDbContext db, IObjectStorageProvider storage)
{
    public async Task<StorageReconciliationResult> ReconcileAsync(int sessionOffset, int tombstoneOffset, int batchSize,
        DateTimeOffset now, DateTimeOffset pendingBeforeUtc, CancellationToken cancellationToken = default)
    {
        if (batchSize < 1 || sessionOffset < 0 || tombstoneOffset < 0 || pendingBeforeUtc > now)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }
        var ids = await db.UploadSessions.AsNoTracking()
            .Where(x => x.RetentionPolicyId != null && x.ContentType != null)
            .OrderBy(x => x.Id).Skip(sessionOffset).Take(batchSize).Select(x => x.Id).ToListAsync(cancellationToken);
        var inconsistent = 0;
        foreach (var id in ids)
        {
            try
            {
                if (!await ReconcileUploadAsync(id, now, cancellationToken))
                {
                    inconsistent++;
                }
            }
            catch (CentralDomainException)
            {
                inconsistent++;
            }
        }
        var tombstones = await db.EncryptedObjects.AsNoTracking().Where(x => x.IsTombstoned)
            .OrderBy(x => x.Id).Skip(tombstoneOffset).Take(batchSize).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var id in tombstones)
        {
            try
            {
                if (!await ReconcileTombstoneAsync(id, now, cancellationToken))
                {
                    inconsistent++;
                }
            }
            catch (CentralDomainException)
            {
                inconsistent++;
            }
        }
        var pending = await storage.CleanupPendingPublicationsAsync(pendingBeforeUtc, batchSize, cancellationToken);
        var expiredGrantsRemoved = await CleanupExpiredDownloadGrantsAsync(now, batchSize, cancellationToken);
        return new(ids.Count, tombstones.Count, inconsistent, pending.Removed, pending.Deferred, expiredGrantsRemoved);
    }

    public async Task<int> CleanupExpiredDownloadGrantsAsync(DateTimeOffset now, int maximumGrants, CancellationToken cancellationToken = default)
    {
        if (maximumGrants < 1 || maximumGrants > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGrants));
        }

        var expired = await db.ObjectDownloadGrants
            .Where(x => x.ExpiresAtUtc <= now)
            .OrderBy(x => x.ExpiresAtUtc).ThenBy(x => x.Id)
            .Take(maximumGrants)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return 0;
        }

        db.ObjectDownloadGrants.RemoveRange(expired);
        return await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ReconcileUploadAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString("D")}, 0))", cancellationToken);
        var session = await db.UploadSessions.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (session is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        await StorageConcurrency.LockObjectAsync(db, session.TargetEntityId, cancellationToken);
        string? orphanTombstonePath = null;
        if (session.Status is UploadSession.StatusInitiated or UploadSession.StatusUploading && session.ExpiresAtUtc <= now)
        {
            session.Expire(now);
            RecordTransition(session.Id, "storage.upload_expired", "UPLOAD_EXPIRED", now);
            await db.SaveChangesAsync(cancellationToken);
        }
        if (session.Status is UploadSession.StatusInitiated or UploadSession.StatusUploading)
        {
            // Never mutate the offset from filesystem observations. Missing bytes may indicate a wrong root.
            await storage.TruncateUploadContentAsync(session.ShopId, session.Id, session.AcceptedOffset, cancellationToken);
        }
        else if (session.Status == UploadSession.StatusFinalized)
        {
            var record = await db.EncryptedObjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.TargetEntityId && x.ShopId == session.ShopId, cancellationToken);
            if (record is null || !record.IsTombstoned && !await VerifyCiphertextAsync(record.StoragePath, record.ContentLength, record.CiphertextSha256, cancellationToken))
            {
                return false;
            }
            if (record.IsTombstoned && (record.IsUnderLegalHold || record.RetentionUntilUtc is not DateTimeOffset until || until > now))
            {
                return false;
            }
            await storage.DeleteUploadContentAsync(session.ShopId, session.Id, cancellationToken);
        }
        else if (session.Status is UploadSession.StatusExpired or UploadSession.StatusQuarantined &&
            !session.IsUnderLegalHold && session.RetentionUntilUtc is DateTimeOffset retainUntil && retainUntil <= now)
        {
            await storage.DeleteUploadContentAsync(session.ShopId, session.Id, cancellationToken);
            var objectPath = $"{session.ShopId:N}/{session.TargetEntityId:N}.bin";
            var hasMetadata = await db.EncryptedObjects.AsNoTracking().AnyAsync(x => x.Id == session.TargetEntityId, cancellationToken);
            var hasProtectedOwner = await db.UploadSessions.AsNoTracking().AnyAsync(x => x.Id != session.Id &&
                x.TargetEntityId == session.TargetEntityId &&
                (x.Status == UploadSession.StatusFinalized || x.IsUnderLegalHold || x.RetentionUntilUtc == null || x.RetentionUntilUtc > now ||
                (x.Status == UploadSession.StatusInitiated || x.Status == UploadSession.StatusUploading) && x.ExpiresAtUtc > now), cancellationToken);
            if (!hasMetadata && !hasProtectedOwner && await storage.ExistsAsync(objectPath, cancellationToken))
            {
                if (session.RetentionPolicyId is not Guid policy || session.ContentType is null ||
                    !await VerifyCiphertextAsync(objectPath, session.ExpectedBytes, session.ExpectedSha256, cancellationToken))
                {
                    return false;
                }
                // A crash may have published bytes before metadata committed. Preserve deletion intent
                // as an immutable object tombstone before any physical deletion; never reuse its identity.
                var orphan = EncryptedObjectRecord.Create(session.TargetEntityId, session.ShopId, session.UploaderPrincipalId,
                    session.Purpose, session.ExpectedBytes, session.ContentType, session.ExpectedSha256, objectPath,
                    policy, session.LocalManifestAuthenticationOpaque, session.CreatedAtUtc, session.RetentionUntilUtc, false);
                orphan.Tombstone(now);
                db.EncryptedObjects.Add(orphan);
                RecordTransition(session.Id, "storage.unpublished_object_tombstoned", "UNPUBLISHED_OBJECT_RECONCILED", now);
                await db.SaveChangesAsync(cancellationToken);
                orphanTombstonePath = objectPath;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        if (orphanTombstonePath is not null)
        {
            await storage.DeleteCiphertextAsync(orphanTombstonePath, cancellationToken);
        }
        return true;
    }

    public async Task<bool> ReconcileTombstoneAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var record = await db.EncryptedObjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsTombstoned, cancellationToken);
        if (record is null)
        {
            return true;
        }
        await StorageConcurrency.LockObjectAsync(db, record.Id, cancellationToken);
        record = await db.EncryptedObjects.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        if (record.IsUnderLegalHold || record.RetentionUntilUtc is not DateTimeOffset retainedUntil || retainedUntil > now)
        {
            return false;
        }
        await storage.DeleteCiphertextAsync(record.StoragePath, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> VerifyCiphertextAsync(string path, long length, string digest, CancellationToken cancellationToken)
    {
        if (!await storage.ExistsAsync(path, cancellationToken))
        {
            return false;
        }
        await using var content = await storage.GetCiphertextAsync(path, cancellationToken);
        return content.Length == length && string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken)), digest, StringComparison.OrdinalIgnoreCase);
    }

    private void RecordTransition(Guid id, string action, string reason, DateTimeOffset now) =>
        db.CentralAuditRecords.Add(CentralAuditRecord.Create("central.storage.reconciler", null, null,
            action, "upload", id.ToString("D"), reason, Guid.NewGuid().ToString("D"), null, null, null, "{}", true, now));
}
