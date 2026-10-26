using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgeRetails.Central.Api.Controllers;
using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Api;

public sealed class CanonicalEvidenceUploadApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ICanonicalJsonSerializer _serializer = new CanonicalJsonSerializer();

    public CanonicalEvidenceUploadApiIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private CentralDbContext GetDbContext()
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CentralDbContext>();
    }

    [Fact]
    public async Task CanonicalUpload_InitUsesAdminCapabilityAndPersistedInstallation_FinalizePublishesCiphertext()
    {
        RateLimitingMiddleware.Reset();
        var policyId = Guid.Parse("d0d8959b-278f-4cda-a81c-3eb88732f071");
        Environment.SetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_POLICY_ID", policyId.ToString("D"));
        Environment.SetEnvironmentVariable("CENTRAL_STORAGE_RETENTION_DAYS", "30");
        Environment.SetEnvironmentVariable("CENTRAL_STORAGE_LEGAL_HOLD_ENABLED", "false");
        Environment.SetEnvironmentVariable("CENTRAL_UPLOAD_SESSION_TTL_SECONDS", "600");
        Environment.SetEnvironmentVariable("CENTRAL_UPLOAD_MAX_OBJECT_BYTES", "104857600");
        Environment.SetEnvironmentVariable("CENTRAL_UPLOAD_MAX_CHUNK_BYTES", "10485760");

        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var customer = Customer.Create($"Synthetic evidence customer {Guid.NewGuid():N}", $"evidence-{Guid.NewGuid():N}@example.invalid", string.Empty, now);
        using var key = RSA.Create(2048);
        var publicKey = key.ExportRSAPublicKeyPem();
        var shop = Shop.Create(customer.Id, $"EU_{Guid.NewGuid():N}"[..10], "Synthetic evidence upload shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        var plan = PlanCatalogItem.Create($"EP{Guid.NewGuid():N}"[..12], "Synthetic Evidence Plan", PlanCatalogItem.TargetBaseLicense,
            PlanCatalogItem.TermFixed, "P1M", 100m, "USD");
        var pricingSnapshot = PricingSnapshot.Create(shopId, plan, 1, now);
        var order = Order.CreateFromSnapshot(pricingSnapshot, customer.Id, now);
        var foreignShopId = Guid.NewGuid();
        var foreignCustomer = Customer.Create($"Synthetic foreign evidence customer {Guid.NewGuid():N}", $"foreign-evidence-{Guid.NewGuid():N}@example.invalid", string.Empty, now);
        var foreignShop = Shop.Create(foreignCustomer.Id, $"EU_{Guid.NewGuid():N}"[..10], "Synthetic foreign evidence shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(foreignShop, foreignShopId);
        var foreignSnapshot = PricingSnapshot.Create(foreignShopId, plan, 1, now);
        var foreignOrder = Order.CreateFromSnapshot(foreignSnapshot, foreignCustomer.Id, now);
        var installation = Installation.Register(shopId, licenseId, "SYNTHETIC_UPLOAD_FP", publicKey,
            "upload-test-host", "Windows 11", DateTimeOffset.UtcNow);
        installation.Approve(DateTimeOffset.UtcNow);
        typeof(Installation).GetProperty("Id")!.SetValue(installation, installationId);
        using (var db = GetDbContext())
        {
            db.Customers.Add(customer);
            db.Customers.Add(foreignCustomer);
            db.Shops.Add(shop);
            db.Shops.Add(foreignShop);
            db.PlanCatalogItems.Add(plan);
            db.PricingSnapshots.Add(pricingSnapshot);
            db.PricingSnapshots.Add(foreignSnapshot);
            db.Orders.Add(order);
            db.Orders.Add(foreignOrder);
            db.Installations.Add(installation);
            await db.SaveChangesAsync();
        }

        var admin = await CreateAdminClientAsync("evidence.upload.init", "evidence.upload.finalize", "payment.review", "queues.view");
        var unprivileged = await CreateAdminClientAsync();
        var ciphertext = Encoding.UTF8.GetBytes("SYNTHETIC_PAYMENT_PROOF_CIPHERTEXT_029D");
        var objectId = order.Id;
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(ciphertext));
        const string opaqueAuthentication = "  Opaque:v1/Å  ";
        var initPayload = new
        {
            shopId,
            installationId,
            objectId,
            purpose = EncryptedObjectRecord.PurposePaymentProof,
            contentLength = (long)ciphertext.Length,
            contentType = "application/octet-stream",
            ciphertextSha256 = sha256,
            localManifestAuthenticationOpaque = opaqueAuthentication,
            retentionPolicyId = policyId
        };
        var initOperationId = Guid.NewGuid();
        var initRequest = new
        {
            meta = new
            {
                clientOperationId = initOperationId,
                operationType = "evidence.upload.init",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(initPayload).Sha256Hex,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Synthetic canonical evidence upload integration fixture"
            },
            payload = initPayload
        };

        using var deniedResponse = await unprivileged.PostAsJsonAsync("/api/admin/v1/evidence/uploads", initRequest);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

        var wrongShopPayload = new
        {
            shopId = Guid.NewGuid(),
            installationId,
            objectId = Guid.NewGuid(),
            purpose = EncryptedObjectRecord.PurposePaymentProof,
            contentLength = (long)ciphertext.Length,
            contentType = "application/octet-stream",
            ciphertextSha256 = sha256,
            retentionPolicyId = policyId
        };
        var wrongShopRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "evidence.upload.init",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(wrongShopPayload).Sha256Hex,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Synthetic cross-shop denial fixture"
            },
            payload = wrongShopPayload
        };
        using var crossShop = await admin.PostAsJsonAsync("/api/admin/v1/evidence/uploads", wrongShopRequest);
        Assert.False(crossShop.IsSuccessStatusCode);
        using (var db = GetDbContext())
        {
            Assert.False(await db.UploadSessions.AnyAsync(x => x.TargetEntityId == wrongShopPayload.objectId));
        }

        var foreignOrderPayload = new
        {
            shopId,
            installationId,
            objectId = foreignOrder.Id,
            purpose = EncryptedObjectRecord.PurposePaymentProof,
            contentLength = (long)ciphertext.Length,
            contentType = "application/octet-stream",
            ciphertextSha256 = sha256,
            retentionPolicyId = policyId
        };
        var foreignOrderRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "evidence.upload.init",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(foreignOrderPayload).Sha256Hex,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Synthetic cross-shop order denial fixture"
            },
            payload = foreignOrderPayload
        };
        using (var foreignOrderResponse = await admin.PostAsJsonAsync("/api/admin/v1/evidence/uploads", foreignOrderRequest))
        {
            Assert.False(foreignOrderResponse.IsSuccessStatusCode);
        }
        using (var db = GetDbContext())
        {
            Assert.False(await db.UploadSessions.AnyAsync(x => x.TargetEntityId == foreignOrder.Id));
        }

        using var initResponse = await admin.PostAsJsonAsync("/api/admin/v1/evidence/uploads", initRequest);
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var sessionJson = await ReadJsonAsync(initResponse);
        var uploadId = Guid.Parse(sessionJson["uploadId"]!.GetValue<string>());
        Assert.Equal(objectId.ToString("D"), sessionJson["authorizedObjectId"]!.GetValue<string>());
        Assert.Equal($"/api/licensing/v1/uploads/{uploadId:D}/content", sessionJson["chunkRoute"]!.GetValue<string>());
        Assert.Equal($"/api/admin/v1/evidence/uploads/{uploadId:D}/finalize", sessionJson["finalizeRoute"]!.GetValue<string>());

        using (var initReplay = await admin.PostAsJsonAsync("/api/admin/v1/evidence/uploads", initRequest))
        {
            Assert.Equal(HttpStatusCode.OK, initReplay.StatusCode);
            Assert.Equal(uploadId, Guid.Parse((await ReadJsonAsync(initReplay))["uploadId"]!.GetValue<string>()));
        }

        var keyId = Installation.ComputePublicKeyFingerprintSha256(publicKey);
        using var installationClient = _factory.CreateDefaultClient();
        installationClient.BaseAddress = new Uri("https://localhost");
        var firstChunk = ciphertext[..(ciphertext.Length / 2)];
        var secondChunk = ciphertext[firstChunk.Length..];
        var firstChunkSha256 = Convert.ToHexStringLower(SHA256.HashData(firstChunk));
        using (var chunkRequest = new HttpRequestMessage(HttpMethod.Put, sessionJson["chunkRoute"]!.GetValue<string>()))
        {
            chunkRequest.Content = new ByteArrayContent(firstChunk);
            chunkRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            chunkRequest.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, firstChunk.Length - 1, ciphertext.Length);
            chunkRequest.Headers.Add("chunk-sha256", firstChunkSha256);
            AddInstallationProof(chunkRequest, installationId, shopId, licenseId, keyId, firstChunk, key);
            using var chunkResponse = await installationClient.SendAsync(chunkRequest);
            Assert.Equal(HttpStatusCode.OK, chunkResponse.StatusCode);
        }

        // A lost response may cause the device to resend its last accepted range. The exact
        // same bytes must be idempotent, while different bytes at that range must be rejected.
        using (var exactRetry = new HttpRequestMessage(HttpMethod.Put, sessionJson["chunkRoute"]!.GetValue<string>()))
        {
            exactRetry.Content = new ByteArrayContent(firstChunk);
            exactRetry.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            exactRetry.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, firstChunk.Length - 1, ciphertext.Length);
            exactRetry.Headers.Add("chunk-sha256", firstChunkSha256);
            AddInstallationProof(exactRetry, installationId, shopId, licenseId, keyId, firstChunk, key);
            using var retryResponse = await installationClient.SendAsync(exactRetry);
            Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
            Assert.Equal(firstChunk.Length, (await ReadJsonAsync(retryResponse))["acceptedOffset"]!.GetValue<long>());
        }

        var conflictingRetryBytes = firstChunk.ToArray();
        conflictingRetryBytes[^1] ^= 0x01;
        using (var conflictingRetry = new HttpRequestMessage(HttpMethod.Put, sessionJson["chunkRoute"]!.GetValue<string>()))
        {
            conflictingRetry.Content = new ByteArrayContent(conflictingRetryBytes);
            conflictingRetry.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            conflictingRetry.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, conflictingRetryBytes.Length - 1, ciphertext.Length);
            conflictingRetry.Headers.Add("chunk-sha256", Convert.ToHexStringLower(SHA256.HashData(conflictingRetryBytes)));
            AddInstallationProof(conflictingRetry, installationId, shopId, licenseId, keyId, conflictingRetryBytes, key);
            using var conflictResponse = await installationClient.SendAsync(conflictingRetry);
            Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        }

        using (var resumedChunk = new HttpRequestMessage(HttpMethod.Put, sessionJson["chunkRoute"]!.GetValue<string>()))
        {
            resumedChunk.Content = new ByteArrayContent(secondChunk);
            resumedChunk.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            resumedChunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(firstChunk.Length, ciphertext.Length - 1, ciphertext.Length);
            resumedChunk.Headers.Add("chunk-sha256", Convert.ToHexStringLower(SHA256.HashData(secondChunk)));
            AddInstallationProof(resumedChunk, installationId, shopId, licenseId, keyId, secondChunk, key);
            using var resumeResponse = await installationClient.SendAsync(resumedChunk);
            Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);
            Assert.Equal(ciphertext.Length, (await ReadJsonAsync(resumeResponse))["acceptedOffset"]!.GetValue<long>());
        }

        int uploadVersion;
        using (var db = GetDbContext())
        {
            uploadVersion = (await db.UploadSessions.AsNoTracking().SingleAsync(x => x.Id == uploadId)).RowVersion;
        }

        var mismatchedFinalizePayload = new { uploadId = Guid.NewGuid(), objectId, expectedLength = (long)ciphertext.Length, expectedSha256 = sha256 };
        var mismatchedFinalize = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "evidence.upload.finalize",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(mismatchedFinalizePayload).Sha256Hex,
                expectedVersion = 0,
                policyVersion = 0,
                reason = "Synthetic route and body identity mismatch fixture"
            },
            payload = mismatchedFinalizePayload
        };
        using (var mismatchResponse = await admin.PostAsJsonAsync($"/api/admin/v1/evidence/uploads/{uploadId:D}/finalize", mismatchedFinalize))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mismatchResponse.StatusCode);
        }

        var finalizePayload = new { uploadId, objectId, expectedLength = (long)ciphertext.Length, expectedSha256 = sha256 };
        var staleFinalizeRequest = new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "evidence.upload.finalize",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(finalizePayload).Sha256Hex,
                expectedVersion = Math.Max(0, uploadVersion - 1),
                policyVersion = 0,
                reason = "Synthetic stale upload revision denial fixture"
            },
            payload = finalizePayload
        };
        using (var staleFinalize = await admin.PostAsJsonAsync($"/api/admin/v1/evidence/uploads/{uploadId:D}/finalize", staleFinalizeRequest))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleFinalize.StatusCode);
            Assert.Equal("CONCURRENCY_CONFLICT", (await ReadJsonAsync(staleFinalize))["code"]!.GetValue<string>());
        }

        var finalizeOperationId = Guid.NewGuid();
        var finalizeRequest = new
        {
            meta = new
            {
                clientOperationId = finalizeOperationId,
                operationType = "evidence.upload.finalize",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(finalizePayload).Sha256Hex,
                expectedVersion = uploadVersion,
                policyVersion = 0,
                reason = "Synthetic canonical evidence finalize integration fixture"
            },
            payload = finalizePayload
        };
        using var finalizeResponse = await admin.PostAsJsonAsync($"/api/admin/v1/evidence/uploads/{uploadId:D}/finalize", finalizeRequest);
        Assert.Equal(HttpStatusCode.OK, finalizeResponse.StatusCode);
        var finalizeOutcome = await ReadJsonAsync(finalizeResponse);
        Assert.Equal("SUCCEEDED", finalizeOutcome["state"]!.GetValue<string>());
        using (var finalizeReplay = await admin.PostAsJsonAsync($"/api/admin/v1/evidence/uploads/{uploadId:D}/finalize", finalizeRequest))
        {
            Assert.Equal(HttpStatusCode.OK, finalizeReplay.StatusCode);
            Assert.Equal(finalizeOutcome["outcomeId"]!.GetValue<string>(), (await ReadJsonAsync(finalizeReplay))["outcomeId"]!.GetValue<string>());
        }

        using (var db = GetDbContext())
        {
            var stored = await db.EncryptedObjects.AsNoTracking().SingleAsync(x => x.Id == objectId);
            var persistedSession = await db.UploadSessions.AsNoTracking().SingleAsync(x => x.Id == uploadId);
            var evidence = await db.PaymentEvidences.AsNoTracking().SingleAsync(x => x.UploadSessionId == uploadId);
            Assert.Equal(shopId, stored.ShopId);
            Assert.Equal(installationId, stored.InstallationId);
            Assert.Equal(order.Id, stored.Id);
            Assert.Equal(sha256, stored.CiphertextSha256);
            Assert.Equal(opaqueAuthentication, stored.LocalManifestAuthenticationOpaque);
            Assert.NotEqual("HMAC-SHA256:VALIDATED", stored.LocalManifestAuthenticationOpaque);
            Assert.Equal(EncryptedObjectRecord.PurposePaymentProof, stored.Purpose);
            Assert.Equal(opaqueAuthentication, persistedSession.LocalManifestAuthenticationOpaque);
            Assert.Equal(UploadSession.StatusFinalized, persistedSession.Status);
            Assert.Equal(order.Id, evidence.OrderId);
            Assert.Equal(shopId, evidence.ShopId);
            Assert.Equal(persistedSession.Id, evidence.UploadSessionId);
            Assert.Equal(sha256, evidence.Sha256Digest);
            Assert.Equal(PaymentEvidence.ReviewPending, evidence.ReviewStatus);
            Assert.True(await db.CentralOutboxMessages.AnyAsync(x => x.Id == evidence.Id && x.EventType == "payment.evidence_submitted"));
            Assert.Equal(1, await db.CentralOperationRecords.CountAsync(x => x.ClientOperationId == initOperationId));
            Assert.Equal(1, await db.CentralOperationRecords.CountAsync(x => x.ClientOperationId == finalizeOperationId));
        }

        Guid proofId;
        using (var db = GetDbContext())
        {
            proofId = await db.PaymentEvidences.AsNoTracking().Where(x => x.UploadSessionId == uploadId).Select(x => x.Id).SingleAsync();
        }
        using (var queueResponse = await admin.GetAsync("/api/admin/v1/queues/PAYMENT_REVIEW?pageSize=20"))
        {
            Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);
            var queue = await ReadJsonAsync(queueResponse);
            var item = queue["items"]!.AsArray().Single(x => x!["entityId"]!.GetValue<string>() == proofId.ToString("D"));
            Assert.Equal(order.Id.ToString("D"), item!["orderId"]!.GetValue<string>());
            Assert.Equal(1, item["resourceVersion"]!.GetValue<int>());
        }

        var reviewPayload = new { orderId = order.Id, proofId, decision = "APPROVE", reasonCode = "SYNTHETIC_ENCRYPTED_PROOF_CONFIRMED" };
        var reviewOperationId = Guid.NewGuid();
        var reviewRequest = new
        {
            meta = new
            {
                clientOperationId = reviewOperationId,
                operationType = "payment.review",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(reviewPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Synthetic encrypted payment evidence review fixture"
            },
            payload = reviewPayload
        };
        string reviewOutcomeId;
        using (var reviewResponse = await admin.PostAsJsonAsync($"/api/admin/v1/payments/{proofId:D}/review", reviewRequest))
        {
            Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
            var reviewOutcome = await ReadJsonAsync(reviewResponse);
            Assert.Equal("SUCCEEDED", reviewOutcome["state"]!.GetValue<string>());
            reviewOutcomeId = reviewOutcome["outcomeId"]!.GetValue<string>();
        }
        using (var reviewReplay = await admin.PostAsJsonAsync($"/api/admin/v1/payments/{proofId:D}/review", reviewRequest))
        {
            Assert.Equal(HttpStatusCode.OK, reviewReplay.StatusCode);
            Assert.Equal(reviewOutcomeId, (await ReadJsonAsync(reviewReplay))["outcomeId"]!.GetValue<string>());
        }
        var staleOperationId = Guid.NewGuid();
        var staleReviewRequest = new
        {
            meta = new
            {
                clientOperationId = staleOperationId,
                operationType = "payment.review",
                fingerprintVersion = 1,
                payloadSha256 = _serializer.SerializeAndHash(reviewPayload).Sha256Hex,
                expectedVersion = 1,
                policyVersion = 0,
                reason = "Stale canonical encrypted proof revision must not create a second review"
            },
            payload = reviewPayload
        };
        using (var staleReview = await admin.PostAsJsonAsync($"/api/admin/v1/payments/{proofId:D}/review", staleReviewRequest))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleReview.StatusCode);
        }
        using (var db = GetDbContext())
        {
            Assert.Equal(PaymentEvidence.ReviewApproved, (await db.PaymentEvidences.AsNoTracking().SingleAsync(x => x.Id == proofId)).ReviewStatus);
            Assert.Equal(Order.StatusPaidApproved, (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == order.Id)).Status);
            Assert.Equal(1, await db.PaymentReviewRecords.AsNoTracking().CountAsync(x => x.EvidenceId == proofId));
            Assert.Equal(0, await db.CentralOperationRecords.AsNoTracking().CountAsync(x => x.ClientOperationId == staleOperationId));
        }
    }

    private async Task<HttpClient> CreateAdminClientAsync(params string[] capabilities)
    {
        const string password = "AdminPassword#123";
        var username = $"canonical_upload_{Guid.NewGuid():N}";
        var totp = _factory.Services.GetRequiredService<ITotpService>();
        var hasher = _factory.Services.GetRequiredService<IPasswordHasher>();
        var secret = totp.GenerateSecret();
        using (var db = GetDbContext())
        {
            var user = AdminUser.Create(username, hasher.HashPassword(password), "salt", secret, true, DateTimeOffset.UtcNow);
            var role = AdminRole.Create($"canonical_upload_role_{Guid.NewGuid():N}", "Canonical upload", capabilities);
            db.AdminUsers.Add(user);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateDefaultClient(new CanonicalFingerprintDelegatingHandler());
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
        Assert.True(mfa.Headers.TryGetValues("Set-Cookie", out var cookies));
        client.DefaultRequestHeaders.Add("Cookie", cookies.Select(x => x.Split(';', 2)[0])
            .Single(x => x.StartsWith("central_session=", StringComparison.Ordinal)));
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return client;
    }

    private void AddInstallationProof(HttpRequestMessage request, Guid installationId, Guid shopId, Guid licenseId,
        string keyId, byte[] content, RSA privateKey)
    {
        Environment.SetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS", "120");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid();
        var range = request.Content!.Headers.ContentRange!;
        var proof = new Dictionary<string, object?>
        {
            ["profile"] = "CENTRAL_INSTALLATION_BINARY_REQUEST_V1",
            ["purpose"] = "installation.request",
            ["keyId"] = keyId,
            ["installationId"] = installationId,
            ["shopId"] = shopId,
            ["licenseId"] = licenseId,
            ["timestamp"] = timestamp,
            ["nonce"] = nonce,
            ["httpMethod"] = request.Method.Method.ToUpperInvariant(),
            ["httpPath"] = request.RequestUri!.OriginalString,
            ["bodyDigest"] = Convert.ToHexStringLower(SHA256.HashData(content)),
            ["binaryRange"] = new
            {
                from = range.From,
                to = range.To,
                totalLength = range.Length,
                contentLength = request.Content.Headers.ContentLength,
                chunkSha256 = request.Headers.GetValues("chunk-sha256").Single()
            }
        };
        var canonical = _serializer.SerializeCanonical(proof);
        request.Headers.Add("X-Installation-Id", installationId.ToString("D"));
        request.Headers.Add("X-Installation-Key-Id", keyId);
        request.Headers.Add("X-Installation-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Installation-Nonce", nonce.ToString("D"));
        request.Headers.Add("X-Installation-Signature", Convert.ToBase64String(privateKey.SignData(
            Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("Expected a JSON response.");
}
