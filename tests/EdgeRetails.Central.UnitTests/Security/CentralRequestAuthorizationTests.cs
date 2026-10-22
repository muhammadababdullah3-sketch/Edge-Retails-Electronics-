using System.Net;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace EdgeRetails.Central.UnitTests.Security;

public class CentralRequestAuthorizationTests
{
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
    public void PermissionMap_CoversActualControllerRoutes(string method, string path)
    {
        Assert.NotNull(CentralRequestAuthorizationFilter.ResolveRequiredPermission(method, path));
    }

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
}
