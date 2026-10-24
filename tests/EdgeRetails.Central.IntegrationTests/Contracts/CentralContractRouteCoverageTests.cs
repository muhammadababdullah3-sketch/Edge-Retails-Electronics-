using System.Text.Json;
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
