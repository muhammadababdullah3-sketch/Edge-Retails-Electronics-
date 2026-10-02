using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Production.Backup;

namespace EdgeRetails.UnitTests;

public sealed class Phase3RestoreRecoveryStoreTests
{
    [Fact]
    public async Task OperationLookupAndRecoverableList_ReturnSafeMetadataFromAuthenticatedJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase3RestoreStore_" + Guid.NewGuid().ToString("N"));
        var store = new HmacRestoreSessionStore(root, new FixedJournalKeyProvider());
        var operationId = Guid.NewGuid();
        var session = new RestoreSessionRecord(
            Guid.NewGuid(),
            "production-secret-db",
            "staging-secret-db",
            101,
            0,
            Path.Combine(root, "private-backup.erbak"),
            new string('A', 64),
            DateTimeOffset.UtcNow,
            RestoreSessionState.Preparing,
            ClientOperationId: operationId);

        try
        {
            await store.CreateAsync(session);

            var lookedUp = await store.GetSummaryByOperationIdAsync(operationId);
            Assert.NotNull(lookedUp);
            Assert.Equal(session.RestoreId, lookedUp.RestoreId);
            Assert.Equal(operationId, lookedUp.ClientOperationId);
            Assert.Equal(RestoreSessionState.Preparing, lookedUp.State);

            var summaries = await store.ListRecoverableAsync();
            var summary = Assert.Single(summaries);
            Assert.Equal(session.RestoreId, summary.RestoreId);
            Assert.Equal(operationId, summary.ClientOperationId);
            Assert.Equal(RestoreSessionState.Preparing, summary.State);

            var exposedProperties = typeof(RestoreSessionSummary).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain("TargetDatabase", exposedProperties);
            Assert.DoesNotContain("StagingDatabase", exposedProperties);
            Assert.DoesNotContain("BackupFilePath", exposedProperties);
            Assert.DoesNotContain("VerifiedSha256", exposedProperties);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.UpdateAsync(session with { ClientOperationId = Guid.NewGuid() }));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Create_RejectsOperationIdAlreadyBoundToAnotherRestore()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase3RestoreStore_" + Guid.NewGuid().ToString("N"));
        var store = new HmacRestoreSessionStore(root, new FixedJournalKeyProvider());
        var operationId = Guid.NewGuid();
        RestoreSessionRecord NewSession() => new(
            Guid.NewGuid(),
            "prod",
            "stage",
            1,
            0,
            "backup.erbak",
            new string('B', 64),
            DateTimeOffset.UtcNow,
            RestoreSessionState.Preparing,
            ClientOperationId: operationId);

        try
        {
            await store.CreateAsync(NewSession());
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(NewSession()));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class FixedJournalKeyProvider : IRestoreJournalIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
    }
}
