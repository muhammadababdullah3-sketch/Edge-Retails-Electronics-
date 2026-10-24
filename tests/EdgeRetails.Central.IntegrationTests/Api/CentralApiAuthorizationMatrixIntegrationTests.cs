using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Api;

/// <summary>
/// Actual-host coverage for the source-defined granular session-management permissions.
/// This intentionally leaves support shop scoping and response redaction out of scope:
/// those policies are not fully specified by the current contract.
/// </summary>
public sealed class CentralApiAuthorizationMatrixIntegrationTests(
    WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task PortalSessionRoutes_RequireTheirExactSessionCapabilities()
    {
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var adminManageOnly = await CreateClientAsync(AdminCapabilities.AdminManage);
        using var sessionReader = await CreateClientAsync("sessions.view");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/admin/v1/portal/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await adminManageOnly.GetAsync("/api/admin/v1/portal/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await adminManageOnly.GetAsync("/api/admin/v1/sessions")).StatusCode);

        using var contextResponse = await sessionReader.GetAsync("/api/admin/v1/portal/session");
        Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
        using var context = JsonDocument.Parse(await contextResponse.Content.ReadAsStringAsync());
        Assert.True(context.RootElement.TryGetProperty("username", out _));

        using var pageResponse = await sessionReader.GetAsync("/api/admin/v1/sessions?pageSize=5");
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
        Assert.True(page.RootElement.GetProperty("items").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task PortalSession_AcceptsEachDefinedCredentialTransportAndRejectsConflicts()
    {
        var fixture = await CreateSessionsAsync(["sessions.view"], includeTarget: true);

        async Task<(HttpStatusCode Status, string Body)> ReadWithAsync(Action<HttpRequestMessage> configure)
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/v1/portal/session");
            configure(request);
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var sessionId = fixture.CallerSessionId.ToString("D");
        var accepted = new[]
        {
            await ReadWithAsync(request => request.Headers.Add(CentralSessionCredentials.SessionHeader, sessionId)),
            await ReadWithAsync(request => request.Headers.Add(CentralSessionCredentials.AdminSessionHeader, sessionId)),
            await ReadWithAsync(request => request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {sessionId}")),
            await ReadWithAsync(request => request.Headers.TryAddWithoutValidation("Cookie", $"{CentralSessionCredentials.CookieName}={sessionId}")),
            await ReadWithAsync(request =>
            {
                request.Headers.Add(CentralSessionCredentials.SessionHeader, sessionId);
                request.Headers.TryAddWithoutValidation("Cookie", $"{CentralSessionCredentials.CookieName}={sessionId}");
            })
        };
        Assert.All(accepted, response => Assert.Equal(HttpStatusCode.OK, response.Status));
        Assert.All(accepted, response =>
        {
            using var body = JsonDocument.Parse(response.Body);
            Assert.True(body.RootElement.TryGetProperty("username", out _));
        });

        var operationId = Guid.NewGuid();
        var conflict = await ReadWithAsync(request =>
        {
            request.Headers.Add(CentralSessionCredentials.SessionHeader, sessionId);
            request.Headers.Add(CentralSessionCredentials.AdminSessionHeader, fixture.TargetSessionId!.Value.ToString("D"));
            request.Headers.Add("X-Client-Operation-Id", operationId.ToString("D"));
        });
        Assert.Equal(HttpStatusCode.BadRequest, conflict.Status);
        Assert.Contains("SESSION_CREDENTIAL_CONFLICT", conflict.Body, StringComparison.Ordinal);

        var malformed = await ReadWithAsync(request =>
        {
            request.Headers.Add(CentralSessionCredentials.SessionHeader, sessionId);
            request.Headers.TryAddWithoutValidation("Authorization", "Basic malformed");
        });
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.Status);
        Assert.Contains("SESSION_INVALID", malformed.Body, StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var denial = await db.CentralAuditRecords.AsNoTracking().SingleAsync(record =>
            record.Action == "security.request.denied" && record.CorrelationId == operationId.ToString());
        Assert.False(denial.IsSuccess);
        Assert.Null(denial.SessionId);
    }

    [Fact]
    public async Task SessionRevoke_InsufficientCapabilityAndForgedIdentityHeadersHaveNoBusinessEffect()
    {
        var fixture = await CreateSessionsAsync(["sessions.view"], includeTarget: true);
        using var client = ClientFor(fixture.CallerSessionId);
        var operationId = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("X-Admin-User-Id", Guid.NewGuid().ToString("D"));
        client.DefaultRequestHeaders.Add("X-Shop-Id", Guid.NewGuid().ToString("D"));

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/admin/v1/portal/sessions/revoke?targetSessionId={fixture.TargetSessionId!.Value:D}");
        request.Headers.Add("Origin", "https://localhost");
        request.Headers.Add("X-Client-Operation-Id", operationId.ToString("D"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var target = await db.AdminSessions.AsNoTracking().SingleAsync(x => x.Id == fixture.TargetSessionId);
            Assert.False(target.IsRevoked);
            var denial = await db.CentralAuditRecords.AsNoTracking().SingleAsync(record =>
                record.Action == "security.request.denied" && record.CorrelationId == operationId.ToString());
            Assert.False(denial.IsSuccess);
            Assert.Equal(fixture.CallerSessionId, denial.SessionId);
        }
    }

    [Fact]
    public async Task SessionRevoke_ExactCapabilityCanRevokeExplicitSameAdminTarget()
    {
        var fixture = await CreateSessionsAsync(["sessions.revoke"], includeTarget: true);
        using var client = ClientFor(fixture.CallerSessionId);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/admin/v1/portal/sessions/revoke?targetSessionId={fixture.TargetSessionId!.Value:D}");
        request.Headers.Add("Origin", "https://localhost");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var target = await db.AdminSessions.AsNoTracking().SingleAsync(x => x.Id == fixture.TargetSessionId);
        Assert.True(target.IsRevoked);
        var caller = await db.AdminSessions.AsNoTracking().SingleAsync(x => x.Id == fixture.CallerSessionId);
        Assert.False(caller.IsRevoked);
    }

    [Fact]
    public async Task EveryMappedApiRoute_HasAnExplicitBoundary_AndHostRejectsMissingOrWrongAuthority()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var raw = endpoint.RoutePattern.RawText;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return [];
                }

                var path = raw.StartsWith('/') ? raw : $"/{raw}";
                if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
                    !path.StartsWith("/internal/v1/", StringComparison.OrdinalIgnoreCase))
                {
                    return [];
                }

                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                return methods.Select(method => new MappedApiRoute(
                    method.ToUpperInvariant(),
                    ToRepresentativePath(path)));
            })
            .DistinctBy(route => (route.Method, route.Path))
            .OrderBy(route => route.Method, StringComparer.Ordinal)
            .ThenBy(route => route.Path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(endpoints);
        var matrix = endpoints.Select(route =>
        {
            var permission = CentralRequestAuthorizationFilter.ResolveRequiredPermission(route.Method, route.Path);
            var boundary = DescribeHostBoundary(route.Method, route.Path, permission);
            return new HostAuthorizationMatrixRow(route.Method, route.Path, permission, boundary);
        }).ToArray();
        var unclassified = matrix.Where(row => row.Boundary is null).ToArray();
        Assert.True(unclassified.Length == 0,
            $"Mapped Central API routes without an explicit authorization boundary: {string.Join("; ", unclassified.Select(row => $"{row.Method} {row.Path}"))}");

        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        var adminManageFixture = await CreateSessionsAsync([AdminCapabilities.AdminManage], includeTarget: false);
        using var adminManage = ClientFor(adminManageFixture.CallerSessionId);
        var observations = new List<HostAuthorizationObservation>(matrix.Length);

        foreach (var row in matrix)
        {
            using var anonymousRequest = CreateProbeRequest(row.Method, row.Path);
            using var anonymousResponse = await anonymous.SendAsync(anonymousRequest);
            var anonymousStatus = (int)anonymousResponse.StatusCode;

            if (row.Boundary == "customer-restore-deferred")
            {
                Assert.Equal(HttpStatusCode.NotImplemented, anonymousResponse.StatusCode);
                Assert.Contains("CUSTOMER_RESTORE_NOT_SUPPORTED", await anonymousResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            else if (row.Boundary is "admin-capability" or "session-bound" or "session-capability" or "installation-proof" or "internal-signer" or "legacy-fail-closed")
            {
                Assert.True(anonymousResponse.StatusCode == HttpStatusCode.Unauthorized, $"Expected anonymous denial for {row.Method} {row.Path} ({row.Boundary}), got {(int)anonymousResponse.StatusCode} {anonymousResponse.StatusCode}: {await anonymousResponse.Content.ReadAsStringAsync()}");
            }
            else
            {
                Assert.InRange(anonymousStatus, 400, 499);
            }

            int? adminManageStatus = null;
            if ((row.Boundary == "admin-capability" && row.RequiredPermission != AdminCapabilities.AdminManage) ||
                row.Boundary == "session-capability" || row.Boundary == "legacy-fail-closed")
            {
                using var wrongCapabilityRequest = CreateProbeRequest(row.Method, row.Path);
                using var wrongCapabilityResponse = await adminManage.SendAsync(wrongCapabilityRequest);
                adminManageStatus = (int)wrongCapabilityResponse.StatusCode;
                Assert.Equal(HttpStatusCode.Forbidden, wrongCapabilityResponse.StatusCode);
            }

            observations.Add(new HostAuthorizationObservation(
                row.Method, row.Path, row.RequiredPermission, row.Boundary!, anonymousStatus, adminManageStatus));
        }

        var evidencePath = Environment.GetEnvironmentVariable("CENTRAL_AUTHORIZATION_MATRIX_EVIDENCE_PATH");
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            var fullPath = Path.GetFullPath(evidencePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(new
            {
                schema = "CENTRAL_ACTUAL_HOST_AUTHORIZATION_MATRIX_V1",
                routeCount = observations.Count,
                routes = observations
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        using var unknownRoute = new HttpRequestMessage(HttpMethod.Get, "/api/admin/v1/__unmapped_authorization_probe");
        using var unknownResponse = await anonymous.SendAsync(unknownRoute);
        Assert.Equal(HttpStatusCode.NotFound, unknownResponse.StatusCode);
    }

    private static HttpRequestMessage CreateProbeRequest(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = method == "PUT" && path.Contains("/uploads/", StringComparison.OrdinalIgnoreCase)
                ? new ByteArrayContent([0x00])
                : method == "POST" && path.Equals("/internal/v1/sign", StringComparison.OrdinalIgnoreCase)
                    ? new StringContent(JsonSerializer.Serialize(new
                    {
                        signingRequestId = Guid.NewGuid(),
                        issuanceRequestId = Guid.NewGuid(),
                        payloadSha256 = new string('0', 64),
                        authorityRevision = 1,
                        keyId = "authorization-matrix-invalid-key",
                        keyVersion = 1,
                        purpose = "LICENSE",
                        canonicalPayload = "{}"
                    }), Encoding.UTF8, "application/json")
                    : new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static string ToRepresentativePath(string route)
    {
        return Regex.Replace(route, @"\{([^}:?]+)(?::([^}?]+))?\??\}", match =>
        {
            var constraint = match.Groups[2].Value;
            return constraint.Equals("guid", StringComparison.OrdinalIgnoreCase)
                ? "00000000-0000-0000-0000-000000000001"
                : "authorization-probe";
        });
    }

    private static string? DescribeHostBoundary(string method, string path, string? permission)
    {
        if (permission is not null)
        {
            return "admin-capability";
        }

        if (method == "GET" && Regex.IsMatch(path,
                @"^/api/licensing/v1/backups/download-grants/[^/]+/content$", RegexOptions.IgnoreCase))
        {
            return "opaque-download-grant";
        }

        if (method == "POST" && (path.Equals("/api/admin/v1/auth/login", StringComparison.OrdinalIgnoreCase) ||
                                  path.Equals("/api/admin/v1/auth/mfa", StringComparison.OrdinalIgnoreCase) ||
                                  path.Equals("/api/activation/v1/challenge", StringComparison.OrdinalIgnoreCase) ||
                                  path.Equals("/api/licensing/v1/bootstrap/challenge", StringComparison.OrdinalIgnoreCase)))
        {
            return "public-bootstrap";
        }

        if (method == "POST" && path.Equals("/api/licensing/v1/backups/rehearse", StringComparison.OrdinalIgnoreCase))
        {
            return "customer-restore-deferred";
        }

        if (method == "POST" && (path.Equals("/api/licensing/v1/primary/register", StringComparison.OrdinalIgnoreCase) ||
                                  path.Equals("/api/licensing/v1/activation/redeem", StringComparison.OrdinalIgnoreCase)))
        {
            return "bootstrap-proof";
        }

        if (method == "POST" && path.Equals("/api/admin/v1/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            return "session-bound";
        }

        if ((method == "GET" && path.Equals("/api/admin/v1/portal/session", StringComparison.OrdinalIgnoreCase)) ||
            (method == "POST" && path.Equals("/api/admin/v1/portal/sessions/revoke", StringComparison.OrdinalIgnoreCase)))
        {
            return "session-capability";
        }

        if (method == "POST" && path.Equals("/internal/v1/sign", StringComparison.OrdinalIgnoreCase))
        {
            return "internal-signer";
        }

        if ((method == "POST" && (path.Equals("/api/activation/v1/installations", StringComparison.OrdinalIgnoreCase) ||
                                   path.Equals("/api/activation/v1/redeem", StringComparison.OrdinalIgnoreCase))))
        {
            return "legacy-fail-closed";
        }

        if (IsInstallationProofBoundary(method, path))
        {
            return "installation-proof";
        }

        return null;
    }

    private static bool IsInstallationProofBoundary(string method, string path) =>
        (method == "POST" && (path.Equals("/api/licensing/v1/reconcile", StringComparison.OrdinalIgnoreCase) ||
                              path.Equals("/api/licensing/v1/support/bundles", StringComparison.OrdinalIgnoreCase) ||
                              path.StartsWith("/api/licensing/v1/backups/", StringComparison.OrdinalIgnoreCase))) ||
        (method == "GET" && (path.Equals("/api/licensing/v1/trust-set", StringComparison.OrdinalIgnoreCase) ||
                              Regex.IsMatch(path, @"^/api/licensing/v1/operations/[^/]+$", RegexOptions.IgnoreCase) ||
                              Regex.IsMatch(path, @"^/api/licensing/v1/activation/[^/]+/status$", RegexOptions.IgnoreCase) ||
                              path.StartsWith("/api/licensing/v1/backups/", StringComparison.OrdinalIgnoreCase))) ||
        (method == "PUT" && Regex.IsMatch(path, @"^/api/licensing/v1/uploads/[^/]+/content$", RegexOptions.IgnoreCase));

    private sealed record MappedApiRoute(string Method, string Path);
    private sealed record HostAuthorizationMatrixRow(string Method, string Path, string? RequiredPermission, string? Boundary);
    private sealed record HostAuthorizationObservation(string Method, string Path, string? RequiredPermission,
        string Boundary, int AnonymousStatus, int? AdminManageStatus);

    private async Task<HttpClient> CreateClientAsync(params string[] capabilities)
    {
        var fixture = await CreateSessionsAsync(capabilities, includeTarget: false);
        return ClientFor(fixture.CallerSessionId);
    }

    private async Task<(Guid CallerSessionId, Guid? TargetSessionId)> CreateSessionsAsync(
        string[] capabilities, bool includeTarget)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
        var now = DateTimeOffset.UtcNow;
        var admin = AdminUser.Create($"auth-matrix-{Guid.NewGuid():N}", "synthetic", "synthetic", null, false, now);
        var caller = CreateSession(admin, capabilities, now);
        var target = includeTarget ? CreateSession(admin, ["dashboard.view"], now) : null;
        db.AdminUsers.Add(admin);
        db.AdminSessions.Add(caller);
        if (target is not null)
        {
            db.AdminSessions.Add(target);
        }
        await db.SaveChangesAsync();
        return (caller.Id, target?.Id);
    }

    private static AdminSession CreateSession(AdminUser admin, IEnumerable<string> capabilities, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString("D")))).ToLowerInvariant();
        var session = AdminSession.Create(admin.Id, tokenHash, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8),
            true, admin.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, id);
        return session;
    }

    private HttpClient ClientFor(Guid sessionId)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Add("X-Session-Id", sessionId.ToString("D"));
        return client;
    }
}
