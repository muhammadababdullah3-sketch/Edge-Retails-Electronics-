using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Storage;

public sealed class StorageCustodyRemediationTests
{
    [Fact]
    public void EncryptedBackupSession_PersistsOpaqueValuesAndAllowsLegacyNullableMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        const string opaque = "  Opaque:v1/Å\t";
        var session = UploadSession.Create(
            UploadSession.PurposeEncryptedBackup,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            42,
            new string('a', 64),
            "pending.bin",
            now.AddHours(1),
            now,
            "application/octet-stream",
            Guid.NewGuid(),
            opaque);

        Assert.Equal(opaque, session.LocalManifestAuthenticationOpaque);
        Assert.Equal("application/octet-stream", session.ContentType);
        Assert.NotNull(session.RetentionPolicyId);
        Assert.True(session.LocalManifestAuthenticationOpaque!.Length <= 512);
    }

    [Fact]
    public void UploadSessionModel_UsesNullableMetadataColumnsForUpgradeCompatibility()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase($"StorageMetadata_{Guid.NewGuid():N}")
            .Options;
        using var context = new CentralDbContext(options);
        var entity = context.Model.FindEntityType(typeof(UploadSession));
        Assert.NotNull(entity);
        Assert.True(entity!.FindProperty(nameof(UploadSession.ContentType))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(UploadSession.RetentionPolicyId))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(UploadSession.LocalManifestAuthenticationOpaque))!.IsNullable);
        Assert.Equal(128, entity.FindProperty(nameof(UploadSession.ContentType))!.GetMaxLength());
        Assert.Equal(512, entity.FindProperty(nameof(UploadSession.LocalManifestAuthenticationOpaque))!.GetMaxLength());
    }

    [Fact]
    public async Task RestoreRehearsalService_RejectsWithoutAcceptingAnyCustomerKeyOrLookingUpObjects()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase($"RestoreFailClosed_{Guid.NewGuid():N}")
            .Options;
        await using var context = new CentralDbContext(options);
        var storage = new DiskObjectStorageProvider(Path.Combine(Path.GetTempPath(), $"central_restore_test_{Guid.NewGuid():N}"));
        var ledger = new ThrowingLedger();
        var service = new Wave3StorageAndSupportService(context, storage, ledger);

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() => service.RehearseRestoreAsync());
        Assert.Equal("CUSTOMER_RESTORE_NOT_SUPPORTED", ex.Code);
        Assert.False(context.EncryptedObjects.Any());
    }

    private sealed class ThrowingLedger : EdgeRetails.Central.Application.Abstractions.IOperationLedgerService
    {
        public Task<EdgeRetails.Central.Application.DTOs.OutcomeDto> ExecuteOrReplayAsync<TCommand, TResult>(
            string actorScope,
            Guid? shopId,
            EdgeRetails.Central.Application.DTOs.CommandMetaDto meta,
            TCommand payload,
            Func<TCommand, Task<(TResult Result, string? ResultReference, int ResourceVersion, int AuthorityRevision)>> executeAsync,
            string? targetResource = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(EdgeRetails.Central.Application.DTOs.OutcomeDto Outcome, TResult Result)> ExecuteAndCaptureOrReplayAsync<TCommand, TResult>(
            string actorScope,
            Guid? shopId,
            EdgeRetails.Central.Application.DTOs.CommandMetaDto meta,
            TCommand payload,
            Func<TCommand, Task<(TResult Result, string? ResultReference, int ResourceVersion, int AuthorityRevision)>> executeAsync,
            Func<EdgeRetails.Central.Domain.Operations.CentralOperationRecord, Task<TResult>>? recoverResultAsync = null,
            string? targetResource = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EdgeRetails.Central.Application.DTOs.OperationStatusDto?> GetOperationStatusAsync(Guid clientOperationId, string actorScope, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EdgeRetails.Central.Application.DTOs.OutcomeDto?> GetOutcomeAsync(Guid clientOperationId, string actorScope, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EdgeRetails.Central.Application.DTOs.OutcomeDto?> GetLicensingOutcomeAsync(Guid clientOperationId, string? shopScope, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
