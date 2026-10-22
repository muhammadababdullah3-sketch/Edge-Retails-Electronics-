using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Storage;

public sealed class InstallationBinaryProofTests
{
    [Theory]
    [InlineData(null, "1", false)]
    [InlineData("100", null, false)]
    [InlineData("100", "101", false)]
    [InlineData("0", "1", false)]
    [InlineData("104857601", "1", false)]
    [InlineData("100", "10", true)]
    public void UploadBounds_RequireExplicitValidConfiguration(string? objectValue, string? chunkValue, bool valid)
    {
        Assert.Equal(valid, StorageUploadLimits.TryParse(objectValue, chunkValue, out _));
    }

    [Fact]
    public async Task OversizedBinaryProof_IsRejectedBeforeReadingBodyOrRecordingNonce()
    {
        Assert.True(StorageUploadLimits.TryRead(out var limits));
        Assert.Equal(10485760, limits.MaxChunkBytes);
        Assert.Equal("120", Environment.GetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS"));
        using var rsa = RSA.Create(2048);
        var installation = Installation.Register(Guid.NewGuid(), Guid.NewGuid(), "synthetic-proof-bound", rsa.ExportRSAPublicKeyPem(), "synthetic", "windows", DateTimeOffset.UtcNow);
        installation.Approve(DateTimeOffset.UtcNow);
        var options = new DbContextOptionsBuilder<CentralDbContext>().UseInMemoryDatabase($"proof-bound-{Guid.NewGuid():N}").Options;
        await using var db = new CentralDbContext(options);
        db.Installations.Add(installation);
        await db.SaveChangesAsync();
        var request = new DefaultHttpContext().Request;
        request.Method = "PUT";
        request.Path = $"/api/licensing/v1/uploads/{Guid.NewGuid():D}/content";
        request.ContentType = "application/octet-stream";
        request.ContentLength = 10485761;
        request.Headers["Content-Range"] = "bytes 0-10485760/10485761";
        request.Headers["chunk-sha256"] = new string('a', 64);
        request.Headers["X-Installation-Id"] = installation.Id.ToString("D");
        request.Headers["X-Installation-Key-Id"] = installation.PublicKeyFingerprintSha256;
        request.Headers["X-Installation-Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        request.Headers["X-Installation-Nonce"] = Guid.NewGuid().ToString("D");
        request.Headers["X-Installation-Signature"] = "invalid-but-bounded-signature";
        using var body = new RejectReadStream();
        request.Body = body;
        // The signer is unreachable after bound rejection; null is intentional fixture evidence.
        var verifier = new InstallationRequestProofService(db, new CanonicalJsonSerializer(), null!);
        Assert.False(await verifier.AuthenticateAsync(request, false, default));
        Assert.False(body.WasRead);
        Assert.Empty(await db.InstallationRequestNonces.ToListAsync());
    }

    private sealed class RejectReadStream : MemoryStream
    {
        public bool WasRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            throw new InvalidOperationException("Oversized body must not be read.");
        }
    }
}
