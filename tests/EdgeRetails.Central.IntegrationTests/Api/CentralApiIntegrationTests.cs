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
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Signing;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Api;

public class CentralApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly ICanonicalJsonSerializer _serializer = new CanonicalJsonSerializer();
    private readonly ITotpService _totpService = new TotpService();
    private readonly IPasswordHasher _passwordHasher = new PasswordHasher();
    private readonly string? _previousDualApprovalActions;

    public CentralApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _previousDualApprovalActions = Environment.GetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS");
        Environment.SetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS", "NONE"); // labelled synthetic integration policy fixture
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });

        RateLimitingMiddleware.Reset();
    }

    private CentralDbContext GetDbContext()
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CentralDbContext>();
    }

    private void AddInstallationRequestProof(HttpRequestMessage request, Installation installation, RSA privateKey, RSA? signingKey = null, DateTimeOffset? timestampUtc = null)
    {
        var keyId = installation.PublicKeyFingerprintSha256;
        var timestamp = (timestampUtc ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var nonce = Guid.NewGuid();
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
        var bodyDigest = _serializer.ComputeSha256Hex(_serializer.SerializeCanonical(body));
        var envelope = _serializer.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_REQUEST_V1",
            purpose = "installation.request",
            keyId,
            installationId = installation.Id,
            shopId = installation.ShopId,
            licenseId = installation.LicenseId,
            timestamp,
            nonce,
            httpMethod = request.Method.Method.ToUpperInvariant(),
            httpPath = request.RequestUri!.OriginalString,
            bodyDigest
        });
        var signature = (signingKey ?? privateKey).SignData(Encoding.UTF8.GetBytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.Headers.Add("X-Installation-Id", installation.Id.ToString("D"));
        request.Headers.Add("X-Installation-Key-Id", keyId);
        request.Headers.Add("X-Installation-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Installation-Nonce", nonce.ToString("D"));
        request.Headers.Add("X-Installation-Signature", Convert.ToBase64String(signature));
    }

    [Fact]
    public async Task Auth_StartLogin_ValidCredentials_ReturnsPreAuthChallengeWithNoBusinessAuthority()
    {
        var username = $"admin_{Guid.NewGuid():N}";
        const string password = "TestPassword#123";
        var totpSecret = _totpService.GenerateSecret();

        using (var db = GetDbContext())
        {
            var user = AdminUser.Create(
                username,
                _passwordHasher.HashPassword(password),
                "salt",
                totpSecret,
                true,
                DateTimeOffset.UtcNow);

            db.AdminUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync("/api/admin/v1/auth/login", new LoginRequestDto
        {
            Username = username,
            PasswordOrProviderAssertion = password
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.NotNull(json);

        Assert.Equal("MFA_CHALLENGE", json["kind"]?.GetValue<string>());
        Assert.False(json["businessAuthority"]?.GetValue<bool>());
        Assert.NotNull(json["challengeId"]?.GetValue<string>());
    }

    [Fact]
    public async Task Auth_StartLogin_InvalidCredentials_Returns401Unauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/admin/v1/auth/login", new LoginRequestDto
        {
            Username = "nonexistent_admin",
            PasswordOrProviderAssertion = "WrongPassword#999"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.NotNull(json);
        Assert.Equal("INVALID_CREDENTIALS", json["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task Auth_CompleteMfa_ValidTotp_SetsSecureSessionCookie_AndGrantsCapabilities()
    {
        var username = $"admin_mfa_{Guid.NewGuid():N}";
        const string password = "TestPassword#123";
        var totpSecret = _totpService.GenerateSecret();
        var now = DateTimeOffset.UtcNow;

        using (var db = GetDbContext())
        {
            var user = AdminUser.Create(
                username,
                _passwordHasher.HashPassword(password),
                "salt",
                totpSecret,
                true,
                now);

            var role = AdminRole.Create($"ManagerRole_{Guid.NewGuid():N}", "Manager role", new[] { AdminCapabilities.AdminManage, "sessions.view" });
            db.AdminRoles.Add(role);
            db.AdminUsers.Add(user);
            db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, now));
            await db.SaveChangesAsync();
        }

        // Step 1: Start login
        var loginResponse = await _client.PostAsJsonAsync("/api/admin/v1/auth/login", new LoginRequestDto
        {
            Username = username,
            PasswordOrProviderAssertion = password
        });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonNode>();
        var challengeId = Guid.Parse(loginJson!["challengeId"]!.GetValue<string>());

        // Step 2: Complete MFA
        var validTotp = _totpService.GenerateCode(totpSecret, DateTimeOffset.UtcNow);
        var mfaResponse = await _client.PostAsJsonAsync("/api/admin/v1/auth/mfa", new MfaRequestDto
        {
            LoginChallengeId = challengeId,
            Proof = validTotp
        });

        Assert.Equal(HttpStatusCode.OK, mfaResponse.StatusCode);

        // Verify Set-Cookie header has HttpOnly, Secure, SameSite=Strict
        Assert.True(mfaResponse.Headers.TryGetValues("Set-Cookie", out var cookieHeaders));
        var cookieHeader = cookieHeaders.First();
        Assert.Contains("central_session=", cookieHeader);
        Assert.Contains("httponly", cookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookieHeader, StringComparison.OrdinalIgnoreCase);

        var mfaJson = await mfaResponse.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("AUTHENTICATED", mfaJson!["kind"]?.GetValue<string>());
        Assert.True(mfaJson["mfaSatisfied"]?.GetValue<bool>());
        Assert.Null(mfaJson["sessionId"]);

        var capabilities = mfaJson["capabilities"]?.AsArray().Select(c => c?.GetValue<string>()).ToList();
        Assert.NotNull(capabilities);
        Assert.Contains(AdminCapabilities.AdminManage, capabilities);

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/v1/portal/session");
        sessionRequest.Headers.Add("Cookie", cookieHeader.Split(';', 2)[0]);
        using var sessionResponse = await _client.SendAsync(sessionRequest);
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        var sessionContext = await sessionResponse.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(username.ToLowerInvariant(), sessionContext!["username"]?.GetValue<string>());
    }

    [Fact]
    public async Task Transport_HttpRefusal_RejectsWith400HttpsRequired_WithZeroMutation()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        var operationId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequestDto
            {
                Username = "any_admin",
                PasswordOrProviderAssertion = "AnyPassword#123"
            })
        };
        request.Headers.Add("X-Client-Operation-Id", operationId.ToString());
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.NotNull(json);
        Assert.Equal("HTTPS_REQUIRED", json["code"]?.GetValue<string>());
        var cacheControl = response.Headers.GetValues("Cache-Control").Single();
        Assert.Contains("no-store", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must-revalidate", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-cache", response.Headers.GetValues("Pragma").Single());
        using var auditDb = GetDbContext();
        var denial = await auditDb.CentralAuditRecords.AsNoTracking().SingleAsync(record =>
            record.Action == "security.request.denied" && record.CorrelationId == operationId.ToString());
        Assert.False(denial.IsSuccess);
        Assert.Contains("HTTPS_REQUIRED", denial.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Security_CsrfProtection_BlocksCookieAuthenticatedMutationsWithoutOriginOrReferer()
    {
        // 1. Authenticate to establish session
        var (sessionId, _) = await AuthenticateAdminAsync(new[] { AdminCapabilities.AdminManage });

        var targetUserId = Guid.NewGuid();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Add("Cookie", $"central_session={sessionId}");
        // Deliberately omitting Origin and Referer headers

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(new AdminActionCommandDto
            {
                AdminUserId = targetUserId,
                Action = "DISABLE"
            })
        };
        var operationId = Guid.NewGuid();
        request.Headers.Add("X-Client-Operation-Id", operationId.ToString());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("CSRF_REJECTED", json!["code"]?.GetValue<string>());
        using var auditDb = GetDbContext();
        var denial = await auditDb.CentralAuditRecords.AsNoTracking().SingleAsync(record =>
            record.Action == "security.request.denied" && record.CorrelationId == operationId.ToString());
        Assert.Equal(sessionId, denial.SessionId);
        Assert.Equal($"/api/admin/v1/admin-users/{targetUserId}/commands", denial.ResourceId);
        Assert.False(denial.IsSuccess);
        Assert.Contains("CSRF_REJECTED", denial.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Security_CsrfProtection_RejectsMismatchedSchemeOrPortOrHost_WithZeroMutation()
    {
        var (sessionId, _) = await AuthenticateAdminAsync(new[] { AdminCapabilities.AdminManage });
        var targetUserId = Guid.NewGuid();

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Add("Cookie", $"central_session={sessionId}");
        var deniedOperations = new List<Guid>();

        // Sub-test A: Mismatched scheme (http instead of https)
        var reqWrongScheme = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(new AdminActionCommandDto { AdminUserId = targetUserId, Action = "DISABLE" })
        };
        reqWrongScheme.Headers.Add("Origin", "http://localhost");
        var wrongSchemeOperation = Guid.NewGuid();
        deniedOperations.Add(wrongSchemeOperation);
        reqWrongScheme.Headers.Add("X-Client-Operation-Id", wrongSchemeOperation.ToString());

        var resWrongScheme = await client.SendAsync(reqWrongScheme);
        Assert.Equal(HttpStatusCode.Forbidden, resWrongScheme.StatusCode);
        var jsonScheme = await resWrongScheme.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("CSRF_REJECTED", jsonScheme!["code"]?.GetValue<string>());

        // Sub-test B: Mismatched port (8080 instead of 443)
        var reqWrongPort = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(new AdminActionCommandDto { AdminUserId = targetUserId, Action = "DISABLE" })
        };
        reqWrongPort.Headers.Add("Origin", "https://localhost:8080");
        var wrongPortOperation = Guid.NewGuid();
        deniedOperations.Add(wrongPortOperation);
        reqWrongPort.Headers.Add("X-Client-Operation-Id", wrongPortOperation.ToString());

        var resWrongPort = await client.SendAsync(reqWrongPort);
        Assert.Equal(HttpStatusCode.Forbidden, resWrongPort.StatusCode);
        var jsonPort = await resWrongPort.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("CSRF_REJECTED", jsonPort!["code"]?.GetValue<string>());

        // Sub-test C: Mismatched host (attacker.com)
        var reqWrongHost = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(new AdminActionCommandDto { AdminUserId = targetUserId, Action = "DISABLE" })
        };
        reqWrongHost.Headers.Add("Origin", "https://attacker.com");
        var wrongHostOperation = Guid.NewGuid();
        deniedOperations.Add(wrongHostOperation);
        reqWrongHost.Headers.Add("X-Client-Operation-Id", wrongHostOperation.ToString());

        var resWrongHost = await client.SendAsync(reqWrongHost);
        Assert.Equal(HttpStatusCode.Forbidden, resWrongHost.StatusCode);
        var jsonHost = await resWrongHost.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("CSRF_REJECTED", jsonHost!["code"]?.GetValue<string>());

        using var auditDb = GetDbContext();
        foreach (var operationId in deniedOperations)
        {
            var denial = await auditDb.CentralAuditRecords.AsNoTracking().SingleAsync(record =>
                record.Action == "security.request.denied" && record.CorrelationId == operationId.ToString());
            Assert.Equal(sessionId, denial.SessionId);
            Assert.False(denial.IsSuccess);
            Assert.Contains("CSRF_REJECTED", denial.DetailsJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Security_RateLimiting_RejectsExcessiveLoginAttempts()
    {
        RateLimitingMiddleware.Reset();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        HttpResponseMessage? lastResponse = null;
        var rateLimitOperation = Guid.Empty;
        for (int i = 0; i < 6; i++)
        {
            var operationId = Guid.NewGuid();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/v1/auth/login")
            {
                Content = JsonContent.Create(new LoginRequestDto
                {
                    Username = "test_rate_limit",
                    PasswordOrProviderAssertion = "BadPass"
                })
            };
            request.Headers.Add("X-Client-Operation-Id", operationId.ToString());
            lastResponse = await client.SendAsync(request);
            if (i == 5) { rateLimitOperation = operationId; }
        }

        Assert.NotNull(lastResponse);
        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
        var cacheControl = lastResponse.Headers.GetValues("Cache-Control").Single();
        Assert.Contains("no-store", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must-revalidate", cacheControl, StringComparison.OrdinalIgnoreCase);

        var json = await lastResponse.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("RATE_LIMITED", json!["code"]?.GetValue<string>());
        using var auditDb = GetDbContext();
        var rateLimitDenial = await auditDb.CentralAuditRecords.AsNoTracking().SingleAsync(record =>
            record.Action == "security.request.denied" &&
            record.ResourceId == "/api/admin/v1/auth/login" &&
            record.CorrelationId == rateLimitOperation.ToString());
        Assert.Contains("RATE_LIMITED", rateLimitDenial.DetailsJson, StringComparison.Ordinal);
        Assert.False(rateLimitDenial.IsSuccess);
    }

    [Fact]
    public async Task AdminSessions_IdleAndAbsoluteExpiryBoundaries_AreRejectedByHostedPortalSession()
    {
        var now = DateTimeOffset.UtcNow;
        Guid idleExpiredSessionId;
        Guid absoluteExpiredSessionId;
        using (var db = GetDbContext())
        {
            var admin = AdminUser.Create(
                $"expiry_admin_{Guid.NewGuid():N}",
                _passwordHasher.HashPassword("ExpiryTest#123"),
                "salt",
                _totpService.GenerateSecret(),
                true,
                now);

            // Labelled compatibility fixtures from the current implementation: 15-minute idle,
            // 8-hour absolute. The hosted API must reject either independently expired boundary.
            var idleExpired = AdminSession.Create(
                admin.Id,
                $"idle-expired-{Guid.NewGuid():N}",
                now.AddMinutes(-16),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(8),
                true,
                admin.SecurityRevision,
                ["dashboard.view"]);
            var absoluteExpired = AdminSession.Create(
                admin.Id,
                $"absolute-expired-{Guid.NewGuid():N}",
                now.AddHours(-8).AddSeconds(-1),
                TimeSpan.FromHours(9),
                TimeSpan.FromHours(8),
                true,
                admin.SecurityRevision,
                ["dashboard.view"]);

            db.AdminUsers.Add(admin);
            db.AdminSessions.AddRange(idleExpired, absoluteExpired);
            await db.SaveChangesAsync();
            idleExpiredSessionId = idleExpired.Id;
            absoluteExpiredSessionId = absoluteExpired.Id;
        }

        foreach (var sessionId in new[] { idleExpiredSessionId, absoluteExpiredSessionId })
        {
            using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
            client.DefaultRequestHeaders.Add("Cookie", $"central_session={sessionId}");

            using var response = await client.GetAsync("/api/admin/v1/portal/session");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task AdminSessions_ExplicitlyRevokedSession_IsRejectedByHostedPortalSession()
    {
        var now = DateTimeOffset.UtcNow;
        Guid revokedSessionId;
        using (var db = GetDbContext())
        {
            var admin = AdminUser.Create(
                $"revoked_session_admin_{Guid.NewGuid():N}",
                _passwordHasher.HashPassword("RevokedSession#123"),
                "salt",
                _totpService.GenerateSecret(),
                true,
                now);
            var session = AdminSession.Create(
                admin.Id,
                $"revoked-session-{Guid.NewGuid():N}",
                now,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(8),
                true,
                admin.SecurityRevision,
                ["dashboard.view"]);
            session.Revoke("Revoked-session integration fixture", now);

            db.AdminUsers.Add(admin);
            db.AdminSessions.Add(session);
            await db.SaveChangesAsync();
            revokedSessionId = session.Id;
        }

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Add("Cookie", $"central_session={revokedSessionId}");

        using var response = await client.GetAsync("/api/admin/v1/portal/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var verify = GetDbContext();
        var persisted = await verify.AdminSessions.AsNoTracking().SingleAsync(x => x.Id == revokedSessionId);
        Assert.True(persisted.IsRevoked);
        Assert.NotNull(persisted.RevokedAtUtc);
    }
    [Fact]
    public async Task AdminUsers_AssignRoleCommand_ExecutesAndIdempotentlyReplays()
    {
        RateLimitingMiddleware.Reset();
        var (sessionId, callerUserId) = await AuthenticateAdminAsync(new[] { AdminCapabilities.AdminManage, AdminCapabilities.AdminUsersRead });

        Guid targetUserId;
        Guid targetRoleId;
        Guid targetSessionId;
        using (var db = GetDbContext())
        {
            var targetUser = AdminUser.Create(
                $"target_admin_{Guid.NewGuid():N}",
                _passwordHasher.HashPassword("TargetPass#123"),
                "salt",
                _totpService.GenerateSecret(),
                true,
                DateTimeOffset.UtcNow);

            var role = AdminRole.Create($"TestRole_{Guid.NewGuid():N}", "Description", new[] { AdminCapabilities.AdminUsersRead });
            var targetSession = AdminSession.Create(targetUser.Id, $"role-change-{Guid.NewGuid():N}", DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, targetUser.SecurityRevision, ["dashboard.view", "sessions.view"]);
            db.AdminUsers.Add(targetUser);
            db.AdminRoles.Add(role);
            db.AdminSessions.Add(targetSession);
            await db.SaveChangesAsync();

            targetUserId = targetUser.Id;
            targetRoleId = role.Id;
            targetSessionId = targetSession.Id;
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"central_session={sessionId}");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionId.ToString());

        using var targetClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        targetClient.DefaultRequestHeaders.Add("Cookie", $"central_session={targetSessionId}");
        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/api/admin/v1/portal/session")).StatusCode);

        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto
        {
            AdminUserId = targetUserId,
            Action = "ASSIGN_ROLE",
            RoleId = targetRoleId
        };

        var (_, sha256) = _serializer.SerializeAndHash(command);
        var canonicalBody = new
        {
            meta = new
            {
                clientOperationId = opId,
                operationType = "admin.manage",
                fingerprintVersion = 1,
                payloadSha256 = sha256,
                expectedVersion = 1,
                policyVersion = 1,
                reason = "Assigning role via canonical contract integration test"
            },
            payload = command
        };

        var roleGrant = await CreateGrantAsync(sessionId, "admin.user.assign_role", sha256);
        var badDigestRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(new
            {
                meta = new
                {
                    clientOperationId = opId,
                    operationType = "admin.manage",
                    fingerprintVersion = 1,
                    payloadSha256 = new string('0', 64),
                    expectedVersion = 1,
                    policyVersion = 1,
                    reason = "Digest mismatch must not mutate"
                },
                payload = command
            })
        };
        badDigestRequest.Headers.Add("X-Step-Up-Grant-Id", roleGrant.ToString());
        var badDigestResponse = await client.SendAsync(badDigestRequest);
        Assert.Equal(HttpStatusCode.BadRequest, badDigestResponse.StatusCode);

        // First execution
        var request1 = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(canonicalBody)
        };
        request1.Headers.Add("X-Step-Up-Grant-Id", roleGrant.ToString());
        request1.Headers.Add("X-Client-Operation-Id", opId.ToString());
        request1.Headers.Add("X-Operation-Type", "admin.manage");
        request1.Headers.Add("X-Payload-Sha256", sha256);
        request1.Headers.Add("X-Expected-Version", "1");
        request1.Headers.Add("X-Policy-Version", "1");
        request1.Headers.Add("X-Reason", "Assigning role via API integration test");

        var response1 = await client.SendAsync(request1);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        var outcome1 = await response1.Content.ReadFromJsonAsync<OutcomeDto>();
        Assert.NotNull(outcome1);
        Assert.Equal("SUCCEEDED", outcome1.State);
        Assert.Equal($"admin_users/{targetUserId}", outcome1.ResultReference);

        // Replay identical command with identical ClientOperationId
        var request2 = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(canonicalBody)
        };
        request2.Headers.Add("X-Step-Up-Grant-Id", roleGrant.ToString());
        request2.Headers.Add("X-Client-Operation-Id", opId.ToString());
        request2.Headers.Add("X-Operation-Type", "admin.manage");
        request2.Headers.Add("X-Payload-Sha256", sha256);
        request2.Headers.Add("X-Expected-Version", "1");
        request2.Headers.Add("X-Policy-Version", "1");
        request2.Headers.Add("X-Reason", "Assigning role via API integration test");

        var response2 = await client.SendAsync(request2);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        var outcome2 = await response2.Content.ReadFromJsonAsync<OutcomeDto>();
        Assert.NotNull(outcome2);
        Assert.Equal(outcome1.OutcomeId, outcome2.OutcomeId);
        Assert.Equal(outcome1.ResultReference, outcome2.ResultReference);

        // A persisted role change bumps the target user's security revision, invalidating every
        // session carrying the prior capability snapshot before it can access another route.
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/admin/v1/portal/session")).StatusCode);
        using (var db = GetDbContext())
        {
            var updatedTarget = await db.AdminUsers.AsNoTracking().SingleAsync(x => x.Id == targetUserId);
            Assert.Equal(2, updatedTarget.SecurityRevision);
            Assert.False((await db.AdminSessions.AsNoTracking().SingleAsync(x => x.Id == targetSessionId)).IsRevoked);
        }

        // Conflict check: Same ClientOperationId, different payload
        var conflictingCommand = new AdminActionCommandDto
        {
            AdminUserId = targetUserId,
            Action = "DISABLE"
        };
        var (_, conflictingSha256) = _serializer.SerializeAndHash(conflictingCommand);

        var requestConflict = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(conflictingCommand)
        };
        requestConflict.Headers.Add("X-Client-Operation-Id", opId.ToString());
        requestConflict.Headers.Add("X-Operation-Type", "admin.manage");
        requestConflict.Headers.Add("X-Payload-Sha256", conflictingSha256);
        requestConflict.Headers.Add("X-Expected-Version", "1");
        requestConflict.Headers.Add("X-Policy-Version", "1");
        requestConflict.Headers.Add("X-Reason", "Conflict probe");

        var responseConflict = await client.SendAsync(requestConflict);
        Assert.Equal(HttpStatusCode.Conflict, responseConflict.StatusCode);
    }

    [Fact]
    public async Task AdminUsers_CanonicalSessionActions_RevokeOnlyTargetSessions_AndKeepCallerSessionValid()
    {
        RateLimitingMiddleware.Reset();
        var (callerSessionId, _) = await AuthenticateAdminAsync(new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuthStepUp });
        Guid targetUserId;
        Guid sessionToRevokeId;
        Guid remainingSessionOneId;
        Guid remainingSessionTwoId;
        Guid outsiderSessionId;
        using (var db = GetDbContext())
        {
            var target = AdminUser.Create(
                $"session_target_{Guid.NewGuid():N}", _passwordHasher.HashPassword("TargetPass#123"), "salt",
                _totpService.GenerateSecret(), true, DateTimeOffset.UtcNow);
            var outsider = AdminUser.Create(
                $"session_outsider_{Guid.NewGuid():N}", _passwordHasher.HashPassword("OutsiderPass#123"), "salt",
                _totpService.GenerateSecret(), true, DateTimeOffset.UtcNow);
            db.AdminUsers.Add(target);
            db.AdminUsers.Add(outsider);
            var now = DateTimeOffset.UtcNow;
            var sessionToRevoke = AdminSession.Create(target.Id, $"hash-{Guid.NewGuid():N}", now,
                TimeSpan.FromHours(1), TimeSpan.FromHours(8), true, target.SecurityRevision, Array.Empty<string>());
            var remainingOne = AdminSession.Create(target.Id, $"hash-{Guid.NewGuid():N}", now,
                TimeSpan.FromHours(1), TimeSpan.FromHours(8), true, target.SecurityRevision, Array.Empty<string>());
            var remainingTwo = AdminSession.Create(target.Id, $"hash-{Guid.NewGuid():N}", now,
                TimeSpan.FromHours(1), TimeSpan.FromHours(8), true, target.SecurityRevision, Array.Empty<string>());
            var outsiderSession = AdminSession.Create(outsider.Id, $"hash-{Guid.NewGuid():N}", now,
                TimeSpan.FromHours(1), TimeSpan.FromHours(8), true, outsider.SecurityRevision, Array.Empty<string>());
            db.AdminSessions.AddRange(sessionToRevoke, remainingOne, remainingTwo, outsiderSession);
            await db.SaveChangesAsync();
            targetUserId = target.Id;
            sessionToRevokeId = sessionToRevoke.Id;
            remainingSessionOneId = remainingOne.Id;
            remainingSessionTwoId = remainingTwo.Id;
            outsiderSessionId = outsiderSession.Id;
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"central_session={callerSessionId}");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");

        async Task ExecuteCanonicalActionAsync(
            AdminActionCommandDto payload, string actionScope, string reason,
            HttpStatusCode expectedStatus = HttpStatusCode.OK, string? expectedError = null)
        {
            var (_, digest) = _serializer.SerializeAndHash(payload);
            var grant = await CreateGrantAsync(callerSessionId, actionScope, digest);
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
            {
                Content = JsonContent.Create(new
                {
                    meta = new
                    {
                        clientOperationId = Guid.NewGuid(), operationType = "admin.manage", fingerprintVersion = 1,
                        payloadSha256 = digest, expectedVersion = 1, policyVersion = 1, reason
                    },
                    payload
                })
            };
            request.Headers.Add("X-Step-Up-Grant-Id", grant.ToString());
            using var response = await client.SendAsync(request);
            Assert.Equal(expectedStatus, response.StatusCode);
            if (expectedStatus == HttpStatusCode.OK)
            {
                var outcome = await response.Content.ReadFromJsonAsync<OutcomeDto>();
                Assert.NotNull(outcome);
                Assert.Equal("SUCCEEDED", outcome.State);
            }
            else
            {
                var error = await response.Content.ReadFromJsonAsync<JsonNode>();
                Assert.Equal(expectedError, error?["code"]?.GetValue<string>());
            }
        }

        await ExecuteCanonicalActionAsync(new AdminActionCommandDto
        {
            AdminUserId = targetUserId, Action = "REVOKE_SESSION", SessionId = outsiderSessionId
        }, "admin.user.revoke_session", "Cross-admin session ID must not authorize revocation",
            HttpStatusCode.BadRequest, "SESSION_NOT_FOUND");
        using (var db = GetDbContext())
        {
            Assert.False((await db.AdminSessions.SingleAsync(s => s.Id == outsiderSessionId)).IsRevoked);
            Assert.False((await db.AdminSessions.SingleAsync(s => s.Id == sessionToRevokeId)).IsRevoked);
        }

        await ExecuteCanonicalActionAsync(new AdminActionCommandDto
        {
            AdminUserId = targetUserId, Action = "REVOKE_SESSION", SessionId = sessionToRevokeId
        }, "admin.user.revoke_session", "Revoke one persisted target session");

        using (var db = GetDbContext())
        {
            Assert.True((await db.AdminSessions.SingleAsync(s => s.Id == sessionToRevokeId)).IsRevoked);
            Assert.False((await db.AdminSessions.SingleAsync(s => s.Id == remainingSessionOneId)).IsRevoked);
            Assert.False((await db.AdminSessions.SingleAsync(s => s.Id == remainingSessionTwoId)).IsRevoked);
        }

        await ExecuteCanonicalActionAsync(new AdminActionCommandDto
        {
            AdminUserId = targetUserId, Action = "REVOKE_ALL"
        }, "admin.user.revoke_all", "Revoke all persisted sessions for target admin");

        using (var db = GetDbContext())
        {
            Assert.All(await db.AdminSessions.Where(s => s.AdminUserId == targetUserId).ToListAsync(), s => Assert.True(s.IsRevoked));
            Assert.False((await db.AdminSessions.SingleAsync(s => s.Id == callerSessionId)).IsRevoked);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<IAdminAuthService>();
            Assert.NotNull(await auth.ValidateSessionAsync(callerSessionId));
        }
    }

    [Fact]
    public async Task SigningAndLicensing_SignPayload_And_CrossVerifyWithTrustSetPublicKeys()
    {
        // 1. Generate active signing key via ProtectedSigningEngine
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IProtectedSigningEngine>();

        var keyId = $"api-test-key-{Guid.NewGuid():N}";
        await engine.GenerateAndRegisterKeyAsync(keyId, 1, SigningPurpose.License, TimeSpan.FromDays(365));

        const string canonicalPayload = """{"licenseId":"lic-pos-integration-123","shopId":"shop-001"}""";
        var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));

        var signingRequest = new SigningRequestDto
        {
            SigningRequestId = Guid.NewGuid(),
            IssuanceRequestId = Guid.NewGuid(),
            PayloadSha256 = payloadSha256,
            AuthorityRevision = 1,
            KeyId = keyId,
            KeyVersion = 1,
            Purpose = SigningPurpose.License,
            CanonicalPayload = canonicalPayload
        };

        await engine.AuthorizeTestRequestAsync(signingRequest);
        // 2. Sign via API endpoint: POST /internal/v1/sign with authorized token
        var signMsg = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/sign")
        {
            Content = JsonContent.Create(signingRequest)
        };
        signMsg.Headers.Add("X-Internal-Signer-Token", (Environment.GetEnvironmentVariable("CENTRAL_INTERNAL_SIGNER_TOKEN") ?? throw new InvalidOperationException("Explicit isolated signer credential required")));

        var signResponse = await _client.SendAsync(signMsg);
        Assert.Equal(HttpStatusCode.OK, signResponse.StatusCode);

        var signResult = await signResponse.Content.ReadFromJsonAsync<SigningResponseDto>();
        Assert.NotNull(signResult);
        Assert.Equal("RS256", signResult.AlgorithmProfileId);
        Assert.NotEmpty(signResult.Signature);

        // 3. Fetch public trust set: GET /api/licensing/v1/trust-set
        Environment.SetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS", "120"); // labelled synthetic integration fixture
        using var installationPrivateKey = RSA.Create(2048);
        var installationPublicKey = installationPrivateKey.ExportRSAPublicKeyPem();
        var installation = Installation.Register(Guid.NewGuid(), Guid.NewGuid(), "TEST-HW", installationPublicKey, "test", "Windows", DateTimeOffset.UtcNow);
        installation.Approve(DateTimeOffset.UtcNow);
        using (var db = GetDbContext())
        {
            db.Installations.Add(installation);
            await db.SaveChangesAsync();
        }
        using var trustRequest = new HttpRequestMessage(HttpMethod.Get, "/api/licensing/v1/trust-set");
        AddInstallationRequestProof(trustRequest, installation, installationPrivateKey);
        var trustResponse = await _client.SendAsync(trustRequest);
        Assert.Equal(HttpStatusCode.OK, trustResponse.StatusCode);

        // The exact signed proof succeeds once. Persisted nonce replay must fail before serving trust bytes again.
        var proofNonce = Guid.Parse(trustRequest.Headers.GetValues("X-Installation-Nonce").Single());
        using var replayRequest = new HttpRequestMessage(HttpMethod.Get, "/api/licensing/v1/trust-set");
        foreach (var header in trustRequest.Headers.Where(pair => pair.Key.StartsWith("X-Installation-", StringComparison.Ordinal)))
        {
            replayRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        using var replayResponse = await _client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        using (var db = GetDbContext())
        {
            Assert.Equal(1, await db.InstallationRequestNonces.CountAsync(x => x.InstallationId == installation.Id && x.Nonce == proofNonce.ToString("D")));
        }

        using var wrongPrivateKey = RSA.Create(2048);
        using var substitutedKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/licensing/v1/trust-set");
        AddInstallationRequestProof(substitutedKeyRequest, installation, installationPrivateKey, wrongPrivateKey);
        using var substitutedKeyResponse = await _client.SendAsync(substitutedKeyRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, substitutedKeyResponse.StatusCode);

        using var pathTamperRequest = new HttpRequestMessage(HttpMethod.Get, "/api/licensing/v1/trust-set");
        AddInstallationRequestProof(pathTamperRequest, installation, installationPrivateKey);
        pathTamperRequest.RequestUri = new Uri("https://localhost/api/licensing/v1/trust-set?altered=1");
        using var pathTamperResponse = await _client.SendAsync(pathTamperRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, pathTamperResponse.StatusCode);
        using var expiredProofRequest = new HttpRequestMessage(HttpMethod.Get, "/api/licensing/v1/trust-set");
        AddInstallationRequestProof(expiredProofRequest, installation, installationPrivateKey, timestampUtc: DateTimeOffset.UtcNow.AddMinutes(-3));
        var expiredProofNonce = Guid.Parse(expiredProofRequest.Headers.GetValues("X-Installation-Nonce").Single());
        Console.WriteLine($"EXPIRED_INSTALLATION_PROOF_FIXTURE installation={installation.Id:D} nonce={expiredProofNonce:D}");
        using var expiredProofResponse = await _client.SendAsync(expiredProofRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredProofResponse.StatusCode);
        using (var db = GetDbContext())
        {
            Assert.Equal(0, await db.InstallationRequestNonces.CountAsync(x => x.InstallationId == installation.Id && x.Nonce == expiredProofNonce.ToString("D")));
        }

        var trustSet = await trustResponse.Content.ReadFromJsonAsync<TrustSetDto>();
        Assert.NotNull(trustSet);
        Assert.NotEmpty(trustSet.Keys);

        var matchingKey = trustSet.Keys.FirstOrDefault(k => k.KeyId == keyId);
        Assert.NotNull(matchingKey);
        Assert.NotEmpty(matchingKey.PublicKey);

        // 4. Cross-verify signature with public key using RSASignaturePadding.Pkcs1 & SHA256 (matching POS verifier)
        using var rsa = RSA.Create();
        rsa.ImportFromPem(matchingKey.PublicKey);

        var signatureBytes = Convert.FromBase64String(signResult.Signature);
        var isValid = rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.True(isValid, "Signature produced by Central must be 100% cryptographically verifiable by POS RS256 verifier.");
    }

    [Fact]
    public async Task SupportBundle_InstallationProof_PersistsPortalTicketAndEnforcesShopAndAttachmentScope()
    {
        var oldWindow = Environment.GetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS");
        Environment.SetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS", "120"); // isolated development proof window fixture
        try
        {
            var now = DateTimeOffset.UtcNow;
            var customer = Customer.Create($"Synthetic support {Guid.NewGuid():N}", $"support-{Guid.NewGuid():N}@example.invalid", "synthetic", now);
            var shop = Shop.Create(customer.Id, $"SB{Guid.NewGuid():N}"[..10], "Synthetic support shop", now);
            var otherCustomer = Customer.Create($"Synthetic other {Guid.NewGuid():N}", $"other-{Guid.NewGuid():N}@example.invalid", "synthetic", now);
            var otherShop = Shop.Create(otherCustomer.Id, $"SO{Guid.NewGuid():N}"[..10], "Synthetic other shop", now);
            using var privateKey = RSA.Create(2048);
            var installation = Installation.Register(shop.Id, Guid.NewGuid(), "SUPPORT-BUNDLE-HW", privateKey.ExportRSAPublicKeyPem(), "support-test", "Windows", now);
            installation.Approve(now);
            var ownAttachmentBytes = Encoding.UTF8.GetBytes("SYNTHETIC_OWN_FILE");
            var foreignAttachmentBytes = Encoding.UTF8.GetBytes("SYNTHETIC_FOREIGN_FILE");
            var ownAttachmentId = Guid.NewGuid();
            var foreignAttachmentId = Guid.NewGuid();
            var objectStorage = _factory.Services.GetRequiredService<IObjectStorageProvider>();
            var ownAttachmentPath = await objectStorage.SaveCiphertextAsync(shop.Id, ownAttachmentId, new MemoryStream(ownAttachmentBytes));
            var foreignAttachmentPath = await objectStorage.SaveCiphertextAsync(otherShop.Id, foreignAttachmentId, new MemoryStream(foreignAttachmentBytes));
            var ownAttachment = EncryptedObjectRecord.Create(ownAttachmentId, shop.Id, installation.Id,
                EncryptedObjectRecord.PurposeSupportAttachment, ownAttachmentBytes.Length, "application/octet-stream",
                Convert.ToHexStringLower(SHA256.HashData(ownAttachmentBytes)), ownAttachmentPath, Guid.NewGuid(), null, now);
            var foreignAttachment = EncryptedObjectRecord.Create(foreignAttachmentId, otherShop.Id, Guid.NewGuid(),
                EncryptedObjectRecord.PurposeSupportAttachment, foreignAttachmentBytes.Length, "application/octet-stream",
                Convert.ToHexStringLower(SHA256.HashData(foreignAttachmentBytes)), foreignAttachmentPath, Guid.NewGuid(), null, now);
            using (var db = GetDbContext())
            {
                db.Customers.AddRange(customer, otherCustomer);
                db.Shops.AddRange(shop, otherShop);
                db.Installations.Add(installation);
                db.EncryptedObjects.AddRange(ownAttachment, foreignAttachment);
                await db.SaveChangesAsync();
            }

            Dictionary<string, string[]>? originalProof = null;
            var alteredBodyNonce = Guid.Empty;
            async Task<(HttpResponseMessage Response, Guid OperationId)> SendBundle(
                Guid submittedShopId,
                Guid attachmentId,
                Guid? operationId = null,
                bool reuseProof = false,
                bool alterBodyAfterSigning = false)
            {
                var clientOperationId = operationId ?? Guid.NewGuid();
                var payload = new
                {
                    shopId = submittedShopId,
                    subject = "Synthetic installation support request",
                    summary = "A labelled development fixture submitted through installation proof.",
                    attachmentObjectIds = new[] { attachmentId },
                    redactionProfileVersion = 1
                };
                var payloadHash = _serializer.SerializeAndHash(payload).Sha256Hex;
                var envelope = new
                {
                    meta = new
                    {
                        clientOperationId,
                        operationType = "support.bundle",
                        fingerprintVersion = 1,
                        payloadSha256 = payloadHash,
                        expectedVersion = 0,
                        policyVersion = 0,
                        reason = "synthetic installation support contract test"
                    },
                    payload
                };
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/licensing/v1/support/bundles")
                { Content = JsonContent.Create(envelope) };
                if (reuseProof)
                {
                    Assert.NotNull(originalProof);
                    foreach (var header in originalProof!) { request.Headers.Add(header.Key, header.Value); }
                }
                else
                {
                    AddInstallationRequestProof(request, installation, privateKey);
                    originalProof ??= request.Headers.Where(x => x.Key.StartsWith("X-Installation-", StringComparison.Ordinal))
                        .ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                }

                if (alterBodyAfterSigning)
                {
                    alteredBodyNonce = Guid.Parse(request.Headers.GetValues("X-Installation-Nonce").Single());
                    var signedBody = await request.Content!.ReadAsStringAsync();
                    var alteredBody = signedBody.Replace(
                        "A labelled development fixture submitted through installation proof.",
                        "Payload changed after installation proof signing.",
                        StringComparison.Ordinal);
                    Assert.NotEqual(signedBody, alteredBody);
                    request.Content = new StringContent(alteredBody, Encoding.UTF8, "application/json");
                }

                return (await _client.SendAsync(request), clientOperationId);
            }

            var (createdResponse, operationId) = await SendBundle(shop.Id, ownAttachment.Id);
            Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
            var createdOutcome = await createdResponse.Content.ReadFromJsonAsync<OutcomeDto>();
            Assert.NotNull(createdOutcome);
            Assert.Equal("support.bundle", createdOutcome.OperationType);
            Assert.Equal("SUCCEEDED", createdOutcome.State);
            Assert.NotNull(createdOutcome.ResultReference);
            var ticketId = Guid.Parse(createdOutcome.ResultReference.Split('/').Last());

            using (var db = GetDbContext())
            {
                var persisted = await db.SupportTickets.AsNoTracking().SingleAsync(x => x.Id == ticketId);
                Assert.Equal(shop.Id, persisted.ShopId);
                Assert.Equal(installation.Id, persisted.RequesterPrincipalId);
                Assert.Contains(ownAttachment.Id.ToString("D"), persisted.AttachmentObjectIdsJson);
                Assert.Equal(1, await db.SupportTickets.CountAsync(x => x.Id == ticketId));
                Assert.Contains(await db.CentralAuditRecords.AsNoTracking().Where(x => x.CorrelationId == operationId.ToString()).ToListAsync(),
                    x => x.Action == "SUPPORT_TICKET_CREATED" && x.ActorId == $"installation:{installation.Id:D}" && x.SessionId == null);
            }

            // A fresh installation nonce with the same operation identity replays the original outcome.
            var (replayResponse, _) = await SendBundle(shop.Id, ownAttachment.Id, operationId);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            var replayOutcome = await replayResponse.Content.ReadFromJsonAsync<OutcomeDto>();
            Assert.NotNull(replayOutcome);
            Assert.Equal(createdOutcome.OutcomeId, replayOutcome.OutcomeId);
            using (var db = GetDbContext())
            {
                Assert.Equal(1, await db.SupportTickets.CountAsync(x => x.Id == ticketId));
                Assert.Single(await db.CentralOutboxMessages.Where(x => x.Id == operationId && x.EventType == "support.ticket_created").ToListAsync());
            }

            // Reusing the exact signed nonce is rejected before command processing.
            var (nonceReplayResponse, _) = await SendBundle(shop.Id, ownAttachment.Id, operationId, reuseProof: true);
            Assert.Equal(HttpStatusCode.Unauthorized, nonceReplayResponse.StatusCode);

            // The proof covers the canonical body digest; changing the body after signing is rejected before nonce persistence or command effects.
            var (alteredBodyResponse, alteredBodyOperationId) = await SendBundle(shop.Id, ownAttachment.Id, alterBodyAfterSigning: true);
            Console.WriteLine($"INSTALLATION_BODY_TAMPER_FIXTURE installation={installation.Id:D} nonce={alteredBodyNonce:D} operation={alteredBodyOperationId:D}");
            Assert.Equal(HttpStatusCode.Unauthorized, alteredBodyResponse.StatusCode);
            using (var db = GetDbContext())
            {
                Assert.Equal(0, await db.InstallationRequestNonces.CountAsync(x => x.InstallationId == installation.Id && x.Nonce == alteredBodyNonce.ToString("D")));
                Assert.Equal(1, await db.SupportTickets.CountAsync(x => x.RequesterPrincipalId == installation.Id));
                Assert.False(await db.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == alteredBodyOperationId));
                Assert.False(await db.CentralOutboxMessages.AnyAsync(x => x.Id == alteredBodyOperationId));
            }

            // The signed installation cannot select another shop or attach another shop's object.
            var (wrongShopResponse, _) = await SendBundle(otherShop.Id, ownAttachment.Id);
            Assert.Equal(HttpStatusCode.Forbidden, wrongShopResponse.StatusCode);
            var (foreignAttachmentResponse, _) = await SendBundle(shop.Id, foreignAttachment.Id);
            Assert.Equal(HttpStatusCode.Forbidden, foreignAttachmentResponse.StatusCode);
            using (var db = GetDbContext())
            {
                Assert.Equal(1, await db.SupportTickets.CountAsync(x => x.RequesterPrincipalId == installation.Id));
            }

            // The existing authenticated portal support view sees the persisted installation-originated ticket.
            var (sessionId, _) = await AuthenticateAdminAsync(["support.view"]);
            using var portalRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/v1/support/tickets?filter={Uri.EscapeDataString("Synthetic installation support request")}");
            portalRequest.Headers.Add("Cookie", $"central_session={sessionId}");
            var portalResponse = await _client.SendAsync(portalRequest);
            Assert.Equal(HttpStatusCode.OK, portalResponse.StatusCode);
            var portalJson = JsonNode.Parse(await portalResponse.Content.ReadAsStringAsync());
            Assert.Contains(portalJson!["items"]!.AsArray(), item => item?["id"]?.GetValue<string>() == ticketId.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS", oldWindow);
        }
    }

    [Fact]
    public async Task AdminUsers_SelfEscalation_IsProhibited_WithZeroMutation()
    {
        RateLimitingMiddleware.Reset();
        var (sessionId, callerUserId) = await AuthenticateAdminAsync(new[] { AdminCapabilities.AdminManage });

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"central_session={sessionId}");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");

        // Attempt to execute command targeting self (callerUserId)
        var command = new AdminActionCommandDto
        {
            AdminUserId = callerUserId,
            Action = "ASSIGN_ROLE",
            RoleId = Guid.NewGuid()
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{callerUserId}/commands")
        {
            Content = JsonContent.Create(command)
        };
        request.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.NotNull(json);
        Assert.Equal("SELF_ESCALATION_PROHIBITED", json["code"]?.GetValue<string>());

        // Verify user in DB has zero mutation
        using var db = GetDbContext();
        var userInDb = await db.AdminUsers.Include(u => u.UserRoles).FirstAsync(u => u.Id == callerUserId);
        Assert.DoesNotContain(userInDb.UserRoles, ur => ur.RoleId == command.RoleId);
    }

    [Fact]
    public async Task AdminUsers_SensitiveAction_RequiresStepUp_And_ConsumesGrant_PreventingReplay()
    {
        RateLimitingMiddleware.Reset();
        var (sessionId, callerUserId) = await AuthenticateAdminAsync(new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuthStepUp });

        Guid targetUserId;
        string callerTotpSecret;
        using (var db = GetDbContext())
        {
            var targetUser = AdminUser.Create(
                $"target_{Guid.NewGuid():N}",
                _passwordHasher.HashPassword("Pass#123"),
                "salt",
                _totpService.GenerateSecret(),
                true,
                DateTimeOffset.UtcNow);
            db.AdminUsers.Add(targetUser);

            var caller = await db.AdminUsers.FirstAsync(u => u.Id == callerUserId);
            callerTotpSecret = caller.MfaSecret!;

            await db.SaveChangesAsync();
            targetUserId = targetUser.Id;
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"central_session={sessionId}");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");

        var command = new AdminActionCommandDto
        {
            AdminUserId = targetUserId,
            Action = "DISABLE"
        };
        var (_, sha256) = _serializer.SerializeAndHash(command);

        // 1. Attempt DISABLE without step-up approval header -> Expect 403 APPROVAL_REQUIRED
        var reqNoStepUp = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(command)
        };
        reqNoStepUp.Headers.Add("X-Expected-Version", "1");
        reqNoStepUp.Headers.Add("X-Policy-Version", "1");
        reqNoStepUp.Headers.Add("X-Reason", "Security integration probe");
        reqNoStepUp.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString());
        reqNoStepUp.Headers.Add("X-Payload-Sha256", sha256);

        var resNoStepUp = await client.SendAsync(reqNoStepUp);
        Assert.Equal(HttpStatusCode.Forbidden, resNoStepUp.StatusCode);
        var jsonNoStepUp = await resNoStepUp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("APPROVAL_REQUIRED", jsonNoStepUp!["code"]?.GetValue<string>());

        // Verify target user is still active in DB (zero mutation)
        using (var db = GetDbContext())
        {
            var target = await db.AdminUsers.FirstAsync(u => u.Id == targetUserId);
            Assert.True(target.IsActive);
        }

        // 2. Obtain fresh step-up grant via /api/admin/v1/auth/step-up
        var stepUpProof = _totpService.GenerateCode(callerTotpSecret, DateTimeOffset.UtcNow);
        var stepUpResponse = await client.PostAsJsonAsync("/api/admin/v1/auth/step-up", new StepUpRequestDto
        {
            SessionId = sessionId,
            ActionScope = "admin.user.disable",
            TargetPayloadSha256 = sha256,
            Proof = stepUpProof
        });
        Assert.Equal(HttpStatusCode.OK, stepUpResponse.StatusCode);

        var stepUpJson = await stepUpResponse.Content.ReadFromJsonAsync<JsonNode>();
        var grantId = Guid.Parse(stepUpJson!["stepUpGrantId"]!.GetValue<string>());

        // 3. Execute DISABLE command with step-up grant -> Succeeds and consumes grant
        var reqWithStepUp = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(command)
        };
        var opId = Guid.NewGuid();
        reqWithStepUp.Headers.Add("X-Expected-Version", "1");
        reqWithStepUp.Headers.Add("X-Policy-Version", "1");
        reqWithStepUp.Headers.Add("X-Reason", "Security integration probe");
        reqWithStepUp.Headers.Add("X-Client-Operation-Id", opId.ToString());
        reqWithStepUp.Headers.Add("X-Payload-Sha256", sha256);
        reqWithStepUp.Headers.Add("X-Step-Up-Grant-Id", grantId.ToString());

        var resWithStepUp = await client.SendAsync(reqWithStepUp);
        Assert.Equal(HttpStatusCode.OK, resWithStepUp.StatusCode);

        // Verify target user is now disabled in DB
        using (var db = GetDbContext())
        {
            var target = await db.AdminUsers.FirstAsync(u => u.Id == targetUserId);
            Assert.False(target.IsActive);
        }

        // 4. Attempt to REPLAY the same consumed step-up grant on another operation -> Expect 403 INVALID_STEPUP_GRANT
        var command2 = new AdminActionCommandDto
        {
            AdminUserId = targetUserId,
            Action = "DISABLE"
        };
        var reqReplay = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(command2)
        };
        reqReplay.Headers.Add("X-Expected-Version", "1");
        reqReplay.Headers.Add("X-Policy-Version", "1");
        reqReplay.Headers.Add("X-Reason", "Security integration probe");
        reqReplay.Headers.Add("X-Client-Operation-Id", Guid.NewGuid().ToString());
        reqReplay.Headers.Add("X-Payload-Sha256", sha256);
        reqReplay.Headers.Add("X-Step-Up-Grant-Id", grantId.ToString());

        var resReplay = await client.SendAsync(reqReplay);
        Assert.Equal(HttpStatusCode.Forbidden, resReplay.StatusCode);
        var jsonReplay = await resReplay.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("INVALID_STEPUP_GRANT", jsonReplay!["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task ProtectedSigner_CallerAuthentication_And_CrossOriginRejection_And_Auditing()
    {
        var keyId = $"signer-auth-test-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<IProtectedSigningEngine>();
            await engine.GenerateAndRegisterKeyAsync(keyId, 1, SigningPurpose.License, TimeSpan.FromDays(30));
        }

        const string canonical = """{"licenseId":"test-auth"}""";
        var payloadBytes = Encoding.UTF8.GetBytes(canonical);
        var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));

        var signingRequest = new SigningRequestDto
        {
            SigningRequestId = Guid.NewGuid(),
            IssuanceRequestId = Guid.NewGuid(),
            PayloadSha256 = payloadSha256,
            AuthorityRevision = 1,
            KeyId = keyId,
            KeyVersion = 1,
            Purpose = SigningPurpose.License,
            CanonicalPayload = canonical
        };

        using (var authorityScope = _factory.Services.CreateScope())
        {
            await authorityScope.ServiceProvider.GetRequiredService<IProtectedSigningEngine>().AuthorizeTestRequestAsync(signingRequest);
        }
        // Sub-test A: Unauthenticated caller (no token header) -> 401 CALLER_UNAUTHORIZED
        var reqNoToken = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/sign")
        {
            Content = JsonContent.Create(signingRequest)
        };
        var resNoToken = await _client.SendAsync(reqNoToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resNoToken.StatusCode);
        var jsonNoToken = await resNoToken.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("CALLER_UNAUTHORIZED", jsonNoToken!["code"]?.GetValue<string>());

        // Sub-test B: Cross-origin browser attempt with Origin header -> 403 CROSS_ORIGIN_PROHIBITED
        var reqCrossOrigin = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/sign")
        {
            Content = JsonContent.Create(signingRequest)
        };
        reqCrossOrigin.Headers.Add("Origin", "https://untrusted-browser.com");
        reqCrossOrigin.Headers.Add("X-Internal-Signer-Token", (Environment.GetEnvironmentVariable("CENTRAL_INTERNAL_SIGNER_TOKEN") ?? throw new InvalidOperationException("Explicit isolated signer credential required")));

        var resCrossOrigin = await _client.SendAsync(reqCrossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, resCrossOrigin.StatusCode);
        var jsonCrossOrigin = await resCrossOrigin.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("CROSS_ORIGIN_PROHIBITED", jsonCrossOrigin!["code"]?.GetValue<string>());

        // Sub-test C: Authorized caller -> 200 OK
        var reqAuthorized = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/sign")
        {
            Content = JsonContent.Create(signingRequest)
        };
        reqAuthorized.Headers.Add("X-Internal-Signer-Token", (Environment.GetEnvironmentVariable("CENTRAL_INTERNAL_SIGNER_TOKEN") ?? throw new InvalidOperationException("Explicit isolated signer credential required")));

        var resAuthorized = await _client.SendAsync(reqAuthorized);
        Assert.Equal(HttpStatusCode.OK, resAuthorized.StatusCode);

        // Sub-test D: Verify audit records exist for both rejected and succeeded attempts
        using (var db = GetDbContext())
        {
            var auditRecords = await db.CentralAuditRecords
                .Where(a => a.CorrelationId == signingRequest.SigningRequestId.ToString())
                .ToListAsync();

            Assert.Contains(auditRecords, a => a.Action == "signer.sign.rejected");
            Assert.Contains(auditRecords, a => a.Action == "signer.sign.succeeded");
        }
    }

    [Fact]
    public async Task ProtectedSigner_KeyRotationAndRevocation_RequiresSecurityManageCapability()
    {
        var keyId = $"rotate-test-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<IProtectedSigningEngine>();
            await engine.GenerateAndRegisterKeyAsync(keyId, 1, SigningPurpose.License, TimeSpan.FromDays(30));
        }

        // Sub-test A: Unauthenticated -> 401
        var clientNoAuth = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var resUnauth = await clientNoAuth.PostAsync($"/internal/v1/keys/{keyId}/rotate", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resUnauth.StatusCode);

        // Sub-test B: Authenticated but lacking SecurityManage / AdminManage -> 403
        var (unprivilegedSessionId, _) = await AuthenticateAdminAsync(new[] { AdminCapabilities.AdminUsersRead });
        var clientUnpriv = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        clientUnpriv.DefaultRequestHeaders.Add("Cookie", $"central_session={unprivilegedSessionId}");

        var resForbidden = await clientUnpriv.PostAsync($"/internal/v1/keys/{keyId}/rotate", null);
        Assert.Equal(HttpStatusCode.Forbidden, resForbidden.StatusCode);

        // Sub-test C: Authenticated with SecurityManage -> 200 OK
        var (privilegedSessionId, _) = await AuthenticateAdminAsync(new[] { AdminCapabilities.SecurityManage });
        var clientPriv = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        clientPriv.DefaultRequestHeaders.Add("Cookie", $"central_session={privilegedSessionId}");

        var (_, keyHash) = _serializer.SerializeAndHash(new { keyId, action = "rotate" });
        var keyGrant = await CreateGrantAsync(privilegedSessionId, "signer.key.rotate", keyHash);
        clientPriv.DefaultRequestHeaders.Add("X-Step-Up-Grant-Id", keyGrant.ToString());
        var resRotate = await clientPriv.PostAsync($"/internal/v1/keys/{keyId}/rotate", null);
        Assert.Equal(HttpStatusCode.OK, resRotate.StatusCode);

        // Verify audit log for rotation
        using (var db = GetDbContext())
        {
            var audit = await db.CentralAuditRecords.FirstOrDefaultAsync(a => a.Action == "signer.key.rotated" && a.ResourceId == keyId);
            Assert.NotNull(audit);
            Assert.Equal(privilegedSessionId, audit.SessionId);
        }

        var revokeKeyId = $"revoke-test-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<IProtectedSigningEngine>();
            await engine.GenerateAndRegisterKeyAsync(revokeKeyId, 1, SigningPurpose.License, TimeSpan.FromDays(30));
        }

        var unauthenticatedRevoke = await clientNoAuth.PostAsync($"/internal/v1/keys/{revokeKeyId}/revoke", null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedRevoke.StatusCode);

        var forbiddenRevoke = await clientUnpriv.PostAsync($"/internal/v1/keys/{revokeKeyId}/revoke", null);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRevoke.StatusCode);

        var (_, revokeHash) = _serializer.SerializeAndHash(new { keyId = revokeKeyId, action = "revoke" });
        var revokeGrant = await CreateGrantAsync(privilegedSessionId, "signer.key.revoke", revokeHash);
        var clientRevoker = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        clientRevoker.DefaultRequestHeaders.Add("Cookie", $"central_session={privilegedSessionId}");
        clientRevoker.DefaultRequestHeaders.Add("X-Step-Up-Grant-Id", revokeGrant.ToString());
        using var revokeResponse = await clientRevoker.PostAsync($"/internal/v1/keys/{revokeKeyId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
        var revokeBody = await revokeResponse.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("revoke", revokeBody?["action"]?.GetValue<string>());

        using (var db = GetDbContext())
        {
            var revokedKey = await db.CentralSigningKeyRecords.SingleAsync(x => x.KeyId == revokeKeyId);
            Assert.Equal(KeyStatus.Revoked, revokedKey.Status);
            Assert.True(revokedKey.EmergencyRolloverRevision > 0);
            var audit = await db.CentralAuditRecords.FirstOrDefaultAsync(a => a.Action == "signer.key.revoked" && a.ResourceId == revokeKeyId);
            Assert.NotNull(audit);
            Assert.Equal(privilegedSessionId, audit.SessionId);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<IProtectedSigningEngine>();
            var trustSet = await engine.GetTrustSetAsync();
            Assert.Contains(revokeKeyId, trustSet.RevokedKeyIds);
            Assert.True(trustSet.EmergencyRolloverRevision > 0);
        }
    }

    [Fact]
    public async Task Audit_QueriesBoundedRecords_WithActingAdminAttribution_AndPiiMasking()
    {
        RateLimitingMiddleware.Reset();
        var (sessionId, callerUserId) = await AuthenticateAdminAsync(new[] { AdminCapabilities.AdminManage, AdminCapabilities.AuditRead });

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"central_session={sessionId}");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var anonymousClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousClient.GetAsync("/api/admin/v1/audit?page=1")).StatusCode);

        // Execute an admin action with a sensitive field to generate an audit log
        var targetUserId = Guid.NewGuid();
        var command = new AdminActionCommandDto
        {
            AdminUserId = targetUserId,
            Action = "ASSIGN_ROLE",
            RoleId = Guid.NewGuid()
        };

        var opId = Guid.NewGuid();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/v1/admin-users/{targetUserId}/commands")
        {
            Content = JsonContent.Create(command)
        };
        req.Headers.Add("X-Client-Operation-Id", opId.ToString());

        await client.SendAsync(req);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<ICentralAuditService>();
            await audit.RecordAuditAsync("audit.cursor.fixture.one", "integration-test", null, null, null, null, null,
                "AuditTest", Guid.NewGuid().ToString(), "{}");
            await audit.RecordAuditAsync("audit.cursor.fixture.two", "integration-test", null, null, null, null, null,
                "AuditTest", Guid.NewGuid().ToString(), "{}");
        }

        // Query audit records using the effective040 cursor/snapshot contract.
        var queryResponse = await client.GetAsync("/api/admin/v1/audit?pageSize=1");
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);

        var page = await queryResponse.Content.ReadFromJsonAsync<AuditPageDto>();
        Assert.NotNull(page);
        Assert.NotEmpty(page.Items);
        Assert.True(page.AuthorizedCount > 0);
        Assert.True(page.AsOfVersion >= 0);
        Assert.Equal(page.AsOfVersion, page.ProjectionRevision);
        Assert.NotNull(page.NextCursor);

        using (var responseJson = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync()))
        {
            var properties = responseJson.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(new[] { "asOfVersion", "authorizedCount", "items", "nextCursor", "projectionRevision" }.OrderBy(name => name), properties.OrderBy(name => name));
        }

        var secondPageResponse = await client.GetAsync($"/api/admin/v1/audit?pageSize=1&cursor={Uri.EscapeDataString(page.NextCursor!)}&asOfVersion={page.AsOfVersion}");
        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
        var secondPage = await secondPageResponse.Content.ReadFromJsonAsync<AuditPageDto>();
        Assert.NotNull(secondPage);
        Assert.Single(secondPage.Items);
        Assert.NotEqual(page.Items[0].Id, secondPage.Items[0].Id);
        Assert.Equal(page.AsOfVersion, secondPage.AsOfVersion);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/v1/audit?page=1&pageSize=10")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/v1/audit?resource=Customer")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.GetAsync($"/api/admin/v1/audit?pageSize=1&asOfVersion={page.AsOfVersion + 1}")).StatusCode);

        // Verify actor attribution matches acting admin user ID (callerUserId).
        var attributionResponse = await client.GetAsync($"/api/admin/v1/audit?pageSize=100&filter={Uri.EscapeDataString(opId.ToString())}");
        Assert.Equal(HttpStatusCode.OK, attributionResponse.StatusCode);
        var attributionPage = await attributionResponse.Content.ReadFromJsonAsync<AuditPageDto>();
        var matchingRecord = attributionPage?.Items.SingleOrDefault(a => a.CorrelationId == opId.ToString());
        Assert.NotNull(matchingRecord);
        Assert.Equal(callerUserId.ToString(), matchingRecord.ActorId);
        Assert.Equal(sessionId, matchingRecord.SessionId);
    }

    private async Task<Guid> CreateGrantAsync(Guid sessionId, string action, string hash)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var session = await db.AdminSessions.Include(x => x.AdminUser).SingleAsync(x => x.Id == sessionId);
        var dto = await scope.ServiceProvider.GetRequiredService<IStepUpService>().VerifyAndGrantStepUpAsync(new StepUpRequestDto
        {
            SessionId = sessionId, ActionScope = action, TargetPayloadSha256 = hash,
            ChallengeId = Guid.NewGuid(), Proof = _totpService.GenerateCode(session.AdminUser.MfaSecret!, DateTimeOffset.UtcNow)
        });
        return dto.StepUpGrantId;
    }

    private async Task<(Guid SessionId, Guid UserId)> AuthenticateAdminAsync(string[] capabilities)
    {
        RateLimitingMiddleware.Reset();
        var username = $"auth_admin_{Guid.NewGuid():N}";
        const string password = "AdminPassword#123";
        var totpSecret = _totpService.GenerateSecret();
        var now = DateTimeOffset.UtcNow;

        Guid userId;
        using (var db = GetDbContext())
        {
            var user = AdminUser.Create(
                username,
                _passwordHasher.HashPassword(password),
                "salt",
                totpSecret,
                true,
                now);

            var role = AdminRole.Create($"Role_{Guid.NewGuid():N}", "Role", capabilities);
            db.AdminRoles.Add(role);
            db.AdminUsers.Add(user);
            db.AdminUserRoles.Add(new AdminUserRole(user.Id, role.Id, now));
            await db.SaveChangesAsync();

            userId = user.Id;
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var loginResponse = await client.PostAsJsonAsync("/api/admin/v1/auth/login", new LoginRequestDto
        {
            Username = username,
            PasswordOrProviderAssertion = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonNode>();
        var challengeId = Guid.Parse(loginJson!["challengeId"]!.GetValue<string>());

        var validTotp = _totpService.GenerateCode(totpSecret, DateTimeOffset.UtcNow);
        var mfaResponse = await client.PostAsJsonAsync("/api/admin/v1/auth/mfa", new MfaRequestDto
        {
            LoginChallengeId = challengeId,
            Proof = validTotp
        });
        Assert.Equal(HttpStatusCode.OK, mfaResponse.StatusCode);

        Assert.True(mfaResponse.Headers.TryGetValues("Set-Cookie", out var cookieHeaders));
        var sessionCookie = cookieHeaders
            .Select(header => header.Split(';', 2)[0])
            .Single(header => header.StartsWith("central_session=", StringComparison.OrdinalIgnoreCase));
        var sessionId = Guid.Parse(sessionCookie["central_session=".Length..]);

        return (sessionId, userId);
    }

    public void Dispose()
    {
        _client.Dispose();
        Environment.SetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS", _previousDualApprovalActions);
        GC.SuppressFinalize(this);
    }
}
