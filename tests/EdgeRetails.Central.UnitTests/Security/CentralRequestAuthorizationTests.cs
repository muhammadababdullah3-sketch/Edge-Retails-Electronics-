using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Xunit.Abstractions;

namespace EdgeRetails.Central.UnitTests.Security;

public class CentralRequestAuthorizationTests
{
    private readonly ITestOutputHelper _output;

    public CentralRequestAuthorizationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private class TestAdminAuthService : IAdminAuthService
    {
        public Dictionary<Guid, AdminSessionDto> Sessions { get; } = new();

        public Task<PreAuthChallengeDto> StartLoginAsync(LoginRequestDto request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PreAuthChallengeDto { ChallengeId = Guid.NewGuid(), ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5) });

        public Task<AdminSessionDto> CompleteMfaAsync(MfaRequestDto request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AdminSessionDto?> ValidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session);
        }

        public Task<bool> LogoutAsync(Guid sessionId, string reason, CancellationToken cancellationToken = default)
        {
            Sessions.Remove(sessionId);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public void SessionCredentials_Parse_ExtractsFromSingleValidCookie()
    {
        var context = new DefaultHttpContext();
        var id = Guid.NewGuid();
        context.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={id:D}";

        var result = CentralSessionCredentials.Parse(context.Request);

        Assert.True(result.IsPresent);
        Assert.True(result.IsValid);
        Assert.Equal(id, result.SessionId);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void SessionCredentials_Parse_ExtractsFromBearerAuthorization()
    {
        var context = new DefaultHttpContext();
        var id = Guid.NewGuid();
        context.Request.Headers.Authorization = $"Bearer {id:D}";

        var result = CentralSessionCredentials.Parse(context.Request);

        Assert.True(result.IsPresent);
        Assert.True(result.IsValid);
        Assert.Equal(id, result.SessionId);
    }

    [Fact]
    public void SessionCredentials_Parse_ExtractsFromCustomSessionHeaders()
    {
        var id = Guid.NewGuid();

        var adminContext = new DefaultHttpContext();
        adminContext.Request.Headers[CentralSessionCredentials.AdminSessionHeader] = id.ToString();
        var adminResult = CentralSessionCredentials.Parse(adminContext.Request);
        Assert.True(adminResult.IsValid);
        Assert.Equal(id, adminResult.SessionId);

        var sessionContext = new DefaultHttpContext();
        sessionContext.Request.Headers[CentralSessionCredentials.SessionHeader] = id.ToString();
        var sessionResult = CentralSessionCredentials.Parse(sessionContext.Request);
        Assert.True(sessionResult.IsValid);
        Assert.Equal(id, sessionResult.SessionId);
    }

    [Fact]
    public void SessionCredentials_Parse_ConflictingTransports_ReturnsConflictError()
    {
        var context = new DefaultHttpContext();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        context.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={id1:D}";
        context.Request.Headers.Authorization = $"Bearer {id2:D}";

        var result = CentralSessionCredentials.Parse(context.Request);

        Assert.True(result.IsPresent);
        Assert.False(result.IsValid);
        Assert.Equal("SESSION_CREDENTIAL_CONFLICT", result.ErrorCode);
        Assert.Equal(Guid.Empty, result.SessionId);
    }

    [Fact]
    public void SessionCredentials_Parse_MalformedGuid_ReturnsInvalid()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}=not-a-valid-guid";

        var result = CentralSessionCredentials.Parse(context.Request);

        Assert.True(result.IsPresent);
        Assert.False(result.IsValid);
        Assert.Equal("SESSION_CREDENTIAL_INVALID", result.ErrorCode);
    }

    [Theory]
    [InlineData("POST", "/api/admin/v1/auth/login")]
    [InlineData("POST", "/api/admin/v1/auth/mfa")]
    [InlineData("POST", "/api/activation/v1/challenge")]
    [InlineData("POST", "/api/licensing/v1/bootstrap/challenge")]
    public async Task Filter_ExplicitAnonymousExceptions_AreAllowedWithoutSession(string method, string path)
    {
        var authService = new TestAdminAuthService();
        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        Assert.Null(filterContext.Result);
        Assert.Equal("no-store, no-cache, must-revalidate", httpContext.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("GET", "/api/licensing/v1/operations/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/licensing/v1/certificates")]
    public async Task Filter_InstallationEndpoints_FailClosedWithoutAsymmetricProof(string method, string path)
    {
        var authService = new TestAdminAuthService();
        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        httpContext.Request.Headers["X-Installation-Id"] = "inst-123";
        httpContext.Request.Headers["X-Shop-Scope"] = "shop-456";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(filterContext.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/support/v1/tickets")]
    [InlineData("POST", "/api/admin/v1/support/tickets")]
    public async Task Filter_SupportTicketCreation_RequiresPersistedCapabilityNotClientHeaders(string method, string path)
    {
        var authService = new TestAdminAuthService();
        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        httpContext.Request.Headers["X-Caller-Scope"] = "shop_operator";
        httpContext.Request.Headers["X-Requester-Id"] = Guid.NewGuid().ToString();

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        Assert.IsType<UnauthorizedObjectResult>(filterContext.Result);

        var sessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "support.create" }
        };
        httpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";
        filterContext.Result = null;
        await filter.OnAuthorizationAsync(filterContext);
        Assert.Null(filterContext.Result);
    }

    [Theory]
    [InlineData("POST", "/api/support/v1/tickets/11111111-1111-1111-1111-111111111111/actions")]
    [InlineData("POST", "/api/support/v1/tickets/11111111-1111-1111-1111-111111111111/commands")]
    public async Task Filter_SupportTicketActions_RequiresPersistedCapabilityNotClientHeaders(string method, string path)
    {
        var authService = new TestAdminAuthService();
        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        httpContext.Request.Headers["X-Caller-Scope"] = "support_agent";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        Assert.IsType<UnauthorizedObjectResult>(filterContext.Result);

        var sessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "support.reply" }
        };
        httpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";
        filterContext.Result = null;
        await filter.OnAuthorizationAsync(filterContext);
        if (path.EndsWith("/actions", StringComparison.Ordinal))
        {
            Assert.Null(filterContext.Result);
        }
        else
        {
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(filterContext.Result).StatusCode);
        }
    }

    [Fact]
    public async Task Filter_InternalSigner_Cross_Origin_IsProhibited()
    {
        var authService = new TestAdminAuthService();
        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/internal/v1/sign";
        httpContext.Request.Headers.Origin = "https://evil.example.com";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        var forbidden = Assert.IsType<ObjectResult>(filterContext.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Filter_InternalSigner_WithoutOriginPassesToAuditedControllerAuthorization()
    {
        var authService = new TestAdminAuthService();
        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/internal/v1/sign";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        Assert.Null(filterContext.Result);
    }

    [Fact]
    public async Task Filter_PortalSessionRevoke_DifferentTarget_FailsForbidden()
    {
        var authService = new TestAdminAuthService();
        var sessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "sessions.view" }
        };

        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/admin/v1/portal/sessions/revoke";
        httpContext.Request.QueryString = new QueryString($"?targetSessionId={Guid.NewGuid()}");
        httpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        var forbidden = Assert.IsType<ObjectResult>(filterContext.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Filter_PortalSessionRevoke_RequiresSpecificCapabilityAndAllowsAuthorizedTarget()
    {
        var authService = new TestAdminAuthService();
        var sessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "sessions.revoke" }
        };

        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/admin/v1/portal/sessions/revoke";
        httpContext.Request.QueryString = new QueryString($"?targetSessionId={otherSessionId:D}");
        httpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        Assert.Null(filterContext.Result);
    }

    [Fact]
    public async Task Filter_PortalSession_DoesNotAllowGenericAdminManage()
    {
        var authService = new TestAdminAuthService();
        var sessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "admin.manage" }
        };
        var filter = new CentralRequestAuthorizationFilter(authService);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/admin/v1/portal/session";
        httpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        var forbidden = Assert.IsType<ObjectResult>(filterContext.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Filter_AdminManageCapability_DoesNotBridgeToCustomerCreate()
    {
        var authService = new TestAdminAuthService();
        var sessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "admin.manage" }
        };

        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/admin/v1/customers";
        httpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        var forbidden = Assert.IsType<ObjectResult>(filterContext.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Filter_ExactCapability_GrantsAccess()
    {
        var authService = new TestAdminAuthService();
        var sessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "customer.create" }
        };

        var filter = new CentralRequestAuthorizationFilter(authService);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/admin/v1/customers";
        httpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        Assert.Null(filterContext.Result);
    }

    [Theory]
    [InlineData("/api/admin/v1/payments/11111111-1111-1111-1111-111111111111/review", "payment.review")]
    [InlineData("/api/admin/v1/licenses/issue", "license.issue")]
    public async Task Filter_ManualCommercialActionsRequireTheirOwnPersistedCapability(string path, string capability)
    {
        var authService = new TestAdminAuthService();
        var filter = new CentralRequestAuthorizationFilter(authService);
        var requestContext = new DefaultHttpContext();
        requestContext.Request.Method = "POST";
        requestContext.Request.Path = path;
        requestContext.Request.Headers["X-Caller-Scope"] = "admin";
        requestContext.Request.Headers["X-Admin-Role"] = capability;
        var actionContext = new ActionContext(requestContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);
        Assert.IsType<UnauthorizedObjectResult>(filterContext.Result);

        var sessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "admin.manage" }
        };
        requestContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";
        filterContext.Result = null;
        await filter.OnAuthorizationAsync(filterContext);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(filterContext.Result).StatusCode);

        authService.Sessions[sessionId].Capabilities = new List<string> { capability };
        filterContext.Result = null;
        await filter.OnAuthorizationAsync(filterContext);
        Assert.Null(filterContext.Result);
    }

    [Fact]
    public async Task Filter_PortalSession_RequiresSessionViewCapability()
    {
        var authService = new TestAdminAuthService();
        var sessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "dashboard.view" }
        };
        var filter = new CentralRequestAuthorizationFilter(authService);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/api/admin/v1/portal/session";
        httpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        var forbidden = Assert.IsType<ObjectResult>(filterContext.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/admin/v1/sensitive-approvals/11111111-1111-1111-1111-111111111111/decision")]
    [InlineData("POST", "/api/admin/v1/sensitive-approvals/unmask-pii")]
    [InlineData("POST", "/api/lifecycle/v1/sensitive-approvals/11111111-1111-1111-1111-111111111111/decide")]
    [InlineData("POST", "/api/lifecycle/v1/policies")]
    [InlineData("POST", "/api/lifecycle/v1/licenses/11111111-1111-1111-1111-111111111111/pause")]
    [InlineData("POST", "/api/lifecycle/v1/licenses/11111111-1111-1111-1111-111111111111/resume")]
    [InlineData("POST", "/api/lifecycle/v1/licenses/11111111-1111-1111-1111-111111111111/suspend")]
    [InlineData("POST", "/api/lifecycle/v1/licenses/11111111-1111-1111-1111-111111111111/reinstate")]
    [InlineData("POST", "/api/lifecycle/v1/licenses/11111111-1111-1111-1111-111111111111/revoke")]
    [InlineData("POST", "/api/admin/v1/licenses/11111111-1111-1111-1111-111111111111/pause")]
    [InlineData("POST", "/api/admin/v1/licenses/11111111-1111-1111-1111-111111111111/resume")]
    [InlineData("POST", "/api/admin/v1/licenses/11111111-1111-1111-1111-111111111111/suspend")]
    [InlineData("POST", "/api/admin/v1/licenses/11111111-1111-1111-1111-111111111111/reinstate")]
    [InlineData("POST", "/api/admin/v1/licenses/11111111-1111-1111-1111-111111111111/revoke")]
    [InlineData("GET", "/api/admin/v1/dashboard/customer360/11111111-1111-1111-1111-111111111111")]
    [InlineData("GET", "/api/admin/v1/dashboard/queues/SUPPORT_INBOX")]
    [InlineData("GET", "/api/admin/v1/backups")]
    public void PermissionMap_CoversActualControllerRoutes(string method, string path)
    {
        Assert.NotNull(CentralRequestAuthorizationFilter.ResolveRequiredPermission(method, path));
    }

    [Fact]
    public void EveryMappedControllerRoute_HasAnExplicitAuthorizationBoundary()
    {
        var controllerTypes = typeof(CentralRequestAuthorizationFilter).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type));
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var controllerType in controllerTypes)
        {
            var controllerRoute = controllerType.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(attribute => attribute.Template)
                .FirstOrDefault(template => template is not null) ?? string.Empty;

            foreach (var action in controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                foreach (var route in action.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
                {
                    var methods = route.HttpMethods?.ToArray() ?? Array.Empty<string>();
                    var template = ResolveRouteTemplate(controllerRoute, route.Template);
                    var path = ToRepresentativePath(template);

                    foreach (var method in methods)
                    {
                        routes.Add($"{method.ToUpperInvariant()} {path}");
                    }
                }
            }
        }

        Assert.NotEmpty(routes);
        _output.WriteLine($"Discovered mapped controller routes: {routes.Count}");
        foreach (var route in routes.OrderBy(route => route, StringComparer.OrdinalIgnoreCase))
        {
            _output.WriteLine($"Covered route: {route} => {DescribeAuthorizationBoundary(route)}");
        }
        var unclassified = routes
            .Where(route => DescribeAuthorizationBoundary(route) is null)
            .OrderBy(route => route, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(unclassified.Length == 0,
            $"Controller routes without an explicit authorization boundary: {string.Join("; ", unclassified)}");
    }

    [Theory]
    [InlineData("/api/activation/v1/installations")]
    [InlineData("/api/activation/v1/redeem")]
    public async Task LegacyActivationRoutes_FailClosedEvenForAdminManage(string path)
    {
        var authService = new TestAdminAuthService();
        var sessionId = Guid.NewGuid();
        authService.Sessions[sessionId] = new AdminSessionDto
        {
            SessionId = sessionId,
            AdminUserId = Guid.NewGuid(),
            Capabilities = new List<string> { "admin.manage", "activation.issue" }
        };
        var filter = new CentralRequestAuthorizationFilter(authService);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = path;
        httpContext.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={sessionId:D}";
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        await filter.OnAuthorizationAsync(filterContext);

        var denied = Assert.IsType<ObjectResult>(filterContext.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);
        Assert.Equal("FORBIDDEN", denied.Value?.GetType().GetProperty("code")?.GetValue(denied.Value));
    }

    private static string? DescribeAuthorizationBoundary(string route)
    {
        var split = route.IndexOf(' ');
        var method = route[..split];
        var path = route[(split + 1)..];

        var permission = CentralRequestAuthorizationFilter.ResolveRequiredPermission(method, path);
        if (permission is not null)
        {
            return $"admin-capability:{permission}";
        }

        if (method == "GET" && Regex.IsMatch(path,
                "^/api/licensing/v1/backups/download-grants/[^/]+/content$", RegexOptions.IgnoreCase))
        {
            return "opaque-download-grant";
        }

        if (method == "POST" && path.Equals("/api/licensing/v1/backups/rehearse", StringComparison.OrdinalIgnoreCase))
        {
            return "customer-restore-explicit-501-deferral";
        }

        if (IsInstallationProofRoute(method, path))
        {
            return "installation-asymmetric-proof-or-one-time-bootstrap-proof";
        }

        return (method, path.ToLowerInvariant()) switch
        {
            ("POST", "/api/admin/v1/auth/login") => "pre-authentication-login",
            ("POST", "/api/admin/v1/auth/mfa") => "pre-authentication-mfa",
            ("POST", "/api/activation/v1/challenge") => "anonymous-rate-limited-bootstrap-challenge",
            ("POST", "/api/licensing/v1/bootstrap/challenge") => "anonymous-rate-limited-bootstrap-challenge",
            ("POST", "/api/licensing/v1/primary/register") => "one-time-bootstrap-proof-controller",
            ("POST", "/api/licensing/v1/activation/redeem") => "one-time-bootstrap-proof-controller",
            ("GET", "/api/admin/v1/portal/session") => "validated-admin-session-context",
            ("POST", "/api/admin/v1/auth/logout") => "validated-admin-session-logout",
            ("POST", "/api/admin/v1/portal/sessions/revoke") => "sessions-revoke-capability",
            ("POST", "/internal/v1/sign") => "internal-signer-token-and-origin-boundary",
            ("POST", "/api/activation/v1/installations") => "fail-closed-unapproved-legacy-route",
            ("POST", "/api/activation/v1/redeem") => "fail-closed-unapproved-legacy-route",
            _ => null
        };
    }

    private static bool IsInstallationProofRoute(string method, string path) =>
        (method == "POST" && (path.Equals("/api/licensing/v1/reconcile", StringComparison.OrdinalIgnoreCase) ||
                               path.Equals("/api/licensing/v1/support/bundles", StringComparison.OrdinalIgnoreCase) ||
                               path.StartsWith("/api/licensing/v1/backups/", StringComparison.OrdinalIgnoreCase))) ||
        (method == "GET" && (path.Equals("/api/licensing/v1/trust-set", StringComparison.OrdinalIgnoreCase) ||
                              Regex.IsMatch(path, "^/api/licensing/v1/operations/[^/]+$", RegexOptions.IgnoreCase) ||
                              Regex.IsMatch(path, "^/api/licensing/v1/activation/[^/]+/status$", RegexOptions.IgnoreCase) ||
                              path.StartsWith("/api/licensing/v1/backups/", StringComparison.OrdinalIgnoreCase))) ||
        (method == "PUT" && Regex.IsMatch(path, "^/api/licensing/v1/uploads/[^/]+/content$", RegexOptions.IgnoreCase)) ||
        (method == "GET" && Regex.IsMatch(path, "^/api/licensing/v1/backups/download-grants/[^/]+/content$", RegexOptions.IgnoreCase)) ||
        (method == "POST" && (path.Equals("/api/licensing/v1/primary/register", StringComparison.OrdinalIgnoreCase) ||
                               path.Equals("/api/licensing/v1/activation/redeem", StringComparison.OrdinalIgnoreCase)));

    private static string ResolveRouteTemplate(string controllerTemplate, string? actionTemplate)
    {
        if (actionTemplate is null)
        {
            return controllerTemplate;
        }

        if (actionTemplate.StartsWith("~/", StringComparison.Ordinal) || actionTemplate.StartsWith("/", StringComparison.Ordinal))
        {
            return actionTemplate.TrimStart('~', '/');
        }

        return string.IsNullOrEmpty(controllerTemplate)
            ? actionTemplate
            : $"{controllerTemplate.TrimEnd('/')}/{actionTemplate.TrimStart('/')}";
    }

    private static string ToRepresentativePath(string template) =>
        "/" + Regex.Replace(template, "\\{([^}:]+)(?::[^}]+)?\\}", match =>
        {
            var name = match.Groups[1].Value;
            return name.Contains("id", StringComparison.OrdinalIgnoreCase)
                ? "00000000-0000-0000-0000-000000000001"
                : "sample";
        }).TrimStart('/');

    [Fact]
    public async Task CsrfMiddleware_CustomerRestoreShim_BypassesDatabaseAndSessionChecks()
    {
        var called = false;
        var middleware = new CsrfProtectionMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Scheme = "https";
        context.Request.Method = "POST";
        context.Request.Path = "/api/licensing/v1/backups/rehearse";

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        Assert.Contains("CUSTOMER_RESTORE_NOT_SUPPORTED", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task CsrfMiddleware_ConflictingCredentialsOnMutatingRequest_ReturnsConflict()
    {
        var middleware = new CsrfProtectionMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Method = "POST";
        context.Request.Path = "/api/admin/v1/customers";
        context.Request.Host = new HostString("central.edgeretails.local");

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        context.Request.Headers.Cookie = $"{CentralSessionCredentials.CookieName}={id1:D}";
        context.Request.Headers[CentralSessionCredentials.AdminSessionHeader] = id2.ToString();

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task CsrfMiddleware_CustomHeaderAlias_RequiresOriginValidationOnMutatingRequest()
    {
        var middleware = new CsrfProtectionMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Method = "POST";
        context.Request.Path = "/api/admin/v1/customers";
        context.Request.Host = new HostString("central.edgeretails.local");

        var id = Guid.NewGuid();
        context.Request.Headers[CentralSessionCredentials.AdminSessionHeader] = id.ToString();
        context.Request.Headers.Origin = "https://unauthorized-origin.com";

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task RateLimitingMiddleware_MissingPolicy_FailsClosedOnProtectedEndpoint()
    {
        var previousAttempts = Environment.GetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS");
        var previousWindow = Environment.GetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS");
        RateLimitingMiddleware.Reset();
        try
        {
            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS", null);
            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS", null);
            var nextCalled = false;
            var middleware = new RateLimitingMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Request.Method = "POST";
            context.Request.Path = "/api/admin/v1/auth/login";

            await middleware.InvokeAsync(context);

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
            Assert.Contains("no-store", context.Response.Headers["Cache-Control"].ToString(), StringComparison.OrdinalIgnoreCase);
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            Assert.Contains("RATE_LIMIT_CONFIGURATION_REQUIRED", await reader.ReadToEndAsync());

            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS", "2");
            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS", "60");
            var nextCalls = 0;
            var configuredMiddleware = new RateLimitingMiddleware(_ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            });
            DefaultHttpContext CreateContext()
            {
                var configured = new DefaultHttpContext();
                configured.Response.Body = new MemoryStream();
                configured.Request.Method = "POST";
                configured.Request.Path = "/api/admin/v1/auth/login";
                configured.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                return configured;
            }

            await configuredMiddleware.InvokeAsync(CreateContext());
            await configuredMiddleware.InvokeAsync(CreateContext());
            var limitedContext = CreateContext();
            await configuredMiddleware.InvokeAsync(limitedContext);
            Assert.Equal(2, nextCalls);
            Assert.Equal(StatusCodes.Status429TooManyRequests, limitedContext.Response.StatusCode);
            Assert.Contains("no-store", limitedContext.Response.Headers["Cache-Control"].ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS", previousAttempts);
            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS", previousWindow);
            RateLimitingMiddleware.Reset();
        }
    }

    [Fact]
    public async Task RateLimitingMiddleware_ThrottlesExactInstallationBootstrapProofRoutes()
    {
        var previousAttempts = Environment.GetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS");
        var previousWindow = Environment.GetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS");
        RateLimitingMiddleware.Reset();
        try
        {
            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS", "5");
            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS", "60");
            var nextCalls = 0;
            var middleware = new RateLimitingMiddleware(_ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            });

            foreach (var path in new[]
            {
                "/api/licensing/v1/bootstrap/challenge",
                "/api/licensing/v1/primary/register",
                "/api/licensing/v1/activation/redeem"
            })
            {
                for (var attempt = 1; attempt <= 6; attempt++)
                {
                    var context = new DefaultHttpContext();
                    context.Response.Body = new MemoryStream();
                    context.Request.Method = "POST";
                    context.Request.Path = path;
                    context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");

                    await middleware.InvokeAsync(context);

                    Assert.Equal(attempt <= 5 ? StatusCodes.Status200OK : StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
                    if (attempt == 6)
                    {
                        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            // Similar-looking bootstrap paths are not implicitly treated as protected routes.
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                var context = new DefaultHttpContext();
                context.Request.Method = "POST";
                context.Request.Path = "/api/licensing/v1/bootstrap/challenge/extra";
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
                await middleware.InvokeAsync(context);
                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            }

            Assert.Equal(21, nextCalls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS", previousAttempts);
            Environment.SetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS", previousWindow);
            RateLimitingMiddleware.Reset();
        }
    }

}
