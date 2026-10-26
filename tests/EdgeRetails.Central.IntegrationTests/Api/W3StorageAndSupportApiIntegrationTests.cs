using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Domain.Operations;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.Central.IntegrationTests.Api;

public class W3StorageAndSupportApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly ICanonicalJsonSerializer _serializer = new CanonicalJsonSerializer();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RSA> InstallationTestKeys = new(StringComparer.OrdinalIgnoreCase);

    public W3StorageAndSupportApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CENTRAL_OBJECT_STORAGE_ROOT")))
        {
            Environment.SetEnvironmentVariable("CENTRAL_OBJECT_STORAGE_ROOT", Path.Combine(AppContext.BaseDirectory, "test-artifacts", "central-object-store"));
        }
        _factory = factory;
        _client = factory.CreateDefaultClient();
        _client.BaseAddress = new Uri("https://localhost");

        RateLimitingMiddleware.Reset();
    }

    private CentralDbContext GetDbContext()
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CentralDbContext>();
    }

    private string CreateInstallationTestKey()
    {
        var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportRSAPublicKeyPem();
        InstallationTestKeys[Installation.ComputePublicKeyFingerprintSha256(publicKey)] = rsa;
        return publicKey;
    }

    private void AddInstallationProof(HttpRequestMessage request, Guid installationId, Guid shopId, Guid licenseId, string publicKeyPem)
    {
        Environment.SetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS", "120"); // labelled synthetic integration fixture
        var keyId = Installation.ComputePublicKeyFingerprintSha256(publicKeyPem);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid();
        var bodyBytes = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? Array.Empty<byte>();
        var isBinary = string.Equals(request.Content?.Headers.ContentType?.MediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
        var bodyDigest = isBinary
            ? Convert.ToHexStringLower(SHA256.HashData(bodyBytes))
            : bodyBytes.Length == 0
                ? _serializer.ComputeSha256Hex(string.Empty)
                : _serializer.ComputeSha256Hex(_serializer.SerializeCanonical(Encoding.UTF8.GetString(bodyBytes)));
        var proofPayload = new Dictionary<string, object?>
        {
            ["profile"] = isBinary ? "CENTRAL_INSTALLATION_BINARY_REQUEST_V1" : "CENTRAL_INSTALLATION_REQUEST_V1",
            ["purpose"] = "installation.request",
            ["keyId"] = keyId,
            ["installationId"] = installationId,
            ["shopId"] = shopId,
            ["licenseId"] = licenseId,
            ["timestamp"] = timestamp,
            ["nonce"] = nonce,
            ["httpMethod"] = request.Method.Method.ToUpperInvariant(),
            ["httpPath"] = request.RequestUri!.OriginalString,
            ["bodyDigest"] = bodyDigest
        };
        if (isBinary)
        {
            var range = request.Content!.Headers.ContentRange!;
            proofPayload["binaryRange"] = new { from = range.From, to = range.To, totalLength = range.Length, contentLength = request.Content.Headers.ContentLength, chunkSha256 = request.Headers.GetValues("chunk-sha256").Single().ToLowerInvariant() };
        }
        var envelope = _serializer.SerializeCanonical(proofPayload);
        var signature = InstallationTestKeys[keyId].SignData(Encoding.UTF8.GetBytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.Headers.Add("X-Installation-Id", installationId.ToString("D"));
        request.Headers.Add("X-Installation-Key-Id", keyId);
        request.Headers.Add("X-Installation-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Installation-Nonce", nonce.ToString("D"));
        request.Headers.Add("X-Installation-Signature", Convert.ToBase64String(signature));
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(content) ?? throw new InvalidOperationException("Failed to parse JSON response: " + content);
    }

    private async Task<HttpClient> CreateSupportAdminClientAsync(
        bool includeSupportView = true,
        bool includeSupportCreate = true,
        bool includeSupportReply = true)
    {
        const string password = "AdminPassword#123";
        var username = $"support_admin_{Guid.NewGuid():N}";
        var totp = _factory.Services.GetRequiredService<ITotpService>();
        var hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        var secret = totp.GenerateSecret();
        using (var db = GetDbContext())
        {
            var user = AdminUser.Create(username, hasher.HashPassword(password), "salt", secret, true, DateTimeOffset.UtcNow);
            var capabilities = new List<string>();
            if (includeSupportCreate) { capabilities.Add("support.create"); }
            if (includeSupportReply) { capabilities.Add("support.reply"); }
            if (includeSupportView) { capabilities.Add("support.view"); }
            var role = AdminRole.Create($"support_role_{Guid.NewGuid():N}", "Support", capabilities);
            db.AdminUsers.Add(user);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateDefaultClient();
        client.BaseAddress = new Uri("https://localhost");
        var login = await client.PostAsJsonAsync("/api/admin/v1/auth/login", new LoginRequestDto
        {
            Username = username,
            PasswordOrProviderAssertion = password
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var challengeId = Guid.Parse((await ReadJsonAsync(login))["challengeId"]!.GetValue<string>());
        var mfa = await client.PostAsJsonAsync("/api/admin/v1/auth/mfa", new MfaRequestDto
        {
            LoginChallengeId = challengeId,
            Proof = totp.GenerateCode(secret, DateTimeOffset.UtcNow)
        });
        Assert.Equal(HttpStatusCode.OK, mfa.StatusCode);
        Assert.True(mfa.Headers.TryGetValues("Set-Cookie", out var cookieHeaders));
        var cookie = cookieHeaders.Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith("central_session=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return client;
    }

    [Fact]
    public async Task CONE_T15_T16_Upload_Download_Lifecycle_EndToEnd()
    {
        RateLimitingMiddleware.Reset();
        using var database = GetDbContext();
        var customerId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var publicKeyPem = CreateInstallationTestKey();
        var otherShopId = Guid.NewGuid();
        var otherLicenseId = Guid.NewGuid();
        var otherInstallationId = Guid.NewGuid();
        var otherPublicKeyPem = CreateInstallationTestKey();
        var syntheticRetentionPolicyId = Guid.Parse("d0d8959b-278f-4cda-a81c-3eb88732f071");
        Environment.SetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_POLICY_ID", syntheticRetentionPolicyId.ToString("D"));
        Environment.SetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_DAYS", "30");
        Environment.SetEnvironmentVariable("CENTRAL_UPLOAD_SESSION_TTL_SECONDS", "600");

        var shop = Shop.Create(customerId, $"SH_{Guid.NewGuid():N}"[..10], "W3 Storage Shop", DateTimeOffset.UtcNow);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        database.Shops.Add(shop);

        var installation = Installation.Register(
            shopId,
            licenseId,
            "FP_TEST_W3",
            publicKeyPem,
            "test-host",
            "linux",
            DateTimeOffset.UtcNow);
        installation.Approve(DateTimeOffset.UtcNow);
        typeof(Installation).GetProperty("Id")!.SetValue(installation, installationId);
        database.Installations.Add(installation);

        var otherShop = Shop.Create(Guid.NewGuid(), $"SH_{Guid.NewGuid():N}"[..10], "Other Storage Shop", DateTimeOffset.UtcNow);
        typeof(Shop).GetProperty("Id")!.SetValue(otherShop, otherShopId);
        database.Shops.Add(otherShop);
        var otherInstallation = Installation.Register(
            otherShopId,
            otherLicenseId,
            "FP_TEST_W3_OTHER",
            otherPublicKeyPem,
            "other-test-host",
            "linux",
            DateTimeOffset.UtcNow);
        otherInstallation.Approve(DateTimeOffset.UtcNow);
        typeof(Installation).GetProperty("Id")!.SetValue(otherInstallation, otherInstallationId);
        database.Installations.Add(otherInstallation);

        await database.SaveChangesAsync();

        var objectId = Guid.NewGuid();
        var contentBytes = Encoding.UTF8.GetBytes("ENCRYPTED_BACKUP_CIPHERTEXT_SAMPLE_12345");
        var sha256Hex = Convert.ToHexStringLower(SHA256.HashData(contentBytes));

        // Required idempotency/reason metadata must be explicit; the server must not invent a new operation ID.
        using (var missingMeta = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/backups/uploads"))
        {
            missingMeta.Content = JsonContent.Create(new UploadInitDto
            {
                ShopId = shopId, InstallationId = installationId, ObjectId = Guid.NewGuid(),
                Purpose = EncryptedObjectRecord.PurposeEncryptedBackup, ContentLength = contentBytes.Length,
                ContentType = "application/octet-stream", CiphertextSha256 = sha256Hex,
                RetentionPolicyId = syntheticRetentionPolicyId
            });
            AddInstallationProof(missingMeta, installationId, shopId, licenseId, publicKeyPem);
            using var missingMetaResponse = await _client.SendAsync(missingMeta);
            Assert.Equal(HttpStatusCode.BadRequest, missingMetaResponse.StatusCode);
            Assert.Equal("INVALID_COMMAND_META", (await ReadJsonAsync(missingMetaResponse))["code"]?.GetValue<string>());
        }

        using (var malformedFinalize = new HttpRequestMessage(HttpMethod.Post, $"/api/licensing/v1/backups/uploads/{Guid.NewGuid():D}/finalize"))
        {
            malformedFinalize.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString("D"));
            malformedFinalize.Headers.Add("X-Reason", "test malformed body");
            malformedFinalize.Content = new StringContent("{\"ciphertextBase64\":\"!\"}", Encoding.UTF8, "application/json");
            AddInstallationProof(malformedFinalize, installationId, shopId, licenseId, publicKeyPem);
            using var malformedFinalizeResponse = await _client.SendAsync(malformedFinalize);
            Assert.Equal(HttpStatusCode.BadRequest, malformedFinalizeResponse.StatusCode);
            Assert.Equal("INVALID_UPLOAD_BODY", (await ReadJsonAsync(malformedFinalizeResponse))["code"]?.GetValue<string>());
        }

        // 1. Init Upload
        var initRequest = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/backups/uploads");
        initRequest.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString());
        initRequest.Headers.Add("X-Reason", "synthetic encrypted upload fixture");
        initRequest.Headers.Add("X-Caller-Scope", "installation");
        initRequest.Content = JsonContent.Create(new UploadInitDto
        {
            ShopId = shopId,
            InstallationId = installationId,
            ObjectId = objectId,
            Purpose = EncryptedObjectRecord.PurposeEncryptedBackup,
            ContentLength = contentBytes.Length,
            ContentType = "application/octet-stream",
            CiphertextSha256 = sha256Hex,
            RetentionPolicyId = syntheticRetentionPolicyId
        });
        AddInstallationProof(initRequest, installationId, shopId, licenseId, publicKeyPem);

        var initResponse = await _client.SendAsync(initRequest);
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);

        var initJson = await ReadJsonAsync(initResponse);
        var uploadIdStr = initJson["response"]?["uploadId"]?.GetValue<string>();
        Assert.NotNull(uploadIdStr);
        var uploadId = Guid.Parse(uploadIdStr);
        Assert.Equal($"/api/licensing/v1/uploads/{uploadId:D}/content", initJson["response"]?["chunkRoute"]?.GetValue<string>());

        using (var unsupportedFinalize = new HttpRequestMessage(HttpMethod.Post, $"/api/licensing/v1/backups/uploads/{uploadId:D}/finalize"))
        {
            unsupportedFinalize.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString("D"));
            unsupportedFinalize.Headers.Add("X-Reason", "test unsupported upload media type");
            unsupportedFinalize.Content = JsonContent.Create(new UploadFinalizeDto { UploadId = uploadId, ObjectId = objectId });
            unsupportedFinalize.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/jsonp");
            AddInstallationProof(unsupportedFinalize, installationId, shopId, licenseId, publicKeyPem);
            using var unsupportedFinalizeResponse = await _client.SendAsync(unsupportedFinalize);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupportedFinalizeResponse.StatusCode);
        }

        using var crossShopInit = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/backups/uploads");
        crossShopInit.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString("D"));
        crossShopInit.Headers.Add("X-Reason", "synthetic cross-shop denial fixture");
        crossShopInit.Content = JsonContent.Create(new UploadInitDto
        {
            ShopId = otherShopId, InstallationId = otherInstallationId, ObjectId = objectId,
            Purpose = EncryptedObjectRecord.PurposeEncryptedBackup, ContentLength = contentBytes.Length,
            ContentType = "application/octet-stream", CiphertextSha256 = sha256Hex, RetentionPolicyId = syntheticRetentionPolicyId
        });
        AddInstallationProof(crossShopInit, otherInstallationId, otherShopId, otherLicenseId, otherPublicKeyPem);
        using var crossShopInitResponse = await _client.SendAsync(crossShopInit);
        Assert.Equal(HttpStatusCode.Conflict, crossShopInitResponse.StatusCode);

        // A different active installation cannot finalize another install's persisted upload session.
        var crossInstallationFinalize = new HttpRequestMessage(HttpMethod.Post, $"/api/licensing/v1/backups/uploads/{uploadId}/finalize");
        crossInstallationFinalize.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString());
        crossInstallationFinalize.Headers.Add("X-Reason", "synthetic cross-installation denial fixture");
        crossInstallationFinalize.Content = JsonContent.Create(new UploadFinalizeDto
        {
            UploadId = uploadId,
            ObjectId = objectId,
            ExpectedLength = contentBytes.Length,
            ExpectedSha256 = sha256Hex,
            CiphertextBase64 = Convert.ToBase64String(contentBytes)
        });
        AddInstallationProof(crossInstallationFinalize, otherInstallationId, otherShopId, otherLicenseId, otherPublicKeyPem);
        var crossInstallationResponse = await _client.SendAsync(crossInstallationFinalize);
        Assert.Equal(HttpStatusCode.Forbidden, crossInstallationResponse.StatusCode);
        Assert.Equal("FORBIDDEN", (await ReadJsonAsync(crossInstallationResponse))["code"]?.GetValue<string>());
        using (var db = GetDbContext())
        {
            var unchangedSession = await db.UploadSessions.FindAsync(uploadId);
            Assert.NotNull(unchangedSession);
            Assert.Equal(0, unchangedSession.UploadedBytes);
            Assert.Equal(UploadSession.StatusInitiated, unchangedSession.Status);
            Assert.False(await db.EncryptedObjects.AnyAsync(x => x.Id == objectId));
        }

        // 2. Upload in resumable chunks. Exact retry is idempotent; altered retry and gaps conflict.
        var firstChunk = contentBytes[..17];
        var secondChunk = contentBytes[17..];
        async Task<(HttpStatusCode Status, string? Offset)> SendChunk(byte[] bytes, long offset, bool alterSignedRange = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/licensing/v1/uploads/{uploadId:D}/content");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, offset + bytes.Length - 1, contentBytes.Length);
            request.Headers.Add("chunk-sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            AddInstallationProof(request, installationId, shopId, licenseId, publicKeyPem);
            if (alterSignedRange)
            {
                request.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset + 1, offset + bytes.Length, contentBytes.Length);
            }
            using var response = await _client.SendAsync(request);
            var json = await ReadJsonAsync(response);
            return (response.StatusCode, json["acceptedOffset"]?.ToString());
        }

        var alteredRange = await SendChunk(firstChunk, 0, alterSignedRange: true);
        Assert.Equal(HttpStatusCode.Unauthorized, alteredRange.Status);
        var firstAccepted = await SendChunk(firstChunk, 0);
        Assert.Equal(HttpStatusCode.OK, firstAccepted.Status);
        Assert.Equal(firstChunk.Length.ToString(), firstAccepted.Offset);
        var exactRetry = await SendChunk(firstChunk, 0);
        Assert.Equal(HttpStatusCode.OK, exactRetry.Status);
        Assert.Equal(firstChunk.Length.ToString(), exactRetry.Offset);
        var alteredFirstChunk = firstChunk.ToArray();
        alteredFirstChunk[0] ^= 0x01;
        var alteredRetry = await SendChunk(alteredFirstChunk, 0);
        Assert.Equal(HttpStatusCode.Conflict, alteredRetry.Status);
        var gapRetry = await SendChunk(secondChunk[..10], firstChunk.Length + 1);
        Assert.Equal(HttpStatusCode.Conflict, gapRetry.Status);
        var secondAccepted = await SendChunk(secondChunk, firstChunk.Length);
        Assert.Equal(HttpStatusCode.OK, secondAccepted.Status);
        Assert.Equal(contentBytes.Length.ToString(), secondAccepted.Offset);

        // 3. Finalize Upload from durable staging bytes.
        var finalizeRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/licensing/v1/backups/uploads/{uploadId}/finalize");
        finalizeRequest.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString());
        finalizeRequest.Headers.Add("X-Reason", "synthetic encrypted upload finalize fixture");
        finalizeRequest.Headers.Add("X-Caller-Scope", "installation");
        finalizeRequest.Content = JsonContent.Create(new UploadFinalizeDto
        {
            UploadId = uploadId,
            ObjectId = objectId,
            ExpectedLength = contentBytes.Length,
            ExpectedSha256 = sha256Hex,
        });
        AddInstallationProof(finalizeRequest, installationId, shopId, licenseId, publicKeyPem);

        var finalizeResponse = await _client.SendAsync(finalizeRequest);
        Assert.True(finalizeResponse.StatusCode == HttpStatusCode.OK, await finalizeResponse.Content.ReadAsStringAsync());
        async Task<HttpResponseMessage> InitAgain(Guid operationId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/backups/uploads");
            request.Headers.Add("X-Client-Operation-Id", operationId.ToString("D"));
            request.Headers.Add("X-Reason", "synthetic encrypted upload replay fixture");
            request.Content = new StringContent(await initRequest.Content!.ReadAsStringAsync(), Encoding.UTF8, "application/json");
            AddInstallationProof(request, installationId, shopId, licenseId, publicKeyPem);
            return await _client.SendAsync(request);
        }
        using var initReplay = await InitAgain(Guid.Parse(initRequest.Headers.GetValues("X-Client-Operation-Id").Single()));
        Assert.Equal(HttpStatusCode.OK, initReplay.StatusCode);
        Assert.Equal(uploadIdStr, (await ReadJsonAsync(initReplay))["response"]?["uploadId"]?.GetValue<string>());
        using var duplicateInit = await InitAgain(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, duplicateInit.StatusCode);
        Assert.Equal("STORAGE_OBJECT_CONFLICT", (await ReadJsonAsync(duplicateInit))["code"]?.GetValue<string>());
        var stagingPath = Path.Combine(Environment.GetEnvironmentVariable("CENTRAL_OBJECT_STORAGE_ROOT")!, ".uploads", shopId.ToString("N"), $"{uploadId:N}.part");
        Assert.False(File.Exists(stagingPath), "Finalized upload staging bytes must be removed after durable publication.");
        using (var db = GetDbContext())
        {
            var finalizedSession = await db.UploadSessions.AsNoTracking().SingleAsync(x => x.Id == uploadId);
            Assert.Equal(UploadSession.StatusFinalized, finalizedSession.Status);
            Assert.Equal(contentBytes.Length, finalizedSession.UploadedBytes);
            Assert.True(await db.EncryptedObjects.AsNoTracking().AnyAsync(x => x.Id == objectId && x.ShopId == shopId && x.CiphertextSha256 == sha256Hex));
            Assert.Equal(1, await db.UploadSessions.CountAsync(x => x.TargetEntityId == objectId));
        }

        // 3. Installation proof requests a short-lived grant; only its bearer token can fetch bytes.
        using (var mismatchedGrantRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/backups/{objectId:D}/download"))
        {
            mismatchedGrantRequest.Content = JsonContent.Create(new ObjectActionDto { ObjectId = Guid.NewGuid(), Purpose = EncryptedObjectRecord.PurposeEncryptedBackup });
            AddInstallationProof(mismatchedGrantRequest, installationId, shopId, licenseId, publicKeyPem);
            using var mismatchedGrantResponse = await _client.SendAsync(mismatchedGrantRequest);
            Assert.Equal(HttpStatusCode.BadRequest, mismatchedGrantResponse.StatusCode);
        }
        using var grantRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/backups/{objectId:D}/download");
        grantRequest.Content = JsonContent.Create(new ObjectActionDto { ObjectId = objectId, Purpose = EncryptedObjectRecord.PurposeEncryptedBackup });
        AddInstallationProof(grantRequest, installationId, shopId, licenseId, publicKeyPem);
        using var grantResponse = await _client.SendAsync(grantRequest);
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);
        var grantJson = await ReadJsonAsync(grantResponse);
        var grantId = Guid.Parse(grantJson["grantId"]!.GetValue<string>());
        var downloadToken = grantJson["downloadToken"]!.GetValue<string>();
        var contentRoute = grantJson["contentRoute"]!.GetValue<string>();
        Assert.Equal($"/api/licensing/v1/backups/download-grants/{grantId:D}/content", contentRoute);
        var expiresAt = DateTimeOffset.Parse(grantJson["expiresAtUtc"]!.GetValue<string>());
        Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddSeconds(1), DateTimeOffset.UtcNow.AddSeconds(301));
        using (var db = GetDbContext())
        {
            var persistedGrant = await db.ObjectDownloadGrants.AsNoTracking().SingleAsync(x => x.Id == grantId);
            Assert.Equal(installationId, persistedGrant.InstallationId);
            Assert.Equal(shopId, persistedGrant.ShopId);
            Assert.Equal(objectId, persistedGrant.ObjectId);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(downloadToken))), persistedGrant.TokenSha256);
            Assert.NotEqual(downloadToken, persistedGrant.TokenSha256);
            Assert.Equal(64, persistedGrant.TokenSha256.Length);
        }

        using var invalidGrantRequest = new HttpRequestMessage(HttpMethod.Get, contentRoute);
        invalidGrantRequest.Headers.Add("X-Central-Download-Token", "wrong-token");
        using var invalidGrantResponse = await _client.SendAsync(invalidGrantRequest);
        Assert.Equal(HttpStatusCode.NotFound, invalidGrantResponse.StatusCode);

        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, contentRoute);
        downloadRequest.Headers.Add("X-Central-Download-Token", downloadToken);
        var downloadResponse = await _client.SendAsync(downloadRequest);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(contentBytes, downloadedBytes);
        Assert.Equal(contentBytes.Length, downloadResponse.Content.Headers.ContentLength);
        Assert.Equal(objectId.ToString("D"), downloadResponse.Headers.GetValues("X-Object-Id").Single());
        Assert.Equal($"sha-256={Convert.ToBase64String(SHA256.HashData(contentBytes))}", downloadResponse.Headers.GetValues("Digest").Single());

        using var legacyDirectRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/backups/objects/{objectId:D}");
        AddInstallationProof(legacyDirectRequest, installationId, shopId, licenseId, publicKeyPem);
        using var legacyDirectResponse = await _client.SendAsync(legacyDirectRequest);
        Assert.Equal(HttpStatusCode.NotFound, legacyDirectResponse.StatusCode);

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, contentRoute);
        rangeRequest.Headers.Add("X-Central-Download-Token", downloadToken);
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        using var rangeResponse = await _client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal(contentBytes[2..6], await rangeResponse.Content.ReadAsByteArrayAsync());

        // Another persisted shop/installation cannot mint a grant for this object.
        using var crossShopRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/backups/{objectId:D}/download");
        crossShopRequest.Content = JsonContent.Create(new ObjectActionDto { ObjectId = objectId, Purpose = EncryptedObjectRecord.PurposeEncryptedBackup });
        AddInstallationProof(crossShopRequest, otherInstallationId, otherShopId, otherLicenseId, otherPublicKeyPem);
        using var crossShopResponse = await _client.SendAsync(crossShopRequest);
        Assert.Equal(HttpStatusCode.NotFound, crossShopResponse.StatusCode);

        var attachmentId = Guid.NewGuid();
        var storage = _factory.Services.GetRequiredService<IObjectStorageProvider>();
        var attachmentPath = await storage.SaveCiphertextAsync(shopId, attachmentId, new MemoryStream(contentBytes));
        using (var db = GetDbContext())
        {
            db.EncryptedObjects.Add(EncryptedObjectRecord.Create(attachmentId, shopId, installationId,
                EncryptedObjectRecord.PurposeSupportAttachment, contentBytes.Length, "application/octet-stream", sha256Hex,
                attachmentPath, syntheticRetentionPolicyId, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)));
            await db.SaveChangesAsync();
        }
        using var wrongPurpose = new HttpRequestMessage(HttpMethod.Get, $"/api/licensing/v1/backups/{attachmentId:D}/download");
        wrongPurpose.Content = JsonContent.Create(new ObjectActionDto { ObjectId = attachmentId, Purpose = EncryptedObjectRecord.PurposeEncryptedBackup });
        AddInstallationProof(wrongPurpose, installationId, shopId, licenseId, publicKeyPem);
        using var wrongPurposeResponse = await _client.SendAsync(wrongPurpose);
        Assert.Equal(HttpStatusCode.NotFound, wrongPurposeResponse.StatusCode);

        var migrationConnection = Environment.GetEnvironmentVariable("CENTRAL_MIGRATION_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Owned migration connection must be explicitly supplied for the expiry fixture.");
        var migrationOptions = new DbContextOptionsBuilder<CentralDbContext>()
            .UseNpgsql(migrationConnection, p => p.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema))
            .Options;
        await using (var ownerDb = new CentralDbContext(migrationOptions, new EdgeRetails.Central.Application.Common.DatabaseTargetGuard()))
        {
            var persistedGrant = await ownerDb.ObjectDownloadGrants.SingleAsync(x => x.Id == grantId);
            ownerDb.Entry(persistedGrant).Property(x => x.ExpiresAtUtc).CurrentValue = DateTimeOffset.UtcNow.AddSeconds(-1);
            await ownerDb.SaveChangesAsync();
        }
        using var expiredGrantRequest = new HttpRequestMessage(HttpMethod.Get, contentRoute);
        expiredGrantRequest.Headers.Add("X-Central-Download-Token", downloadToken);
        using var expiredGrantResponse = await _client.SendAsync(expiredGrantRequest);
        Assert.Equal(HttpStatusCode.NotFound, expiredGrantResponse.StatusCode);
    }

    [Fact]
    public async Task CONE_T18_CreateSupportTicket_And_AddReply_EndToEnd()
    {
        RateLimitingMiddleware.Reset();
        var adminClient = await CreateSupportAdminClientAsync();
        var forgedRequesterId = Guid.NewGuid();
        var forgedAuthorId = Guid.NewGuid();
        var forgedShopId = Guid.NewGuid();
        adminClient.DefaultRequestHeaders.Add("X-Requester-Principal-Id", forgedRequesterId.ToString("D"));
        adminClient.DefaultRequestHeaders.Add("X-Author-Id", forgedAuthorId.ToString("D"));
        adminClient.DefaultRequestHeaders.Add("X-Admin-Role", "OWNER");
        adminClient.DefaultRequestHeaders.Add("X-Shop-Id", forgedShopId.ToString("D"));
        using var database = GetDbContext();
        var shopId = Guid.NewGuid();

        var shop = Shop.Create(Guid.NewGuid(), $"SH_{Guid.NewGuid():N}"[..10], "Support Shop", DateTimeOffset.UtcNow);
        var otherShop = Shop.Create(Guid.NewGuid(), $"SH_{Guid.NewGuid():N}"[..10], "Other Support Shop", DateTimeOffset.UtcNow);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        database.Shops.AddRange(shop, otherShop);
        await database.SaveChangesAsync();

        var attachmentId = Guid.NewGuid();
        var attachmentBytes = Encoding.UTF8.GetBytes("SYNTHETIC_ENCRYPTED_SUPPORT_ATTACHMENT");
        var attachmentHash = Convert.ToHexStringLower(SHA256.HashData(attachmentBytes));
        var storage = _factory.Services.GetRequiredService<IObjectStorageProvider>();
        var attachmentPath = await storage.SaveCiphertextAsync(shopId, attachmentId, new MemoryStream(attachmentBytes));
        database.EncryptedObjects.Add(EncryptedObjectRecord.Create(attachmentId, shopId, Guid.NewGuid(),
            EncryptedObjectRecord.PurposeSupportAttachment, attachmentBytes.Length, "application/octet-stream", attachmentHash,
            attachmentPath, Guid.NewGuid(), null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)));
        var foreignAttachmentId = Guid.NewGuid();
        var foreignAttachmentBytes = Encoding.UTF8.GetBytes("SYNTHETIC_FOREIGN_SHOP_SUPPORT_ATTACHMENT");
        var foreignAttachmentPath = await storage.SaveCiphertextAsync(otherShop.Id, foreignAttachmentId, new MemoryStream(foreignAttachmentBytes));
        database.EncryptedObjects.Add(EncryptedObjectRecord.Create(foreignAttachmentId, otherShop.Id, Guid.NewGuid(),
            EncryptedObjectRecord.PurposeSupportAttachment, foreignAttachmentBytes.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(foreignAttachmentBytes)), foreignAttachmentPath, Guid.NewGuid(), null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)));
        await database.SaveChangesAsync();

        var secretMarkers = new[]
        {
            "BEGIN RSA PRIVATE KEY",
            "BEGIN PRIVATE KEY",
            "password=forbidden-fixture-secret",
            "token=forbidden-fixture-secret",
            "secret=forbidden-fixture-secret"
        };
        foreach (var (secretMarker, index) in secretMarkers.Select((marker, index) => (marker, index)))
        {
            var unredactedSubject = $"Unredacted synthetic support diagnostic {index}";
            using var unredactedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/support/v1/tickets");
            unredactedRequest.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString("D"));
            unredactedRequest.Content = JsonContent.Create(new TicketDto
            {
                ShopId = shopId,
                Subject = unredactedSubject,
                Summary = $"Synthetic support diagnostic: {secretMarker}",
                AttachmentObjectIds = new List<Guid>(),
                RedactionProfileVersion = 1
            });
            using var unredactedResponse = await adminClient.SendAsync(unredactedRequest);
            Assert.Equal(HttpStatusCode.BadRequest, unredactedResponse.StatusCode);
            Assert.Equal("SENSITIVE_SECRET_LEAKAGE", (await ReadJsonAsync(unredactedResponse))["code"]?.GetValue<string>());
            Assert.False(await database.SupportTickets.AnyAsync(x => x.ShopId == shopId && x.Subject == unredactedSubject));
        }

        // 1. Create Ticket
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/support/v1/tickets");
        var createOperationId = Guid.NewGuid();
        createRequest.Headers.Add("X-Client-Operation-Id", createOperationId.ToString());
        createRequest.Content = JsonContent.Create(new TicketDto
        {
            ShopId = shopId,
            Subject = "Backup sync inquiry",
            Summary = "Nightly backup failed with timeout code 504.",
            AttachmentObjectIds = new List<Guid> { attachmentId },
            RedactionProfileVersion = 1
        });

        var createResponse = await adminClient.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var createJson = await ReadJsonAsync(createResponse);
        var ticketIdStr = createJson["ticketId"]?.GetValue<string>();
        Assert.NotNull(ticketIdStr);
        var ticketId = Guid.Parse(ticketIdStr);

        foreach (var (secretMarker, index) in secretMarkers.Select((marker, index) => (marker, index)))
        {
            using var unredactedReplyRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/support/v1/tickets/{ticketId:D}/actions");
            unredactedReplyRequest.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString("D"));
            unredactedReplyRequest.Content = JsonContent.Create(new TicketActionDto
            {
                TicketId = ticketId,
                Action = "REPLY",
                ReplyText = $"Synthetic support reply {index}: {secretMarker}"
            });
            using var unredactedReplyResponse = await adminClient.SendAsync(unredactedReplyRequest);
            Assert.Equal(HttpStatusCode.BadRequest, unredactedReplyResponse.StatusCode);
            Assert.Equal("SENSITIVE_SECRET_LEAKAGE", (await ReadJsonAsync(unredactedReplyResponse))["code"]?.GetValue<string>());
        }
        database.ChangeTracker.Clear();
        var ticketAfterRedactionDenials = await database.SupportTickets.SingleAsync(x => x.Id == ticketId);
        Assert.Equal(1, ticketAfterRedactionDenials.RowVersion);
        Assert.Empty(JsonNode.Parse(ticketAfterRedactionDenials.RepliesJson)!.AsArray());

        // The foreign object exists and belongs to a different persisted shop. A valid ticket
        // session must still receive the same not-found result because it is not linked to this ticket.
        using (var crossShopRequest = new HttpRequestMessage(HttpMethod.Get,
                   $"/api/admin/v1/support/tickets/{ticketId:D}/attachments/{foreignAttachmentId:D}/download"))
        using (var crossShopResponse = await adminClient.SendAsync(crossShopRequest))
        {
            Assert.Equal(HttpStatusCode.NotFound, crossShopResponse.StatusCode);
            Assert.Equal("SUPPORT_ATTACHMENT_NOT_FOUND", (await ReadJsonAsync(crossShopResponse))["code"]?.GetValue<string>());
            Assert.Equal("application/json", crossShopResponse.Content.Headers.ContentType?.MediaType);
        }

        using (var downloadResponse = await adminClient.GetAsync($"/api/admin/v1/support/tickets/{ticketId:D}/attachments/{attachmentId:D}/download"))
        {
            Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
            Assert.Equal(attachmentBytes, await downloadResponse.Content.ReadAsByteArrayAsync());
            Assert.Equal(attachmentBytes.Length, downloadResponse.Content.Headers.ContentLength);
            Assert.Equal(attachmentId.ToString("D"), downloadResponse.Headers.GetValues("X-Object-Id").Single());
            Assert.Equal($"sha-256={Convert.ToBase64String(SHA256.HashData(attachmentBytes))}", downloadResponse.Headers.GetValues("Digest").Single());
        }

        using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/v1/support/tickets/{ticketId:D}/attachments/{attachmentId:D}/download"))
        {
            rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 8);
            using var rangeResponse = await adminClient.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal(attachmentBytes[2..9], await rangeResponse.Content.ReadAsByteArrayAsync());
        }

        using (var unlinkedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/v1/support/tickets/{Guid.NewGuid():D}/attachments/{attachmentId:D}/download"))
        using (var unlinkedResponse = await adminClient.SendAsync(unlinkedRequest))
        {
            Assert.Equal(HttpStatusCode.NotFound, unlinkedResponse.StatusCode);
            Assert.Equal("SUPPORT_ATTACHMENT_NOT_FOUND", (await ReadJsonAsync(unlinkedResponse))["code"]?.GetValue<string>());
        }

        using (var noSessionResponse = await _client.GetAsync($"/api/admin/v1/support/tickets/{ticketId:D}/attachments/{attachmentId:D}/download"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noSessionResponse.StatusCode);
        }

        var viewerlessClient = await CreateSupportAdminClientAsync(includeSupportView: false);
        using (var deniedResponse = await viewerlessClient.GetAsync($"/api/admin/v1/support/tickets/{ticketId:D}/attachments/{attachmentId:D}/download"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        }

        // 2. Add Reply
        var replyOperationId = Guid.NewGuid();
        var replyCommand = new TicketActionDto
        {
            TicketId = ticketId,
            Action = "REPLY",
            ReplyText = "We checked your network configuration and observed transient upstream packet drop."
        };
        var replyRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/support/v1/tickets/{ticketId}/actions");
        replyRequest.Headers.Add("X-Client-Operation-Id", replyOperationId.ToString());
        replyRequest.Content = JsonContent.Create(replyCommand);

        var replyResponse = await adminClient.SendAsync(replyRequest);
        Assert.Equal(HttpStatusCode.OK, replyResponse.StatusCode);

        // A retry with the same operation identity and payload must not duplicate the persisted reply.
        var replyRetry = new HttpRequestMessage(HttpMethod.Post, $"/api/support/v1/tickets/{ticketId}/actions");
        replyRetry.Headers.Add("X-Client-Operation-Id", replyOperationId.ToString());
        replyRetry.Content = JsonContent.Create(replyCommand);
        var replyRetryResponse = await adminClient.SendAsync(replyRetry);
        Assert.Equal(HttpStatusCode.OK, replyRetryResponse.StatusCode);

        var statusRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/support/tickets/{ticketId}/actions");
        statusRequest.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString());
        statusRequest.Content = JsonContent.Create(new TicketActionDto
        {
            TicketId = ticketId,
            Action = "STATUS",
            Status = "IN_PROGRESS"
        });
        var statusResponse = await adminClient.SendAsync(statusRequest);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var viewResponse = await adminClient.GetAsync("/api/admin/v1/support/tickets?cursor=0&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, viewResponse.StatusCode);
        var view = await ReadJsonAsync(viewResponse);
        Assert.Contains(view["items"]!.AsArray(), item => item!["id"]!.GetValue<string>() == ticketId.ToString("D"));

        // 3. Verify in DB
        using (var db = GetDbContext())
        {
            var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
            Assert.NotNull(ticket);
            Assert.Equal(shopId, ticket.ShopId);
            Assert.NotEqual(forgedShopId, ticket.ShopId);
            Assert.NotEqual(forgedRequesterId, ticket.RequesterPrincipalId);
            Assert.True(await db.AdminUsers.AnyAsync(x => x.Id == ticket.RequesterPrincipalId));
            Assert.Contains(attachmentId.ToString("D"), ticket.AttachmentObjectIdsJson);
            Assert.Equal(SupportTicket.StatusInProgress, ticket.Status);
            Assert.Contains(ticket.RequesterPrincipalId.ToString("D"), ticket.RepliesJson);
            using var events = JsonDocument.Parse(ticket.RepliesJson);
            Assert.Equal(2, events.RootElement.GetArrayLength());
            Assert.Equal(SupportReplyDto.KindMessage, events.RootElement[0].GetProperty("Kind").GetString());
            Assert.Equal(ticket.RequesterPrincipalId.ToString("D"), events.RootElement[0].GetProperty("Author").GetString());
            Assert.NotEqual(forgedAuthorId.ToString("D"), events.RootElement[0].GetProperty("Author").GetString());
            Assert.Equal(SupportReplyDto.KindStatusChanged, events.RootElement[1].GetProperty("Kind").GetString());
            Assert.NotEqual(Guid.Empty, Guid.Parse(events.RootElement[1].GetProperty("Author").GetString()!));
            Assert.Equal(3, await db.CentralOutboxMessages.CountAsync(x => x.Id == createOperationId || x.Id == replyOperationId || x.AggregateId == ticketId.ToString("D")));
            Assert.Equal(3, await db.CentralAuditRecords.CountAsync(x => x.ResourceId == ticketId.ToString("D")));
        }

        // Recreate the API host and use the persisted session cookie to prove the ticket, reply and
        // status history are reconstructed from durable Central storage rather than host memory.
        using var restartedFactory = _factory.WithWebHostBuilder(_ => { });
        using var restartedClient = restartedFactory.CreateDefaultClient();
        restartedClient.BaseAddress = new Uri("https://localhost");
        restartedClient.DefaultRequestHeaders.Add("Cookie", adminClient.DefaultRequestHeaders.GetValues("Cookie").Single());
        restartedClient.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var restartedViewResponse = await restartedClient.GetAsync("/api/admin/v1/support/tickets?cursor=0&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, restartedViewResponse.StatusCode);
        var restartedView = await ReadJsonAsync(restartedViewResponse);
        var persistedTicket = restartedView["items"]!.AsArray().SingleOrDefault(item =>
            item!["id"]!.GetValue<string>() == ticketId.ToString("D"));
        Assert.NotNull(persistedTicket);
        Assert.Equal("Backup sync inquiry", persistedTicket!["subject"]!.GetValue<string>());
        Assert.Equal(SupportTicket.StatusInProgress, persistedTicket["status"]!.GetValue<string>());
        using var restartedHistory = JsonDocument.Parse(persistedTicket["repliesJson"]!.GetValue<string>());
        Assert.Equal(2, restartedHistory.RootElement.GetArrayLength());
        Assert.Equal("We checked your network configuration and observed transient upstream packet drop.",
            restartedHistory.RootElement[0].GetProperty("Text").GetString());
        Assert.Equal(SupportReplyDto.KindStatusChanged, restartedHistory.RootElement[1].GetProperty("Kind").GetString());
        using var restartedAttachmentResponse = await restartedClient.GetAsync(
            $"/api/admin/v1/support/tickets/{ticketId:D}/attachments/{attachmentId:D}/download");
        Assert.Equal(HttpStatusCode.OK, restartedAttachmentResponse.StatusCode);
        Assert.Equal(attachmentBytes, await restartedAttachmentResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(attachmentBytes.Length, restartedAttachmentResponse.Content.Headers.ContentLength);
        Assert.Equal(attachmentId.ToString("D"), restartedAttachmentResponse.Headers.GetValues("X-Object-Id").Single());
        Assert.Equal($"sha-256={Convert.ToBase64String(SHA256.HashData(attachmentBytes))}",
            restartedAttachmentResponse.Headers.GetValues("Digest").Single());
    }

    [Fact]
    public async Task CONE_T18_CanonicalSupportCommands_PersistRepliesStatusAndAssignment()
    {
        RateLimitingMiddleware.Reset();
        var adminClient = await CreateSupportAdminClientAsync();
        using var db = GetDbContext();
        var shop = Shop.Create(Guid.NewGuid(), $"SC_{Guid.NewGuid():N}"[..10], "Canonical support shop", DateTimeOffset.UtcNow);
        var assignee = AdminUser.Create($"support_assignee_{Guid.NewGuid():N}", "synthetic-password-hash", "synthetic-salt",
            "JBSWY3DPEHPK3PXP", true, DateTimeOffset.UtcNow);
        db.Shops.Add(shop);
        db.AdminUsers.Add(assignee);
        await db.SaveChangesAsync();

        var uniqueSubject = $"Canonical support command {Guid.NewGuid():N}";
        var createPayload = new
        {
            shopId = shop.Id,
            subject = uniqueSubject,
            summary = "Persist the canonical staff support workflow.",
            attachmentObjectIds = Array.Empty<Guid>(),
            redactionProfileVersion = 1
        };
        var createOperationId = Guid.NewGuid();
        var createRequest = new
        {
            meta = new
            {
                clientOperationId = createOperationId,
                operationType = "support.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(createPayload).Sha256Hex,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Canonical support create fixture"
            },
            payload = createPayload
        };
        var createResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/support/tickets", createRequest);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var createOutcome = await ReadJsonAsync(createResponse);
        Assert.Equal("SUCCEEDED", createOutcome["state"]!.GetValue<string>());
        var ticketId = Guid.Parse(createOutcome["resultReference"]!.GetValue<string>()[("support/tickets/".Length)..]);
        var operationIds = new List<Guid> { createOperationId };
        var secondCreatePayload = new
        {
            shopId = shop.Id,
            subject = $"{uniqueSubject} second",
            summary = "Exercise the second page of the support snapshot.",
            attachmentObjectIds = Array.Empty<Guid>(),
            redactionProfileVersion = 1
        };
        var secondCreateRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "support.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(secondCreatePayload).Sha256Hex,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Second support page fixture"
            },
            payload = secondCreatePayload
        };
        using var secondCreateResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/support/tickets", secondCreateRequest);
        Assert.Equal(HttpStatusCode.OK, secondCreateResponse.StatusCode);
        var secondTicketId = Guid.Parse((await ReadJsonAsync(secondCreateResponse))["resultReference"]!.GetValue<string>()[("support/tickets/".Length)..]);
        using (var pageResponse = await adminClient.GetAsync($"/api/admin/v1/support/tickets?pageSize=1&filter={Uri.EscapeDataString(uniqueSubject)}"))
        {
            Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
            var page = await ReadJsonAsync(pageResponse);
            Assert.Contains(page["items"]![0]!["id"]!.GetValue<string>(), new[] { ticketId.ToString("D"), secondTicketId.ToString("D") });
            Assert.Equal(2, page["authorizedCount"]!.GetValue<int>());
            using var pageDocument = JsonDocument.Parse(page.ToJsonString());
            var cursor = pageDocument.RootElement.GetProperty("nextCursor").GetString();
            Assert.Equal("1", cursor);
            Assert.NotNull(page["asOfVersion"]);
            Assert.NotNull(page["projectionRevision"]);
            Assert.Null(page["page"]);
            Assert.Null(page["totalCount"]);
            var asOfVersion = page["asOfVersion"]!.GetValue<long>();
            using var nextPageResponse = await adminClient.GetAsync($"/api/admin/v1/support/tickets?pageSize=1&filter={Uri.EscapeDataString(uniqueSubject)}&cursor={cursor}&asOfVersion={asOfVersion}");
            Assert.Equal(HttpStatusCode.OK, nextPageResponse.StatusCode);
            var nextPage = await ReadJsonAsync(nextPageResponse);
            Assert.DoesNotContain(nextPage["items"]![0]!["id"]!.GetValue<string>(), new[] { page["items"]![0]!["id"]!.GetValue<string>() });
            using var stalePageResponse = await adminClient.GetAsync($"/api/admin/v1/support/tickets?pageSize=1&filter={Uri.EscapeDataString(uniqueSubject)}&cursor={cursor}&asOfVersion={asOfVersion + 1}");
            Assert.Equal(HttpStatusCode.Conflict, stalePageResponse.StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await adminClient.GetAsync("/api/admin/v1/support/tickets?page=1")).StatusCode);
        var noViewClient = await CreateSupportAdminClientAsync(includeSupportView: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await noViewClient.GetAsync("/api/admin/v1/support/tickets")).StatusCode);

        var replayResponse = await adminClient.PostAsJsonAsync("/api/admin/v1/support/tickets", createRequest);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(createOutcome["outcomeId"]!.GetValue<string>(), (await ReadJsonAsync(replayResponse))["outcomeId"]!.GetValue<string>());

        async Task<JsonNode> SendTicketCommandAsync(string action, int version, string? replyText = null, string? status = null, Guid? assigneeId = null)
        {
            var payload = new Dictionary<string, object?>
            {
                ["ticketId"] = ticketId,
                ["action"] = action
            };
            if (replyText is not null) { payload["replyText"] = replyText; }
            if (status is not null) { payload["status"] = status; }
            if (assigneeId.HasValue) { payload["assigneeId"] = assigneeId.Value; }
            var operationId = Guid.NewGuid();
            var request = new
            {
                meta = new
                {
                    clientOperationId = operationId,
                    operationType = "support.reply",
                    fingerprintVersion = 1,
                    payloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex,
                    expectedVersion = version,
                    policyVersion = 0,
                    reason = $"Canonical support {action.ToLowerInvariant()} fixture"
                },
                payload
            };
            operationIds.Add(operationId);
            using var response = await adminClient.PostAsJsonAsync($"/api/admin/v1/support/tickets/{ticketId:D}/commands", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await ReadJsonAsync(response);
        }

        var reply = await SendTicketCommandAsync("REPLY", 1, replyText: "Staff reply persisted.");
        Assert.Equal("SUCCEEDED", reply["state"]!.GetValue<string>());
        var status = await SendTicketCommandAsync("SET_STATUS", 2, status: SupportTicket.StatusInProgress);
        Assert.Equal("SUCCEEDED", status["state"]!.GetValue<string>());
        var assignment = await SendTicketCommandAsync("ASSIGN", 3, assigneeId: assignee.Id);
        Assert.Equal("SUCCEEDED", assignment["state"]!.GetValue<string>());

        using var readDb = GetDbContext();
        var ticket = await readDb.SupportTickets.SingleAsync(x => x.Id == ticketId);
        Assert.Equal(SupportTicket.StatusInProgress, ticket.Status);
        Assert.Equal(assignee.Id, ticket.AssignedAdminUserId);
        using var events = JsonDocument.Parse(ticket.RepliesJson);
        Assert.Equal(3, events.RootElement.GetArrayLength());
        Assert.Equal(SupportReplyDto.KindMessage, events.RootElement[0].GetProperty("Kind").GetString());
        Assert.Equal(SupportReplyDto.KindStatusChanged, events.RootElement[1].GetProperty("Kind").GetString());
        Assert.Equal(SupportReplyDto.KindAssignmentChanged, events.RootElement[2].GetProperty("Kind").GetString());
        Assert.Equal(4, await readDb.CentralOutboxMessages.CountAsync(x => operationIds.Contains(x.Id)));
        Assert.Equal(4, await readDb.CentralAuditRecords.CountAsync(x => x.ResourceId == ticketId.ToString("D")));
    }

    [Fact]
    public async Task CONE_T18_SupportCreateAndReply_RequireTheirExactPersistedCapabilities()
    {
        RateLimitingMiddleware.Reset();
        var shop = Shop.Create(Guid.NewGuid(), $"SC_{Guid.NewGuid():N}"[..10], "Support capability shop", DateTimeOffset.UtcNow);
        await using (var seed = GetDbContext())
        {
            seed.Shops.Add(shop);
            await seed.SaveChangesAsync();
        }

        var subject = $"Support create capability {Guid.NewGuid():N}";
        var deniedCreatePayload = new
        {
            shopId = shop.Id,
            subject,
            summary = "A caller without support.create must not persist a ticket.",
            attachmentObjectIds = Array.Empty<Guid>(),
            redactionProfileVersion = 1
        };
        var deniedCreateOperationId = Guid.NewGuid();
        var deniedCreateClient = await CreateSupportAdminClientAsync(includeSupportCreate: false);
        using (var deniedCreate = await deniedCreateClient.PostAsJsonAsync("/api/admin/v1/support/tickets", new
        {
            meta = new
            {
                clientOperationId = deniedCreateOperationId,
                operationType = "support.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(deniedCreatePayload).Sha256Hex,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Denied support.create capability fixture"
            },
            payload = deniedCreatePayload
        }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedCreate.StatusCode);
        }

        var creator = await CreateSupportAdminClientAsync();
        var createdPayload = new
        {
            shopId = shop.Id,
            subject,
            summary = "A capable administrator creates the persisted support ticket.",
            attachmentObjectIds = Array.Empty<Guid>(),
            redactionProfileVersion = 1
        };
        var createOperationId = Guid.NewGuid();
        using var createdResponse = await creator.PostAsJsonAsync("/api/admin/v1/support/tickets", new
        {
            meta = new
            {
                clientOperationId = createOperationId,
                operationType = "support.create",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(createdPayload).Sha256Hex,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Authorized support.create fixture"
            },
            payload = createdPayload
        });
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        var createdOutcome = await ReadJsonAsync(createdResponse);
        var ticketId = Guid.Parse(createdOutcome["resultReference"]!.GetValue<string>()["support/tickets/".Length..]);

        var replyText = "This reply must be denied without support.reply.";
        var replyPayload = new { ticketId, action = "REPLY", replyText };
        var deniedReplyOperationId = Guid.NewGuid();
        var noReplyClient = await CreateSupportAdminClientAsync(includeSupportReply: false);
        using (var deniedReply = await noReplyClient.PostAsJsonAsync($"/api/admin/v1/support/tickets/{ticketId:D}/commands", new
        {
            meta = new
            {
                clientOperationId = deniedReplyOperationId,
                operationType = "support.reply",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(replyPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Denied support.reply capability fixture"
            },
            payload = replyPayload
        }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedReply.StatusCode);
        }

        await using var verify = GetDbContext();
        var persisted = await verify.SupportTickets.AsNoTracking().SingleAsync(x => x.Id == ticketId);
        Assert.Equal(1, persisted.RowVersion);
        Assert.Equal("[]", persisted.RepliesJson);
        Assert.False(await verify.SupportTickets.AnyAsync(x => x.Subject == subject && x.Id != ticketId));
        Assert.True(await verify.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == createOperationId && x.State == OperationState.SUCCEEDED));
        Assert.False(await verify.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == deniedCreateOperationId || x.ClientOperationId == deniedReplyOperationId));
        Assert.False(await verify.CentralOutboxMessages.AnyAsync(x => x.Id == deniedCreateOperationId || x.Id == deniedReplyOperationId));
        Assert.Equal(1, await verify.CentralAuditRecords.CountAsync(x => x.Action == "SUPPORT_TICKET_CREATED" && x.ResourceId == ticketId.ToString("D")));
    }

    [Fact]
    public async Task CONE_T17_RehearseRestore_IsUnavailableAndDoesNotAcceptCustomerKey()
    {
        RateLimitingMiddleware.Reset();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/backups/rehearse");
        request.Content = JsonContent.Create(new
        {
            shopId = Guid.NewGuid(),
            backupObjectId = Guid.NewGuid(),
            encryptionKeyHex = new string('f', 64),
            targetDatabaseName = "central054_test"
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("CUSTOMER_RESTORE_NOT_SUPPORTED", body["code"]?.GetValue<string>());
        Assert.Equal("restore.edge_verifier_required", body["safeMessageKey"]?.GetValue<string>());
    }
}
