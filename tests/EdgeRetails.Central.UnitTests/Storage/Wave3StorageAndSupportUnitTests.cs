using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.Central.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Storage;

public class Wave3StorageAndSupportUnitTests
{
    private readonly CentralDbContext _dbContext;
    private readonly DiskObjectStorageProvider _storageProvider;
    private readonly OperationLedgerService _ledger;
    private readonly Wave3StorageAndSupportService _service;

    public Wave3StorageAndSupportUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(databaseName: $"CentralWave3TestDb_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbContext = new CentralDbContext(options);
        var testStoragePath = Path.Combine(Path.GetTempPath(), $"central_test_storage_{Guid.NewGuid():N}");
        _storageProvider = new DiskObjectStorageProvider(testStoragePath);
        var serializer = new CanonicalJsonSerializer();
        _ledger = new OperationLedgerService(_dbContext, serializer);
        _service = new Wave3StorageAndSupportService(_dbContext, _storageProvider, _ledger);
    }

    [Fact]
    public async Task CONE_T15_InitUpload_ValidPayload_ReturnsAuthorizedSession()
    {
        var customerId = Guid.NewGuid();
        var shop = Shop.Create(customerId, "SHOP01", "Test Shop", DateTimeOffset.UtcNow);
        var shopId = shop.Id;

        var installation = Installation.Register(shopId, Guid.NewGuid(), "FINGERPRINT123", "-----BEGIN PUBLIC KEY-----\nMIIB\n-----END PUBLIC KEY-----", "host", "linux", DateTimeOffset.UtcNow);
        var installationId = installation.Id;

        _dbContext.Shops.Add(shop);
        _dbContext.Installations.Add(installation);
        await _dbContext.SaveChangesAsync();

        var objectId = Guid.NewGuid();
        var meta = new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Init upload" };
        var dto = new UploadInitDto
        {
            ShopId = shopId,
            InstallationId = installationId,
            ObjectId = objectId,
            Purpose = EncryptedObjectRecord.PurposeEncryptedBackup,
            ContentLength = 1024,
            ContentType = "application/octet-stream",
            CiphertextSha256 = new string('a', 64),
            LocalManifestAuthenticationOpaque = "  Opaque:v1/Å  ",
            RetentionPolicyId = Guid.NewGuid()
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.InitUploadAsync(meta, dto, "installation"));

        Assert.Equal("application/octet-stream", dto.ContentType);
        Assert.NotEqual(Guid.Empty, dto.RetentionPolicyId);
        Assert.Equal("  Opaque:v1/Å  ", dto.LocalManifestAuthenticationOpaque);
        Assert.Equal("RETENTION_POLICY_AUTHORITY_NOT_ACTIVE", ex.Code);
        Assert.Empty(await _dbContext.UploadSessions.ToListAsync());
    }

    [Fact]
    public async Task SFR16_InitUpload_WithoutConfiguredRetentionPolicyFailsClosed()
    {
        var customerId = Guid.NewGuid();
        var shop = Shop.Create(customerId, "SHOP-MISSING-POLICY", "Test Shop", DateTimeOffset.UtcNow);
        var installation = Installation.Register(shop.Id, Guid.NewGuid(), "FP-MISSING-POLICY", "-----BEGIN PUBLIC KEY-----\\nMIIB\\n-----END PUBLIC KEY-----", "host", "linux", DateTimeOffset.UtcNow);
        _dbContext.Shops.Add(shop);
        _dbContext.Installations.Add(installation);
        await _dbContext.SaveChangesAsync();
        var dto = new UploadInitDto
        {
            ShopId = shop.Id,
            InstallationId = installation.Id,
            ObjectId = Guid.NewGuid(),
            Purpose = EncryptedObjectRecord.PurposeEncryptedBackup,
            ContentLength = 32,
            ContentType = "application/octet-stream",
            CiphertextSha256 = new string('a', 64),
            RetentionPolicyId = Guid.NewGuid()
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() => _service.InitUploadAsync(
            new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Missing policy authority" }, dto, "installation"));

        Assert.Equal("RETENTION_POLICY_AUTHORITY_NOT_ACTIVE", ex.Code);
        Assert.Empty(await _dbContext.UploadSessions.ToListAsync());
    }

    [Fact]
    public void SFR16_UploadSession_PreservesOpaqueMetadataExactly()
    {
        const string opaque = "  Opaque:v1/Å\t";
        var createdAt = DateTimeOffset.UtcNow;
        var session = UploadSession.Create(
            UploadSession.PurposeEncryptedBackup,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            expectedBytes: 8,
            expectedSha256: new string('a', 64),
            storagePath: "pending.bin",
            expiresAtUtc: createdAt.AddHours(1),
            createdAtUtc: createdAt,
            contentType: "application/octet-stream",
            retentionPolicyId: Guid.NewGuid(),
            localManifestAuthenticationOpaque: opaque);

        Assert.Equal(opaque, session.LocalManifestAuthenticationOpaque);
    }

    [Theory]
    [InlineData("not-a-hex-digest")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void SFR16_UploadSession_RejectsInvalidSha256(string digest)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var ex = Assert.Throws<CentralDomainException>(() => UploadSession.Create(
            UploadSession.PurposeEncryptedBackup,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            expectedBytes: 8,
            expectedSha256: digest,
            storagePath: "pending.bin",
            expiresAtUtc: createdAt.AddHours(1),
            createdAtUtc: createdAt));

        Assert.Equal("INVALID_SHA256", ex.Code);
    }

    [Fact]
    public void SFR16_UploadSession_RejectsOpaqueMetadataOverColumnLimit()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var ex = Assert.Throws<CentralDomainException>(() => UploadSession.Create(
            UploadSession.PurposeEncryptedBackup,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            expectedBytes: 8,
            expectedSha256: new string('a', 64),
            storagePath: "pending.bin",
            expiresAtUtc: createdAt.AddHours(1),
            createdAtUtc: createdAt,
            contentType: "application/octet-stream",
            retentionPolicyId: Guid.NewGuid(),
            localManifestAuthenticationOpaque: new string('x', 513)));

        Assert.Equal("INVALID_MANIFEST_METADATA", ex.Code);
    }

    [Fact]
    public async Task CONE_T15_InitUpload_Oversize_FailsClosed()
    {
        var customerId = Guid.NewGuid();
        var shop = Shop.Create(customerId, "SHOP02", "Test Shop", DateTimeOffset.UtcNow);
        var shopId = shop.Id;

        var installation = Installation.Register(shopId, Guid.NewGuid(), "FP123", "-----BEGIN PUBLIC KEY-----\nMIIB\n-----END PUBLIC KEY-----", "host", "linux", DateTimeOffset.UtcNow);
        var installationId = installation.Id;

        _dbContext.Shops.Add(shop);
        _dbContext.Installations.Add(installation);
        await _dbContext.SaveChangesAsync();

        var meta = new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Init upload" };
        var dto = new UploadInitDto
        {
            ShopId = shopId,
            InstallationId = installationId,
            ObjectId = Guid.NewGuid(),
            Purpose = EncryptedObjectRecord.PurposeEncryptedBackup,
            ContentLength = 101 * 1024 * 1024, // 101 MB exceeds 100MB bound
            ContentType = "application/octet-stream",
            CiphertextSha256 = new string('b', 64),
            RetentionPolicyId = Guid.NewGuid()
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.InitUploadAsync(meta, dto, "installation"));

        Assert.Equal("OVERSIZE_UPLOAD", ex.Code);
    }

    [Fact]
    public async Task CONE_T15_FinalizeUpload_ShaMismatch_FailsClosed()
    {
        var shopId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var objectId = Guid.NewGuid();

        var session = UploadSession.Create(
            UploadSession.PurposeEncryptedBackup,
            shopId,
            installationId,
            objectId,
            expectedBytes: 10,
            expectedSha256: new string('c', 64),
            storagePath: "test.bin",
            expiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            createdAtUtc: DateTimeOffset.UtcNow,
            contentType: "application/octet-stream",
            retentionPolicyId: Guid.NewGuid(),
            localManifestAuthenticationOpaque: null);

        _dbContext.UploadSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        var meta = new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Finalize upload" };
        var dto = new UploadFinalizeDto
        {
            UploadId = session.Id,
            ObjectId = objectId,
            ExpectedLength = 10,
            ExpectedSha256 = new string('d', 64) // mismatched expected sha
        };

        var contentBytes = Encoding.UTF8.GetBytes("0123456789");
        using var stream = new MemoryStream(contentBytes);

        var failure = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.FinalizeUploadAsync(meta, dto, stream, $"installation:{installationId:D}"));
        Assert.Equal("SHA256_MISMATCH", failure.Code);
    }

    [Fact]
    public async Task SFR16_FinalizeUpload_PreservesDeclaredMetadataWithoutAuthenticationClaim()
    {
        var shopId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("ciphertext bytes");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        const string opaque = "  Opaque:v1/Å\t";
        var policyId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var session = UploadSession.Create(
            UploadSession.PurposeEncryptedBackup,
            shopId,
            installationId,
            objectId,
            bytes.Length,
            digest,
            "pending.bin",
            createdAt.AddHours(1),
            createdAt,
            "application/octet-stream",
            policyId,
            opaque);
        _dbContext.UploadSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        var dto = new UploadFinalizeDto
        {
            UploadId = session.Id,
            ObjectId = objectId,
            ExpectedLength = bytes.Length,
            ExpectedSha256 = digest
        };
        using var content = new MemoryStream(bytes);
        var (_, success) = await _service.FinalizeUploadAsync(
            new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Finalize metadata upload" },
            dto,
            content,
            $"installation:{installationId:D}");

        Assert.True(success);
        var stored = await _dbContext.EncryptedObjects.SingleAsync(o => o.Id == objectId);
        Assert.Equal(bytes.Length, stored.ContentLength);
        Assert.Equal("application/octet-stream", stored.ContentType);
        Assert.Equal(policyId, stored.RetentionPolicyId);
        Assert.Equal(opaque, stored.LocalManifestAuthenticationOpaque);
        Assert.NotEqual("HMAC-SHA256:VALIDATED", stored.LocalManifestAuthenticationOpaque);
    }

    [Fact]
    public async Task SFR16_FinalizeUpload_RejectsDifferentDeclaredLengthBeforeReading()
    {
        var session = await CreateUploadSession(expectedBytes: 10, expectedSha256: new string('a', 64));
        var dto = new UploadFinalizeDto { UploadId = session.Id, ObjectId = session.TargetEntityId, ExpectedLength = 9, ExpectedSha256 = session.ExpectedSha256 };
        using var stream = new ReadTrackingStream(Encoding.UTF8.GetBytes("0123456789"));

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() => _service.FinalizeUploadAsync(
            new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Length mismatch" }, dto, stream, $"installation:{session.UploaderPrincipalId:D}"));

        Assert.Equal("UPLOAD_LENGTH_MISMATCH", ex.Code);
        Assert.False(stream.ReadAttempted);
    }

    [Fact]
    public async Task SFR16_FinalizeUpload_ExpiredSessionRejectsBeforeReading()
    {
        var session = await CreateUploadSession(expectedBytes: 10, expectedSha256: new string('a', 64), expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1), createdAtUtc: DateTimeOffset.UtcNow.AddHours(-2));
        var dto = new UploadFinalizeDto { UploadId = session.Id, ObjectId = session.TargetEntityId, ExpectedLength = 10, ExpectedSha256 = session.ExpectedSha256 };
        using var stream = new ReadTrackingStream(Encoding.UTF8.GetBytes("0123456789"));

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() => _service.FinalizeUploadAsync(
            new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Expired upload" }, dto, stream, $"installation:{session.UploaderPrincipalId:D}"));

        Assert.Equal("UPLOAD_EXPIRED", ex.Code);
        Assert.False(stream.ReadAttempted);
    }

    [Fact]
    public async Task SFR16_FinalizeUpload_RejectsLengthMismatchWithoutPersistingSuccess()
    {
        var session = await CreateUploadSession(expectedBytes: 10, expectedSha256: new string('a', 64));
        var dto = new UploadFinalizeDto { UploadId = session.Id, ObjectId = session.TargetEntityId, ExpectedLength = 10, ExpectedSha256 = session.ExpectedSha256 };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("short"));

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() => _service.FinalizeUploadAsync(
            new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Short upload" }, dto, stream, $"installation:{session.UploaderPrincipalId:D}"));

        Assert.Equal("INCOMPLETE_UPLOAD", ex.Code);
        Assert.Empty(await _dbContext.EncryptedObjects.ToListAsync());
    }

    [Fact]
    public async Task SFR16_FinalizeUpload_RejectsOversizedStream()
    {
        var session = await CreateUploadSession(expectedBytes: 10, expectedSha256: new string('a', 64));
        var dto = new UploadFinalizeDto { UploadId = session.Id, ObjectId = session.TargetEntityId, ExpectedLength = 10, ExpectedSha256 = session.ExpectedSha256 };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("0123456789x"));

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() => _service.FinalizeUploadAsync(
            new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Oversize upload" }, dto, stream, $"installation:{session.UploaderPrincipalId:D}"));

        Assert.Equal("OVERSIZE_UPLOAD", ex.Code);
        Assert.Empty(await _dbContext.EncryptedObjects.ToListAsync());
    }

    [Fact]
    public async Task CONE_T16_DownloadCiphertext_CrossShop_FailsClosed()
    {
        var shopA = Guid.NewGuid();
        var shopB = Guid.NewGuid();
        var objectId = Guid.NewGuid();

        var record = EncryptedObjectRecord.Create(
            objectId: objectId,
            shopId: shopA,
            installationId: Guid.NewGuid(),
            purpose: EncryptedObjectRecord.PurposeEncryptedBackup,
            contentLength: 100,
            contentType: "application/octet-stream",
            ciphertextSha256: new string('e', 64),
            storagePath: "shops/a/uploads/obj.bin",
            retentionPolicyId: Guid.NewGuid(),
            localManifestAuthenticationOpaque: "OPAQUE",
            createdAtUtc: DateTimeOffset.UtcNow);

        _dbContext.EncryptedObjects.Add(record);
        await _dbContext.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.DownloadCiphertextAsync(objectId, requestingShopId: shopB));

        Assert.Equal("CROSS_SHOP_ACCESS_DENIED", ex.Code);
    }

    [Fact]
    public async Task CONE_T16_PathTraversal_Rejected()
    {
        var ex = Assert.Throws<CentralDomainException>(() =>
            EncryptedObjectRecord.Create(
                objectId: Guid.NewGuid(),
                shopId: Guid.NewGuid(),
                installationId: Guid.NewGuid(),
                purpose: EncryptedObjectRecord.PurposeEncryptedBackup,
                contentLength: 100,
                contentType: "application/octet-stream",
                ciphertextSha256: new string('f', 64),
                storagePath: "../../../etc/passwd",
                retentionPolicyId: Guid.NewGuid(),
                localManifestAuthenticationOpaque: null,
                createdAtUtc: DateTimeOffset.UtcNow));

        Assert.Equal("PATH_TRAVERSAL_DETECTED", ex.Code);
    }

    [Fact]
    public async Task CONE_T18_CreateSupportTicket_UnredactedSecret_FailsClosed()
    {
        var shop = Shop.Create(Guid.NewGuid(), $"SH_{Guid.NewGuid():N}"[..10], "Support test shop", DateTimeOffset.UtcNow);
        _dbContext.Shops.Add(shop);
        await _dbContext.SaveChangesAsync();
        var meta = new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Create ticket" };
        var dto = new TicketDto
        {
            ShopId = shop.Id,
            Subject = "Need help with database password=secret123",
            Summary = "Error on backup",
            AttachmentObjectIds = new List<Guid>(),
            RedactionProfileVersion = 1
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.CreateSupportTicketAsync(meta, dto, Guid.NewGuid(), "operator"));

        Assert.Equal("SENSITIVE_SECRET_LEAKAGE", ex.Code);
    }

    [Fact]
    public async Task CONE_T18_CreateSupportTicket_CrossShopAttachment_FailsClosed()
    {
        var shopA = Guid.NewGuid();
        var shopB = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();

        var persistedShopA = Shop.Create(Guid.NewGuid(), $"SA_{Guid.NewGuid():N}"[..10], "Support shop A", DateTimeOffset.UtcNow);
        var persistedShopB = Shop.Create(Guid.NewGuid(), $"SB_{Guid.NewGuid():N}"[..10], "Support shop B", DateTimeOffset.UtcNow);
        typeof(Shop).GetProperty("Id")!.SetValue(persistedShopA, shopA);
        typeof(Shop).GetProperty("Id")!.SetValue(persistedShopB, shopB);
        _dbContext.Shops.AddRange(persistedShopA, persistedShopB);

        var foreignAttachment = EncryptedObjectRecord.Create(
            objectId: attachmentId,
            shopId: shopB,
            installationId: Guid.NewGuid(),
            purpose: EncryptedObjectRecord.PurposeSupportAttachment,
            contentLength: 50,
            contentType: "application/octet-stream",
            ciphertextSha256: new string('0', 64),
            storagePath: "shops/b/uploads/att.bin",
            retentionPolicyId: Guid.NewGuid(),
            localManifestAuthenticationOpaque: null,
            createdAtUtc: DateTimeOffset.UtcNow);

        _dbContext.EncryptedObjects.Add(foreignAttachment);
        await _dbContext.SaveChangesAsync();

        var meta = new CommandMetaDto { ClientOperationId = Guid.NewGuid(), Reason = "Create ticket" };
        var dto = new TicketDto
        {
            ShopId = shopA,
            Subject = "Valid Subject",
            Summary = "Valid summary with diagnostic details.",
            AttachmentObjectIds = new List<Guid> { attachmentId },
            RedactionProfileVersion = 1
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.CreateSupportTicketAsync(meta, dto, Guid.NewGuid(), "operator"));

        Assert.Equal("SUPPORT_ATTACHMENT_ACCESS_DENIED", ex.Code);
    }

    [Fact]
    public async Task CONE_T17_RehearseRestore_AlwaysRequiresEdgeVerifier_WithoutLookingUpCustomerKeyOrBackup()
    {
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.RehearseRestoreAsync());

        Assert.Equal("CUSTOMER_RESTORE_NOT_SUPPORTED", ex.Code);
        Assert.Empty(await _dbContext.EncryptedObjects.ToListAsync());
    }

    [Fact]
    public async Task CONE_T17_RehearseRestore_DoesNotRequireOrAcceptCustomerKey()
    {
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _service.RehearseRestoreAsync());

        Assert.Equal("CUSTOMER_RESTORE_NOT_SUPPORTED", ex.Code);
        Assert.Empty(await _dbContext.EncryptedObjects.ToListAsync());
    }

    [Fact]
    public void SFR16_UploadSession_SetStoragePath_RequiresFinalizedBoundedPath()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var session = UploadSession.Create(
            UploadSession.PurposeEncryptedBackup,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new string('a', 64),
            "pending.bin",
            createdAt.AddHours(1),
            createdAt,
            "application/octet-stream",
            Guid.NewGuid());
        var ex = Assert.Throws<CentralDomainException>(() => session.SetStoragePath("C:/outside/object.bin"));
        Assert.Equal("INVALID_STORAGE_PATH", ex.Code);
    }

    [Fact]
    public void SFR16_UploadSessionEfModel_UsesNullableMigrationCompatibleColumns()
    {
        var entity = _dbContext.Model.FindEntityType(typeof(UploadSession));
        Assert.NotNull(entity);
        Assert.True(entity!.FindProperty(nameof(UploadSession.ContentType))!.IsNullable);
        Assert.Equal(128, entity.FindProperty(nameof(UploadSession.ContentType))!.GetMaxLength());
        Assert.True(entity.FindProperty(nameof(UploadSession.RetentionPolicyId))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(UploadSession.LocalManifestAuthenticationOpaque))!.IsNullable);
        Assert.Equal(512, entity.FindProperty(nameof(UploadSession.LocalManifestAuthenticationOpaque))!.GetMaxLength());
    }

    [Fact]
    public void CONE_T18_SupportTicketAssignment_PersistsAssigneeAndHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var ticket = SupportTicket.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Assign support ticket",
            "Synthetic assignment fixture", new List<Guid>(), 1, now);
        var assigneeId = Guid.NewGuid();

        ticket.AssignTo(assigneeId, Guid.NewGuid().ToString("D"), now.AddMinutes(1));

        Assert.Equal(assigneeId, ticket.AssignedAdminUserId);
        Assert.Equal(2, ticket.RowVersion);
        using var history = JsonDocument.Parse(ticket.RepliesJson);
        Assert.Single(history.RootElement.EnumerateArray());
        Assert.Equal(SupportReplyDto.KindAssignmentChanged, history.RootElement[0].GetProperty("Kind").GetString());
        Assert.Equal(assigneeId, Guid.Parse(history.RootElement[0].GetProperty("AssigneeAdminUserId").GetString()!));
    }

    private async Task<UploadSession> CreateUploadSession(
        long expectedBytes,
        string expectedSha256,
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? createdAtUtc = null)
    {
        var now = createdAtUtc ?? DateTimeOffset.UtcNow;
        var session = UploadSession.Create(
            UploadSession.PurposeEncryptedBackup,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            expectedBytes,
            expectedSha256,
            "pending.bin",
            expiresAtUtc ?? now.AddHours(1),
            now,
            "application/octet-stream",
            Guid.NewGuid(),
            null);
        _dbContext.UploadSessions.Add(session);
        await _dbContext.SaveChangesAsync();
        return session;
    }

    private sealed class ReadTrackingStream(byte[] content) : MemoryStream(content)
    {
        public bool ReadAttempted { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
