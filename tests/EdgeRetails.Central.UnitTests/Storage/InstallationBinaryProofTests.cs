using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.ProtectedSigner.Keys;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Storage;

public sealed class InstallationBinaryProofTests
{
    [Fact]
    public async Task BodylessGetProof_IsAcceptedOnceAndBindsTheRequestedPath()
    {
        using var rsa = RSA.Create(2048);
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var installation = Installation.Register(shopId, licenseId, "synthetic-bodyless-get", rsa.ExportRSAPublicKeyPem(), "synthetic", "windows", DateTimeOffset.UtcNow);
        installation.Approve(DateTimeOffset.UtcNow);
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase($"bodyless-proof-{Guid.NewGuid():N}")
            .Options;
        await using var db = new CentralDbContext(options);
        db.Installations.Add(installation);
        await db.SaveChangesAsync();

        var serializer = new CanonicalJsonSerializer();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid();
        const string path = "/api/licensing/v1/trust-set";
        var request = CreateBodylessGet(path, installation, timestamp, nonce,
            Sign(rsa, serializer, installation, timestamp, nonce, path));
        var verifier = new InstallationRequestProofService(db, serializer, new ProtectedSigningEngine(db, new NoOpPrivateKeyStore()));

        Assert.True(await verifier.AuthenticateAsync(request, false, CancellationToken.None));
        Assert.IsType<Installation>(request.HttpContext.Items["AuthenticatedInstallation"]);
        Assert.Single(await db.InstallationRequestNonces.ToListAsync());

        var replay = CreateBodylessGet(path, installation, timestamp, nonce,
            Sign(rsa, serializer, installation, timestamp, nonce, path));
        Assert.False(await verifier.AuthenticateAsync(replay, false, CancellationToken.None));
        Assert.Single(await db.InstallationRequestNonces.ToListAsync());

        var alteredPathNonce = Guid.NewGuid();
        var alteredPath = CreateBodylessGet("/api/licensing/v1/operations/receipt", installation, timestamp, alteredPathNonce,
            Sign(rsa, serializer, installation, timestamp, alteredPathNonce, path));
        Assert.False(await verifier.AuthenticateAsync(alteredPath, false, CancellationToken.None));
        Assert.Single(await db.InstallationRequestNonces.ToListAsync());
    }

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

    private static HttpRequest CreateBodylessGet(string path, Installation installation, long timestamp, Guid nonce, string signature)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = "GET";
        request.Path = path;
        request.ContentLength = 0;
        request.Body = new MemoryStream();
        request.Headers["X-Installation-Id"] = installation.Id.ToString("D");
        request.Headers["X-Installation-Key-Id"] = installation.PublicKeyFingerprintSha256;
        request.Headers["X-Installation-Timestamp"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        request.Headers["X-Installation-Nonce"] = nonce.ToString("D");
        request.Headers["X-Installation-Signature"] = signature;
        return request;
    }

    private static string Sign(RSA rsa, CanonicalJsonSerializer serializer, Installation installation, long timestamp, Guid nonce, string path)
    {
        var proof = new Dictionary<string, object?>
        {
            ["profile"] = "CENTRAL_INSTALLATION_REQUEST_V1",
            ["purpose"] = "installation.request",
            ["keyId"] = installation.PublicKeyFingerprintSha256,
            ["installationId"] = installation.Id,
            ["shopId"] = installation.ShopId,
            ["licenseId"] = installation.LicenseId,
            ["timestamp"] = timestamp,
            ["nonce"] = nonce,
            ["httpMethod"] = "GET",
            ["httpPath"] = path,
            ["bodyDigest"] = serializer.ComputeSha256Hex(string.Empty)
        };
        var canonical = serializer.SerializeCanonical(proof);
        return Convert.ToBase64String(rsa.SignData(System.Text.Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private sealed class NoOpPrivateKeyStore : IPrivateKeyStore
    {
        public void StoreKey(string keyId, RSA rsa) => throw new NotSupportedException();
        public RSA? GetKey(string keyId) => null;
        public bool HasKey(string keyId) => false;
        public string? GetPublicKeyPem(string keyId) => null;
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
