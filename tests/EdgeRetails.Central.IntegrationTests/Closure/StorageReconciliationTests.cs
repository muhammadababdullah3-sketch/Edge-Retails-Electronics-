using System.Security.Cryptography;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.Central.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EdgeRetails.Central.IntegrationTests.Closure;

public sealed class StorageReconciliationTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4, 5];
    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Bytes));
    private static CentralDbContext Open() => new CentralDbContextFactory().CreateDbContext([]);
    private static DiskObjectStorageProvider Storage() => new(Environment.GetEnvironmentVariable("CENTRAL_OBJECT_STORAGE_ROOT"));
    private static string Stage(UploadSession session) => Path.Combine(Environment.GetEnvironmentVariable("CENTRAL_OBJECT_STORAGE_ROOT")!, ".uploads", session.ShopId.ToString("N"), $"{session.Id:N}.part");

    [Fact]
    public async Task ExpiredDownloadGrantCleanup_IsBoundedAndPreservesLiveCapabilities()
    {
        var now = DateTimeOffset.UtcNow;
        var grants = new[]
        {
            ObjectDownloadGrant.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray())), now.AddMinutes(-2), now.AddMinutes(-1)),
            ObjectDownloadGrant.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray())), now.AddMinutes(-2), now.AddSeconds(-30)),
            ObjectDownloadGrant.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray())), now, now.AddMinutes(1))
        };
        await using (var seed = Open())
        {
            seed.ObjectDownloadGrants.AddRange(grants);
            await seed.SaveChangesAsync();
        }

        int expiredBefore;
        await using (var snapshot = Open())
        {
            expiredBefore = await snapshot.ObjectDownloadGrants.CountAsync(x => x.ExpiresAtUtc <= now);
        }
        await using (var first = Open())
        {
            Assert.Equal(1, await new StorageReconciliationService(first, Storage()).CleanupExpiredDownloadGrantsAsync(now, 1));
        }
        await using (var middle = Open())
        {
            Assert.Equal(expiredBefore - 1, await middle.ObjectDownloadGrants.CountAsync(x => x.ExpiresAtUtc <= now));
            Assert.True(await middle.ObjectDownloadGrants.AnyAsync(x => x.Id == grants[2].Id));
        }
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var cleanup = Open();
            var remainingTarget = await cleanup.ObjectDownloadGrants.CountAsync(x => grants.Take(2).Select(g => g.Id).Contains(x.Id));
            if (remainingTarget == 0)
            {
                break;
            }
            Assert.Equal(1, await new StorageReconciliationService(cleanup, Storage()).CleanupExpiredDownloadGrantsAsync(now, 1));
        }
        await using var verify = Open();
        Assert.Equal(0, await verify.ObjectDownloadGrants.CountAsync(x => grants.Take(2).Select(g => g.Id).Contains(x.Id)));
        Assert.True(await verify.ObjectDownloadGrants.AnyAsync(x => x.Id == grants[2].Id));
    }

    private static UploadSession NewSession(DateTimeOffset now, bool expired = false, bool hold = false, bool retained = false) => UploadSession.Create(
        UploadSession.PurposeEncryptedBackup, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Bytes.Length, Digest,
        "synthetic-pending.bin", expired ? now.AddMinutes(-1) : now.AddHours(1), now.AddDays(-2),
        "application/octet-stream", Guid.NewGuid(), null, retained ? now.AddDays(1) : now.AddHours(-1), hold);

    [Fact]
    public async Task RecreatedServices_ReconcileTailFromPersistedOffset_WithoutChangingAcknowledgedBytes()
    {
        var now = DateTimeOffset.UtcNow;
        var session = NewSession(now);
        session.AppendChunk(3);
        await using (var seed = Open())
        {
            seed.UploadSessions.Add(session);
            await seed.SaveChangesAsync();
        }
        await Storage().AppendUploadChunkAsync(session.ShopId, session.Id, 0, 0, new byte[] { 1, 2, 3, 99, 99 });
        await using (var fresh = Open())
        {
            Assert.True(await new StorageReconciliationService(fresh, Storage()).ReconcileUploadAsync(session.Id, now));
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Stage(session)));
        await using var verify = Open();
        var persisted = await verify.UploadSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(3, persisted.AcceptedOffset);
        Assert.Equal(UploadSession.StatusUploading, persisted.Status);
    }

    [Fact]
    public async Task MissingStage_FailsClosedAndPreservesPersistedAcknowledgement()
    {
        var now = DateTimeOffset.UtcNow;
        var session = NewSession(now);
        session.AppendChunk(3);
        await using (var seed = Open())
        {
            seed.UploadSessions.Add(session);
            await seed.SaveChangesAsync();
        }
        try
        {
            await using (var fresh = Open())
            {
                var failure = await Assert.ThrowsAsync<CentralDomainException>(() => new StorageReconciliationService(fresh, Storage()).ReconcileUploadAsync(session.Id, now));
                Assert.Equal("UPLOAD_STORAGE_INCONSISTENT", failure.Code);
            }
            await using var verify = Open();
            var persisted = await verify.UploadSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
            Assert.Equal(3, persisted.AcceptedOffset);
            Assert.Equal(UploadSession.StatusUploading, persisted.Status);
        }
        finally
        {
            // Preserve the negative assertion, then transition the synthetic orphan through
            // the audited expiry path so recovery sees no uploading session with missing bytes.
            await using var cleanup = Open();
            var leftover = await cleanup.UploadSessions.SingleOrDefaultAsync(x => x.Id == session.Id);
            if (leftover is not null)
            {
                Assert.True(await new StorageReconciliationService(cleanup, Storage()).ReconcileUploadAsync(session.Id, now.AddDays(1)));
                var expired = await cleanup.UploadSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
                Assert.Equal(UploadSession.StatusExpired, expired.Status);
            }
        }
    }
    [Fact]
    public async Task ExpiredStagingCleanup_PersistsExpiryAuditAndPreservesHoldAndRetention()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = NewSession(now, expired: true);
        var held = NewSession(now, expired: true, hold: true);
        var retained = NewSession(now, expired: true, retained: true);
        var sessions = new[] { expired, held, retained };
        await using (var seed = Open())
        {
            foreach (var session in sessions)
            {
                session.AppendChunk(Bytes.Length);
                seed.UploadSessions.Add(session);
                await Storage().AppendUploadChunkAsync(session.ShopId, session.Id, 0, 0, Bytes);
            }
            await seed.SaveChangesAsync();
        }
        foreach (var session in sessions)
        {
            await using var fresh = Open();
            Assert.True(await new StorageReconciliationService(fresh, Storage()).ReconcileUploadAsync(session.Id, now));
        }
        Assert.False(File.Exists(Stage(expired)));
        Assert.True(File.Exists(Stage(held)));
        Assert.True(File.Exists(Stage(retained)));
        await using (var repeat = Open())
        {
            Assert.True(await new StorageReconciliationService(repeat, Storage()).ReconcileUploadAsync(expired.Id, now));
        }
        await using var verify = Open();
        foreach (var session in sessions)
        {
            Assert.Equal(UploadSession.StatusExpired, (await verify.UploadSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
            Assert.Equal(1, await verify.CentralAuditRecords.CountAsync(x => x.Action == "storage.upload_expired" && x.ResourceId == session.Id.ToString("D")));
        }
    }

    [Fact]
    public async Task FinalizedStageAndCommittedTombstone_AreReconciledIdempotently()
    {
        var now = DateTimeOffset.UtcNow;
        var session = NewSession(now);
        var path = await Storage().SaveCiphertextAsync(session.ShopId, session.TargetEntityId, new MemoryStream(Bytes));
        session.AppendChunk(Bytes.Length);
        session.FinalizeUpload(Digest);
        session.SetStoragePath(path);
        var record = EncryptedObjectRecord.Create(session.TargetEntityId, session.ShopId, session.UploaderPrincipalId,
            EncryptedObjectRecord.PurposeEncryptedBackup, Bytes.Length, "application/octet-stream", Digest, path,
            session.RetentionPolicyId!.Value, null, now.AddDays(-2), now.AddHours(-1));
        await using (var seed = Open())
        {
            seed.UploadSessions.Add(session);
            seed.EncryptedObjects.Add(record);
            await seed.SaveChangesAsync();
        }
        await Storage().AppendUploadChunkAsync(session.ShopId, session.Id, 0, 0, Bytes);
        await using (var fresh = Open())
        {
            Assert.True(await new StorageReconciliationService(fresh, Storage()).ReconcileUploadAsync(session.Id, now));
            var persisted = await fresh.EncryptedObjects.SingleAsync(x => x.Id == record.Id);
            persisted.Tombstone(now);
            await fresh.SaveChangesAsync();
        }
        Assert.False(File.Exists(Stage(session)));
        await using (var fresh = Open())
        {
            var service = new StorageReconciliationService(fresh, Storage());
            Assert.True(await service.ReconcileTombstoneAsync(record.Id, now));
            Assert.True(await service.ReconcileTombstoneAsync(record.Id, now));
        }
        Assert.False(File.Exists(path));
        await using var verify = Open();
        Assert.True((await verify.EncryptedObjects.AsNoTracking().SingleAsync(x => x.Id == record.Id)).IsTombstoned);
    }

    [Fact]
    public async Task TombstoneCleanup_EnforcesPersistedLegalHoldAndRetentionAcrossServiceRestarts()
    {
        var now = DateTimeOffset.UtcNow;
        var shopId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var path = await Storage().SaveCiphertextAsync(shopId, objectId, new MemoryStream(Bytes));
        var record = EncryptedObjectRecord.Create(objectId, shopId, Guid.NewGuid(),
            EncryptedObjectRecord.PurposeEncryptedBackup, Bytes.Length, "application/octet-stream", Digest, path,
            Guid.NewGuid(), null, now.AddDays(-2), now.AddHours(-1));
        record.Tombstone(now);
        await using (var seed = Open())
        {
            seed.EncryptedObjects.Add(record);
            await seed.SaveChangesAsync();
            // Simulate a persisted hold applied after deletion intent was recorded. The reconciler
            // must honor authoritative persisted state even for legacy/inconsistent rows.
            await seed.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE central.encrypted_objects SET \"IsUnderLegalHold\" = TRUE WHERE \"Id\" = {objectId}");
        }

        await using (var held = Open())
        {
            Assert.False(await new StorageReconciliationService(held, Storage()).ReconcileTombstoneAsync(objectId, now));
        }
        Assert.True(File.Exists(path));

        await using (var releaseHold = Open())
        {
            await releaseHold.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE central.encrypted_objects SET \"IsUnderLegalHold\" = FALSE, \"RetentionUntilUtc\" = {now.AddHours(1)} WHERE \"Id\" = {objectId}");
        }
        await using (var retained = Open())
        {
            Assert.False(await new StorageReconciliationService(retained, Storage()).ReconcileTombstoneAsync(objectId, now));
        }
        Assert.True(File.Exists(path));

        await using (var retentionElapsed = Open())
        {
            await retentionElapsed.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE central.encrypted_objects SET \"RetentionUntilUtc\" = {now.AddSeconds(-1)} WHERE \"Id\" = {objectId}");
        }
        await using (var eligible = Open())
        {
            Assert.True(await new StorageReconciliationService(eligible, Storage()).ReconcileTombstoneAsync(objectId, now));
        }
        Assert.False(File.Exists(path));
        await using var verify = Open();
        var persisted = await verify.EncryptedObjects.AsNoTracking().SingleAsync(x => x.Id == objectId);
        Assert.True(persisted.IsTombstoned);
        Assert.False(persisted.IsUnderLegalHold);
        var expectedRetentionUtc = now.AddSeconds(-1);
        var expectedPostgresTimestamp = expectedRetentionUtc.AddTicks(-(expectedRetentionUtc.UtcTicks % 10));
        Assert.Equal(expectedPostgresTimestamp, persisted.RetentionUntilUtc);
    }

    [Fact]
    public async Task CorruptPermanentCiphertext_PreservesRecoverableStagingAndReportsInconsistency()
    {
        var now = DateTimeOffset.UtcNow;
        var session = NewSession(now);
        var path = await Storage().SaveCiphertextAsync(session.ShopId, session.TargetEntityId, new MemoryStream(Bytes));
        session.AppendChunk(Bytes.Length);
        session.FinalizeUpload(Digest);
        session.SetStoragePath(path);
        await using (var seed = Open())
        {
            seed.UploadSessions.Add(session);
            seed.EncryptedObjects.Add(EncryptedObjectRecord.Create(session.TargetEntityId, session.ShopId, session.UploaderPrincipalId,
                session.Purpose, Bytes.Length, "application/octet-stream", Digest, path, session.RetentionPolicyId!.Value, null, now, now.AddDays(1)));
            await seed.SaveChangesAsync();
        }
        await Storage().AppendUploadChunkAsync(session.ShopId, session.Id, 0, 0, Bytes);
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 9, 9, 9, 9, 9 });
            await using var fresh = Open();
            Assert.False(await new StorageReconciliationService(fresh, Storage()).ReconcileUploadAsync(session.Id, now));
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(Stage(session)));
        }
        finally
        {
            await File.WriteAllBytesAsync(path, Bytes);
        }
    }

    [Theory]
    [InlineData(false, false, false, true, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, false, true)]
    public async Task UnpublishedPermanentObject_RequiresExpiredRetentionAndNoProtectedOwnerBeforeDurableTombstone(
        bool hold, bool retained, bool protectedOwner, bool shouldDelete, bool crossShopOwner)
    {
        var now = DateTimeOffset.UtcNow;
        var session = NewSession(now, expired: true, hold: hold, retained: retained);
        session.AppendChunk(Bytes.Length);
        var path = await Storage().SaveCiphertextAsync(session.ShopId, session.TargetEntityId, new MemoryStream(Bytes));
        await using (var seed = Open())
        {
            seed.UploadSessions.Add(session);
            if (protectedOwner || crossShopOwner)
            {
                seed.UploadSessions.Add(UploadSession.Create(session.Purpose, crossShopOwner ? Guid.NewGuid() : session.ShopId, Guid.NewGuid(), session.TargetEntityId,
                    Bytes.Length, Digest, "synthetic-pending.bin", now.AddHours(1), now.AddHours(-1), "application/octet-stream",
                    session.RetentionPolicyId, null, now.AddDays(1)));
            }
            await seed.SaveChangesAsync();
        }
        await using (var fresh = Open())
        {
            Assert.True(await new StorageReconciliationService(fresh, Storage()).ReconcileUploadAsync(session.Id, now));
        }
        Assert.Equal(!shouldDelete, File.Exists(path));
        await using var verify = Open();
        var record = await verify.EncryptedObjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.TargetEntityId);
        if (shouldDelete)
        {
            Assert.NotNull(record);
            Assert.True(record.IsTombstoned);
            Assert.Equal(Digest, record.CiphertextSha256);
            Assert.Equal(1, await verify.CentralAuditRecords.CountAsync(x => x.ResourceId == session.Id.ToString("D") && x.Action == "storage.unpublished_object_tombstoned"));
        }
        else
        {
            Assert.Null(record);
        }
    }

    [Fact]
    public async Task ConcurrentInitAndOrphanCleanup_ShareObjectLockAndPreserveNewProtectedOwner()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = NewSession(now, expired: true);
        expired.AppendChunk(Bytes.Length);
        using var key = RSA.Create(2048);
        var installation = Installation.Register(expired.ShopId, Guid.NewGuid(), "synthetic-concurrent-storage", key.ExportRSAPublicKeyPem(), "synthetic", "windows", now);
        installation.Approve(now);
        await using (var seed = Open())
        {
            seed.UploadSessions.Add(expired);
            seed.Installations.Add(installation);
            await seed.SaveChangesAsync();
        }
        var path = await Storage().SaveCiphertextAsync(expired.ShopId, expired.TargetEntityId, new MemoryStream(Bytes));
        var initName = $"storage-init-{Guid.NewGuid():N}";
        var reconcileName = $"storage-reconcile-{Guid.NewGuid():N}";
        await using var initDb = Open();
        initDb.Database.SetConnectionString(new NpgsqlConnectionStringBuilder(initDb.Database.GetConnectionString()) { ApplicationName = initName }.ConnectionString);
        await using var reconcileDb = Open();
        reconcileDb.Database.SetConnectionString(new NpgsqlConnectionStringBuilder(reconcileDb.Database.GetConnectionString()) { ApplicationName = reconcileName }.ConnectionString);
        await using var observer = Open();
        await observer.Database.OpenConnectionAsync();
        async Task WaitForObjectLock(string applicationName)
        {
            using var query = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name=@name AND wait_event_type='Lock'", (NpgsqlConnection)observer.Database.GetDbConnection());
            query.Parameters.AddWithValue("name", applicationName);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            do
            {
                if ((long)(await query.ExecuteScalarAsync())! == 1)
                {
                    return;
                }
                await Task.Delay(25);
            } while (DateTimeOffset.UtcNow < deadline);
            throw new TimeoutException("Expected actual PostgreSQL lock waiter was not observed.");
        }
        await using var blocker = Open();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        var lockKey = $"storage-object:{expired.TargetEntityId:D}";
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 2))");
        var initService = new Wave3StorageAndSupportService(initDb, Storage(), new OperationLedgerService(initDb, new CanonicalJsonSerializer()));
        var initTask = initService.InitUploadAsync(new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Synthetic concurrent initialization" },
            new UploadInitDto { ShopId = expired.ShopId, InstallationId = installation.Id, ObjectId = expired.TargetEntityId,
                Purpose = expired.Purpose, ContentLength = Bytes.Length, ContentType = "application/octet-stream", CiphertextSha256 = Digest,
                RetentionPolicyId = Guid.Parse(Environment.GetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_POLICY_ID")!) },
            $"installation:{installation.Id:D}");
        Task<bool>? reconcileTask = null;
        try
        {
            await WaitForObjectLock(initName);
            reconcileTask = new StorageReconciliationService(reconcileDb, Storage()).ReconcileUploadAsync(expired.Id, now);
            await WaitForObjectLock(reconcileName);
            await transaction.CommitAsync();
            var initialized = await initTask;
            Assert.Equal("SUCCEEDED", initialized.Outcome.State);
            Assert.True(await reconcileTask);
        }
        finally
        {
            await transaction.DisposeAsync();
            try { await initTask; } catch { }
            if (reconcileTask is not null)
            {
                try { await reconcileTask; } catch { }
            }
        }
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(path));
        await using var verify = Open();
        Assert.False(await verify.EncryptedObjects.AnyAsync(x => x.Id == expired.TargetEntityId));
        Assert.True(await verify.UploadSessions.AnyAsync(x => x.TargetEntityId == expired.TargetEntityId && x.Id != expired.Id && x.ExpiresAtUtc > now));
    }

    [Theory]
    [InlineData("digest", "SHA256_MISMATCH")]
    [InlineData("incomplete", "INCOMPLETE_UPLOAD")]
    [InlineData("oversize", "OVERSIZE_UPLOAD")]
    public async Task FinalizeIntegrityFailure_PersistsQuarantineOutsideRolledBackOperation(string scenario, string expectedCode)
    {
        var session = NewSession(DateTimeOffset.UtcNow);
        if (scenario == "digest")
        {
            session.AppendChunk(Bytes.Length);
            await Storage().AppendUploadChunkAsync(session.ShopId, session.Id, 0, 0, new byte[] { 9, 9, 9, 9, 9 });
        }
        await using (var seed = Open())
        {
            seed.UploadSessions.Add(session);
            await seed.SaveChangesAsync();
        }
        var operationId = Guid.NewGuid();
        await using (var execute = Open())
        {
            var ledger = new OperationLedgerService(execute, new CanonicalJsonSerializer());
            var service = new Wave3StorageAndSupportService(execute, Storage(), ledger);
            await using var body = new MemoryStream(scenario == "incomplete" ? new byte[] { 1, 2, 3 } : new byte[] { 1, 2, 3, 4, 5, 6 });
            var failure = await Assert.ThrowsAsync<CentralDomainException>(() => service.FinalizeUploadAsync(
                new CommandMetaDto { ClientOperationId = operationId, Reason = "Synthetic integrity failure" },
                new UploadFinalizeDto { UploadId = session.Id, ObjectId = session.TargetEntityId, ExpectedLength = Bytes.Length, ExpectedSha256 = Digest },
                body, $"installation:{session.UploaderPrincipalId:D}"));
            Assert.Equal(expectedCode, failure.Code);
        }
        await using var verify = Open();
        Assert.Equal(UploadSession.StatusQuarantined, (await verify.UploadSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
        Assert.False(await verify.EncryptedObjects.AnyAsync(x => x.Id == session.TargetEntityId));
        Assert.False(await verify.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == operationId));
        Assert.Equal(1, await verify.CentralAuditRecords.CountAsync(x => x.ResourceId == session.Id.ToString("D") && x.Action == "storage.upload_quarantined" && x.Reason == expectedCode));
    }
}
