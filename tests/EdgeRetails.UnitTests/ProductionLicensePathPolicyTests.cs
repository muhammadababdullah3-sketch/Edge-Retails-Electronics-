using EdgeRetails.Infrastructure.Production.Licensing;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class ProductionLicensePathPolicyTests
{
    [Fact]
    public void DefaultProductionLicense_UsesMachineWideDocumentedPath()
    {
        var commonData = Path.Combine(Path.GetTempPath(), "ProgramData");

        Assert.Equal(
            Path.Combine(commonData, "EdgeRetails", "license.erlic"),
            ProductionLicensePathPolicy.Resolve(commonData, null));
    }

    [Fact]
    public void ExplicitStateRoot_KeepsIsolatedRehearsalLicenseSeparate()
    {
        var commonData = Path.Combine(Path.GetTempPath(), "ProgramData");
        var isolatedRoot = Path.Combine(Path.GetTempPath(), "EdgeRetails-Isolated", "state");

        Assert.Equal(
            Path.Combine(isolatedRoot, "license.erlic"),
            ProductionLicensePathPolicy.Resolve(commonData, isolatedRoot));
    }

    [Fact]
    public void MissingOrBlankRoots_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => ProductionLicensePathPolicy.Resolve("", null));
        Assert.Throws<ArgumentException>(() => ProductionLicensePathPolicy.Resolve(Path.GetTempPath(), " "));
    }
}
