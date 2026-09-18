using System.Reflection;

namespace EdgeRetails.IntegrationTests;

public sealed class ArchitectureDependencyTests
{
    [Fact]
    public void Infrastructure_DoesNotReferenceDesktopOrWorker()
    {
        var references = Assembly.Load("EdgeRetails.Infrastructure")
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("EdgeRetails.", StringComparison.Ordinal))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("EdgeRetails.Desktop", references);
        Assert.DoesNotContain("EdgeRetails.Worker", references);
    }
}
