using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using EdgeRetails.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

// Reuse the same using set from Phase2ApiContractAndSecurityTests so this test file stays self-contained.
using PermissionKeys = EdgeRetails.Application.Features.Identity.PermissionKeys;

namespace EdgeRetails.IntegrationTests;

/// <summary>
/// Complete authorization matrix for every mutating route on <c>CatalogController</c>.
/// </summary>
/// <remarks>
/// <para>
/// CAT-107 open item (2) requires proof that each equivalent catalog mutation route enforces
/// <c>InventoryManage</c>, not only <c>/upgrade/fixed-units</c>. Previously a single route had a
/// route-level authorization test while fourteen others had none, so a regression that dropped a
/// permission check anywhere else would not have been detected.
/// </para>
/// <para>
/// The matrix below is generated from the controller's own metadata, so a newly added endpoint
/// cannot be silently omitted: the theory data is discovered by reflection over the actual
/// <c>HttpPost</c>/<c>HttpPut</c>/<c>HttpPatch</c>/<c>HttpDelete</c> actions and any endpoint that
/// is not listed in the known-route table fails the coverage assertion.
/// </para>
/// </remarks>
[Collection("Phase11CatalogSeed")]
public sealed class CatalogMutationRouteAuthorizationMatrixTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Every mutating catalog route, with a request body that BINDS SUCCESSFULLY.
    /// Path placeholders are substituted at request time.
    /// </summary>
    /// <remarks>
    /// The bodies below are deliberately complete rather than empty. Controllers are marked
    /// <c>[ApiController]</c>, so an empty <c>{}</c> fails automatic model-state validation on the
    /// non-nullable members (<c>Product</c>, <c>Name</c>, <c>Symbol</c>) and the pipeline answers
    /// <c>400 BadRequest</c> — the action body, and therefore <c>RequirePermission</c>, never runs.
    /// An under-specified body would silently weaken this matrix into a *binding* test that proves
    /// nothing about authorization. Supplying bindable payloads is what makes the assertion reach
    /// the permission guard. <see cref="MalformedBody_FromUnprivilegedCaller_NeverMutates"/> keeps
    /// the 400 path covered separately.
    /// </remarks>
    private static readonly Dictionary<string, (string Method, string Path, string? Body)> KnownRoutes =
        new(StringComparer.Ordinal)
        {
            ["upgrade/fixed-units"] = ("POST", "/api/catalog/upgrade/fixed-units", null),

            // CreateProductCommand(Guid ActorId, ProductCatalogInput Product) — Product is required,
            // and ProductCatalogInput.Name is a non-nullable reference member under [ApiController].
            ["products"] = ("POST", "/api/catalog/products",
                "{\"product\":{\"name\":\"Authorization Probe Product\",\"sku\":\"AUTH-PROBE\",\"baseUnitId\":\"00000000-0000-0000-0000-000000000000\"}}"),

            // SaveProductAggregateCommand — Product, Units and LinkedSupplierIds are all required.
            ["products/aggregate"] = ("POST", "/api/catalog/products/aggregate",
                "{\"product\":{\"name\":\"Authorization Probe Aggregate\",\"sku\":\"AUTH-PROBE-AGG\"},\"units\":[],\"linkedSupplierIds\":[]}"),

            // UpdateProductCommand — ExpectedVersion plus the nested Product are both required.
            ["products/{id:guid}"] = ("PUT", "/api/catalog/products/{id}",
                "{\"expectedVersion\":1,\"product\":{\"name\":\"Authorization Probe Update\",\"sku\":\"AUTH-PROBE-UPD\"}}"),

            ["products/{id:guid}/suppliers/{supplierId:guid}/active"] =
                ("POST", "/api/catalog/products/{id}/suppliers/{sid}/active", "{\"isActive\":true,\"expectedVersion\":1}"),
            ["products/{id:guid}/deactivate"] =
                ("POST", "/api/catalog/products/{id}/deactivate", "{\"expectedVersion\":1}"),
            ["products/{id:guid}/reactivate"] =
                ("POST", "/api/catalog/products/{id}/reactivate", "{\"expectedVersion\":1}"),

            // SaveCompanyCommand(ActorId, CompanyId?, Name, Code?) — Name is required.
            ["companies"] = ("POST", "/api/catalog/companies", "{\"name\":\"Authorization Probe Company\"}"),
            ["companies/{id:guid}/active"] =
                ("POST", "/api/catalog/companies/{id}/active", "{\"isActive\":true}"),

            // SaveCategoryCommand(ActorId, CategoryId?, Name, IdentitySymbol?) — Name is required.
            ["categories"] = ("POST", "/api/catalog/categories", "{\"name\":\"Authorization Probe Category\"}"),
            ["categories/{id:guid}/active"] =
                ("POST", "/api/catalog/categories/{id}/active", "{\"isActive\":true}"),

            // SaveUnitCommand(ActorId, UnitId?, Name, Symbol, DisplayDecimalPlaces) — Name and Symbol required.
            ["units"] = ("POST", "/api/catalog/units",
                "{\"name\":\"Authorization Probe Unit\",\"symbol\":\"apu\",\"displayDecimalPlaces\":2}"),
            ["units/{id:guid}/active"] =
                ("POST", "/api/catalog/units/{id}/active", "{\"isActive\":true}"),

            ["products/{id:guid}/units"] = ("POST", "/api/catalog/products/{id}/units", "[]"),
            ["units/{unitId:guid}/barcode"] = ("POST", "/api/catalog/units/{id}/barcode", "{\"barcode\":\"123\"}"),
        };

    public static TheoryData<string> MutatingRoutes()
    {
        var data = new TheoryData<string>();
        foreach (var key in KnownRoutes.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            data.Add(key);
        }

        return data;
    }

    /// <summary>
    /// Guards the matrix itself. If a new mutating action is added to the controller without being
    /// added here, this fails instead of the suite quietly reporting full coverage.
    /// </summary>
    [Fact]
    public void MatrixCoversEveryMutatingControllerAction()
    {
        var controller = typeof(EdgeRetails.Server.Controllers.CatalogController);
        var mutatingTemplates = new List<string>();

        foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var verb = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                .SelectMany(a => a.HttpMethods)
                .Select(m => m.ToUpperInvariant())
                .Where(m => m is "POST" or "PUT" or "PATCH" or "DELETE")
                .ToList();

            if (verb.Count == 0)
            {
                continue;
            }

            var template = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                .FirstOrDefault()?.Template;
            Assert.False(string.IsNullOrWhiteSpace(template), $"Action {method.Name} has no route template.");
            mutatingTemplates.Add(template!);
        }

        Assert.NotEmpty(mutatingTemplates);

        var uncovered = mutatingTemplates
            .Where(t => !KnownRoutes.ContainsKey(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            "These mutating controller actions have no authorization matrix entry: " + string.Join(", ", uncovered));

        // The matrix is not allowed to describe routes that no longer exist.
        var stale = KnownRoutes.Keys
            .Where(k => !mutatingTemplates.Contains(k, StringComparer.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            "These matrix entries no longer correspond to a controller action and must be removed: " + string.Join(", ", stale));
    }

    /// <summary>
    /// Every mutating catalog route must reject an anonymous caller before doing any work.
    /// </summary>
    [Theory]
    [MemberData(nameof(MutatingRoutes))]
    public async Task MutatingRoute_WithoutSession_Returns_401(string routeKey)
    {
        var (method, path, body) = KnownRoutes[routeKey];

        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        using var client = baseFactory.CreateClient();

        using var request = BuildRequest(baseFactory, method, Materialize(path), body, sessionId: null);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.session_missing", payload.GetProperty("code").GetString());
    }

    /// <summary>
    /// Every mutating catalog route must reject an authenticated caller who lacks
    /// <c>InventoryManage</c>. This is the core of CAT-107 open item (2).
    /// </summary>
    [Theory]
    [MemberData(nameof(MutatingRoutes))]
    public async Task MutatingRoute_WithoutInventoryManagePermission_Returns_403(string routeKey)
    {
        var (method, path, body) = KnownRoutes[routeKey];

        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();

        // Authenticated, active, correctly-roled user — but deliberately without InventoryManage.
        var unprivilegedUserId = Guid.NewGuid();
        baseFactory.Identity.SeedUser(new User
        {
            Id = unprivilegedUserId,
            RoleId = baseFactory.ValidRoleId,
            DisplayName = "Cashier Without Inventory Manage",
            Status = UserStatus.Active
        });
        baseFactory.Identity.SetPermissions(unprivilegedUserId, new HashSet<string>
        {
            PermissionKeys.SalesCreate,
            PermissionKeys.SalesPosUse,
        });

        var sessionId = Guid.NewGuid();
        baseFactory.Sessions.SeedSession(new UserSession
        {
            Id = sessionId,
            UserId = unprivilegedUserId,
            StartedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        });

        using var client = baseFactory.CreateClient();
        using var request = BuildRequest(baseFactory, method, Materialize(path), body, sessionId);
        using var response = await client.SendAsync(request);

        // The denial must be an authorization denial, never a success or an unrelated validation
        // error that happens to be non-2xx.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("authorization.denied", payload.GetProperty("code").GetString());
    }

    /// <summary>
    /// A caller holding <c>InventoryManage</c> must get past authorization. The route may still
    /// fail on its payload or missing entity, but it must never answer 401/403.
    /// </summary>
    [Theory]
    [MemberData(nameof(MutatingRoutes))]
    public async Task MutatingRoute_WithInventoryManagePermission_IsNotDenied(string routeKey)
    {
        var (method, path, body) = KnownRoutes[routeKey];

        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        using var client = baseFactory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();

        using var request = BuildRequest(baseFactory, method, Materialize(path), body, sessionId);
        using var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Defense in depth: an authenticated caller without <c>InventoryManage</c> must never receive a
    /// success status, whichever rejection layer fires first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MutatingRoute_WithoutInventoryManagePermission_Returns_403"/> pins the strong
    /// property — the permission guard itself denies — using bodies that bind cleanly. This test
    /// deliberately sends a body that cannot satisfy the binding model, which on the six routes with
    /// required reference members trips MVC's automatic model-state filter and yields
    /// <c>400 BadRequest</c> before the action (and its <c>RequirePermission</c> call) is reached.
    /// </para>
    /// <para>
    /// Which layer rejects is an implementation detail; that <em>nothing succeeds</em> is not. The
    /// assertion is therefore "not 2xx" rather than a specific code, so it keeps holding if the
    /// authorization guard is later moved ahead of model binding and the status changes from 400 to
    /// 403.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(MutatingRoutes))]
    public async Task MutatingRoute_UnprivilegedCallerWithMalformedBody_NeverSucceeds(string routeKey)
    {
        var (method, path, _) = KnownRoutes[routeKey];

        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();

        var unprivilegedUserId = Guid.NewGuid();
        baseFactory.Identity.SeedUser(new User
        {
            Id = unprivilegedUserId,
            RoleId = baseFactory.ValidRoleId,
            DisplayName = "Cashier Without Inventory Manage",
            Status = UserStatus.Active
        });
        baseFactory.Identity.SetPermissions(unprivilegedUserId, new HashSet<string>
        {
            PermissionKeys.SalesCreate,
            PermissionKeys.SalesPosUse,
        });

        var sessionId = Guid.NewGuid();
        baseFactory.Sessions.SeedSession(new UserSession
        {
            Id = sessionId,
            UserId = unprivilegedUserId,
            StartedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        });

        using var client = baseFactory.CreateClient();

        // "{}" satisfies no required reference member; where the route has none it is simply an
        // empty mutation, which must still be rejected rather than silently accepted.
        using var request = BuildRequest(baseFactory, method, Materialize(path), "{}", sessionId);
        using var response = await client.SendAsync(request);

        Assert.False(
            response.IsSuccessStatusCode,
            $"Unprivileged caller received {(int)response.StatusCode} {response.StatusCode} on '{routeKey}'.");
    }

    private static HttpRequestMessage BuildRequest(
        Phase2ServerWebApplicationFactory factory,
        string method,
        string path,
        string? body,
        Guid? sessionId)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Terminal-Id", factory.ActiveTerminalId.ToString());
        request.Headers.Add("X-Terminal-Secret", factory.ActiveTerminalSecret);
        if (sessionId.HasValue)
        {
            request.Headers.Add("X-Session-Id", sessionId.Value.ToString());
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>Replaces route placeholders with concrete, non-empty identifiers.</summary>
    private static string Materialize(string template) =>
        template
            .Replace("{id}", Guid.NewGuid().ToString("D"), StringComparison.Ordinal)
            .Replace("{sid}", Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
}
