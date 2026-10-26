using System.Text.Json;
using EdgeRetails.Central.Api.Security;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdgeRetails.Central.IntegrationTests.Contracts;

public sealed class CentralContractRouteCoverageTests
{
    [Fact]
    public void EveryEffective040RouteHasAnImplementedControllerMethod()
    {
        var contractPath = FindContractPath();
        using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));
        var expected = contract.RootElement.GetProperty("routes").EnumerateArray()
            .Select(route => new RouteKey(
                route.GetProperty("method").GetString()!,
                Normalize(route.GetProperty("path").GetString()!)))
            .ToHashSet();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(Program).Assembly);
        using var provider = services.BuildServiceProvider();
        var actions = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items;

        var actual = new HashSet<RouteKey>();
        foreach (var action in actions)
        {
            var template = action.AttributeRouteInfo?.Template;
            if (string.IsNullOrWhiteSpace(template))
            {
                continue;
            }

            var constraints = action.ActionConstraints?.OfType<HttpMethodActionConstraint>()
                .SelectMany(constraint => constraint.HttpMethods)
                .ToArray();
            if (constraints is null || constraints.Length == 0)
            {
                continue;
            }

            foreach (var method in constraints)
            {
                actual.Add(new RouteKey(method.ToUpperInvariant(), Normalize(template)));
            }
        }

        var missing = expected.Except(actual)
            .OrderBy(route => route.Path, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .Select(route => $"{route.Method} {route.Path}")
            .ToArray();

        Assert.True(missing.Length == 0,
            $"Effective040 contract routes are missing from Central API controllers ({missing.Length}):{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryEffective040PermissionMapsToAnExplicitHostBoundary()
    {
        var contractPath = FindContractPath();
        using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));
        var unmapped = new List<string>();

        foreach (var route in contract.RootElement.GetProperty("routes").EnumerateArray())
        {
            var method = route.GetProperty("method").GetString()!.ToUpperInvariant();
            var path = route.GetProperty("path").GetString()!;
            var permission = route.GetProperty("permission").GetString()!;
            var expectedBoundary = ExpectedBoundary(method, path, permission);
            if (expectedBoundary is null)
            {
                unmapped.Add($"{method} {path} ({permission}): no explicit boundary classification");
                continue;
            }

            var resolvedPermission = CentralRequestAuthorizationFilter.ResolveRequiredPermission(
                method, RepresentativePath(path));
            var matches = expectedBoundary == "admin-capability"
                ? string.Equals(resolvedPermission, permission, StringComparison.Ordinal)
                : resolvedPermission is null;
            if (!matches)
            {
                unmapped.Add($"{method} {path} ({permission}): expected {expectedBoundary}, filter resolved {resolvedPermission ?? "<none>"}");
            }
        }

        Assert.True(unmapped.Count == 0,
            $"Effective040 permissions do not match explicit Central authorization boundaries ({unmapped.Count}):{Environment.NewLine}" +
            string.Join(Environment.NewLine, unmapped));
    }

    private static string? ExpectedBoundary(string method, string path, string permission)
    {
        if (method == "POST" && permission == "auth.login" && path == "/api/admin/v1/auth/login" ||
            method == "POST" && permission == "auth.mfa" && path == "/api/admin/v1/auth/mfa" ||
            method == "POST" && permission == "installation.bootstrap.challenge" && path == "/api/licensing/v1/bootstrap/challenge")
        {
            return "public-bootstrap";
        }

        if (method == "POST" && permission == "installation.register" && path == "/api/licensing/v1/primary/register" ||
            method == "POST" && permission == "activation.redeem" && path == "/api/licensing/v1/activation/redeem")
        {
            return "bootstrap-proof";
        }

        if (method == "POST" && permission == "installation.reconcile" && path == "/api/licensing/v1/reconcile" ||
            method == "POST" && permission == "backup.upload.init" && path == "/api/licensing/v1/backups/uploads" ||
            method == "POST" && permission == "backup.upload.finalize" && path == "/api/licensing/v1/backups/uploads/{id}/finalize" ||
            method == "POST" && permission == "support.bundle" && path == "/api/licensing/v1/support/bundles" ||
            method == "GET" && permission == "backup.download" && path == "/api/licensing/v1/backups/{id}/download" ||
            method == "GET" && permission == "operation.lookup" && path == "/api/licensing/v1/operations/{id}" ||
            method == "GET" && permission == "activation.status" && path == "/api/licensing/v1/activation/{id}/status" ||
            method == "GET" && permission == "trustset.get" && path == "/api/licensing/v1/trust-set")
        {
            return "installation-proof";
        }

        if (method == "GET" && permission == "object.download.grant" &&
            path == "/api/licensing/v1/backups/download-grants/{grantId}/content")
        {
            return "opaque-download-grant";
        }

        if (method == "POST" && permission == "certificate.sign" && path == "/internal/v1/sign")
        {
            return "internal-signer";
        }

        return "admin-capability";
    }

    private static string RepresentativePath(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, "\\{[^}:?]+(?::[^}?]+)?\\??\\}", "authorization-probe");

    private static string FindContractPath()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var cursor = new DirectoryInfo(start);
            while (cursor is not null)
            {
                var candidate = Path.Combine(cursor.FullName, "contracts", "central", "CONTRACT_ROUTES.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                cursor = cursor.Parent;
            }
        }

        throw new FileNotFoundException("Could not locate the source-bound effective040 Central route contract.");
    }

    private static string Normalize(string path)
    {
        var normalized = path.Trim().TrimStart('/').TrimEnd('/');
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "\\{[^}]+\\}", "{}");
        return "/" + normalized.ToLowerInvariant();
    }

    private readonly record struct RouteKey(string Method, string Path);
}
