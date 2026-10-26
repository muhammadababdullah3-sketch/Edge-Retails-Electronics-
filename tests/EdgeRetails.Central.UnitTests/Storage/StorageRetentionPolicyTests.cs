using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Storage;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Storage;

public sealed class StorageRetentionPolicyTests
{
    private static EncryptedObjectRecord CreateObject(DateTimeOffset? retainUntil, bool legalHold) =>
        EncryptedObjectRecord.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EncryptedObjectRecord.PurposeEncryptedBackup,
            12, "application/octet-stream", new string('a', 64), "objects/ciphertext.bin", Guid.NewGuid(), null,
            DateTimeOffset.UtcNow, retainUntil, legalHold);

    [Fact]
    public void Tombstone_BeforeRetentionExpiry_IsDenied()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(1);
        var record = CreateObject(expiresAt, false);

        var exception = Assert.Throws<CentralDomainException>(() => record.Tombstone(DateTimeOffset.UtcNow));

        Assert.Equal("RETENTION_PERIOD_ACTIVE", exception.Code);
        Assert.False(record.IsTombstoned);
    }

    [Fact]
    public void Tombstone_AfterRetentionExpiry_IsAllowed()
    {
        var record = CreateObject(DateTimeOffset.UtcNow.AddDays(-1), false);

        record.Tombstone(DateTimeOffset.UtcNow);

        Assert.True(record.IsTombstoned);
    }

    [Fact]
    public void Tombstone_UnderLegalHold_IsDeniedEvenAfterRetentionExpiry()
    {
        var record = CreateObject(DateTimeOffset.UtcNow.AddDays(-1), true);

        var exception = Assert.Throws<CentralDomainException>(() => record.Tombstone(DateTimeOffset.UtcNow));

        Assert.Equal("LEGAL_HOLD_ACTIVE", exception.Code);
        Assert.False(record.IsTombstoned);
    }
}
