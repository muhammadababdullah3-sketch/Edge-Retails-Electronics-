using System.Reflection;

namespace EdgeRetails.UnitTests;

public sealed class ArchitectureDependencyTests
{
    [Fact]
    public void Domain_DoesNotReferenceOuterLayers()
    {
        var references = GetSolutionReferences("EdgeRetails.Domain");

        Assert.DoesNotContain("EdgeRetails.Application", references);
        Assert.DoesNotContain("EdgeRetails.Infrastructure", references);
        Assert.DoesNotContain("EdgeRetails.Desktop", references);
        Assert.DoesNotContain("EdgeRetails.Worker", references);
    }

    [Fact]
    public void Application_DoesNotReferenceInfrastructureOrHosts()
    {
        var references = GetSolutionReferences("EdgeRetails.Application");

        Assert.DoesNotContain("EdgeRetails.Infrastructure", references);
        Assert.DoesNotContain("EdgeRetails.Desktop", references);
        Assert.DoesNotContain("EdgeRetails.Worker", references);
    }

    private static HashSet<string> GetSolutionReferences(string assemblyName)
    {
        return Assembly.Load(assemblyName)
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("EdgeRetails.", StringComparison.Ordinal))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }
}
